using System;
using System.Collections.Generic;
using LiteNetLib;
using NetworkPlugin.Network.Security;
using NetworkPlugin.Network.Server.Core;

namespace NetworkPlugin.Network.Server;

public abstract class BaseGameServer
{
        protected readonly IServerCore Core;
        protected readonly object SyncRoot;

        protected readonly Dictionary<NetPeer, PlayerSession> SessionsByPeer = new();
        protected readonly Dictionary<string, PlayerSession> SessionsByPlayerId = new();
        protected readonly Dictionary<string, DateTime> DisconnectedAtByPlayerId = new();

        protected BaseGameServer(IServerCore core, object? syncRoot = null)
    {
        Core = core ?? throw new ArgumentNullException(nameof(core));
        SyncRoot = syncRoot ?? new object();

        RegisterCoreEvents();
    }

        public virtual void Start() => Core.Start();
        public virtual void Stop() => Core.Stop();
        public virtual void PollEvents() => Core.PollEvents();

        protected static string GenerateReconnectToken() => SecurityUtils.GenerateReconnectToken();

        protected abstract TimeSpan ReconnectGracePeriod { get; }

        protected abstract string CreatePlayerId(NetPeer peer);
        protected abstract PlayerSession CreateSession(NetPeer peer, string playerId);

        protected abstract bool IsGameEventType(string messageType);
        protected abstract void HandleGameEvent(PlayerSession session, string eventType, string jsonPayload, DeliveryMethod deliveryMethod);
        protected abstract void HandleSystemMessage(PlayerSession session, string messageType, string jsonPayload, DeliveryMethod deliveryMethod);

        protected virtual void OnSessionConnected(PlayerSession session) { }
        protected virtual void OnSessionDisconnected(PlayerSession session, DisconnectInfo disconnectInfo) { }

        protected bool TryGetSession(NetPeer peer, out PlayerSession session)
    {
        lock (SyncRoot)
        {
            return SessionsByPeer.TryGetValue(peer, out session!);
        }
    }

        protected bool TryGetSession(string playerId, out PlayerSession session)
    {
        lock (SyncRoot)
        {
            return SessionsByPlayerId.TryGetValue(playerId, out session!);
        }
    }

        protected bool TryGetDisconnectedAt(string playerId, out DateTime disconnectedAt)
    {
        lock (SyncRoot)
        {
            return DisconnectedAtByPlayerId.TryGetValue(playerId, out disconnectedAt);
        }
    }

        protected void MarkDisconnected(string playerId)
    {
        lock (SyncRoot)
        {
            DisconnectedAtByPlayerId[playerId] = DateTime.UtcNow;
        }
    }

        protected void ClearDisconnectedMark(string playerId)
    {
        lock (SyncRoot)
        {
            DisconnectedAtByPlayerId.Remove(playerId);
        }
    }

