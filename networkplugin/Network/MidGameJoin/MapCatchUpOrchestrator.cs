using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using LBoL.Core;
using LBoL.Core.Stations;
using LBoL.EntityLib.Adventures;
using LBoL.EntityLib.Stages.NormalStages;
using LBoL.Presentation.UI;
using LBoL.Presentation.UI.Dialogs;
using LBoL.Presentation.UI.Panels;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.RoomSync;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Snapshot;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Network.MidGameJoin;

public sealed class MapCatchUpOrchestrator
{
    #region 字段与常量
        private readonly ManualLogSource _logger;
        private readonly RoomSyncManager _roomSyncManager;

        private readonly object _lock = new();

        private FullStateSnapshot _pendingSnapshot;
        private long _pendingReceivedAtUtcTicks;
        private string _pendingCheckpointId = string.Empty;
        private long _lastSeedMismatchLogAtUtcTicks;
        private string _lastSeedMismatchCheckpointId = string.Empty;
        private bool _applied;

        private CatchUpSession _session;

        private bool _startGamePrompted;

        private string _lastRoomStateRequestedCheckpointId = string.Empty;
    #endregion

    #region 嵌套类型
        private sealed class CatchUpSession
    {
                public FullStateSnapshot Snapshot;
                public string CheckpointId;
                public long ReceivedAtUtcTicks;
                public long CreatedAtUtcTicks;

                public bool PathInitialized;
                public int PathIndex;

                public List<KeyValuePair<string, string>> NodeStatePairs;
                public int NodeStateIndex;

                public bool CurrentLocationApplied;
                public bool RoomStateRequested;
                public bool Completed;

                public HashSet<string> ClearedNodeKeys;
                public HashSet<string> SettledNodeKeys;

                public string PendingSettlementNodeKey;
                public Station PendingSettlementStation;
                public bool PendingBossExhibitShown;
                public bool PendingRewardShown;
    }

    #endregion

    #region 构造函数
    public MapCatchUpOrchestrator(ManualLogSource logger, RoomSyncManager roomSyncManager)
    {
        _logger = logger ?? Plugin.Logger;
        _roomSyncManager = roomSyncManager ?? throw new ArgumentNullException(nameof(roomSyncManager));
    }
    #endregion

    #region 公共方法 — 快照管理与主线程驱动
        public void PumpMainThread()
    {
        try
        {

            TryApplyPendingToCurrentRun(pathStepsBudget: 4, nodeStatesBudget: 120);

            if (TryGetPendingFullSnapshot(out FullStateSnapshot snapshot))
            {

                TryPromptStartGameForJoiner_NoThrow(snapshot);
            }
        }
        catch
        {

        }
    }

        public void SetPendingFullSnapshot(FullStateSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        lock (_lock)
        {
            _pendingSnapshot = snapshot;
            _pendingReceivedAtUtcTicks = DateTime.UtcNow.Ticks;
            _pendingCheckpointId = snapshot.MapState?.LastCheckpointId ?? string.Empty;
            _applied = false;
            _startGamePrompted = false;
            _session = null;

            _lastRoomStateRequestedCheckpointId = string.Empty;
        }

        _logger?.LogInfo($"[MapCatchUp] Pending snapshot stored: ts={snapshot.Timestamp}, checkpoint={_pendingCheckpointId}");

    }

        public bool TryGetPendingFullSnapshot(out FullStateSnapshot snapshot)
    {
        snapshot = null;

        lock (_lock)
        {
            if (_pendingSnapshot == null || _applied)
            {
                return false;
            }

            snapshot = _pendingSnapshot;
            return snapshot != null;
        }
    }

