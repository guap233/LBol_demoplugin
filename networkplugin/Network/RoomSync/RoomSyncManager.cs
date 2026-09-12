using System;
using System.Collections.Generic;
using System.Text.Json;
using BepInEx.Logging;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.Snapshot;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Network.RoomSync;

public class RoomSyncManager
{
    private readonly INetworkClient _client;
    private readonly ManualLogSource _logger;

    private readonly object _lock = new();

    private INetworkClient _subscribedClient;
    private bool _subscribed;

    private readonly Dictionary<string, RoomStateSnapshot> _hostRoomStates = new(StringComparer.Ordinal);

    private readonly Dictionary<string, RoomStateSnapshot> _clientRoomStates = new(StringComparer.Ordinal);

    public const int MaxRoomSnapshotPayloadSize = 64 * 1024;
    public const int MaxSnapshotArrayLength = 100;

    private (string roomKey, int act, int x, int y, string stationType, long atUtcTicks)? _lastEntered;

    public RoomSyncManager(INetworkClient networkClient, ManualLogSource logger)
    {
        _client = networkClient ?? throw new ArgumentNullException(nameof(networkClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void EnsureSubscribed(INetworkClient client)
    {
        if (client == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_subscribed && ReferenceEquals(_subscribedClient, client))
            {
                return;
            }

            if (_subscribedClient != null)
                _subscribedClient.OnGameEventReceived -= OnGameEventReceived;

            _subscribedClient = client;
            _subscribedClient.OnGameEventReceived += OnGameEventReceived;
            _subscribed = true;
        }
    }

    public void Unsubscribe()
    {
        lock (_lock)
        {
            if (_subscribedClient != null)
                _subscribedClient.OnGameEventReceived -= OnGameEventReceived;

            _subscribedClient = null;
            _subscribed = false;
        }
    }

        public static string BuildRoomKey(int act, int x, int y, string stationType)
    {
        return $"{act}:{x}:{y}:{stationType}";
    }

    public void SetLastEnteredNode(int act, int x, int y, string stationType)
    {
        try
        {
            string key = BuildRoomKey(act, x, y, stationType ?? string.Empty);
            _lastEntered = (key, act, x, y, stationType ?? string.Empty, DateTime.UtcNow.Ticks);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RoomSyncManager] SetLastEnteredNode error: {ex.Message}");
        }
    }

    public string GetLastEnteredRoomKey()
    {
        return _lastEntered.HasValue ? _lastEntered.Value.roomKey : null;
    }

    public RoomStateSnapshot TryGetClientRoomState(string roomKey)
    {
        if (string.IsNullOrWhiteSpace(roomKey))
        {
            return null;
        }

        lock (_lock)
        {
            return _clientRoomStates.TryGetValue(roomKey, out var s) ? s : null;
        }
    }

    public RoomStateSnapshot TryGetHostRoomState(string roomKey)
    {
        if (string.IsNullOrWhiteSpace(roomKey))
        {
            return null;
        }

        lock (_lock)
        {
            return _hostRoomStates.TryGetValue(roomKey, out var s) ? s : null;
        }
    }

        public void RequestRoomState(string roomKey, long knownVersion)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return;
            }

