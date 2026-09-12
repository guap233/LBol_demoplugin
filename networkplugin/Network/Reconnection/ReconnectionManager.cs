using LBoL.Core;
using LBoL.Core.Stations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Microsoft.Extensions.Logging;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Event;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Network.Snapshot;
using NetworkPlugin.Network.Security;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Network.Reconnection;

public sealed class ReconnectionManager : IDisposable
{
    #region 私有字段

        private readonly ReconnectionConfig _config;

        private readonly ILogger<ReconnectionManager>? _logger;

        private readonly ManualLogSource? _fallbackLogger;

        private readonly INetworkManager _networkManager;

        private readonly object _syncLock = new();

        private readonly INetworkClient _client;

        private int _initialized;

        private readonly Dictionary<string, PlayerStateSnapshot> _playerSnapshots = new(StringComparer.Ordinal);

        private readonly SortedList<long, GameEvent> _eventHistory = new();

        private readonly LinkedList<FullStateSnapshot> _fullSnapshots = new();

    private readonly Dictionary<string, DateTime> _lastHeartbeatUtc = new(StringComparer.Ordinal);
    private readonly HashSet<string> _timedOutPlayers = new(StringComparer.Ordinal);

        private readonly Timer _heartbeatTimer;

        private readonly Timer _snapshotTimer;

        private readonly CancellationTokenSource _cts = new();

        private long _mapCheckpointSequence;

        private string _lastMapCheckpointId = string.Empty;

        private long _lastMapCheckpointAtUtcTicks;

    #endregion

    #region 公共属性

        public bool IsConnected { get; private set; }

        public bool IsReconnecting { get; private set; }

    #endregion

    #region 构造与初始化