        private void TryPromptStartGameForJoiner_NoThrow(FullStateSnapshot snapshot)
    {
        try
        {
            if (_startGamePrompted)
            {
                return;
            }

            if (snapshot?.GameState == null)
            {
                return;
            }

            if (GameStateUtils.GetCurrentGameRun() != null)
            {
                return;
            }

            if (!UiManager.IsInitialized)
            {
                return;
            }

            if (snapshot.GameState.RootSeed == null ||
                snapshot.GameState.Difficulty == null ||
                snapshot.GameState.StageTypeNames == null ||
                snapshot.GameState.StageTypeNames.Count == 0)
            {
                return;
            }

            _startGamePrompted = true;
            MainMenuMultiplayerEntryPatch.HideRoomListOverlay(immediate: true);

            StartGameData data = new StartGameData
            {
                StagesCreateFunc = () =>
                {
                    List<Stage> stages = new();
                    foreach (string name in snapshot.GameState.StageTypeNames)
                    {
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        Stage s = Library.CreateStage(name);
                        if (s != null)
                        {
                            stages.Add(s);
                        }
                    }

                    if (stages.Count == 0)
                    {
                        return new Stage[]
                        {
                            Library.CreateStage<BambooForest>(),
                            Library.CreateStage<XuanwuRavine>(),
                            Library.CreateStage<WindGodLake>().AsNormalFinal(),
                            Library.CreateStage<FinalStage>().AsTrueEndFinal(),
                        };
                    }

                    if (stages.Count >= 4)
                    {
                        stages[2]?.AsNormalFinal();
                        stages[3]?.AsTrueEndFinal();
                    }

                    return stages.ToArray();
                },
                DebutAdventure = typeof(Debut),
            };

            try
            {
                string diff = snapshot.GameState.Difficulty?.ToString() ?? "<unknown>";
                UiManager.GetDialog<MessageDialog>().Show(new MessageContent
                {
                    Text = "检测到正在进行的联机对局。\n\n请先选择角色开始新局以加入追赶。\n\n提示：难度/地图种子/关卡列表将自动锁定为房主设置。\n（你仍可以选择角色）",
                    Icon = MessageIcon.Warning,
                    Buttons = DialogButtons.Confirm,
                    OnConfirm = () =>
                    {
                        try
                        {
                            UiManager.GetPanel<StartGamePanel>()?.Show(data);
                        }
                        catch
                        {

                        }
                    },
                });
            }
            catch
            {

                UiManager.GetPanel<StartGamePanel>()?.Show(data);
            }
        }
        catch
        {

        }
    }