            var client = _client;
            if (client == null || !client.IsConnected)
            {
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(client);
            EnsureSubscribed(client);

            string requesterId = NetworkIdentityTracker.GetSelfPlayerId();
            if (string.IsNullOrWhiteSpace(requesterId))
            {
                requesterId = GameStateUtils.GetCurrentPlayerId();
            }

            var payload = new
            {
                Timestamp = DateTime.UtcNow.Ticks,
                RequesterId = requesterId,
                TargetPlayerId = requesterId,
                RoomKey = roomKey,
                KnownVersion = knownVersion,
            };

            client.SendGameEventData(NetworkMessageTypes.RoomStateRequest, payload);

            if (NetworkIdentityTracker.GetSelfIsHost())
            {
                OnGameEventReceived(NetworkMessageTypes.RoomStateRequest, payload);
            }
        }
        catch
        {

        }
    }

        public void UploadRoomState(RoomStateSnapshot snapshot)
    {
        try
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RoomKey))
            {
                return;
            }

            var client = _client;
            if (client == null || !client.IsConnected)
            {
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(client);
            EnsureSubscribed(client);

            string uploaderId = NetworkIdentityTracker.GetSelfPlayerId();
            if (string.IsNullOrWhiteSpace(uploaderId))
            {
                uploaderId = GameStateUtils.GetCurrentPlayerId();
            }

            snapshot.OwnerPlayerId = string.IsNullOrWhiteSpace(snapshot.OwnerPlayerId) ? uploaderId : snapshot.OwnerPlayerId;
            snapshot.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
            snapshot.GapOptionsEvents = GapOptionsSyncPatch.GetRecentGapOptionsEvents(snapshot.RoomKey);

            var payload = new
            {
                Timestamp = snapshot.UpdatedAtUtcTicks,
                UploaderId = uploaderId,
                TargetPlayerId = uploaderId,
                RoomKey = snapshot.RoomKey,
                RoomVersion = snapshot.RoomVersion,
                Phase = snapshot.Phase.ToString(),
                Act = snapshot.Act,
                X = snapshot.X,
                Y = snapshot.Y,
                StationType = snapshot.StationType,
                BattleId = snapshot.BattleId,
                Enemies = snapshot.Enemies,
                Rewards = snapshot.Rewards,
                GapOptionsEvents = snapshot.GapOptionsEvents,
            };

            client.SendGameEventData(NetworkMessageTypes.RoomStateUpload, payload);

            if (NetworkIdentityTracker.GetSelfIsHost())
            {
                OnGameEventReceived(NetworkMessageTypes.RoomStateUpload, payload);
            }
        }
        catch
        {

        }
    }

    private void OnGameEventReceived(string eventType, object payload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        if (!string.Equals(eventType, NetworkMessageTypes.RoomStateRequest, StringComparison.Ordinal) &&
            !string.Equals(eventType, NetworkMessageTypes.RoomStateResponse, StringComparison.Ordinal) &&
            !string.Equals(eventType, NetworkMessageTypes.RoomStateUpload, StringComparison.Ordinal) &&
            !string.Equals(eventType, NetworkMessageTypes.RoomStateBroadcast, StringComparison.Ordinal))
        {
            return;
        }

        if (!TryGetJsonElement(payload, out JsonElement root) || root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (eventType)
        {
            case NetworkMessageTypes.RoomStateRequest:
                HandleRoomStateRequest(root);
                return;
            case NetworkMessageTypes.RoomStateUpload:
                HandleRoomStateUpload(root);
                return;
            case NetworkMessageTypes.RoomStateResponse:
                HandleRoomStateResponse(root);
                return;
            case NetworkMessageTypes.RoomStateBroadcast:
                HandleRoomStateResponse(root);
                return;
        }
    }

    private void HandleRoomStateRequest(JsonElement root)
    {

        if (!NetworkIdentityTracker.GetSelfIsHost())
        {
            return;
        }

        string requesterId = TryGetString(root, "RequesterId") ?? TryGetString(root, "TargetPlayerId");
        string roomKey = TryGetString(root, "RoomKey");
        long knownVersion = TryGetLong(root, "KnownVersion") ?? 0;

        if (string.IsNullOrWhiteSpace(requesterId) || string.IsNullOrWhiteSpace(roomKey))
        {
            return;
        }

        RoomStateSnapshot snapshot;
        lock (_lock)
        {
            if (!_hostRoomStates.TryGetValue(roomKey, out snapshot!))
            {
                snapshot = new RoomStateSnapshot
                {
                    RoomKey = roomKey,
                    RoomVersion = 0,
                    Phase = RoomPhase.NotVisited,
                    UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,
                };
            }
        }

        if (snapshot.RoomVersion < knownVersion)
        {

        }

        snapshot.GapOptionsEvents = GapOptionsSyncPatch.GetRecentGapOptionsEvents(roomKey);

        SendRoomStateResponseTo(requesterId, snapshot);
    }

    private void HandleRoomStateUpload(JsonElement root)
    {
        if (!NetworkIdentityTracker.GetSelfIsHost())
        {
            return;
        }

        if (root.ValueKind == JsonValueKind.Object && root.GetRawText().Length > MaxRoomSnapshotPayloadSize)
        {
            _logger?.LogWarning($"[RoomSyncManager] 拦截过大的房间上传快照负载: {root.GetRawText().Length} > {MaxRoomSnapshotPayloadSize}");
            return;
        }

        string uploaderId = TryGetString(root, "UploaderId") ?? TryGetString(root, "OwnerPlayerId") ?? TryGetString(root, "TargetPlayerId");
        string roomKey = TryGetString(root, "RoomKey");
        string phaseStr = TryGetString(root, "Phase");

        if (string.IsNullOrWhiteSpace(uploaderId) || string.IsNullOrWhiteSpace(roomKey))
        {
            return;
        }

        RoomPhase phase = RoomPhase.NotVisited;
        if (!string.IsNullOrWhiteSpace(phaseStr) && Enum.TryParse(phaseStr, ignoreCase: true, out RoomPhase parsed))
        {
            phase = parsed;
        }

        RoomStateSnapshot incoming = new()
        {
            RoomKey = roomKey,
            Phase = phase,
            OwnerPlayerId = uploaderId,
            UpdatedAtUtcTicks = TryGetLong(root, "Timestamp") ?? DateTime.UtcNow.Ticks,
            RoomVersion = TryGetLong(root, "RoomVersion") ?? 0,
            Act = (int)(TryGetLong(root, "Act") ?? 0),
            X = (int)(TryGetLong(root, "X") ?? 0),
            Y = (int)(TryGetLong(root, "Y") ?? 0),
            StationType = TryGetString(root, "StationType") ?? string.Empty,
            BattleId = TryGetString(root, "BattleId") ?? string.Empty,
            Rewards = TryDeserialize<BattleRewardSnapshot>(root, "Rewards") ?? new BattleRewardSnapshot(),
            Enemies = TryDeserializeEnemies(root) ?? new List<EnemyStateSnapshot>(),
            GapOptionsEvents = TryDeserializeGapEvents(root) ?? GapOptionsSyncPatch.GetRecentGapOptionsEvents(roomKey),
        };

        RoomStateSnapshot stored;
        lock (_lock)
        {
            if (_hostRoomStates.TryGetValue(roomKey, out stored!))
            {
                if (string.IsNullOrWhiteSpace(stored.OwnerPlayerId))
                {
                    stored.OwnerPlayerId = uploaderId;
                }

                if (!string.Equals(stored.OwnerPlayerId, uploaderId, StringComparison.Ordinal))
                {
                    _logger?.LogWarning($"[RoomSyncManager] 拒绝非所有者上传房间快照: room={roomKey}, owner={stored.OwnerPlayerId}, uploader={uploaderId}");
                    return;
                }

                if (incoming.RoomVersion > 0 && incoming.RoomVersion < stored.RoomVersion)
                {
                    _logger?.LogWarning($"[RoomSyncManager] 忽略陈旧的房间上传版本: room={roomKey}, incomingVersion={incoming.RoomVersion}, storedVersion={stored.RoomVersion}");
                    return;
                }

                stored.RoomVersion = Math.Max(stored.RoomVersion + 1, incoming.RoomVersion);
                stored.Phase = incoming.Phase;
                stored.UpdatedAtUtcTicks = incoming.UpdatedAtUtcTicks;
                stored.Act = incoming.Act;
                stored.X = incoming.X;
                stored.Y = incoming.Y;
                stored.StationType = incoming.StationType;
                stored.BattleId = incoming.BattleId;
                stored.Enemies = incoming.Enemies;
                stored.Rewards = incoming.Rewards;
                stored.GapOptionsEvents = incoming.GapOptionsEvents;
            }
            else
            {
                incoming.RoomVersion = Math.Max(1, incoming.RoomVersion);
                _hostRoomStates[roomKey] = incoming;
                stored = incoming;
            }
        }
    }

    private void HandleRoomStateResponse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.GetRawText().Length > MaxRoomSnapshotPayloadSize)
        {
            _logger?.LogWarning($"[RoomSyncManager] 拦截过大的房间响应快照负载: {root.GetRawText().Length} > {MaxRoomSnapshotPayloadSize}");
            return;
        }

        string roomKey = TryGetString(root, "RoomKey");
        if (string.IsNullOrWhiteSpace(roomKey))
        {
            return;
        }

        RoomPhase phase = RoomPhase.NotVisited;
        string phaseStr = TryGetString(root, "Phase");
        if (!string.IsNullOrWhiteSpace(phaseStr) && Enum.TryParse(phaseStr, ignoreCase: true, out RoomPhase parsed))
        {
            phase = parsed;
        }

        RoomStateSnapshot snapshot = new()
        {
            RoomKey = roomKey,
            Phase = phase,
            RoomVersion = TryGetLong(root, "RoomVersion") ?? 0,
            OwnerPlayerId = TryGetString(root, "OwnerPlayerId") ?? string.Empty,
            UpdatedAtUtcTicks = TryGetLong(root, "Timestamp") ?? DateTime.UtcNow.Ticks,
            Act = (int)(TryGetLong(root, "Act") ?? 0),
            X = (int)(TryGetLong(root, "X") ?? 0),
            Y = (int)(TryGetLong(root, "Y") ?? 0),
            StationType = TryGetString(root, "StationType") ?? string.Empty,
            BattleId = TryGetString(root, "BattleId") ?? string.Empty,
            Rewards = TryDeserialize<BattleRewardSnapshot>(root, "Rewards") ?? new BattleRewardSnapshot(),
            Enemies = TryDeserializeEnemies(root) ?? new List<EnemyStateSnapshot>(),
            GapOptionsEvents = TryDeserializeGapEvents(root) ?? new List<GapOptionsEventSnapshot>(),
        };

        lock (_lock)
        {
            if (_clientRoomStates.TryGetValue(roomKey, out var current) && current != null)
            {
                if (snapshot.RoomVersion <= current.RoomVersion)
                {
                    _logger?.LogDebug($"[RoomSyncManager] 客户端忽略陈旧或重复的快照: room={roomKey}, incomingVersion={snapshot.RoomVersion}, currentVersion={current.RoomVersion}");
                    return;
                }
            }

            _clientRoomStates[roomKey] = snapshot;
        }

        GapOptionsSyncPatch.MergeCatchupGapOptionsEvents(snapshot.RoomKey, snapshot.GapOptionsEvents);
    }

    private void SendRoomStateResponseTo(string targetPlayerId, RoomStateSnapshot snapshot)
    {
        try
        {
            var client = _client;
            if (client == null || !client.IsConnected)
            {
                return;
            }

            EnsureSubscribed(client);

            string hostId = NetworkIdentityTracker.GetSelfPlayerId();
            if (string.IsNullOrWhiteSpace(hostId))
            {
                hostId = GameStateUtils.GetCurrentPlayerId();
            }

            var payload = new
            {
                Timestamp = DateTime.UtcNow.Ticks,
                HostId = hostId,
                TargetPlayerId = targetPlayerId,
                RequesterId = targetPlayerId,
                RoomKey = snapshot.RoomKey,
                RoomVersion = snapshot.RoomVersion,
                Phase = snapshot.Phase.ToString(),
                OwnerPlayerId = snapshot.OwnerPlayerId,
                Act = snapshot.Act,
                X = snapshot.X,
                Y = snapshot.Y,
                StationType = snapshot.StationType,
                BattleId = snapshot.BattleId,
                Enemies = snapshot.Enemies,
                Rewards = snapshot.Rewards,
                GapOptionsEvents = snapshot.GapOptionsEvents,
            };

            client.SendGameEventData(NetworkMessageTypes.RoomStateResponse, payload);
        }
        catch
        {

        }
    }

    private static List<EnemyStateSnapshot> TryDeserializeEnemies(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("Enemies", out var enemiesEl) || enemiesEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            if (enemiesEl.GetArrayLength() > MaxSnapshotArrayLength)
            {
                Plugin.Logger?.LogWarning($"[RoomSyncManager] 拦截超限敌人数组长度: {enemiesEl.GetArrayLength()} > {MaxSnapshotArrayLength}");
                return null;
            }

            return JsonSerializer.Deserialize<List<EnemyStateSnapshot>>(enemiesEl.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private static List<GapOptionsEventSnapshot> TryDeserializeGapEvents(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("GapOptionsEvents", out var gapEl) || gapEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            if (gapEl.GetArrayLength() > MaxSnapshotArrayLength)
            {
                Plugin.Logger?.LogWarning($"[RoomSyncManager] 拦截超限隙间事件数组长度: {gapEl.GetArrayLength()} > {MaxSnapshotArrayLength}");
                return null;
            }

            return JsonSerializer.Deserialize<List<GapOptionsEventSnapshot>>(gapEl.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private static T TryDeserialize<T>(JsonElement root, string property) where T : class
    {
        try
        {
            if (!root.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(el.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetJsonElement(object payload, out JsonElement root)
        => NetworkEventHelper.TryGetJsonElement(payload, out root);

    private static string TryGetString(JsonElement root, string property)
    {
        try
        {
            return root.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetLong(JsonElement root, string property)
    {
        try
        {
            if (!root.TryGetProperty(property, out var p))
            {
                return null;
            }

            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long n))
            {
                return n;
            }

            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out n))
            {
                return n;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

}
