using System;
using HarmonyLib;
using LBoL.Core;
using LBoL.Core.Stations;
using LBoL.Presentation.UI.Panels;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.MidGameJoin;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Patch.Map;

[HarmonyPatch]
public class MapPanelUpdateMapNodesStatusPatch
{
    private static readonly object _locationSendLock = new();
    private static bool _wasConnected;
    private static ulong _lastSentFp;
    private static long _lastSentAtTicks;

        [HarmonyPatch(typeof(MapPanel), "Update")]
    [HarmonyPostfix]
    public static void MapPanel_Update_Postfix(MapPanel __instance)
    {
        try
        {
            IServiceProvider sp = ModService.ServiceProvider;
            sp?.GetService<MapCatchUpOrchestrator>()?.TryApplyPendingToCurrentRun(pathStepsBudget: 1, nodeStatesBudget: 10);
        }
        catch
        {

        }
    }

        [HarmonyPatch(typeof(MapPanel), "UpdateMapNodesStatus")]
    [HarmonyPrefix]
    public static void Prefix(MapPanel __instance)
    {
        try
        {

            IServiceProvider sp = ModService.ServiceProvider;
            sp?.GetService<MapCatchUpOrchestrator>()?.TryApplyPendingToCurrentRun(pathStepsBudget: 2, nodeStatesBudget: 60);
        }
        catch
        {

        }
    }

        [HarmonyPatch(typeof(MapPanel), "UpdateMapNodesStatus")]
    [HarmonyPostfix]
    public static void Postfix(MapPanel __instance)
    {
        try
        {
            IServiceProvider sp = ModService.ServiceProvider;
            if (sp == null)
            {
                Plugin.Logger?.LogWarning("[MapSyncPatch] serviceProvider is null");
                return;
            }

            var networkClient = sp.GetService<INetworkClient>();
            if (networkClient == null || !networkClient.IsConnected)
            {
                Plugin.Logger?.LogDebug("[MapSyncPatch] Network client not available");
                return;
            }

            var gameMapField = Traverse.Create(__instance).Field("_map");
            if (!gameMapField.FieldExists())
            {
                Plugin.Logger?.LogWarning("[MapSyncPatch] _map field not found");
                return;
            }

            var gameMap = gameMapField.GetValue<GameMap>();
            if (gameMap == null)
            {
                Plugin.Logger?.LogWarning("[MapSyncPatch] GameMap is null");
                return;
            }

            var visitingNode = gameMap.VisitingNode;
            if (visitingNode == null)
            {
                Plugin.Logger?.LogWarning("[MapSyncPatch] VisitingNode is null");
                return;
            }

            string characterId = null;
            try
            {
                characterId = GameStateUtils.GetCurrentPlayer()?.ModelName;
            }
            catch
            {

            }

            var localPlayer = GameStateUtils.GetCurrentPlayer();
            var locationData = new
            {
                LocationX = visitingNode.X,
                LocationY = visitingNode.Y,
                LocationName = visitingNode.StationType.ToString(),
                LocationType = visitingNode.GetType().Name,
                Stage = visitingNode.Act,
                CharacterId = characterId,
                Hp = localPlayer?.Hp ?? 0,
                MaxHp = localPlayer?.MaxHp ?? 0,
            };

            string json = JsonCompat.Serialize(locationData);

            bool shouldSend;
            bool isReconnectFirstSend = false;
            long nowTicks = DateTime.UtcNow.Ticks;
            ulong fp = NetLogHelper.ComputeFnv1a64(json);
            lock (_locationSendLock)
            {
                bool connected = networkClient.IsConnected;
                if (connected && !_wasConnected)
                {

                    _lastSentFp = 0;
                    _lastSentAtTicks = 0;
                    isReconnectFirstSend = true;
                }

                _wasConnected = connected;

                long minIntervalTicks = TimeSpan.FromMilliseconds(250).Ticks;
                long refreshIntervalTicks = TimeSpan.FromSeconds(5).Ticks;

                bool samePayload = fp == _lastSentFp;
                bool tooSoon = (nowTicks - _lastSentAtTicks) >= 0 && (nowTicks - _lastSentAtTicks) < minIntervalTicks;
                bool needsRefresh = (nowTicks - _lastSentAtTicks) >= refreshIntervalTicks;

                shouldSend = (!samePayload) || needsRefresh || isReconnectFirstSend;
                if (samePayload && tooSoon)
                {
                    shouldSend = false;
                }

                if (shouldSend)
                {
                    _lastSentFp = fp;
                    _lastSentAtTicks = nowTicks;
                }
            }

            if (!shouldSend)
            {
                return;
            }

            networkClient.SendRequest("UpdatePlayerLocation", json);
            string summary = NetLogHelper.BuildSummary("UpdatePlayerLocation", json);
            Plugin.Logger?.LogInfo($"[MapSyncPatch] 已发送位置同步: ({locationData.LocationX}, {locationData.LocationY}) - {locationData.LocationName} ({summary})");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MapSyncPatch] Error in Postfix: {ex.Message}\n{ex.StackTrace}");
        }
    }

        public static void SendLocationUpdateActive(bool force = false)
    {
        try
        {
            if (force)
            {
                lock (_locationSendLock)
                {
                    _lastSentFp = 0;
                    _lastSentAtTicks = 0;
                }
            }

            var gameRun = GameStateUtils.GetCurrentGameRun();
            var visitingNode = gameRun?.CurrentMap?.VisitingNode;
            if (visitingNode == null)
            {
                return;
            }

            IServiceProvider sp = ModService.ServiceProvider;
            var networkClient = sp?.GetService<INetworkClient>();
            if (networkClient == null || !networkClient.IsConnected)
            {
                return;
            }

            string characterId = null;
            try { characterId = GameStateUtils.GetCurrentPlayer()?.ModelName; }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[MapPanelUpdateMapNodesStatusPatch] GetCurrentPlayer error: {ex.Message}"); }

            var localPlayer = GameStateUtils.GetCurrentPlayer();
            var locationData = new
            {
                LocationX = visitingNode.X,
                LocationY = visitingNode.Y,
                LocationName = visitingNode.StationType.ToString(),
                LocationType = visitingNode.GetType().Name,
                Stage = visitingNode.Act,
                CharacterId = characterId,
                Hp = localPlayer?.Hp ?? 0,
                MaxHp = localPlayer?.MaxHp ?? 0,
            };

            string json = JsonCompat.Serialize(locationData);
            networkClient.SendRequest("UpdatePlayerLocation", json);
            Plugin.Logger?.LogInfo($"[MapSyncPatch] 主动发送位置更新: ({visitingNode.X}, {visitingNode.Y}) - {visitingNode.StationType}");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[MapSyncPatch] SendLocationUpdateActive 失败: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(GameRunController), "EnterStation")]
internal static class GameRunController_EnterStation_ActiveLocationPatch
{
    [HarmonyPostfix]
    public static void Postfix(GameRunController __instance, Station station)
    {
        try
        {
            DeathStateSyncPatch.EnsureInitialHpSynced();
            MapPanelUpdateMapNodesStatusPatch.SendLocationUpdateActive(force: true);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[ActiveLocationPatch] EnterStation 后置位置主动上传失败: {ex.Message}");
        }
    }
}