        public bool TryApplyPendingToCurrentRun(int pathStepsBudget = 1, int nodeStatesBudget = 25)
    {
        FullStateSnapshot snapshot;
        long receivedAt;
        string checkpointId;

        lock (_lock)
        {
            if (_pendingSnapshot == null || _applied)
            {
                return false;
            }

            snapshot = _pendingSnapshot;
            receivedAt = _pendingReceivedAtUtcTicks;
            checkpointId = _pendingCheckpointId;
        }

        try
        {
            GameRunController run = GameStateUtils.GetCurrentGameRun();
            if (run == null || run.CurrentMap == null)
            {
                return false;
            }

            if (snapshot.MapState == null)
            {
                return false;
            }

            if (!IsLikelySameMapSeed(run, snapshot.MapState))
            {
                MaybeLogSeedMismatch(checkpointId);
                return false;
            }

            CatchUpSession session = GetOrCreateSession_NoThrow(snapshot, checkpointId, receivedAt);
            if (session == null)
            {
                return false;
            }

            if (StepDriveSettlementUi_NoThrow(run, session))
            {
                return false;
            }

            if (pathStepsBudget < 0)
            {
                pathStepsBudget = 0;
            }

            if (nodeStatesBudget < 0)
            {
                nodeStatesBudget = 0;
            }

            StepApplyPathByEnterNode(run.CurrentMap, snapshot.MapState.PathHistory, session, pathStepsBudget);

            if (StepDriveSettlementUi_NoThrow(run, session))
            {
                return false;
            }

            if (IsPathDone(session))
            {
                StepApplyNodeStates(run.CurrentMap, session, nodeStatesBudget);
            }

            if (IsPathDone(session) && IsNodeStatesDone(session) && !session.CurrentLocationApplied)
            {
                TryApplyCurrentLocation(run.CurrentMap, snapshot.MapState);
                session.CurrentLocationApplied = true;
            }

            if (IsPathDone(session) && IsNodeStatesDone(session) && session.CurrentLocationApplied && !session.RoomStateRequested)
            {

                TryRequestRoomStateAfterMapApplied_NoThrow(snapshot);
                session.RoomStateRequested = true;
            }

            if (IsPathDone(session) && IsNodeStatesDone(session) && session.CurrentLocationApplied && session.RoomStateRequested)
            {
                session.Completed = true;
                MarkApplied();
                ClearPendingAfterApplied_NoThrow();
                _logger?.LogInfo($"[MapCatchUp] Applied MapState (incremental): checkpoint={checkpointId}, receivedAt={receivedAt}");

                NetworkPlugin.Patch.UI.OtherPlayersOverlayPatch.ForceRefreshRemoteCharacters();

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[MapCatchUp] Apply degraded: {ex.Message}");
            return false;
        }
    }
    #endregion

    #region 私有方法 — 增量追赶核心逻辑

        private CatchUpSession GetOrCreateSession_NoThrow(FullStateSnapshot snapshot, string checkpointId, long receivedAtUtcTicks)
    {
        try
        {
            lock (_lock)
            {
                if (_pendingSnapshot == null || _applied)
                {
                    return null;
                }

                if (!ReferenceEquals(_pendingSnapshot, snapshot))
                {
                    return null;
                }

                if (_session != null && ReferenceEquals(_session.Snapshot, snapshot) &&
                    string.Equals(_session.CheckpointId, checkpointId ?? string.Empty, StringComparison.Ordinal))
                {
                    return _session;
                }

                List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
                try
                {
                    if (snapshot?.MapState?.NodeStates != null && snapshot.MapState.NodeStates.Count > 0)
                    {
                        pairs = snapshot.MapState.NodeStates.ToList();
                    }
                }
                catch
                {
                    pairs = new List<KeyValuePair<string, string>>();
                }

                _session = new CatchUpSession
                {
                    Snapshot = snapshot,
                    CheckpointId = checkpointId ?? string.Empty,
                    ReceivedAtUtcTicks = receivedAtUtcTicks,
                    CreatedAtUtcTicks = DateTime.UtcNow.Ticks,
                    PathInitialized = false,
                    PathIndex = 0,
                    NodeStatePairs = pairs,
                    NodeStateIndex = 0,
                    CurrentLocationApplied = false,
                    RoomStateRequested = false,
                    Completed = false,

                    ClearedNodeKeys = new HashSet<string>(StringComparer.Ordinal),
                    SettledNodeKeys = new HashSet<string>(StringComparer.Ordinal),
                    PendingSettlementNodeKey = string.Empty,
                    PendingSettlementStation = null,
                    PendingBossExhibitShown = false,
                    PendingRewardShown = false,
                };

                try
                {
                    if (snapshot?.MapState?.ClearedNodes != null)
                    {
                        foreach (string k in snapshot.MapState.ClearedNodes)
                        {
                            if (!string.IsNullOrWhiteSpace(k))
                            {
                                _session.ClearedNodeKeys.Add(k);
                            }
                        }
                    }
                }
                catch
                {

                }

                return _session;
            }
        }
        catch
        {

            return null;
        }
    }

        private static bool IsPathDone(CatchUpSession session)
    {
        try
        {
            if (session == null)
            {
                return true;
            }

            int total = session.Snapshot?.MapState?.PathHistory?.Count ?? 0;
            return session.PathIndex >= total;
        }
        catch
        {

            return true;
        }
    }

        private static bool IsNodeStatesDone(CatchUpSession session)
    {
        try
        {
            if (session == null)
            {
                return true;
            }

            int total = session.NodeStatePairs?.Count ?? 0;
            return session.NodeStateIndex >= total;
        }
        catch
        {

            return true;
        }
    }

        private static void StepApplyPathByEnterNode(GameMap map, List<LocationSnapshot> pathHistory, CatchUpSession session, int pathStepsBudget)
    {
        try
        {
            if (map == null || session == null)
            {
                return;
            }

            if (!session.PathInitialized)
            {

                try
                {
                    var list = Traverse.Create(map).Field("_path").GetValue<List<MapNode>>();
                    list?.Clear();
                }
                catch
                {

                }

                try
                {
                    Traverse.Create(map).Property("VisitingNode").SetValue(null);
                }
                catch
                {

                }

                session.PathInitialized = true;
                session.PathIndex = 0;
            }

            int total = pathHistory?.Count ?? 0;
            if (total <= 0)
            {
                session.PathIndex = 0;
                return;
            }

            if (pathStepsBudget <= 0)
            {
                return;
            }

            for (int i = 0; i < pathStepsBudget && session.PathIndex < total; i++)
            {
                LocationSnapshot loc = null;
                try
                {
                    loc = pathHistory[session.PathIndex];
                }
                catch
                {
                    loc = null;
                }

                session.PathIndex++;

                if (loc == null)
                {
                    continue;
                }

                int act;
                int x;
                int y;
                if (!TryParseNodeKey(loc.NodeId, out act, out x, out y, out _))
                {
                    act = 0;
                    x = loc.X;
                    y = loc.Y;
                }

                MapNode node = TryFindNode(map, act, x, y);
                if (node == null)
                {
                    continue;
                }

                TrySetNodeStatus(node, MapNodeStatus.Active);

                try
                {
                    map.EnterNode(node, freeMove: true, forced: true);
                }
                catch
                {

                }

                TryBeginSettlementForNode_NoThrow(map, node, session);
                if (!string.IsNullOrWhiteSpace(session.PendingSettlementNodeKey))
                {
                    break;
                }
            }
        }
        catch
        {

        }
    }
    #endregion

    #region 私有方法 — 结算 UI 驱动

        private static void TryBeginSettlementForNode_NoThrow(GameMap map, MapNode node, CatchUpSession session)
    {
        try
        {
            if (map == null || node == null || session == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(session.PendingSettlementNodeKey))
            {

                return;
            }

            string nodeKey = BuildNodeKey(node);
            if (string.IsNullOrWhiteSpace(nodeKey))
            {
                return;
            }

            if (session.SettledNodeKeys.Contains(nodeKey))
            {
                return;
            }

            if (session.ClearedNodeKeys == null || !session.ClearedNodeKeys.Contains(nodeKey))
            {
                return;
            }

            if (node.StationType != StationType.Enemy && node.StationType != StationType.EliteEnemy && node.StationType != StationType.Boss)
            {
                session.SettledNodeKeys.Add(nodeKey);
                return;
            }

            GameRunController run = null;
            try
            {
                run = GameStateUtils.GetCurrentGameRun();
            }
            catch
            {
                run = null;
            }

            if (run?.CurrentStage == null)
            {
                return;
            }

            Station station = null;
            try
            {
                station = run.CurrentStage.CreateStation(node);
            }
            catch
            {
                station = null;
            }

            if (station == null)
            {
                return;
            }

            try
            {

                (station as BattleStation)?.GenerateRewards();
            }
            catch
            {

            }

            session.PendingSettlementNodeKey = nodeKey;
            session.PendingSettlementStation = station;
            session.PendingBossExhibitShown = false;
            session.PendingRewardShown = false;
        }
        catch
        {

        }
    }

        private bool StepDriveSettlementUi_NoThrow(GameRunController run, CatchUpSession session)
    {
        try
        {
            if (run == null || session == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.PendingSettlementNodeKey) || session.PendingSettlementStation == null)
            {
                return false;
            }

            if (!IsJoinerConnected_NoThrow())
            {
                session.SettledNodeKeys.Add(session.PendingSettlementNodeKey);
                ClearPendingSettlement_NoThrow(session);
                return false;
            }

            if (!UiManager.IsInitialized)
            {
                return true;
            }

            try
            {
                var rp = UiManager.GetPanel<RewardPanel>();
                if (rp != null && rp.gameObject != null && rp.gameObject.activeSelf)
                {
                    return true;
                }
            }
            catch
            {

            }

            try
            {
                var bp = UiManager.GetPanel<BossExhibitPanel>();
                if (bp != null && bp.gameObject != null && bp.gameObject.activeSelf)
                {
                    return true;
                }
            }
            catch
            {

            }

            if (!session.PendingBossExhibitShown)
            {
                if (session.PendingSettlementStation is BossStation bossStation)
                {
                    try
                    {
                        bossStation.GenerateBossRewards();
                        Exhibit[] bossRewards = bossStation.BossRewards;
                        if (bossRewards != null && bossRewards.Length > 0)
                        {
                            UiManager.GetPanel<BossExhibitPanel>()?.Show(bossRewards);
                            session.PendingBossExhibitShown = true;
                            return true;
                        }
                    }
                    catch
                    {

                    }
                }

                session.PendingBossExhibitShown = true;
            }

            if (!session.PendingRewardShown)
            {
                try
                {
                    UiManager.GetPanel<RewardPanel>()?.Show(new ShowRewardContent
                    {
                        RewardType = RewardType.Station,
                        Station = session.PendingSettlementStation,
                        ShowNextButton = true,
                    });

                    session.PendingRewardShown = true;
                    return true;
                }
                catch
                {

                    session.SettledNodeKeys.Add(session.PendingSettlementNodeKey);

                    ClearPendingSettlement_NoThrow(session);
                    return false;
                }
            }

            session.SettledNodeKeys.Add(session.PendingSettlementNodeKey);
            ClearPendingSettlement_NoThrow(session);
            return false;
        }
        catch
        {
            return false;
        }
    }
    #endregion

