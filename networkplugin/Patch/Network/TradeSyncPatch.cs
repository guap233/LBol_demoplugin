using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Utils;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.Core.Trade;

namespace NetworkPlugin.Patch.Network;

public static class TradeSyncPatch
{
    private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

    private static readonly object SyncLock = new();
    private static bool _subscribed;
    private static INetworkClient _subscribedClient;

    private static readonly Dictionary<string, TradeSessionState> _hostSessions = new(StringComparer.Ordinal);

    private const int RequestIdWindowSize = 64;
    private static readonly Dictionary<string, LinkedList<string>> _recentRequestIdsByTrade = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> _recentRequestIdSetsByTrade = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> _lastRequestTimestampBySender = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, TradeSessionState> _lastKnown = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, System.Threading.CancellationTokenSource> _commitTimeoutCts = new(StringComparer.Ordinal);

    private sealed class CompensationReceipt
    {
        public bool Ok { get; set; }
        public string FailureCode { get; set; }
        public string Message { get; set; }
        public long ReceivedTimestamp { get; set; }
    }
    private static readonly Dictionary<string, CompensationReceipt> _compensationReceipts = new(StringComparer.Ordinal);

    public static void ClearCompensationReceiptsForTest()
    {
        lock (SyncLock)
        {
            _compensationReceipts.Clear();
        }
    }

    public static bool IsApplyingTrade { get; private set; }

    public static event Action<TradeSessionState> OnTradeStateUpdated;

    public enum TradeStatus
    {
        Open = 0,
        Preparing = 1,
        Committing = 2,
        Completed = 3,
        Canceled = 4,
        Failed = 5,
        Compensated = 6,
    }

    public sealed class CardRef
    {
        public string CardId { get; set; }
        public int InstanceId { get; set; }
        public bool IsUpgraded { get; set; }
        public int UpgradeCounter { get; set; }
        public int? DeckCounter { get; set; }
        public string CardName { get; set; }
        public string CardType { get; set; }
    }

    public sealed class ExhibitRef
    {
        public string ExhibitId { get; set; }
    }

    public sealed class TradeSessionState
    {
        public string TradeId { get; set; }
        public string PlayerAId { get; set; }
        public string PlayerBId { get; set; }
        public string PlayerAName { get; set; }
        public string PlayerBName { get; set; }
        public int MaxTradeSlots { get; set; }
        public TradeStatus Status { get; set; }
        public bool AConfirmed { get; set; }
        public bool BConfirmed { get; set; }
        public bool APrepared { get; set; }
        public bool BPrepared { get; set; }
        public string CommitId { get; set; }
        public bool ACommitted { get; set; }
        public bool BCommitted { get; set; }
        public string FailureCode { get; set; }
        public string FailureStage { get; set; }
        public string FailurePlayerId { get; set; }
        public string ConflictExhibitId { get; set; }
        public string ReceiverPlayerId { get; set; }
        public int ProtocolVersion { get; set; } = TradeConstants.CurrentProtocolVersion;
        public int ProtocolVersionA { get; set; } = TradeConstants.ProtocolVersionUnknown;
        public int ProtocolVersionB { get; set; } = TradeConstants.ProtocolVersionUnknown;
        public List<CardRef> OfferA { get; set; } = new();
        public List<CardRef> OfferB { get; set; } = new();
        public int MoneyA { get; set; }
        public int MoneyB { get; set; }
        public List<ExhibitRef> ExhibitsA { get; set; } = new();
        public List<ExhibitRef> ExhibitsB { get; set; } = new();
        public string Reason { get; set; }
        public long Timestamp { get; set; }

        public bool IsParticipant(string playerId)
            => !string.IsNullOrWhiteSpace(playerId)
               && (string.Equals(PlayerAId, playerId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(PlayerBId, playerId, StringComparison.OrdinalIgnoreCase));

        public TradeSessionState Clone()
            => new TradeSessionState
            {
                TradeId = TradeId,
                PlayerAId = PlayerAId,
                PlayerBId = PlayerBId,
                PlayerAName = PlayerAName,
                PlayerBName = PlayerBName,
                MaxTradeSlots = MaxTradeSlots,
                Status = Status,
                AConfirmed = AConfirmed,
                BConfirmed = BConfirmed,
                APrepared = APrepared,
                BPrepared = BPrepared,
                CommitId = CommitId,
                ACommitted = ACommitted,
                BCommitted = BCommitted,
                FailureCode = FailureCode,
                FailureStage = FailureStage,
                FailurePlayerId = FailurePlayerId,
                ConflictExhibitId = ConflictExhibitId,
                ReceiverPlayerId = ReceiverPlayerId,
                ProtocolVersion = ProtocolVersion,
                ProtocolVersionA = ProtocolVersionA,
                ProtocolVersionB = ProtocolVersionB,
                OfferA = OfferA?.Select(CloneCard).ToList() ?? new List<CardRef>(),
                OfferB = OfferB?.Select(CloneCard).ToList() ?? new List<CardRef>(),
                MoneyA = MoneyA,
                MoneyB = MoneyB,
                ExhibitsA = ExhibitsA?.Select(CloneExhibit).ToList() ?? new List<ExhibitRef>(),
                ExhibitsB = ExhibitsB?.Select(CloneExhibit).ToList() ?? new List<ExhibitRef>(),
                Reason = Reason,
                Timestamp = Timestamp,
            };

        private static CardRef CloneCard(CardRef c)
            => c == null
                ? null
                : new CardRef
                {
                    CardId = c.CardId,
                    InstanceId = c.InstanceId,
                    IsUpgraded = c.IsUpgraded,
                    UpgradeCounter = c.UpgradeCounter,
                    DeckCounter = c.DeckCounter,
                    CardName = c.CardName,
                    CardType = c.CardType,
                };

        private static ExhibitRef CloneExhibit(ExhibitRef e)
            => e == null
                ? null
                : new ExhibitRef
                {
                    ExhibitId = e.ExhibitId,
                };
    }

