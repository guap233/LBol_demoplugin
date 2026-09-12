using System;
using System.Collections.Generic;
using HarmonyLib;
using LBoL.Base;
using LBoL.Core;
using LBoL.Core.Battle.BattleActions;
using LBoL.Core.Cards;
using LBoL.Core.Units;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Core;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Event;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Patch.Network;

namespace NetworkPlugin.Patch.Actions;

[HarmonyPatch]
public class DamageAction_Patch
{
    #region 依赖注入

        private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

    #endregion

    #region 构造函数补丁（伤害动作）

        [HarmonyPatch(typeof(DamageAction), MethodType.Constructor, typeof(Unit), typeof(IEnumerable<Unit>), typeof(DamageInfo), typeof(string), typeof(GunType))]
    [HarmonyPostfix]
    public static void MultiTargetConstructor_Postfix(DamageAction __instance, Unit source, IEnumerable<Unit> targets, DamageInfo damageInfo, string gunName, GunType gunType)
    {
        try
        {

            if (RemoteCardUsePatch.IsInRemoteCardPipeline)
            {
                return;
            }

            if (!TryGetSyncContext(out ISynchronizationManager syncManager, out INetworkPlayer player))
            {
                return;
            }

            if (source is EnemyUnit enemy)
            {
                if (targets != null)
                {
                    foreach (Unit target in targets)
                    {
                        if (target is PlayerUnit localPlayer)
                        {
                            TryBroadcastEnemyAttackVisual(syncManager, player, enemy, localPlayer, damageInfo, gunName, gunType);
                            break;
                        }
                    }
                }
                return;
            }

            if (source is not PlayerUnit)
            {
                return;
            }

            Dictionary<string, object> damageData = new()
            {
                ["UserName"] = player.userName,
                ["Timestamp"] = DateTime.Now.Ticks,
                ["ActionType"] = "Damage",
                ["GunName"] = gunName,
                ["GunType"] = gunType.ToString(),
                ["Damage"] = damageInfo.Amount,
                ["DamageAmount"] = damageInfo.Amount,
                ["DamageType"] = damageInfo.DamageType.ToString(),
                ["DamageShielded"] = damageInfo.DamageShielded,
                ["DamageBlocked"] = damageInfo.DamageBlocked,
                ["ZeroDamage"] = damageInfo.ZeroDamage,
                ["DontBreakPerfect"] = damageInfo.DontBreakPerfect,
                ["IsAccuracy"] = damageInfo.IsAccuracy,
                ["IsGrazed"] = damageInfo.IsGrazed,
                ["SourceId"] = source.Id,
                ["SourceName"] = source.Name,
                ["TargetCount"] = 1,
            };

            if (targets != null)
            {
                List<Dictionary<string, object>> targetList = new();
                foreach (Unit target in targets)
                {
                    if (target == null)
                    {
                        continue;
                    }

                    targetList.Add(new Dictionary<string, object>
                    {
                        ["TargetId"] = target.Id,
                        ["TargetName"] = target.Name,
                    });
                }

                damageData["Targets"] = targetList;
                damageData["TargetCount"] = targetList.Count;
            }

            GameEvent gameEvent = GameEventManager.CreateEvent(
                NetworkMessageTypes.OnDamageDealt.ToString(),
                player.userName,
                damageData
            );

            syncManager.SendGameEvent(gameEvent);

            Plugin.Logger?.LogInfo($"[DamageSync] 伤害动作: {source.Name} -> 多个目标 (伤害: {damageInfo.Amount}, 武器: {gunName})");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[DamageSync] MultiTargetConstructor_Postfix 错误: {ex.Message}");
        }
    }

    
        private static void TryBroadcastEnemyAttackVisual(
        ISynchronizationManager syncManager,
        INetworkPlayer player,
        EnemyUnit enemy,
        PlayerUnit localPlayer,
        DamageInfo damageInfo,
        string gunName,
        GunType gunType)
    {
        try
        {
            if (damageInfo.DamageType != DamageType.Attack)
            {
                return;
            }

            bool isGrazed = damageInfo.Amount > 0 &&
                            localPlayer.HasStatusEffect<LBoL.Core.StatusEffects.Graze>() &&
                            !damageInfo.IsAccuracy;

            Dictionary<string, object> attackVisualData = new()
            {
                ["PlayerId"] = player.playerId,
                ["UserName"] = player.userName,
                ["Timestamp"] = DateTime.Now.Ticks,
                ["EnemyId"] = enemy.Id,
                ["EnemyName"] = enemy.Name,
                ["GunName"] = string.IsNullOrEmpty(gunName) ? "Instant" : gunName,
                ["GunType"] = gunType.ToString(),
                ["Damage"] = damageInfo.Amount,
                ["DamageType"] = damageInfo.DamageType.ToString(),
                ["IsGrazed"] = isGrazed,
                ["IsAccuracy"] = damageInfo.IsAccuracy,
            };

            GameEvent gameEvent = GameEventManager.CreateEvent(
                NetworkMessageTypes.OnEnemyAttackPlayerVisual,
                player.userName,
                attackVisualData
            );

            syncManager.SendGameEvent(gameEvent);

            Plugin.Logger?.LogInfo($"[DamageSync] 敌人攻击玩家视觉同步: {enemy.Name} -> {localPlayer.Name} (伤害: {damageInfo.Amount}, 擦弹: {isGrazed}, 武器: {gunName})");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[DamageSync] TryBroadcastEnemyAttackVisual 错误: {ex.Message}");
        }
    }