        public ReconnectionManager(
        ReconnectionConfig config,
        ILogger<ReconnectionManager>? logger,
        INetworkClient networkClient,
        INetworkManager networkManager,
        ManualLogSource? fallbackLogger = null)
    {
        _config = config ?? new ReconnectionConfig();
        _logger = logger;
        _client = networkClient ?? throw new ArgumentNullException(nameof(networkClient));
        _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        _fallbackLogger = fallbackLogger ?? Plugin.Logger;

        _heartbeatTimer = new Timer(CheckHeartbeats, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _snapshotTimer = new Timer(SavePeriodicSnapshot, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

        public void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        try
        {

            if (_client == null)
            {
                LogWarning("[ReconnectionManager] Initialize skipped: INetworkClient not available.");
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(_client);

            _client.OnConnectionStateChanged += OnConnectionStateChanged;
            _client.OnGameEventReceived += OnGameEventReceived;

            IsConnected = _client.IsConnected;

            _heartbeatTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            _snapshotTimer.Change(
                TimeSpan.FromSeconds(_config.SnapshotIntervalSeconds),
                TimeSpan.FromSeconds(_config.SnapshotIntervalSeconds));

            LogInformation("[ReconnectionManager] Initialized");
        }
        catch (Exception ex)
        {
            LogError($"[ReconnectionManager] Initialize failed: {ex.Message}");
        }
    }

    #endregion

    #region 周期性快照（主机侧）

        private void SavePeriodicSnapshot(object? state)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {

            INetworkClient? client = _client;
            if (client == null || !client.IsConnected)
            {
                return;
            }

            Plugin.RunOnMainThread(() =>
            {
                try
                {
                    FullStateSnapshot snapshot = CreateFullSnapshot();
                    SaveSnapshot(snapshot);
                }
                catch (Exception ex)
                {
                    LogError($"[ReconnectionManager] Error saving periodic snapshot on main thread: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            LogError($"[ReconnectionManager] Error scheduling periodic snapshot: {ex.Message}");
        }
    }

        public FullStateSnapshot CreateFullSnapshot()
    {
        FullStateSnapshot snapshot = new FullStateSnapshot
        {
            Timestamp = DateTime.UtcNow.Ticks,
            GameState = new GameStateSnapshot(),
            PlayerStates = new List<PlayerStateSnapshot>(),
            BattleState = new BattleStateSnapshot(),
            MapState = new MapStateSnapshot(),
            EventIndex = 0
        };

        try
        {
            lock (_syncLock)
            {
                if (_eventHistory.Count > 0)
                {
                    snapshot.EventIndex = _eventHistory.Keys[_eventHistory.Count - 1];
                }

                snapshot.MapState.LastCheckpointId = _lastMapCheckpointId;
                snapshot.MapState.LastCheckpointAtUtcTicks = _lastMapCheckpointAtUtcTicks;
            }

            INetworkManager? manager = _networkManager;
            if (manager != null)
            {
                foreach (INetworkPlayer p in manager.GetAllPlayers() ?? Enumerable.Empty<INetworkPlayer>())
                {
                    if (p == null)
                    {
                        continue;
                    }

                    snapshot.PlayerStates.Add(CreateSnapshotFromNetworkPlayer(p));
                }
            }

            GameRunController? run = GameStateUtils.GetCurrentGameRun();
            snapshot.GameState.GameStarted = run != null;

            if (run != null)
            {
                try
                {
                    snapshot.GameState.RootSeed = run.RootSeed;
                    snapshot.GameState.UISeed = run.UISeed;
                    snapshot.GameState.StageIndex = run.CurrentStage?.Index;
                    snapshot.MapState.MapSeedUlong = run.CurrentStage?.MapSeed;

                    snapshot.GameState.Difficulty = (int)run.Difficulty;
                    snapshot.GameState.Puzzles = (int)run.Puzzles;
                    snapshot.GameState.GameMode = (int)run.Mode;
                    snapshot.GameState.ShowRandomResult = run.ShowRandomResult;

                    try
                    {
                        snapshot.GameState.StageTypeNames = run.Stages
                            .Where(s => s != null)
                            .Select(s => s.GetType().Name)
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[ReconnectionManager] Reading StageTypeNames failed: {ex.Message}");
                    }

                    try
                    {
                        snapshot.GameState.DebutAdventureTypeName = run.Stages != null && run.Stages.Count > 0
                            ? run.Stages[0]?.DebutAdventureType?.Name
                            : null;
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[ReconnectionManager] Reading DebutAdventureTypeName failed: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"[ReconnectionManager] Reading GameState fields failed: {ex.Message}");
                }

                TryFillMapStateFromRun(run, snapshot.MapState);
            }

            PlayerStateSnapshot? firstPlayer = snapshot.PlayerStates.FirstOrDefault();
            if (firstPlayer?.GameLocation != null)
            {
                snapshot.MapState.CurrentLocation = new LocationSnapshot
                {
                    X = firstPlayer.GameLocation.X,
                    Y = firstPlayer.GameLocation.Y
                };
            }
        }
        catch (Exception ex)
        {
            LogWarning($"[ReconnectionManager] Full snapshot build degraded: {ex.Message}");
        }

        LogDebug($"[ReconnectionManager] Full snapshot created at {snapshot.Timestamp}");
        return snapshot;
    }

        public void MarkMapCheckpoint(string reason, string? nodeKey = null)
    {
        try
        {
            if (!NetworkIdentityTracker.GetSelfIsHost())
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            long seq = Interlocked.Increment(ref _mapCheckpointSequence);
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "checkpoint" : reason;
            string id = $"cp{seq:00000000}:{safeReason}";

            lock (_syncLock)
            {
                _lastMapCheckpointId = id;
                _lastMapCheckpointAtUtcTicks = now;
            }

            LogDebug($"[ReconnectionManager] Map checkpoint: id={id}, at={now}, reason={safeReason}, nodeKey={nodeKey ?? "<null>"}");
        }
        catch (Exception ex)
        {
            LogWarning($"[ReconnectionManager] MarkMapCheckpoint degraded: {ex.Message}");
        }
    }

    private static string BuildNodeKey(MapNode node)
    {
        if (node == null)
        {
            return string.Empty;
        }

        string stationType = node.StationType.ToString();
        return $"{node.Act}:{node.X}:{node.Y}:{stationType}";
    }

    private static LocationSnapshot BuildLocationSnapshot(MapNode node)
    {
        if (node == null)
        {
            return new LocationSnapshot();
        }

        return new LocationSnapshot
        {
            X = node.X,
            Y = node.Y,
            NodeId = BuildNodeKey(node),
            NodeType = node.StationType.ToString(),
            IsBranch = node.FollowerList != null && node.FollowerList.Count > 1,
            VisitTime = 0,
        };
    }

    private void TryFillMapStateFromRun(GameRunController run, MapStateSnapshot mapState)
    {
        try
        {
            if (run == null || mapState == null)
            {
                return;
            }

            GameMap map = run.CurrentMap;
            if (map == null)
            {
                return;
            }

            if (map.VisitingNode != null)
            {
                mapState.CurrentLocation = BuildLocationSnapshot(map.VisitingNode);
            }

            try
            {
                IReadOnlyList<MapNode> path = map.Path;
                mapState.PathHistory = path
                    .Where(n => n != null)
                    .Select(BuildLocationSnapshot)
                    .ToList();
            }
            catch
            {

            }

            try
            {
                Dictionary<string, string> nodeStates = new(StringComparer.Ordinal);
                foreach (MapNode node in map.AllNodes)
                {
                    if (node == null)
                    {
                        continue;
                    }

                    string key = BuildNodeKey(node);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    nodeStates[key] = node.Status.ToString();
                }

                mapState.NodeStates = nodeStates;
            }
            catch
            {

            }

            try
            {
                mapState.VisitedNodes = mapState.PathHistory
                    .Select(p => p?.NodeId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch
            {

            }

            try
            {
                HashSet<string> cleared = new(StringComparer.Ordinal);

                IReadOnlyList<MapNode> path = map.Path;
                int cut = Math.Max(0, path.Count - 1);
                for (int i = 0; i < cut; i++)
                {
                    string key = BuildNodeKey(path[i]);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        cleared.Add(key);
                    }
                }

                if (run.CurrentStation != null && run.CurrentStation.Status == StationStatus.Finished && map.VisitingNode != null)
                {
                    string cur = BuildNodeKey(map.VisitingNode);
                    if (!string.IsNullOrWhiteSpace(cur))
                    {
                        cleared.Add(cur);
                    }
                }

                mapState.ClearedNodes = cleared.ToList();
            }
            catch
            {

            }
        }
        catch
        {

        }
    }

        private void SaveSnapshot(FullStateSnapshot snapshot)
    {
        lock (_syncLock)
        {
            _fullSnapshots.AddLast(snapshot);
            while (_fullSnapshots.Count > _config.MaxSavedFullSnapshots && _fullSnapshots.First != null)
            {
                _fullSnapshots.RemoveFirst();
            }
        }
    }

    #endregion

    #region 心跳与断线检测

        private void CheckHeartbeats(object? state)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            List<string>? timedOut = null;

            lock (_syncLock)
            {

                foreach ((string playerId, DateTime last) in _lastHeartbeatUtc)
                {

                    if (_timedOutPlayers.Contains(playerId))
                    {
                        continue;
                    }

                    if ((now - last).TotalSeconds > _config.HeartbeatTimeoutSeconds)
                    {
                        timedOut ??= [];
                        timedOut.Add(playerId);
                        _timedOutPlayers.Add(playerId);
                    }
                }
            }

            if (timedOut == null)
            {
                return;
            }

            foreach (string playerId in timedOut)
            {
                OnPlayerDisconnected(playerId, DisconnectReason.Timeout);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[ReconnectionManager] Heartbeat check skipped: {ex.Message}");
        }
    }

        public void UpdateHeartbeat(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        lock (_syncLock)
        {

            _lastHeartbeatUtc[playerId] = DateTime.UtcNow;
            _timedOutPlayers.Remove(playerId);
        }
    }

        public void OnPlayerDisconnected(string playerId, DisconnectReason reason)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        LogWarning($"[ReconnectionManager] Player {playerId} disconnected, reason: {reason}");

        SavePlayerSnapshotBeforeDisconnect(playerId);
        PlayerDisconnected?.Invoke(playerId, reason);

        Task.Delay(TimeSpan.FromMinutes(_config.MaxReconnectionMinutes), _cts.Token)
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && !_cts.IsCancellationRequested)
                {
                    CheckReconnectionTimeout(playerId);
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

        private void SavePlayerSnapshotBeforeDisconnect(string playerId)
    {
        try
        {
            Plugin.RunOnMainThread(() =>
            {
                try
                {
                    PlayerStateSnapshot snapshot = SavePlayerStateSnapshot(playerId);

                    snapshot.ReconnectToken = string.IsNullOrWhiteSpace(snapshot.ReconnectToken)
                        ? GenerateReconnectToken()
                        : snapshot.ReconnectToken;
                    snapshot.DisconnectTime = DateTime.UtcNow.Ticks;
                    snapshot.LastUpdateTime = snapshot.DisconnectTime;

                    lock (_syncLock)
                    {
                        _playerSnapshots[playerId] = snapshot;

                        if (_playerSnapshots.Count > _config.MaxSavedPlayerSnapshots)
                        {

                            string? oldestKey = null;
                            long oldestDisconnectTime = long.MaxValue;

                            foreach ((string key, PlayerStateSnapshot s) in _playerSnapshots)
                            {
                                long dt = s.DisconnectTime;
                                if (dt > 0 && dt < oldestDisconnectTime)
                                {
                                    oldestDisconnectTime = dt;
                                    oldestKey = key;
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(oldestKey))
                            {
                                _playerSnapshots.Remove(oldestKey);
                            }
                        }
                    }

                    LogInformation($"[ReconnectionManager] Saved snapshot for player {playerId} before disconnect");
                }
                catch (Exception ex)
                {
                    LogError($"[ReconnectionManager] Error saving player {playerId} snapshot on main thread: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            LogError($"[ReconnectionManager] Error scheduling player {playerId} snapshot: {ex.Message}");
        }
    }

        private PlayerStateSnapshot SavePlayerStateSnapshot(string playerId)
    {
        try
        {
            INetworkManager? manager = _networkManager;
            INetworkPlayer? player = null;

            if (manager != null)
            {

                player = (manager.GetAllPlayers() ?? Enumerable.Empty<INetworkPlayer>())
                    .FirstOrDefault(p => p != null && string.Equals(p.userName, playerId, StringComparison.Ordinal));

                player ??= manager.GetSelf();
            }

            if (player != null)
            {

                PlayerStateSnapshot snapshot = CreateSnapshotFromNetworkPlayer(player);
                snapshot.PlayerId = playerId;
                return snapshot;
            }
        }
        catch
        {

        }

        return new PlayerStateSnapshot
        {
            PlayerId = playerId,
            UserName = playerId,
            Timestamp = DateTime.UtcNow,
            Health = 0,
            MaxHealth = 0,
            Block = 0,
            Shield = 0,
            ManaGroup = [0, 0, 0, 0],
            Gold = 0,
            Cards = [],
            Exhibits = [],
            ToolCards = [],
            StatusEffects = [],
            GameLocation = new LocationSnapshot { X = -1, Y = -1 },
            IsInBattle = false,
        };
    }

    #endregion

    #region 重连流程

        public ReconnectionResult RequestReconnection(string playerId, string reconnectToken)
    {

        if (!IsReconnecting)
        {
            IsReconnecting = true;
        }

        try
        {
            PlayerStateSnapshot snapshot;
            lock (_syncLock)
            {

                if (!_playerSnapshots.TryGetValue(playerId, out snapshot!))
                {
                    return ReconnectionResult.Failed("No saved state for player");
                }
            }

            if (string.IsNullOrWhiteSpace(reconnectToken) ||
                string.IsNullOrWhiteSpace(snapshot.ReconnectToken) ||
                !string.Equals(snapshot.ReconnectToken, reconnectToken, StringComparison.Ordinal))
            {
                return ReconnectionResult.Failed("Invalid reconnect token");
            }

            DateTime disconnectAt = snapshot.DisconnectTime > 0
                ? new DateTime(snapshot.DisconnectTime, DateTimeKind.Utc)
                : snapshot.Timestamp.ToUniversalTime();

            if (DateTime.UtcNow - disconnectAt > TimeSpan.FromMinutes(_config.MaxReconnectionMinutes))
            {
                RemovePlayerSnapshot(playerId);
                return ReconnectionResult.Failed("重连超时");
            }

            string oldToken = snapshot.ReconnectToken;
            string newToken = GenerateReconnectToken();
            snapshot.ReconnectToken = newToken;
            LogInformation($"[ReconnectionManager] 已批准玩家重连并轮换令牌: playerId={playerId}, oldToken={SecurityUtils.MaskToken(oldToken)}, newToken={SecurityUtils.MaskToken(newToken)}");

            SendReconnectionSnapshot(playerId, snapshot);
            NotifyPlayerReconnected(playerId);

            return ReconnectionResult.Success(snapshot);
        }
        catch (Exception ex)
        {
            LogError($"[ReconnectionManager] Error in reconnection request: {ex.Message}");
            return ReconnectionResult.Failed($"Error: {ex.Message}");
        }
    }

        private void SendReconnectionSnapshot(string playerId, PlayerStateSnapshot snapshot)
    {
        try
        {
            INetworkClient? client = _client;
            if (client == null || !client.IsConnected)
            {
                LogWarning($"[ReconnectionManager] 跳过发送重连恢复包：客户端未连接 (target={playerId})");
                return;
            }

            if (!NetworkIdentityTracker.GetSelfIsHost())
            {
                LogDebug($"[ReconnectionManager] 跳过发送重连恢复包：当前不是房主 (target={playerId})");
                return;
            }

            FullStateSnapshot fullSnapshot = CreateFullSnapshot();

            long lastKnownEventIndex = snapshot.DisconnectTime > 0 ? snapshot.DisconnectTime : 0;
            List<GameEvent> missedEvents = GetMissedEvents(lastKnownEventIndex);

            client.SendGameEventData(NetworkMessageTypes.FullStateSyncResponse, new
            {
                TargetPlayerId = playerId,
                PlayerSnapshot = snapshot,
                FullSnapshot = fullSnapshot,
                MissedEvents = missedEvents,
                ServerTime = DateTime.UtcNow.Ticks
            });

            LogInformation($"[ReconnectionManager] 已发送重连恢复包: target={playerId}");
        }
        catch (Exception ex)
        {
            LogError($"[ReconnectionManager] Failed to send reconnection snapshot to player {playerId}: {ex.Message}");
        }
    }

        private void NotifyPlayerReconnected(string playerId)
    {
        try
        {
            INetworkClient? client = _client;
            if (client != null && client.IsConnected && NetworkIdentityTracker.GetSelfIsHost())
            {

                client.SendGameEventData(NetworkMessageTypes.OnReconnectionAttempt, new
                {
                    PlayerId = playerId,
                    Timestamp = DateTime.UtcNow.Ticks
                });
            }
        }
        catch
        {

        }

        PlayerReconnected?.Invoke(playerId);
    }

        private void CheckReconnectionTimeout(string playerId)
    {
        bool hasSnapshot;
        lock (_syncLock)
        {

            hasSnapshot = _playerSnapshots.ContainsKey(playerId);
        }

        if (!hasSnapshot)
        {
            return;
        }

        RemovePlayerSnapshot(playerId);
        ReconnectionTimeout?.Invoke(playerId);
        LogInformation($"[ReconnectionManager] 玩家重连超时: playerId={playerId}");
    }

        private string GenerateReconnectToken()
    {
        return SecurityUtils.GenerateReconnectToken();
    }

        private void RemovePlayerSnapshot(string playerId)
    {
        lock (_syncLock)
        {
            _playerSnapshots.Remove(playerId);
            _lastHeartbeatUtc.Remove(playerId);
            _timedOutPlayers.Remove(playerId);
        }
    }

    #endregion

    #region 事件追赶（主机侧）

        public void RecordGameEvent(GameEvent gameEvent)
    {
        if (gameEvent == null)
        {
            return;
        }

        lock (_syncLock)
        {

            _eventHistory[gameEvent.Timestamp] = gameEvent;

            if (_eventHistory.Count > _config.MaxHistoryEvents)
            {

                int removeCount = _eventHistory.Count - _config.MaxHistoryEvents;
                for (int i = 0; i < removeCount; i++)
                {
                    _eventHistory.RemoveAt(0);
                }
            }
        }
    }

        public List<GameEvent> GetMissedEvents(long lastKnownEventIndex)
    {
        lock (_syncLock)
        {

            return _eventHistory
                .Where(e => e.Key > lastKnownEventIndex)
                .Select(e => e.Value)
                .ToList();
        }
    }

    #endregion

    #region 统计与资源释放

        public ReconnectionManagerStats GetStats()
    {
        lock (_syncLock)
        {
            return new ReconnectionManagerStats
            {
                ActiveSnapshots = _playerSnapshots.Count,
                TotalEvents = _eventHistory.Count,
                IsConnected = IsConnected,
                IsReconnecting = IsReconnecting,
                MaxReconnectionMinutes = _config.MaxReconnectionMinutes,
                SnapshotIntervalSeconds = _config.SnapshotIntervalSeconds
            };
        }
    }

        public void Dispose()
    {
        try
        {
            if (_client != null)
            {
                _client.OnConnectionStateChanged -= OnConnectionStateChanged;
                _client.OnGameEventReceived -= OnGameEventReceived;
            }
        }
        catch
        {

        }

        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            LogWarning($"[ReconnectionManager] Error cancelling pending tasks: {ex.Message}");
        }

        _heartbeatTimer.Dispose();
        _snapshotTimer.Dispose();
    }

    #endregion

    #region 事件与回调

        public event Action<string, DisconnectReason>? PlayerDisconnected;

        public event Action<string>? PlayerReconnected;

        public event Action<string>? ReconnectionTimeout;

        private void OnConnectionStateChanged(bool connected)
    {
        IsConnected = connected;
        if (connected)
        {
            IsReconnecting = false;
        }
    }

        private void OnGameEventReceived(string eventType, object payload)
    {
        try
        {

            if (!NetworkIdentityTracker.GetSelfIsHost())
            {
                return;
            }

            if (string.Equals(eventType, NetworkMessageTypes.FullStateSyncRequest, StringComparison.Ordinal) ||
                string.Equals(eventType, NetworkMessageTypes.FullStateSyncResponse, StringComparison.Ordinal))
            {
                return;
            }

            RecordGameEvent(new GameEvent(eventType, "unknown", payload)
            {
                Timestamp = DateTime.UtcNow.Ticks
            });
        }
        catch
        {

        }
    }

    #endregion

    #region 快照构建工具

        private PlayerStateSnapshot CreateSnapshotFromNetworkPlayer(INetworkPlayer player)
    {

        int[] mana = player.GetManaArraySafe();

        return new PlayerStateSnapshot
        {
            PlayerId = player.userName ?? string.Empty,
            UserName = player.userName ?? string.Empty,
            Timestamp = DateTime.UtcNow,
            Health = player.HP,
            MaxHealth = player.maxHP,
            Block = player.block,
            Shield = player.shield,
            ManaGroup = mana,
            Gold = player.coins,
            Cards = [],
            Exhibits = [],
            ToolCards = [],
            StatusEffects = [],
            GameLocation = new LocationSnapshot { X = player.location_X, Y = player.location_Y },
            IsInBattle = false,
            CharacterType = player.chara ?? string.Empty,
            IsPlayersTurn = !player.endturn
        };
    }

    #endregion

    #region 日志

    private void LogInformation(string message)
    {
        _logger?.LogInformation(message);
        _fallbackLogger?.LogInfo(message);
    }

    private void LogWarning(string message)
    {
        _logger?.LogWarning(message);
        _fallbackLogger?.LogWarning(message);
    }

    private void LogError(string message)
    {
        _logger?.LogError(message);
        _fallbackLogger?.LogError(message);
    }

    private void LogDebug(string message)
    {
        _logger?.LogDebug(message);
        _fallbackLogger?.LogDebug(message);
    }

    internal void SetPlayerSnapshotForTest(string playerId, PlayerStateSnapshot snapshot)
    {
        lock (_syncLock)
        {
            _playerSnapshots[playerId] = snapshot;
        }
    }

    #endregion
}

#region 相关数据结构

public enum DisconnectReason
{
        Timeout,

        Manual,

        NetworkError,

        Kicked,

        ServerShutdown
}

public class ReconnectionConfig
{
        public int MaxReconnectionMinutes { get; set; } = 5;

        public int SnapshotIntervalSeconds { get; set; } = 30;

        public int HeartbeatTimeoutSeconds { get; set; } = 30;

        public int MaxHistoryEvents { get; set; } = 1000;

        public int MaxSavedFullSnapshots { get; set; } = 3;

        public int MaxSavedPlayerSnapshots { get; set; } = 64;
}

public class ReconnectionManagerStats
{
        public int ActiveSnapshots { get; set; }

        public int TotalEvents { get; set; }

        public bool IsConnected { get; set; }

        public bool IsReconnecting { get; set; }

        public int MaxReconnectionMinutes { get; set; }

        public int SnapshotIntervalSeconds { get; set; }
}

#endregion