    public static void EnsureSubscribed(INetworkClient client)
    {
        if (client == null)
        {
            return;
        }

        lock (SyncLock)
        {
            if (_subscribed && ReferenceEquals(_subscribedClient, client))
            {
                return;
            }
        }

        try
        {
            if (_subscribedClient != null)
            {
                _subscribedClient.OnGameEventReceived -= OnGameEventReceived;
                _subscribedClient.OnConnectionStateChanged -= OnConnectionStateChanged;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[TradeSyncPatch] Unsubscribe failed: {ex.Message}");
        }

        try
        {
            client.OnGameEventReceived += OnGameEventReceived;
            client.OnConnectionStateChanged += OnConnectionStateChanged;
            lock (SyncLock)
            {
                _subscribedClient = client;
                _subscribed = true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[TradeSyncPatch] Subscribe failed: {ex.Message}");
            lock (SyncLock)
            {
                _subscribedClient = null;
                _subscribed = false;
            }
        }
    }

    private static void OnConnectionStateChanged(bool connected)
    {
        if (!connected)
        {
            ResetForTest();
            lock (SyncLock)
            {
                foreach (var kvp in _commitTimeoutCts)
                {
                    try { kvp.Value.Cancel(); kvp.Value.Dispose(); }
                    catch (Exception ex) { Plugin.Logger?.LogDebug($"[TradeSyncPatch] Cancel commit timeout error: {ex.Message}"); }
                }
                _commitTimeoutCts.Clear();
            }
        }
    }

    public static void SetClientForTest(INetworkClient client)
    {
        lock (SyncLock)
        {
            if (_subscribedClient != null)
            {
                try
                {
                    _subscribedClient.OnGameEventReceived -= OnGameEventReceived;
                    _subscribedClient.OnConnectionStateChanged -= OnConnectionStateChanged;
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"[TradeSyncPatch] SetClient unsubscribe error: {ex.Message}");
                }
            }
            _subscribedClient = null;
            _subscribed = false;
        }
        EnsureSubscribed(client);
        if (client != null)
        {
            NetworkIdentityTracker.EnsureSubscribed(client);
        }
    }

    public static INetworkClient TryGetClient()
        => ServiceProvider?.GetService<INetworkClient>();

    public static void RequestStartTrade(string tradeId, string playerAId, string playerBId, int maxTradeSlots)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradeStartRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            PlayerAId = playerAId,
            PlayerBId = playerBId,
            MaxTradeSlots = maxTradeSlots,
            ProtocolVersion = TradeConstants.CurrentProtocolVersion,
        });
    }

    public static void RequestOfferUpdate(string tradeId, string requesterPlayerId, List<CardRef> offerCards, int moneyOffer, List<string> exhibitIds)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradeOfferUpdateRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            RequesterPlayerId = requesterPlayerId,
            Offer = offerCards ?? new List<CardRef>(),
            Money = Math.Max(0, moneyOffer),
            Exhibits = (exhibitIds ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray(),
            ProtocolVersion = TradeConstants.CurrentProtocolVersion,
        });
    }

    public static void RequestConfirm(string tradeId, string requesterPlayerId)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradeConfirmRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            RequesterPlayerId = requesterPlayerId,
            ProtocolVersion = TradeConstants.CurrentProtocolVersion,
        });
    }

    public static void RequestCancel(string tradeId, string requesterPlayerId)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradeCancelRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            RequesterPlayerId = requesterPlayerId,
        });
    }

    public static void RequestSnapshot(string tradeId, string requesterPlayerId)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradeSnapshotRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            RequesterPlayerId = requesterPlayerId,
        });
    }

    public static void RequestPrepareResult(
        string tradeId,
        string requesterPlayerId,
        bool ok,
        string reason,
        string conflictExhibitId = null,
        string receiverPlayerId = null)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradePrepareResultRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            RequesterPlayerId = requesterPlayerId,
            Ok = ok,
            Reason = reason,
            ConflictExhibitId = conflictExhibitId,
            ReceiverPlayerId = receiverPlayerId,
        });
    }

    public static void RequestCommitResult(string tradeId, string commitId, string requesterPlayerId, bool ok, string failureCode = null, string failureStage = null)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradeCommitResultRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            CommitId = commitId,
            RequesterPlayerId = requesterPlayerId,
            Ok = ok,
            FailureCode = failureCode,
            FailureStage = failureStage,
        });
    }

    public static void RequestCompensationResult(string tradeId, string commitId, string requesterPlayerId, string failureCode = null, string message = null, bool ok = false)
    {
        string requestId = Guid.NewGuid().ToString("N");
        SendToHost(NetworkMessageTypes.OnTradeCompensationResultRequest, new
        {
            Timestamp = DateTime.UtcNow.Ticks,
            RequestId = requestId,
            TradeId = tradeId,
            CommitId = commitId,
            RequesterPlayerId = requesterPlayerId,
            Ok = ok,
            FailureCode = failureCode,
            Message = message,
        });
    }


    private static void SendToHost(string eventType, object payload)
    {
        try
        {
            INetworkClient client = TryGetClient();
            if (client != null && client.IsConnected)
            {
                NetworkIdentityTracker.EnsureSubscribed(client);
                EnsureSubscribed(client);
                client.BroadcastState(eventType, payload);
                return;
            }

            if (TryGetJsonElement(payload, out JsonElement root))
            {
                HandleRequest(eventType, root);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[TradeSyncPatch] SendToHost error: {ex.Message}");
        }
    }

    private static void OnGameEventReceived(string eventType, object payload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        if (eventType != NetworkMessageTypes.OnTradeStartRequest &&
            eventType != NetworkMessageTypes.OnTradeOfferUpdateRequest &&
            eventType != NetworkMessageTypes.OnTradeConfirmRequest &&
            eventType != NetworkMessageTypes.OnTradeCancelRequest &&
            eventType != NetworkMessageTypes.OnTradeSnapshotRequest &&
            eventType != NetworkMessageTypes.OnTradePrepareResultRequest &&
            eventType != NetworkMessageTypes.OnTradeCommitResultRequest &&
            eventType != NetworkMessageTypes.OnTradeCompensationResultRequest &&
            eventType != NetworkMessageTypes.OnTradeStateUpdate)
        {
            return;
        }

        if (!TryGetJsonElement(payload, out JsonElement root))
        {
            return;
        }

        switch (eventType)
        {
            case NetworkMessageTypes.OnTradeStateUpdate:
                HandleStateUpdate(root);
                return;
            default:
                HandleRequest(eventType, root);
                return;
        }
    }

    private static void HandleRequest(string eventType, JsonElement root)
    {

        if (!NetworkIdentityTracker.GetSelfIsHost())
        {
            return;
        }

        string tradeId = GetString(root, "TradeId");
        if (string.IsNullOrWhiteSpace(tradeId))
        {
            return;
        }

        string requestId = GetString(root, "RequestId");
        long ts = GetLong(root, "Timestamp", 0);
        string senderId = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(senderId))
        {
            senderId = GetString(root, "PlayerAId");
        }
        if (!string.IsNullOrWhiteSpace(requestId) && IsDuplicateRequest_NoLock(tradeId, senderId, requestId, ts))
        {
            return;
        }

        switch (eventType)
        {
            case NetworkMessageTypes.OnTradeStartRequest:
                HandleStart(tradeId, root);
                return;
            case NetworkMessageTypes.OnTradeOfferUpdateRequest:
                HandleOfferUpdate(tradeId, root);
                return;
            case NetworkMessageTypes.OnTradeConfirmRequest:
                HandleConfirm(tradeId, root);
                return;
            case NetworkMessageTypes.OnTradePrepareResultRequest:
                HandlePrepareResult(tradeId, root);
                return;
            case NetworkMessageTypes.OnTradeCommitResultRequest:
                HandleCommitResult(tradeId, root);
                return;
            case NetworkMessageTypes.OnTradeCompensationResultRequest:
                HandleCompensationResult(tradeId, root);
                return;
            case NetworkMessageTypes.OnTradeCancelRequest:
                HandleCancel(tradeId, root);
                return;
            case NetworkMessageTypes.OnTradeSnapshotRequest:
                HandleSnapshotRequest(tradeId, root);
                return;
        }
    }

    private static string ResolvePlayerDisplayNameSafe(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return string.Empty;
        }

        try
        {
            return OtherPlayersOverlayPatch.ResolveDisplayName(playerId, null, isLocal: false);
        }
        catch
        {
            return playerId;
        }
    }

    private static void StartCommitTimeout_NoLock(string tradeId, string commitId, int timeoutSeconds = TradeConstants.DefaultCommitTimeoutSeconds)
    {
        CancelCommitTimeout_NoLock(tradeId);
        var cts = new System.Threading.CancellationTokenSource();
        _commitTimeoutCts[tradeId] = cts;

        _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            TriggerCommitTimeout(tradeId, commitId);
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private static void CancelCommitTimeout_NoLock(string tradeId)
    {
        if (_commitTimeoutCts.TryGetValue(tradeId, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"[TradeSyncPatch] CancelCommitTimeout error: {ex.Message}");
            }
            _commitTimeoutCts.Remove(tradeId);
        }
    }

    public static void TriggerCommitTimeoutForTest(string tradeId, string commitId)
    {
        TriggerCommitTimeout(tradeId, commitId);
    }

    private static void TriggerCommitTimeout(string tradeId, string commitId)
    {
        bool shouldBroadcast = false;
        TradeSessionState state = null;
        lock (SyncLock)
        {
            state = GetHostSession(tradeId);
            if (state != null && state.Status == TradeStatus.Committing && string.Equals(state.CommitId, commitId, StringComparison.Ordinal))
            {
                CancelCommitTimeout_NoLock(tradeId);
                state.Status = TradeStatus.Failed;
                state.FailureCode = TradeFailureCodes.CommitTimeout;
                state.FailureStage = TradeFailureStage.Committing.ToString();
                state.FailurePlayerId = !state.ACommitted ? state.PlayerAId : state.PlayerBId;
                state.Reason = TradeFailureCodes.CommitTimeout;
                state.Timestamp = DateTime.UtcNow.Ticks;
                shouldBroadcast = true;
            }
        }

        if (shouldBroadcast && state != null)
        {
            Plugin.Logger?.LogWarning($"[TradeSyncPatch] Trade {tradeId} commit {commitId} timed out. Transitioned to Failed.");
            BroadcastState(state);
        }
    }

    private static void HandleStart(string tradeId, JsonElement root)
    {
        string a = GetString(root, "PlayerAId");
        string b = GetString(root, "PlayerBId");
        int maxSlots = GetInt(root, "MaxTradeSlots", 5);
        int protocolVersion = GetInt(root, "ProtocolVersion", TradeConstants.LegacyProtocolVersion);

        if (protocolVersion < TradeConstants.MinSupportedProtocolVersion)
        {
            Plugin.Logger?.LogWarning($"[TradeSyncPatch] HandleStart rejected: incompatible protocol version {protocolVersion}");
            BroadcastState(new TradeSessionState
            {
                TradeId = tradeId,
                PlayerAId = a,
                PlayerBId = b,
                MaxTradeSlots = maxSlots,
                Status = TradeStatus.Failed,
                Reason = "ProtocolVersionIncompatible",
                FailureCode = TradeFailureCodes.ProtocolIncompatible,
                FailureStage = TradeFailureStage.Validation.ToString(),
                ProtocolVersion = protocolVersion,
                ProtocolVersionA = protocolVersion,
                ProtocolVersionB = TradeConstants.ProtocolVersionUnknown,
                Timestamp = DateTime.UtcNow.Ticks,
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Logger?.LogWarning($"[TradeSyncPatch] HandleStart rejected: invalid participants (a='{a}', b='{b}')");
            BroadcastState(new TradeSessionState
            {
                TradeId = tradeId,
                PlayerAId = a,
                PlayerBId = b,
                MaxTradeSlots = maxSlots,
                Status = TradeStatus.Failed,
                Reason = "InvalidParticipants",
                FailureCode = TradeFailureCodes.NotParticipant,
                FailureStage = TradeFailureStage.Validation.ToString(),
                ProtocolVersion = protocolVersion,
                ProtocolVersionA = protocolVersion,
                ProtocolVersionB = TradeConstants.ProtocolVersionUnknown,
                Timestamp = DateTime.UtcNow.Ticks,
            });
            return;
        }

        lock (SyncLock)
        {
            if (_hostSessions.TryGetValue(tradeId, out var existingSession) && existingSession != null)
            {
                if (existingSession.Status == TradeStatus.Completed ||
                    existingSession.Status == TradeStatus.Canceled ||
                    existingSession.Status == TradeStatus.Failed ||
                    existingSession.Status == TradeStatus.Compensated)
                {
                    Plugin.Logger?.LogWarning($"[TradeSyncPatch] 交易 {tradeId} 处于终态 ({existingSession.Status})，拒绝重开已有交易ID");
                    return;
                }
            }

            if (!_hostSessions.TryGetValue(tradeId, out var state) || state == null)
            {
                if (_hostSessions.Count >= TradeConstants.MaxConcurrentTradeSessions)
                {
                    var terminalKeys = _hostSessions
                        .Where(kv => kv.Value != null && (kv.Value.Status == TradeStatus.Completed ||
                                                          kv.Value.Status == TradeStatus.Canceled ||
                                                          kv.Value.Status == TradeStatus.Failed ||
                                                          kv.Value.Status == TradeStatus.Compensated))
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var k in terminalKeys)
                    {
                        _hostSessions.Remove(k);
                        _recentRequestIdsByTrade.Remove(k);
                        _recentRequestIdSetsByTrade.Remove(k);
                        var senderKeysToRemove = _lastRequestTimestampBySender.Keys
                            .Where(key => key.StartsWith(k + ":", StringComparison.Ordinal))
                            .ToList();
                        foreach (var sk in senderKeysToRemove)
                        {
                            _lastRequestTimestampBySender.Remove(sk);
                        }
                    }
                }

                if (_hostSessions.Count >= TradeConstants.MaxConcurrentTradeSessions)
                {
                    Plugin.Logger?.LogWarning($"[TradeSyncPatch] 活动交易表达到配额上限 ({TradeConstants.MaxConcurrentTradeSessions})，拒绝新交易 {tradeId}");
                    return;
                }

                state = new TradeSessionState
                {
                    TradeId = tradeId,
                    PlayerAId = a,
                    PlayerBId = b,
                    PlayerAName = ResolvePlayerDisplayNameSafe(a),
                    PlayerBName = ResolvePlayerDisplayNameSafe(b),
                    MaxTradeSlots = maxSlots,
                    Status = TradeStatus.Open,
                    ProtocolVersion = protocolVersion,
                    ProtocolVersionA = protocolVersion,
                    ProtocolVersionB = TradeConstants.ProtocolVersionUnknown,
                    Timestamp = DateTime.UtcNow.Ticks,
                };
                _hostSessions[tradeId] = state;
            }
            else
            {
                state.MaxTradeSlots = maxSlots;
                state.ProtocolVersion = protocolVersion;
                state.ProtocolVersionA = protocolVersion;
                state.ProtocolVersionB = TradeConstants.ProtocolVersionUnknown;
                state.Timestamp = DateTime.UtcNow.Ticks;
                if (string.IsNullOrWhiteSpace(state.PlayerAName))
                {
                    state.PlayerAName = ResolvePlayerDisplayNameSafe(state.PlayerAId);
                }
                if (string.IsNullOrWhiteSpace(state.PlayerBName))
                {
                    state.PlayerBName = ResolvePlayerDisplayNameSafe(state.PlayerBId);
                }
            }

            state.AConfirmed = false;
            state.BConfirmed = false;
            state.APrepared = false;
            state.BPrepared = false;
            state.CommitId = null;
            state.ACommitted = false;
            state.BCommitted = false;
            state.FailureCode = null;
            state.FailureStage = null;
            state.FailurePlayerId = null;
            state.Status = TradeStatus.Open;
            state.Reason = null;
            CancelCommitTimeout_NoLock(tradeId);
        }

        BroadcastState(GetHostSession(tradeId));
    }

    private static void HandleOfferUpdate(string tradeId, JsonElement root)
    {
        string requester = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(requester))
        {
            return;
        }

        TradeSessionState state = GetHostSession(tradeId);
        if (state == null || state.Status != TradeStatus.Open)
        {
            return;
        }

        if (!state.IsParticipant(requester))
        {
            return;
        }

        List<CardRef> offer = SanitizeOffer(ParseOffer(root), state.MaxTradeSlots);
        int money = SanitizeMoney(GetInt(root, "Money", 0));
        List<ExhibitRef> exhibits = SanitizeExhibits(ParseExhibits(root));

        lock (SyncLock)
        {
            state = GetHostSession(tradeId);
            if (state == null || state.Status != TradeStatus.Open)
            {
                return;
            }

            state.AConfirmed = false;
            state.BConfirmed = false;
            state.APrepared = false;
            state.BPrepared = false;
            state.CommitId = null;
            state.ACommitted = false;
            state.BCommitted = false;
            state.FailureCode = null;
            state.FailureStage = null;
            state.FailurePlayerId = null;
            state.ConflictExhibitId = null;
            state.ReceiverPlayerId = null;
            state.Reason = null;

            if (string.Equals(state.PlayerAId, requester, StringComparison.OrdinalIgnoreCase))
            {
                state.OfferA = offer;
                state.MoneyA = money;
                state.ExhibitsA = exhibits;
            }
            else if (string.Equals(state.PlayerBId, requester, StringComparison.OrdinalIgnoreCase))
            {
                state.OfferB = offer;
                state.MoneyB = money;
                state.ExhibitsB = exhibits;
            }
            else
            {
                return;
            }

            int offeredVersion = GetInt(root, "ProtocolVersion", TradeConstants.ProtocolVersionUnknown);
            if (offeredVersion > 0)
            {
                if (string.Equals(state.PlayerAId, requester, StringComparison.OrdinalIgnoreCase))
                {
                    state.ProtocolVersionA = offeredVersion;
                }
                else if (string.Equals(state.PlayerBId, requester, StringComparison.OrdinalIgnoreCase))
                {
                    state.ProtocolVersionB = offeredVersion;
                }

                if (state.ProtocolVersionA > 0 && state.ProtocolVersionB > 0)
                {
                    state.ProtocolVersion = Math.Min(state.ProtocolVersionA, state.ProtocolVersionB);
                }
                else if (state.ProtocolVersionA > 0)
                {
                    state.ProtocolVersion = state.ProtocolVersionA;
                }
                else if (state.ProtocolVersionB > 0)
                {
                    state.ProtocolVersion = state.ProtocolVersionB;
                }
            }

            state.Timestamp = DateTime.UtcNow.Ticks;
        }

        BroadcastState(GetHostSession(tradeId));
    }

    private static void HandleConfirm(string tradeId, JsonElement root)
    {
        string requester = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(requester))
        {
            return;
        }

        TradeSessionState state = GetHostSession(tradeId);
        if (state == null || state.Status != TradeStatus.Open)
        {
            return;
        }

        if (!state.IsParticipant(requester))
        {
            return;
        }

        int requesterVersion = GetInt(root, "ProtocolVersion", TradeConstants.LegacyProtocolVersion);

        lock (SyncLock)
        {
            state = GetHostSession(tradeId);
            if (state == null || state.Status != TradeStatus.Open)
            {
                return;
            }

            if (string.Equals(state.PlayerAId, requester, StringComparison.OrdinalIgnoreCase))
            {
                state.AConfirmed = true;
                state.ProtocolVersionA = requesterVersion;
            }
            else if (string.Equals(state.PlayerBId, requester, StringComparison.OrdinalIgnoreCase))
            {
                state.BConfirmed = true;
                state.ProtocolVersionB = requesterVersion;
            }
            else
            {
                return;
            }

            if (state.ProtocolVersionA > 0 && state.ProtocolVersionB > 0)
            {
                state.ProtocolVersion = Math.Min(state.ProtocolVersionA, state.ProtocolVersionB);
            }
            else if (state.ProtocolVersionA > 0)
            {
                state.ProtocolVersion = state.ProtocolVersionA;
            }
            else if (state.ProtocolVersionB > 0)
            {
                state.ProtocolVersion = state.ProtocolVersionB;
            }
            else
            {
                state.ProtocolVersion = TradeConstants.CurrentProtocolVersion;
            }
            state.Timestamp = DateTime.UtcNow.Ticks;

            if (state.AConfirmed && state.BConfirmed)
            {
                int effectiveA = state.ProtocolVersionA > 0 ? state.ProtocolVersionA : TradeConstants.LegacyProtocolVersion;
                int effectiveB = state.ProtocolVersionB > 0 ? state.ProtocolVersionB : TradeConstants.LegacyProtocolVersion;
                state.ProtocolVersion = Math.Min(effectiveA, effectiveB);

                if (effectiveA < TradeConstants.MinSupportedProtocolVersion ||
                    effectiveB < TradeConstants.MinSupportedProtocolVersion)
                {
                    Plugin.Logger?.LogWarning($"[TradeSyncPatch] HandleConfirm rejected: incompatible protocol version (A={effectiveA}, B={effectiveB})");
                    state.Status = TradeStatus.Failed;
                    state.FailureCode = TradeFailureCodes.ProtocolIncompatible;
                    state.FailureStage = TradeFailureStage.Validation.ToString();
                    state.FailurePlayerId = effectiveA < TradeConstants.MinSupportedProtocolVersion ? state.PlayerAId : state.PlayerBId;
                    state.Reason = TradeConstants.ReasonProtocolVersionIncompatible;
                }
                else
                {
                    state.Status = TradeStatus.Preparing;
                    state.APrepared = false;
                    state.BPrepared = false;
                    state.CommitId = null;
                    state.ACommitted = false;
                    state.BCommitted = false;
                    state.FailureCode = null;
                    state.FailureStage = null;
                    state.FailurePlayerId = null;
                    state.ConflictExhibitId = null;
                    state.ReceiverPlayerId = null;
                    state.Reason = null;
                }
            }
        }

        BroadcastState(GetHostSession(tradeId));
    }

    private static void HandlePrepareResult(string tradeId, JsonElement root)
    {
        string requester = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(requester))
        {
            return;
        }

        bool ok = GetBool(root, "Ok");
        string reason = GetString(root, "Reason");
        string conflictExhibitId = GetString(root, "ConflictExhibitId");
        string receiverPlayerId = GetString(root, "ReceiverPlayerId");

        TradeSessionState state = GetHostSession(tradeId);
        if (state == null || state.Status != TradeStatus.Preparing)
        {
            return;
        }

        if (!state.IsParticipant(requester))
        {
            return;
        }

        bool shouldBroadcast = false;
        lock (SyncLock)
        {
            state = GetHostSession(tradeId);
            if (state == null || state.Status != TradeStatus.Preparing)
            {
                return;
            }

            if (string.Equals(state.PlayerAId, requester, StringComparison.OrdinalIgnoreCase))
            {
                state.APrepared = ok;
            }
            else if (string.Equals(state.PlayerBId, requester, StringComparison.OrdinalIgnoreCase))
            {
                state.BPrepared = ok;
            }
            else
            {
                return;
            }

            state.Timestamp = DateTime.UtcNow.Ticks;

            if (!ok)
            {
                state.Status = TradeStatus.Open;
                state.AConfirmed = false;
                state.BConfirmed = false;
                state.APrepared = false;
                state.BPrepared = false;
                state.CommitId = null;
                state.ACommitted = false;
                state.BCommitted = false;
                state.Reason = string.IsNullOrWhiteSpace(reason) ? "PrepareFailed" : reason;
                state.FailureCode = state.Reason;
                state.FailureStage = TradeFailureStage.Validation.ToString();
                state.FailurePlayerId = requester;
                state.ConflictExhibitId = conflictExhibitId;
                state.ReceiverPlayerId = !string.IsNullOrWhiteSpace(receiverPlayerId) ? receiverPlayerId : requester;
                shouldBroadcast = true;
            }
            else if (state.APrepared && state.BPrepared)
            {
                state.CommitId = Guid.NewGuid().ToString("N");
                state.ACommitted = false;
                state.BCommitted = false;
                state.Status = TradeStatus.Committing;
                state.Reason = null;
                state.FailureCode = null;
                state.FailureStage = null;
                state.FailurePlayerId = null;
                state.ConflictExhibitId = null;
                state.ReceiverPlayerId = null;
                shouldBroadcast = true;
                StartCommitTimeout_NoLock(tradeId, state.CommitId);
            }
        }

        if (shouldBroadcast)
        {
            BroadcastState(GetHostSession(tradeId));
        }
    }

    private static void HandleCommitResult(string tradeId, JsonElement root)
    {
        string requester = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(requester))
        {
            return;
        }

        string commitId = GetString(root, "CommitId");
        bool ok = GetBool(root, "Ok");
        string failureCode = GetString(root, "FailureCode");
        string failureStage = GetString(root, "FailureStage");

        TradeSessionState state = GetHostSession(tradeId);
        if (state == null || state.Status != TradeStatus.Committing)
        {
            return;
        }

        if (!state.IsParticipant(requester))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(commitId) || !string.Equals(state.CommitId, commitId, StringComparison.Ordinal))
        {
            Plugin.Logger?.LogWarning($"[TradeSyncPatch] HandleCommitResult rejected: commitId mismatch (expected='{state.CommitId}', received='{commitId}')");
            return;
        }

        bool shouldBroadcast = false;
        lock (SyncLock)
        {
            state = GetHostSession(tradeId);
            if (state == null || state.Status != TradeStatus.Committing || !string.Equals(state.CommitId, commitId, StringComparison.Ordinal))
            {
                return;
            }

            bool isA = string.Equals(state.PlayerAId, requester, StringComparison.OrdinalIgnoreCase);
            bool isB = string.Equals(state.PlayerBId, requester, StringComparison.OrdinalIgnoreCase);
            if (!isA && !isB)
            {
                return;
            }

            bool alreadyCommitted = isA ? state.ACommitted : state.BCommitted;
            if (ok && alreadyCommitted)
            {
                // 主机重复收到同一参与者、同一 CommitId 的成功回执时，不更新状态时间或重新广播
                return;
            }

            if (isA)
            {
                state.ACommitted = ok;
            }
            else
            {
                state.BCommitted = ok;
            }

            if (!ok)
            {
                state.Timestamp = DateTime.UtcNow.Ticks;
                CancelCommitTimeout_NoLock(tradeId);
                state.Status = TradeStatus.Failed;
                state.FailureCode = string.IsNullOrWhiteSpace(failureCode) ? TradeFailureCodes.PostConditionFailed : failureCode;
                state.FailureStage = string.IsNullOrWhiteSpace(failureStage) ? TradeFailureStage.Committing.ToString() : failureStage;
                state.FailurePlayerId = requester;
                state.Reason = state.FailureCode;
                shouldBroadcast = true;
            }
            else if (state.ACommitted && state.BCommitted)
            {
                state.Timestamp = DateTime.UtcNow.Ticks;
                CancelCommitTimeout_NoLock(tradeId);
                state.Status = TradeStatus.Completed;
                state.Reason = null;
                state.FailureCode = null;
                state.FailureStage = null;
                state.FailurePlayerId = null;
                shouldBroadcast = true;
            }
            else
            {
                state.Timestamp = DateTime.UtcNow.Ticks;
                shouldBroadcast = true;
            }
        }

        if (shouldBroadcast)
        {
            BroadcastState(GetHostSession(tradeId));
        }
    }

    private static void HandleCompensationResult(string tradeId, JsonElement root)
    {
        string requester = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(requester))
        {
            return;
        }

        string commitId = GetString(root, "CommitId");
        bool ok = GetBool(root, "Ok");
        string failureCode = GetString(root, "FailureCode");
        string message = GetString(root, "Message");

        TradeSessionState state = GetHostSession(tradeId);
        if (state == null)
        {
            return;
        }

        if (!state.IsParticipant(requester))
        {
            return;
        }

        bool shouldBroadcast = false;
        lock (SyncLock)
        {
            state = GetHostSession(tradeId);
            if (state == null || !state.IsParticipant(requester))
            {
                return;
            }

            // 1. 提交批次一致性守卫 (CommitId Guard)
            if (string.IsNullOrWhiteSpace(commitId) || !string.Equals(state.CommitId, commitId, StringComparison.Ordinal))
            {
                Plugin.Logger?.LogWarning($"[TradeSync] 收到过期的补偿回执: TradeId={tradeId}, ReqCommit={commitId}, CurrentCommit={state.CommitId}，忽略处理");
                return;
            }

            // 2. 状态机阶段守卫 (State Phase Guard)
            if (state.Status != TradeStatus.Committing && state.Status != TradeStatus.Failed)
            {
                Plugin.Logger?.LogWarning($"[TradeSync] 收到非提交/失败阶段的补偿回执: TradeId={tradeId}, Status={state.Status}，安全忽略");
                return;
            }

            // 3. 幂等去重检查 (Idempotency Guard)
            string receiptKey = $"{tradeId}:{commitId}:{requester}";
            if (_compensationReceipts.TryGetValue(receiptKey, out var existingReceipt))
            {
                if (existingReceipt.Ok == ok && string.Equals(existingReceipt.FailureCode, failureCode, StringComparison.Ordinal))
                {
                    Plugin.Logger?.LogDebug($"[TradeSync] 收到重复补偿回执: TradeId={tradeId}, CommitId={commitId}, Requester={requester}, Ok={ok}，幂等抑制广播");
                    return;
                }
            }

            if (_compensationReceipts.Count >= 128)
            {
                var oldestKey = _compensationReceipts.OrderBy(kv => kv.Value.ReceivedTimestamp).FirstOrDefault().Key;
                if (oldestKey != null)
                {
                    _compensationReceipts.Remove(oldestKey);
                }
            }

            _compensationReceipts[receiptKey] = new CompensationReceipt
            {
                Ok = ok,
                FailureCode = failureCode,
                Message = message,
                ReceivedTimestamp = DateTime.UtcNow.Ticks,
            };

            if (!ok)
            {
                CancelCommitTimeout_NoLock(tradeId);
                state.Status = TradeStatus.Failed;
                state.FailureCode = TradeFailureCodes.CompensationFailed;
                state.FailureStage = TradeFailureStage.Compensation.ToString();
                state.FailurePlayerId = requester;
                state.Reason = !string.IsNullOrWhiteSpace(message) ? message : (string.IsNullOrWhiteSpace(failureCode) ? TradeFailureCodes.CompensationFailed : failureCode);
                state.Timestamp = DateTime.UtcNow.Ticks;
                shouldBroadcast = true;
            }
            else
            {
                // ok == true 协议语义：记录参与者已完成本地自愈，不覆盖为 CompensationFailed，不误触发错误广播
                shouldBroadcast = false;
            }
        }

        if (shouldBroadcast)
        {
            BroadcastState(GetHostSession(tradeId));
        }
    }

    private static void HandleCancel(string tradeId, JsonElement root)
    {
        string requester = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(requester))
        {
            return;
        }

        TradeSessionState state = GetHostSession(tradeId);
        if (state == null)
        {
            return;
        }

        lock (SyncLock)
        {
            state = GetHostSession(tradeId);
            if (state == null)
            {
                return;
            }

            if (!state.IsParticipant(requester))
            {
                return;
            }

            if (state.Status == TradeStatus.Committing)
            {
                Plugin.Logger?.LogWarning($"[TradeSyncPatch] Cancel rejected: trade {tradeId} is currently committing");
                return;
            }

            if (state.Status == TradeStatus.Completed || state.Status == TradeStatus.Failed)
            {
                return;
            }

            CancelCommitTimeout_NoLock(tradeId);
            state.Status = TradeStatus.Canceled;
            state.Reason = "Canceled";
            state.Timestamp = DateTime.UtcNow.Ticks;
        }

        BroadcastState(GetHostSession(tradeId));
    }

    private static void HandleSnapshotRequest(string tradeId, JsonElement root)
    {
        string requester = GetString(root, "RequesterPlayerId");
        if (string.IsNullOrWhiteSpace(requester))
        {
            return;
        }

        TradeSessionState state = GetHostSession(tradeId);
        if (state == null || !state.IsParticipant(requester))
        {
            return;
        }

        BroadcastState(state);
    }

    public static void ResetForTest()
    {
        lock (SyncLock)
        {
            _lastKnown.Clear();
            _hostSessions.Clear();
            _recentRequestIdsByTrade.Clear();
            _recentRequestIdSetsByTrade.Clear();
            _lastRequestTimestampBySender.Clear();
            _compensationReceipts.Clear();
        }
    }

    public static void NotifyTradeStateUpdatedForTest(TradeSessionState state)
    {
        ApplyStateLocal(state);
    }

    private static void ApplyStateLocal(TradeSessionState state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.TradeId))
        {
            return;
        }

        TradeSessionState cloned = state.Clone();

        lock (SyncLock)
        {
            if (_lastKnown.TryGetValue(cloned.TradeId, out var existing))
            {
                // Terminal states: Completed, Compensated, Canceled can never be changed
                if (existing.Status == TradeStatus.Completed ||
                    existing.Status == TradeStatus.Compensated ||
                    existing.Status == TradeStatus.Canceled)
                {
                    Plugin.Logger?.LogWarning($"[TradeSyncPatch] 交易 {cloned.TradeId} 处于终态 ({existing.Status})，忽略新状态 ({cloned.Status})");
                    return;
                }

                // Failed state can only transition to Compensated or update within Failed
                if (existing.Status == TradeStatus.Failed &&
                    cloned.Status != TradeStatus.Failed &&
                    cloned.Status != TradeStatus.Compensated)
                {
                    if (cloned.Status == TradeStatus.Committing)
                    {
                        Plugin.Logger?.LogInfo($"[TradeSyncPatch] 交易 {cloned.TradeId} 失败后收到重复 Committing 广播，通知协调器幂等回复");
                        try
                        {
                            Plugin.RunOnMainThread(() =>
                            {
                                try { OnTradeStateUpdated?.Invoke(cloned); }
                                catch (Exception ex) { Plugin.Logger?.LogError($"[TradeSyncPatch] OnTradeStateUpdated invoke error: {ex.Message}"); }
                            });
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger?.LogDebug($"[TradeSyncPatch] RunOnMainThread error: {ex.Message}");
                            OnTradeStateUpdated?.Invoke(cloned);
                        }
                    }
                    else
                    {
                        Plugin.Logger?.LogWarning($"[TradeSyncPatch] 交易 {cloned.TradeId} 处于失败态，拒绝倒退为 {cloned.Status}");
                    }
                    return;
                }
            }

            if (_lastKnown.Count >= TradeConstants.MaxConcurrentTradeSessions * 2 && !_lastKnown.ContainsKey(cloned.TradeId))
            {
                var oldestKeys = _lastKnown.Keys.Take(TradeConstants.MaxConcurrentTradeSessions).ToList();
                foreach (var k in oldestKeys)
                {
                    _lastKnown.Remove(k);
                }
            }

            _lastKnown[cloned.TradeId] = cloned;
        }

        TryPopIncomingTradeConfirmDialog(cloned);

        try
        {
            Plugin.RunOnMainThread(() =>
            {
                try
                {
                    OnTradeStateUpdated?.Invoke(cloned);
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogError($"[TradeSyncPatch] OnTradeStateUpdated invoke error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[TradeSyncPatch] RunOnMainThread error: {ex.Message}");
            OnTradeStateUpdated?.Invoke(cloned);
        }
    }

    private static void BroadcastState(TradeSessionState state)
    {
        if (state == null)
        {
            return;
        }

        ApplyStateLocal(state);

        try
        {
            INetworkClient client = TryGetClient();
            if (client == null || !client.IsConnected)
            {
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(client);

            var payload = new
            {
                Timestamp = DateTime.UtcNow.Ticks,
                EventType = NetworkMessageTypes.OnTradeStateUpdate,
                TradeId = state.TradeId,
                PlayerAId = state.PlayerAId,
                PlayerBId = state.PlayerBId,
                PlayerAName = state.PlayerAName,
                PlayerBName = state.PlayerBName,
                MaxTradeSlots = state.MaxTradeSlots,
                Status = state.Status.ToString(),
                AConfirmed = state.AConfirmed,
                BConfirmed = state.BConfirmed,
                APrepared = state.APrepared,
                BPrepared = state.BPrepared,
                CommitId = state.CommitId,
                ACommitted = state.ACommitted,
                BCommitted = state.BCommitted,
                FailureCode = state.FailureCode,
                FailureStage = state.FailureStage,
                FailurePlayerId = state.FailurePlayerId,
                ConflictExhibitId = state.ConflictExhibitId,
                ReceiverPlayerId = state.ReceiverPlayerId,
                ProtocolVersion = state.ProtocolVersion,
                OfferA = state.OfferA ?? new List<CardRef>(),
                OfferB = state.OfferB ?? new List<CardRef>(),
                MoneyA = state.MoneyA,
                MoneyB = state.MoneyB,
                ExhibitsA = state.ExhibitsA ?? new List<ExhibitRef>(),
                ExhibitsB = state.ExhibitsB ?? new List<ExhibitRef>(),
                Reason = state.Reason,
            };

            client.BroadcastState(NetworkMessageTypes.OnTradeStateUpdate, payload);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[TradeSyncPatch] BroadcastState failed: {ex.Message}");
        }
    }

    private static void HandleStateUpdate(JsonElement root)
    {
        TradeSessionState state = ParseState(root);
        if (state == null || string.IsNullOrWhiteSpace(state.TradeId))
        {
            return;
        }

        ApplyStateLocal(state);
    }

    private static void TryPopIncomingTradeConfirmDialog(TradeSessionState state)
    {
        try
        {
            if (state == null || state.Status != TradeStatus.Open)
            {
                return;
            }

            string selfId = NetworkIdentityTracker.GetSelfPlayerId();
            if (string.IsNullOrWhiteSpace(selfId))
            {
                INetworkManager netMgr = ServiceProvider?.GetService<INetworkManager>();
                selfId = netMgr?.GetSelf()?.userName;
            }

            if (string.IsNullOrWhiteSpace(selfId))
            {
                return;
            }

            if (!string.Equals(state.PlayerBId, selfId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Plugin.RunOnMainThread(() =>
            {
                try
                {
                    var confirmDialog = UnityEngine.Object.FindAnyObjectByType<NetworkPlugin.UI.Dialogs.TradeRequestConfirmDialog>();
                    if (confirmDialog != null && confirmDialog.IsVisible)
                    {
                        return;
                    }

                    var tradePanel = UnityEngine.Object.FindAnyObjectByType<NetworkPlugin.UI.Panels.TradePanel>();
                    if (tradePanel != null && tradePanel.IsVisible)
                    {
                        return;
                    }

                    var confirm = NetworkPlugin.UI.Factories.TradeRequestConfirmDialogRuntimeFactory.GetOrCreate();
                    if (confirm != null)
                    {
                        Plugin.Logger?.LogInfo($"[TradeSyncPatch] Popping TradeRequestConfirmDialog for incoming trade from {state.PlayerAName} ({state.PlayerAId}): tradeId={state.TradeId}");
                        confirm.Show(state);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogError($"[TradeSyncPatch] TryPopIncomingTradeConfirmDialog failed: {ex.Message}");
                }
            });
        }
        catch
        {

        }
    }

    public static TradeSessionState GetLastKnown(string tradeId)
    {
        if (string.IsNullOrWhiteSpace(tradeId))
        {
            return null;
        }

        lock (SyncLock)
        {
            _lastKnown.TryGetValue(tradeId, out var v);
            return v;
        }
    }

    internal static TradeSessionState GetHostSession(string tradeId)
    {
        if (string.IsNullOrWhiteSpace(tradeId))
        {
            return null;
        }

        lock (SyncLock)
        {
            _hostSessions.TryGetValue(tradeId, out var v);
            return v;
        }
    }

    private static TradeSessionState ParseState(JsonElement root)
    {
        try
        {
            TradeSessionState state = new TradeSessionState
            {
                TradeId = GetString(root, "TradeId"),
                PlayerAId = GetString(root, "PlayerAId"),
                PlayerBId = GetString(root, "PlayerBId"),
                PlayerAName = GetString(root, "PlayerAName"),
                PlayerBName = GetString(root, "PlayerBName"),
                MaxTradeSlots = GetInt(root, "MaxTradeSlots", 5),
                AConfirmed = GetBool(root, "AConfirmed"),
                BConfirmed = GetBool(root, "BConfirmed"),
                APrepared = GetBool(root, "APrepared"),
                BPrepared = GetBool(root, "BPrepared"),
                CommitId = GetString(root, "CommitId"),
                ACommitted = GetBool(root, "ACommitted"),
                BCommitted = GetBool(root, "BCommitted"),
                FailureCode = GetString(root, "FailureCode"),
                FailureStage = GetString(root, "FailureStage"),
                FailurePlayerId = GetString(root, "FailurePlayerId"),
                ConflictExhibitId = GetString(root, "ConflictExhibitId"),
                ReceiverPlayerId = GetString(root, "ReceiverPlayerId"),
                ProtocolVersion = GetInt(root, "ProtocolVersion", TradeConstants.CurrentProtocolVersion),
                ProtocolVersionA = GetInt(root, "ProtocolVersionA", TradeConstants.ProtocolVersionUnknown),
                ProtocolVersionB = GetInt(root, "ProtocolVersionB", TradeConstants.ProtocolVersionUnknown),
                Reason = GetString(root, "Reason"),
                Timestamp = GetLong(root, "Timestamp", DateTime.UtcNow.Ticks),
            };

            string statusStr = GetString(root, "Status");
            if (!Enum.TryParse(statusStr, out TradeStatus status))
            {
                status = TradeStatus.Open;
            }
            state.Status = status;

            state.OfferA = ParseOfferArray(root, "OfferA");
            state.OfferB = ParseOfferArray(root, "OfferB");

            state.MoneyA = GetInt(root, "MoneyA", 0);
            state.MoneyB = GetInt(root, "MoneyB", 0);

            state.ExhibitsA = ParseExhibitArray(root, "ExhibitsA");
            state.ExhibitsB = ParseExhibitArray(root, "ExhibitsB");

            return state;
        }
        catch
        {
            return null;
        }
    }

    private static List<CardRef> ParseOffer(JsonElement root)
    {

        try
        {
            return ParseOfferArray(root, "Offer");
        }
        catch
        {
            return new List<CardRef>();
        }
    }

    private static List<ExhibitRef> ParseExhibits(JsonElement root)
    {

        try
        {
            if (!root.TryGetProperty("Exhibits", out JsonElement arr))
            {
                return new List<ExhibitRef>();
            }

            if (arr.ValueKind == JsonValueKind.Array)
            {
                List<ExhibitRef> list = new List<ExhibitRef>();
                foreach (var e in arr.EnumerateArray())
                {
                    string id = e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }
                    list.Add(new ExhibitRef { ExhibitId = id });
                }

                return list
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ExhibitId))
                    .GroupBy(x => x.ExhibitId, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[TradeSyncPatch] ParseExhibits error: {ex.Message}");
        }

        return new List<ExhibitRef>();
    }

    private static List<ExhibitRef> ParseExhibitArray(JsonElement root, string property)
    {
        List<ExhibitRef> list = new List<ExhibitRef>();
        try
        {
            if (!root.TryGetProperty(property, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var e in arr.EnumerateArray())
            {
                string id = GetString(e, "ExhibitId");
                if (string.IsNullOrWhiteSpace(id) && e.ValueKind == JsonValueKind.String)
                {
                    id = e.GetString();
                }
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                list.Add(new ExhibitRef { ExhibitId = id });
            }
        }
        catch
        {

        }

        return list
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ExhibitId))
            .GroupBy(x => x.ExhibitId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static List<CardRef> ParseOfferArray(JsonElement root, string property)
    {
        List<CardRef> list = new();
        try
        {
            if (!root.TryGetProperty(property, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (JsonElement c in arr.EnumerateArray())
            {
                string cardId = GetString(c, "CardId");
                if (string.IsNullOrWhiteSpace(cardId))
                {
                    continue;
                }

                list.Add(new CardRef
                {
                    CardId = cardId,
                    InstanceId = GetInt(c, "InstanceId", -1),
                    IsUpgraded = GetBool(c, "IsUpgraded"),
                    UpgradeCounter = GetInt(c, "UpgradeCounter", 0),
                    DeckCounter = TryGetNullableInt(c, "DeckCounter"),
                    CardName = GetString(c, "CardName"),
                    CardType = GetString(c, "CardType"),
                });
            }
        }
        catch
        {

        }

        return list
            .GroupBy(x => x.InstanceId >= 0 ? $"i:{x.InstanceId}" : $"c:{x.CardId}")
            .Select(g => g.First())
            .ToList();
    }

    private static List<CardRef> SanitizeOffer(List<CardRef> offer, int maxSlots)
    {
        if (offer == null)
        {
            return new List<CardRef>();
        }

        List<CardRef> clean = offer
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.CardId))
            .ToList();

        if (maxSlots > 0 && clean.Count > maxSlots)
        {
            clean = clean.Take(maxSlots).ToList();
        }

        return clean;
    }

    private static int SanitizeMoney(int money)
    {

        if (money < 0)
        {
            return 0;
        }

        return Math.Min(money, 99999);
    }

    private static List<ExhibitRef> SanitizeExhibits(List<ExhibitRef> exhibits)
    {
        if (exhibits == null)
        {
            return new List<ExhibitRef>();
        }

        return exhibits
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ExhibitId))
            .GroupBy(x => x.ExhibitId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static bool TryGetJsonElement(object payload, out JsonElement root)
        => NetworkEventHelper.TryGetJsonElement(payload, out root);

    private static bool IsDuplicateRequest_NoLock(string tradeId, string senderId, string requestId, long timestamp)
    {

        lock (SyncLock)
        {
            if (!_recentRequestIdsByTrade.TryGetValue(tradeId, out LinkedList<string> order) || order == null)
            {
                order = new LinkedList<string>();
                _recentRequestIdsByTrade[tradeId] = order;
            }

            if (!_recentRequestIdSetsByTrade.TryGetValue(tradeId, out HashSet<string> set) || set == null)
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _recentRequestIdSetsByTrade[tradeId] = set;
            }

            if (set.Contains(requestId))
            {
                return true;
            }

            string senderKey = !string.IsNullOrWhiteSpace(senderId) ? $"{tradeId}:{senderId}" : null;
            if (senderKey != null && timestamp > 0 && _lastRequestTimestampBySender.TryGetValue(senderKey, out long lastTs) && timestamp < lastTs)
            {
                Plugin.Logger?.LogWarning($"[TradeSyncPatch] 交易 {tradeId} 发送者 {senderId} 收到乱序/过期请求 (ts={timestamp} < lastTs={lastTs})，予以丢弃");
                return true;
            }

            set.Add(requestId);
            order.AddLast(requestId);
            while (order.Count > RequestIdWindowSize)
            {
                string oldest = order.First?.Value;
                order.RemoveFirst();
                if (!string.IsNullOrWhiteSpace(oldest))
                {
                    set.Remove(oldest);
                }
            }

            if (senderKey != null && timestamp > 0)
            {
                _lastRequestTimestampBySender[senderKey] = Math.Max(_lastRequestTimestampBySender.TryGetValue(senderKey, out long prev) ? prev : 0, timestamp);
            }

            return false;
        }
    }

    private static string GetString(JsonElement root, string name)
        => NetworkEventHelper.GetString(root, name);

    private static int GetInt(JsonElement elem, string property, int defaultValue)
    {
        if (NetworkEventHelper.TryGetInt(elem, property, out int v))
            return v;
        return defaultValue;
    }

    private static long GetLong(JsonElement elem, string property, long defaultValue)
    {
        if (NetworkEventHelper.TryGetLong(elem, property, out long v))
            return v;
        return defaultValue;
    }

    private static bool GetBool(JsonElement root, string name)
        => NetworkEventHelper.GetBool(root, name);

    private static int? TryGetNullableInt(JsonElement elem, string property)
    {
        if (NetworkEventHelper.TryGetInt(elem, property, out int v))
            return v;
        return null;
    }

    public static IDisposable EnterApplyingTradeScope()
    {
        return new ApplyingTradeScope();
    }

    private sealed class ApplyingTradeScope : IDisposable
    {
        private readonly bool _prev;

        public ApplyingTradeScope()
        {
            _prev = IsApplyingTrade;
            IsApplyingTrade = true;
        }

        public void Dispose()
        {
            IsApplyingTrade = _prev;
        }
    }
}