        private void RegisterCoreEvents()
    {
        Core.PeerConnected += peer =>
        {
            PlayerSession session;
            lock (SyncRoot)
            {
                string playerId = CreatePlayerId(peer);
                session = CreateSession(peer, playerId);

                session.SessionNonce = SecurityUtils.GenerateNonce();
                session.Metadata["SessionNonce"] = session.SessionNonce;
                session.Metadata["ReconnectToken"] = GenerateReconnectToken();
                session.IsConnected = true;
                session.UpdateHeartbeat();
                session.UpdateMessageTime();

                SessionsByPeer[peer] = session;
                SessionsByPlayerId[playerId] = session;
                DisconnectedAtByPlayerId.Remove(playerId);
            }

            OnSessionConnected(session);
        };

        Core.PeerDisconnected += (peer, disconnectInfo) =>
        {
            PlayerSession session;
            lock (SyncRoot)
            {
                if (!SessionsByPeer.TryGetValue(peer, out session!))
                {
                    return;
                }

                if (session.Peer != null && session.Peer != peer)
                {
                    SessionsByPeer.Remove(peer);
                    return;
                }

                SessionsByPeer.Remove(peer);
                session.IsConnected = false;
                DisconnectedAtByPlayerId[session.PlayerId] = DateTime.UtcNow;
            }

            OnSessionDisconnected(session, disconnectInfo);
        };

        Core.PeerLatencyUpdated += (peer, latency) =>
        {
            lock (SyncRoot)
            {
                if (SessionsByPeer.TryGetValue(peer, out var session))
                {
                    session.Ping = latency;
                }
            }
        };

        Core.MessageReceived += inbound =>
        {
            PlayerSession session;
            lock (SyncRoot)
            {
                if (!SessionsByPeer.TryGetValue(inbound.FromPeer, out session!))
                {
                    return;
                }
                session.UpdateMessageTime();
            }

            var sender = session.ToAuthenticatedSender();
            if (!NetworkMessagePolicy.IsMessageAllowed(inbound.Type, in sender, out string reason))
            {
                Plugin.Logger?.LogWarning($"[安全拦截] 拒绝来自 {sender.PlayerId} (Host={sender.IsHost}) 的非法消息: type={inbound.Type}, 原因: {reason}");
                return;
            }

            if (!session.RateLimiter.TryAcquire(inbound.JsonPayload?.Length ?? 0, out string rateReason))
            {
                Plugin.Logger?.LogWarning($"[安全拦截] 限流拒绝来自 {sender.PlayerId} 的消息: {rateReason}");
                return;
            }

            NetworkMessagePolicy.GetTimestampContract(inbound.Type, out var expectedFormat, out bool requireTimestamp);

            if (requireTimestamp || (!string.IsNullOrWhiteSpace(inbound.JsonPayload) && inbound.JsonPayload.StartsWith("{")))
            {
                if (string.IsNullOrWhiteSpace(inbound.JsonPayload) || !inbound.JsonPayload.StartsWith("{"))
                {
                    Plugin.Logger?.LogWarning($"[安全拦截] 缺少必需的 JSON 负载: from={sender.PlayerId}, type={inbound.Type}");
                    return;
                }

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(inbound.JsonPayload);
                    var root = doc.RootElement;
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        Plugin.Logger?.LogWarning($"[安全拦截] 消息负载根元素必须为 JSON Object: from={sender.PlayerId}, type={inbound.Type}");
                        return;
                    }

                    if (root.TryGetProperty("SessionNonce", out var nonceEl) && nonceEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        string claimedNonce = nonceEl.GetString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(claimedNonce) && !string.IsNullOrEmpty(session.SessionNonce) && !string.Equals(claimedNonce, session.SessionNonce, StringComparison.Ordinal))
                        {
                            Plugin.Logger?.LogWarning($"[安全拦截] SessionNonce 不匹配: from={sender.PlayerId}, claimed={claimedNonce}, expected={session.SessionNonce}");
                            return;
                        }
                    }

                    string msgId = root.TryGetProperty("MessageId", out var mEl) && mEl.ValueKind == System.Text.Json.JsonValueKind.String ? mEl.GetString() ?? string.Empty : string.Empty;
                    string reqId = root.TryGetProperty("RequestId", out var rEl) && rEl.ValueKind == System.Text.Json.JsonValueKind.String ? rEl.GetString() ?? string.Empty : string.Empty;
                    long seq = root.TryGetProperty("Sequence", out var sEl) && sEl.TryGetInt64(out long sVal) ? sVal : 0;

                    long ts = 0;
                    if (root.TryGetProperty("Timestamp", out var tEl))
                    {
                        if (tEl.ValueKind != System.Text.Json.JsonValueKind.Number || !tEl.TryGetInt64(out ts))
                        {
                            Plugin.Logger?.LogWarning($"[安全拦截] Timestamp 字段类型错误 (必须为整数数值): from={sender.PlayerId}, type={inbound.Type}");
                            return;
                        }
                    }
                    else if (requireTimestamp)
                    {
                        Plugin.Logger?.LogWarning($"[安全拦截] 消息缺少强制要求的 Timestamp 字段: from={sender.PlayerId}, type={inbound.Type}");
                        return;
                    }

                    if (!session.ReplayGuard.ValidateAndRecord(msgId, reqId, seq, ts, expectedFormat, out string replayReason, requireTimestamp: requireTimestamp))
                    {
                        Plugin.Logger?.LogWarning($"[安全拦截] 重放/序列检查拒绝: from={sender.PlayerId}, type={inbound.Type}, 原因={replayReason}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"[安全拦截] 消息负载解析异常，予以拒绝: from={sender.PlayerId}, type={inbound.Type}, error={ex.Message}");
                    return;
                }
            }

            if (IsGameEventType(inbound.Type))
            {
                HandleGameEvent(session, inbound.Type, inbound.JsonPayload, inbound.DeliveryMethod);
                return;
            }

            HandleSystemMessage(session, inbound.Type, inbound.JsonPayload, inbound.DeliveryMethod);
        };
    }

    internal void ProcessInboundMessageForTest(ServerInboundMessage inbound)
    {
        PlayerSession session;
        lock (SyncRoot)
        {
            if (!SessionsByPeer.TryGetValue(inbound.FromPeer, out session!))
            {
                return;
            }
            session.UpdateMessageTime();
        }

        if (IsGameEventType(inbound.Type))
        {
            HandleGameEvent(session, inbound.Type, inbound.JsonPayload, inbound.DeliveryMethod);
            return;
        }

        HandleSystemMessage(session, inbound.Type, inbound.JsonPayload, inbound.DeliveryMethod);
    }

    internal void ProcessDisconnectForTest(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        PlayerSession session;
        lock (SyncRoot)
        {
            if (!SessionsByPeer.TryGetValue(peer, out session!))
            {
                return;
            }

            if (session.Peer != null && session.Peer != peer)
            {
                SessionsByPeer.Remove(peer);
                return;
            }

            SessionsByPeer.Remove(peer);
            session.IsConnected = false;
            DisconnectedAtByPlayerId[session.PlayerId] = DateTime.UtcNow;
        }

        OnSessionDisconnected(session, disconnectInfo);
    }
}