    #endregion

    #region 静态方法补丁（失去生命/反应）

        [HarmonyPatch(typeof(DamageAction), nameof(DamageAction.LoseLife))]
    [HarmonyPostfix]
    public static void LoseLife_Postfix(DamageAction __result, Unit target, int life)
    {
        try
        {

            if (RemoteCardUsePatch.IsInRemoteCardPipeline)
            {
                return;
            }

            ISynchronizationManager syncManager = GetSyncManager();
            if (syncManager == null)
            {
                return;
            }

            INetworkManager networkManager = GetNetworkManager();
            if (networkManager == null)
            {
                return;
            }

            INetworkPlayer player = networkManager.GetSelf();

            Dictionary<string, object> damageData = new()
            {
                ["Timestamp"] = DateTime.Now.Ticks,
                ["ActionType"] = "LoseLife",
                ["GunName"] = __result.GunName,
                ["DamageAmount"] = life,
                ["DamageType"] = "HpLose",
                ["SourceId"] = target.Id,
                ["SourceName"] = target.Name,
                ["TargetCount"] = 1,
                ["UserName"] = player.userName,
                ["TargetId"] = target.Id,
                ["TargetName"] = target.Name,
                ["TargetType"] = target.GetType().Name,
                ["TargetCurrentHp"] = target.Hp,
            };

            GameEvent gameEvent = GameEventManager.CreateEvent(
                NetworkMessageTypes.OnDamageDealt.ToString(),
                player.userName,
                damageData
            );

            syncManager.SendGameEvent(gameEvent);

            Plugin.Logger?.LogInfo($"[DamageSync] 失去生命: {target.Name} 失去 {life} 点生命");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[DamageSync] LoseLife_Postfix 错误: {ex.Message}");
        }
    }

        [HarmonyPatch(typeof(DamageAction), nameof(DamageAction.Reaction))]
    [HarmonyPostfix]
    public static void Reaction_Postfix(DamageAction __result, Unit target, int damage)
    {
        try
        {

            if (RemoteCardUsePatch.IsInRemoteCardPipeline)
            {
                return;
            }

            ISynchronizationManager syncManager = GetSyncManager();
            if (syncManager == null)
            {
                return;
            }

            INetworkManager networkManager = GetNetworkManager();
            if (networkManager == null)
            {
                return;
            }

            INetworkPlayer player = networkManager.GetSelf();

            Dictionary<string, object> damageData = new()
            {
                ["Timestamp"] = DateTime.Now.Ticks,
                ["ActionType"] = "Reaction",
                ["GunName"] = __result.GunName,
                ["DamageAmount"] = damage,
                ["DamageType"] = "Reaction",
                ["SourceId"] = target.Id,
                ["SourceName"] = target.Name,
                ["TargetCount"] = 1,
                ["UserName"] = player.userName,
                ["TargetId"] = target.Id,
                ["TargetName"] = target.Name,
                ["TargetType"] = target.GetType().Name,
                ["TargetCurrentHp"] = target.Hp,
            };

            GameEvent gameEvent = GameEventManager.CreateEvent(
                NetworkMessageTypes.OnDamageDealt.ToString(),
                player.userName,
                damageData
            );

            syncManager.SendGameEvent(gameEvent);

            Plugin.Logger?.LogInfo($"[DamageSync] 反应伤害: {target.Name} 受到 {damage} 点反应伤害");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[DamageSync] Reaction_Postfix 错误: {ex.Message}");
        }
    }

    #endregion

    #region 辅助方法

        private static ISynchronizationManager GetSyncManager()
    {
        try
        {
            Plugin.LogSynchronizationManagerResolveFromPatch(nameof(DamageAction_Patch), ServiceProvider);
            return ServiceProvider?.GetService<ISynchronizationManager>();
        }
        catch
        {
            return null;
        }
    }

        private static INetworkManager GetNetworkManager()
    {
        try
        {
            return ServiceProvider?.GetService<INetworkManager>();
        }
        catch
        {
            return null;
        }
    }

        private static bool TryGetSyncContext(out ISynchronizationManager syncManager, out INetworkPlayer player)
    {
        syncManager = GetSyncManager();
        if (syncManager == null)
        {
            player = null;
            return false;
        }

        INetworkManager networkManager = GetNetworkManager();
        if (networkManager == null)
        {
            player = null;
            return false;
        }

        player = networkManager.GetSelf();
        return true;
    }

    #endregion
}