    #region 私有方法 — 节点状态应用与生命周期管理

        private static void ClearPendingSettlement_NoThrow(CatchUpSession session)
    {
        try
        {
            if (session == null)
            {
                return;
            }

            session.PendingSettlementNodeKey = string.Empty;
            session.PendingSettlementStation = null;
            session.PendingBossExhibitShown = false;
            session.PendingRewardShown = false;
        }
        catch
        {

        }
    }

        private static string BuildNodeKey(MapNode node)
    {
        try
        {
            if (node == null)
            {
                return string.Empty;
            }

            return $"{node.Act}:{node.X}:{node.Y}:{node.StationType}";
        }
        catch
        {

            return string.Empty;
        }
    }

        private bool IsJoinerConnected_NoThrow()
    {
        try
        {
            IServiceProvider sp = ModService.ServiceProvider;
            INetworkClient client = sp?.GetService<INetworkClient>();
            if (client == null || !client.IsConnected)
            {
                return false;
            }

            NetworkIdentityTracker.EnsureSubscribed(client);
            return !NetworkIdentityTracker.GetSelfIsHost();
        }
        catch
        {

            return false;
        }
    }

        private static void StepApplyNodeStates(GameMap map, CatchUpSession session, int nodeStatesBudget)
    {
        try
        {
            if (map == null || session == null)
            {
                return;
            }

            if (nodeStatesBudget <= 0)
            {
                return;
            }

            var list = session.NodeStatePairs;
            int total = list?.Count ?? 0;
            if (total <= 0)
            {
                session.NodeStateIndex = 0;
                return;
            }

            for (int i = 0; i < nodeStatesBudget && session.NodeStateIndex < total; i++)
            {
                KeyValuePair<string, string> kv;
                try
                {
                    kv = list[session.NodeStateIndex];
                }
                catch
                {
                    session.NodeStateIndex++;
                    continue;
                }

                session.NodeStateIndex++;

                string nodeKey = kv.Key;
                string state = kv.Value;

                if (!TryParseNodeKey(nodeKey, out int act, out int x, out int y, out string stationType))
                {
                    continue;
                }

                MapNode node = TryFindNode(map, act, x, y);
                if (node == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(stationType) &&
                    !string.Equals(node.StationType.ToString(), stationType, StringComparison.Ordinal))
                {

                }

                if (!Enum.TryParse(state, out MapNodeStatus status))
                {
                    continue;
                }

                TrySetNodeStatus(node, status);
            }
        }
        catch
        {

        }
    }
    #endregion

    #region 私有方法 — 房间状态请求与日志辅助

        private void ClearPendingAfterApplied_NoThrow()
    {
        try
        {
            lock (_lock)
            {
                _pendingSnapshot = null;
                _pendingReceivedAtUtcTicks = 0;
                _pendingCheckpointId = string.Empty;
                _session = null;
            }
        }
        catch
        {

        }
    }

        private void MarkApplied()
    {
        lock (_lock)
        {
            _applied = true;
        }
    }

        private void TryRequestRoomStateAfterMapApplied_NoThrow(FullStateSnapshot snapshot)
    {
        try
        {
            if (snapshot?.MapState?.CurrentLocation == null)
            {
                return;
            }

            string checkpointId = snapshot.MapState.LastCheckpointId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(checkpointId))
            {

                checkpointId = "<no-checkpoint>";
            }

            lock (_lock)
            {
                if (string.Equals(_lastRoomStateRequestedCheckpointId, checkpointId, StringComparison.Ordinal))
                {
                    return;
                }

                _lastRoomStateRequestedCheckpointId = checkpointId;
            }

            LocationSnapshot loc = snapshot.MapState.CurrentLocation;
            int act;
            int x;
            int y;
            string stationType;

            if (!TryParseNodeKey(loc.NodeId, out act, out x, out y, out stationType))
            {
                act = 0;
                x = loc.X;
                y = loc.Y;
                stationType = loc.NodeType ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(stationType))
            {
                stationType = "Unknown";
            }

            _roomSyncManager.SetLastEnteredNode(act, x, y, stationType);

            string roomKey = RoomSyncManager.BuildRoomKey(act, x, y, stationType);
            long knownVersion = _roomSyncManager.TryGetClientRoomState(roomKey)?.RoomVersion ?? 0;
            _roomSyncManager.RequestRoomState(roomKey, knownVersion);
        }
        catch
        {

        }
    }

        private void MaybeLogSeedMismatch(string checkpointId)
    {
        try
        {
            long now = DateTime.UtcNow.Ticks;
            const long logIntervalTicks = TimeSpan.TicksPerSecond * 5;

            lock (_lock)
            {
                if (string.Equals(_lastSeedMismatchCheckpointId, checkpointId ?? string.Empty, StringComparison.Ordinal) &&
                    now - _lastSeedMismatchLogAtUtcTicks < logIntervalTicks)
                {
                    return;
                }

                _lastSeedMismatchCheckpointId = checkpointId ?? string.Empty;
                _lastSeedMismatchLogAtUtcTicks = now;
            }

            _logger?.LogWarning($"[MapCatchUp] Pending MapState waiting for map seed alignment: checkpoint={checkpointId}");
        }
        catch
        {

        }
    }

        private static bool IsLikelySameMapSeed(GameRunController run, MapStateSnapshot mapState)
    {
        try
        {
            if (mapState == null || mapState.MapSeedUlong == null)
            {
                return true;
            }

            ulong? local = run?.CurrentStage?.MapSeed;
            if (local == null)
            {
                return true;
            }

            return local.Value == mapState.MapSeedUlong.Value;
        }
        catch
        {

            return true;
        }
    }
    #endregion

    #region 私有方法 — 路径与状态一次性应用

        private void ApplyMapStateToRun(GameRunController run, MapStateSnapshot mapState)
    {
        GameMap map = run.CurrentMap;
        if (map == null)
        {
            return;
        }

        TryApplyPathByEnterNode(map, mapState.PathHistory);

        if (mapState.NodeStates != null && mapState.NodeStates.Count > 0)
        {
            foreach (KeyValuePair<string, string> kv in mapState.NodeStates)
            {
                string nodeKey = kv.Key;
                string state = kv.Value;

                if (!TryParseNodeKey(nodeKey, out int act, out int x, out int y, out string stationType))
                {
                    continue;
                }

                MapNode node = TryFindNode(map, act, x, y);
                if (node == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(stationType) &&
                    !string.Equals(node.StationType.ToString(), stationType, StringComparison.Ordinal))
                {

                }

                if (!Enum.TryParse(state, out MapNodeStatus status))
                {
                    continue;
                }

                TrySetNodeStatus(node, status);
            }
        }

        TryApplyCurrentLocation(map, mapState);
    }
    #endregion

    #region 私有方法 — 节点工具方法

        private static bool TryParseNodeKey(string nodeKey, out int act, out int x, out int y, out string stationType)
    {
        act = 0;
        x = 0;
        y = 0;
        stationType = string.Empty;

        if (string.IsNullOrWhiteSpace(nodeKey))
        {
            return false;
        }

        string[] parts = nodeKey.Split(':');
        if (parts.Length < 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out act) || !int.TryParse(parts[1], out x) || !int.TryParse(parts[2], out y))
        {
            return false;
        }

        stationType = parts[3] ?? string.Empty;
        return true;
    }

        private static MapNode TryFindNode(GameMap map, int act, int x, int y)
    {
        try
        {
            if (map != null && x >= 0 && x < map.Levels && y >= 0 && y < map.Width)
            {
                MapNode node = map.Nodes[x, y];
                if (node != null && (act <= 0 || node.Act == act))
                {
                    return node;
                }
            }

            if (map?.AllNodes == null)
            {
                return null;
            }

            return act > 0
                ? map.AllNodes.FirstOrDefault(n => n != null && n.Act == act && n.X == x && n.Y == y)
                : map.AllNodes.FirstOrDefault(n => n != null && n.X == x && n.Y == y);
        }
        catch
        {

            return null;
        }
    }

        private static void TrySetNodeStatus(MapNode node, MapNodeStatus status)
    {
        try
        {
            Traverse.Create(node).Property("Status").SetValue(status);
        }
        catch
        {

        }
    }

        private static void TryApplyPath(GameMap map, List<LocationSnapshot> pathHistory)
    {
        try
        {
            if (map == null)
            {
                return;
            }

            var list = Traverse.Create(map).Field("_path").GetValue<List<MapNode>>();
            if (list == null)
            {
                return;
            }

            list.Clear();

            if (pathHistory == null || pathHistory.Count == 0)
            {
                return;
            }

            foreach (LocationSnapshot loc in pathHistory)
            {
                if (loc == null)
                {
                    continue;
                }

                int act;
                int x;
                int y;
                if (!TryParseNodeKey(loc.NodeId, out act, out x, out y, out _))
                {
                    act = 0;
                    x = loc.X;
                    y = loc.Y;
                }

                MapNode node = TryFindNode(map, act, x, y);
                if (node != null)
                {
                    list.Add(node);
                }
            }
        }
        catch
        {

        }
    }

        private static void TryApplyPathByEnterNode(GameMap map, List<LocationSnapshot> pathHistory)
    {
        try
        {
            if (map == null)
            {
                return;
            }

            try
            {
                var list = Traverse.Create(map).Field("_path").GetValue<List<MapNode>>();
                list?.Clear();
            }
            catch
            {

            }

            try
            {
                Traverse.Create(map).Property("VisitingNode").SetValue(null);
            }
            catch
            {

            }

            if (pathHistory == null || pathHistory.Count == 0)
            {
                return;
            }

            foreach (LocationSnapshot loc in pathHistory)
            {
                if (loc == null)
                {
                    continue;
                }

                int act;
                int x;
                int y;
                if (!TryParseNodeKey(loc.NodeId, out act, out x, out y, out _))
                {
                    act = 0;
                    x = loc.X;
                    y = loc.Y;
                }

                MapNode node = TryFindNode(map, act, x, y);
                if (node == null)
                {
                    continue;
                }

                try
                {
                    Traverse.Create(node).Property("Status").SetValue(MapNodeStatus.Active);
                }
                catch
                {

                }

                try
                {
                    map.EnterNode(node, freeMove: true, forced: true);
                }
                catch
                {

                }
            }
        }
        catch
        {

        }
    }

        private static void TryApplyCurrentLocation(GameMap map, MapStateSnapshot mapState)
    {
        try
        {
            if (map == null || mapState?.CurrentLocation == null)
            {
                return;
            }

            int act;
            int x;
            int y;
            if (!TryParseNodeKey(mapState.CurrentLocation.NodeId, out act, out x, out y, out _))
            {
                act = 0;
                x = mapState.CurrentLocation.X;
                y = mapState.CurrentLocation.Y;
            }

            MapNode node = TryFindNode(map, act, x, y);
            if (node == null)
            {
                return;
            }

            try
            {
                Traverse.Create(map).Property("VisitingNode").SetValue(node);
            }
            catch
            {

            }
        }
        catch
        {

        }
    }
    #endregion
}
