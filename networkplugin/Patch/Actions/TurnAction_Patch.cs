using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LBoL.Base;
using LBoL.Core;
using LBoL.Core.Battle;
using LBoL.Core.Battle.BattleActions;
using LBoL.Core.StatusEffects;
using LBoL.Core.Units;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Reconnection;
using NetworkPlugin.Utils;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.Snapshot;

namespace NetworkPlugin.Patch.Actions;

[HarmonyPatch]
public class TurnAction_Patch
{
    #region 依赖注入

        private static IServiceProvider serviceProvider => ModService.ServiceProvider;

    #endregion

    #region 玩家回合开始同步

        [HarmonyPatch(typeof(BattleController), "StartPlayerTurn")]
    [HarmonyPostfix]
    public static void StartPlayerTurn_Postfix(BattleController __instance)
    {
        try
        {

            if (serviceProvider == null)
            {
                Plugin.Logger?.LogDebug("[TurnSync] ServiceProvider 未初始化（StartPlayerTurn）");
                return;
            }

            var networkClient = serviceProvider.GetService<INetworkClient>();
            if (networkClient == null || !networkClient.IsConnected)
            {
                Plugin.Logger?.LogDebug("[TurnSync] 网络客户端不可用（StartPlayerTurn）");
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(networkClient);

            BattleController battle = __instance;
            if (battle == null || battle.Player == null)
            {
                Plugin.Logger?.LogDebug("[TurnSync] battle 为空（StartPlayerTurn）");
                return;
            }

            if (battle.Player != GameStateUtils.GetCurrentPlayer())
            {
                return;
            }

            SendTurnBoundarySnapshot(battle, networkClient, BoundaryType.Start);
            Plugin.Logger?.LogInfo("[TurnSync] 玩家回合开始已同步");
        }
        catch (Exception ex)
        {

            Plugin.Logger?.LogError($"[TurnSync] StartPlayerTurn_Postfix 异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    #endregion

    #region 玩家回合结束同步

        [HarmonyPatch(typeof(BattleController), "EndPlayerTurn")]
    [HarmonyPostfix]
    public static void EndPlayerTurn_Postfix(BattleController __instance)
    {
        try
        {

            if (serviceProvider == null)
            {
                Plugin.Logger?.LogDebug("[TurnSync] ServiceProvider 未初始化（EndPlayerTurn）");
                return;
            }

            var networkClient = serviceProvider.GetService<INetworkClient>();
            if (networkClient == null || !networkClient.IsConnected)
            {
                Plugin.Logger?.LogDebug("[TurnSync] 网络客户端不可用（EndPlayerTurn）");
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(networkClient);

            BattleController battle = __instance;
            if (battle == null || battle.Player == null)
            {
                Plugin.Logger?.LogDebug("[TurnSync] battle 为空（EndPlayerTurn）");
                return;
            }

            if (battle.Player != GameStateUtils.GetCurrentPlayer())
            {
                return;
            }

            GameRunController run = battle.GameRun ?? GameStateUtils.GetCurrentGameRun();
            string battleId = GetBattleId(run);
            int round = battle.RoundCounter;

            SendTurnBoundarySnapshot(battle, networkClient, BoundaryType.End, battleId, round);

            Plugin.Logger?.LogInfo("[TurnSync] 玩家回合结束已同步");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[TurnSync] EndPlayerTurn_Postfix 异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    #endregion

    #region 战斗开始/结束同步（预留）

        [HarmonyPatch(typeof(BattleController), "StartBattle")]
    [HarmonyPostfix]
    public static void StartBattle_Postfix(BattleController __instance)
    {
        try
        {
            if (serviceProvider == null)
            {
                return;
            }

            var networkClient = serviceProvider.GetService<INetworkClient>();
            if (networkClient == null || !networkClient.IsConnected)
            {
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(networkClient);
            if (!NetworkIdentityTracker.GetSelfIsHost())
            {

                return;
            }

            BattleController battle = __instance;
            if (battle == null || battle.Player == null)
            {
                return;
            }

            if (battle.Player != GameStateUtils.GetCurrentPlayer())
            {
                return;
            }

            GameRunController run = battle.GameRun ?? GameStateUtils.GetCurrentGameRun();
            string battleId = GetBattleId(run);

            string selfId = NetworkIdentityTracker.GetSelfPlayerId();
            if (string.IsNullOrWhiteSpace(selfId))
            {
                selfId = GameStateUtils.GetCurrentPlayerId();
            }

            BattleStateSnapshot battleState = new BattleStateSnapshot
            {
                IsInBattle = true,
                BattleId = battleId,
                CurrentTurn = Math.Max(1, battle.RoundCounter),
                CurrentTurnPlayerId = selfId,
                TurnPhase = "Player",
                Enemies = CaptureEnemies(battle),
                BattleStartTime = DateTime.UtcNow.Ticks,
                BattleType = run?.CurrentMap?.VisitingNode?.StationType.ToString() ?? "Unknown",
            };

            try
            {
                string t = battleState.BattleType;
                battleState.IsBossBattle = !string.IsNullOrWhiteSpace(t) && t.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                battleState.IsBossBattle = false;
            }

            var payload = new
            {
                Timestamp = DateTime.UtcNow.Ticks,
                SenderId = selfId,
                BattleId = battleId,
                BattleState = battleState,
            };

            networkClient.BroadcastState(NetworkMessageTypes.OnBattleStart, payload);
            Plugin.Logger?.LogInfo($"[TurnSync] 战斗开始已同步: {battleId}");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[TurnSync] StartBattle_Postfix 异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    #endregion

    #region 战斗结束同步

        [HarmonyPatch(typeof(BattleController), "EndBattle")]
    [HarmonyPostfix]
    public static void EndBattle_Postfix(BattleController __instance)
    {
        try
        {
            if (serviceProvider == null)
            {
                return;
            }

            var networkClient = serviceProvider.GetService<INetworkClient>();
            if (networkClient == null || !networkClient.IsConnected)
            {
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(networkClient);
            if (!NetworkIdentityTracker.GetSelfIsHost())
            {
                return;
            }

            BattleController battle = __instance;
            if (battle == null || battle.Player == null)
            {
                return;
            }

            if (battle.Player != GameStateUtils.GetCurrentPlayer())
            {
                return;
            }

            GameRunController run = battle.GameRun ?? GameStateUtils.GetCurrentGameRun();
            string battleId = GetBattleId(run);

            string selfId = NetworkIdentityTracker.GetSelfPlayerId();
            if (string.IsNullOrWhiteSpace(selfId))
            {
                selfId = GameStateUtils.GetCurrentPlayerId();
            }

            BattleStateSnapshot battleState = new BattleStateSnapshot
            {
                IsInBattle = false,
                BattleId = battleId,
                CurrentTurn = Math.Max(1, battle.RoundCounter),
                CurrentTurnPlayerId = selfId,
                TurnPhase = "Finished",
                Enemies = CaptureEnemies(battle),
                BattleStartTime = 0,
                BattleType = run?.CurrentMap?.VisitingNode?.StationType.ToString() ?? "Unknown",
            };

            var payload = new
            {
                Timestamp = DateTime.UtcNow.Ticks,
                SenderId = selfId,
                BattleId = battleId,
                BattleState = battleState,
            };

            try
            {

                var reconnection = serviceProvider.GetService<ReconnectionManager>();
                string nodeKey = run?.CurrentMap?.VisitingNode != null
                    ? $"{run.CurrentMap.VisitingNode.Act}:{run.CurrentMap.VisitingNode.X}:{run.CurrentMap.VisitingNode.Y}:{run.CurrentMap.VisitingNode.StationType}"
                    : null;
                reconnection?.MarkMapCheckpoint("battle_end", nodeKey);
            }
            catch
            {

            }

            networkClient.BroadcastState(NetworkMessageTypes.OnBattleEnd, payload);
            Plugin.Logger?.LogInfo($"[TurnSync] 战斗结束已同步: {battleId}");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[TurnSync] EndBattle_Postfix 异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    #endregion

    #region 辅助方法

        private static int[] GetManaGroup(ManaGroup manaGroup)
    {
        if (manaGroup == null)
        {

            return [0, 0, 0, 0];
        }

        return
        [
            manaGroup.Red,
            manaGroup.Blue,
            manaGroup.Green,
            manaGroup.White,
        ];
    }

        private static Dictionary<ManaColor, int> ConvertManaGroupToDictionary(ManaGroup manaGroup)
    {

        if (manaGroup.IsEmpty)
        {
            return [];
        }

        return new Dictionary<ManaColor, int>
        {
            [ManaColor.Any] = manaGroup.Any,
            [ManaColor.White] = manaGroup.White,
            [ManaColor.Blue] = manaGroup.Blue,
            [ManaColor.Black] = manaGroup.Black,
            [ManaColor.Red] = manaGroup.Red,
            [ManaColor.Green] = manaGroup.Green,
            [ManaColor.Colorless] = manaGroup.Colorless,
            [ManaColor.Philosophy] = manaGroup.Philosophy,
            [ManaColor.Hybrid] = manaGroup.Hybrid,
        };
    }

        private static string[] GetEnemyTypes(IEnumerable<EnemyUnit> enemyGroup)
    {
        if (enemyGroup == null)
        {
            return [];
        }

        List<string> enemyTypes = new List<string>();
        foreach (var enemy in enemyGroup)
        {
            enemyTypes.Add(enemy?.Name ?? "Unknown");
        }

        return enemyTypes.ToArray();
    }

    private static void SendTurnBoundarySnapshot(
        BattleController battle, INetworkClient networkClient,
        BoundaryType boundaryType, string battleId = null, int round = 0)
    {
        PlayerUnit source = battle.Player;
        if (source == null) return;

        GameRunController run = battle.GameRun ?? GameStateUtils.GetCurrentGameRun();
        string selfPlayerId = NetworkIdentityTracker.GetSelfPlayerId();
        if (string.IsNullOrWhiteSpace(selfPlayerId))
            selfPlayerId = GameStateUtils.GetCurrentPlayerId();

        int gold = 0;
        int maxMana = 0;
        try
        {
            gold = run?.Money ?? 0;
            maxMana = run != null ? ManaUtils.GetTotalMana(run.BaseMana) : 0;
        }
        catch (Exception ex) { Plugin.Logger?.LogWarning($"[TurnAction] 获取金币/法力值失败: {ex.Message}"); }

        List<StatusEffectStateSnapshot> statusEffects = CaptureStatusEffects(source);

        var playerState = new PlayerStateSnapshot
        {
            PlayerId = selfPlayerId,
            UserName = networkClient.GetSelf().userName,
            Health = source.Hp,
            MaxHealth = source.MaxHp,
            Block = source.Block,
            Shield = source.Shield,
            ManaGroup = ManaUtils.ManaGroupToArray(battle.BattleMana),
            MaxMana = maxMana,
            Gold = gold,
            TurnNumber = source.TurnCounter,
            IsInBattle = true,
            IsAlive = source.IsAlive,
            IsPlayersTurn = boundaryType == BoundaryType.Start,
            IsInTurn = source.IsInTurn,
            IsExtraTurn = source.IsExtraTurn,
            CharacterType = "Player",
            ReconnectToken = string.Empty,
            DisconnectTime = 0,
            LastUpdateTime = DateTime.Now.Ticks,
            TurnCounter = source.TurnCounter,
            Timestamp = DateTime.Now,
        };

        try
        {
            var node = run?.CurrentMap?.VisitingNode;
            if (node != null)
            {
                playerState.GameLocation = new LocationSnapshot
                {
                    X = node.X,
                    Y = node.Y,
                    NodeId = $"Act{node.Act}:{node.X}:{node.Y}:{node.StationType}",
                    NodeType = node.StationType.ToString(),
                    VisitTime = DateTime.UtcNow.Ticks,
                };
            }
        }
        catch (Exception ex) { Plugin.Logger?.LogWarning($"[TurnBoundaryCapture] 捕获地图位置失败: {ex.Message}"); }

        var intentions = new IntentionSnapshot(battleController: battle);

        var data = new TurnBoundarySnapshot(
            statusEffects: statusEffects,
            playerState: playerState,
            intentions: intentions,
            boundaryType: boundaryType,
            battleId: battleId,
            round: round);

        string json = JsonCompat.Serialize(data);
        string msgType = boundaryType == BoundaryType.Start
            ? NetworkMessageTypes.OnTurnStart
            : NetworkMessageTypes.OnTurnEnd;

        networkClient.SendRequest(msgType, json);
    }

    private static string GetBattleId(GameRunController run)
    {
        string battleId = "battle";
        try
        {
            var node = run?.CurrentMap?.VisitingNode;
            if (node != null)
            {
                battleId = $"Act{node.Act}:{node.X}:{node.Y}:{node.StationType}";
            }
        }
        catch
        {

        }

        return battleId;
    }

    private static List<StatusEffectStateSnapshot> CaptureStatusEffects(Unit unit)
    {
        List<StatusEffectStateSnapshot> result = new List<StatusEffectStateSnapshot>();
        if (unit == null)
        {
            return result;
        }

        try
        {
            foreach (StatusEffect se in unit.StatusEffects)
            {
                if (se == null)
                {
                    continue;
                }

                int level = 0;
                int duration = 0;
                int value = 0;
                bool isPermanent = false;

                try
                {
                    if (se.HasLevel)
                    {
                        level = se.Level;
                    }
                }
                catch
                {
                    level = 0;
                }

                try
                {
                    if (se.HasDuration)
                    {
                        duration = se.Duration;
                        isPermanent = false;
                    }
                    else
                    {
                        duration = 0;
                        isPermanent = true;
                    }
                }
                catch
                {
                    duration = 0;
                }

                try
                {
                    if (se.HasCount)
                    {
                        value = se.Count;
                    }
                }
                catch
                {
                    value = 0;
                }

                string type = "Unknown";
                bool isDebuff = false;
                try
                {
                    type = se.Type.ToString();
                    isDebuff = se.Type == StatusEffectType.Negative;
                }
                catch
                {
                    type = "Unknown";
                    isDebuff = false;
                }

                result.Add(new StatusEffectStateSnapshot
                {
                    EffectId = se.Id ?? string.Empty,
                    EffectName = se.Name ?? string.Empty,
                    EffectType = type,
                    Level = level,
                    Duration = duration,
                    IsDebuff = isDebuff,
                    IsPermanent = isPermanent,
                    EffectValue = value,
                    Description = se.Description ?? string.Empty,
                    SourceId = string.Empty,
                });
            }
        }
        catch
        {

        }

        return result;
    }

    private static List<EnemyStateSnapshot> CaptureEnemies(BattleController battle)
    {
        List<EnemyStateSnapshot> enemies = new List<EnemyStateSnapshot>();
        if (battle?.EnemyGroup == null)
        {
            return enemies;
        }

        try
        {
            int idx = 0;
            foreach (EnemyUnit e in battle.EnemyGroup)
            {
                if (e == null)
                {
                    idx++;
                    continue;
                }

                enemies.Add(new EnemyStateSnapshot
                {
                    EnemyId = e.Id,
                    EnemyName = e.Name,
                    EnemyType = e.GetType().Name,
                    Health = e.Hp,
                    MaxHealth = e.MaxHp,
                    Block = e.Block,
                    Shield = e.Shield,
                    StatusEffects = CaptureStatusEffects(e),
                    Intention = CaptureEnemyIntention(e),
                    Index = idx,
                    IsAlive = e.IsAlive,
                });

                idx++;
            }
        }
        catch
        {

        }

        return enemies;
    }

    private static IntentionSnapshot CaptureEnemyIntention(EnemyUnit enemy)
    {
        try
        {
            if (enemy?.Intentions == null)
            {
                return new IntentionSnapshot();
            }

            var i = enemy.Intentions.FirstOrDefault(x => x != null);
            if (i == null)
            {
                return new IntentionSnapshot();
            }

            return new IntentionSnapshot
            {
                IntentionType = i.Type.ToString(),
                IntentionName = i.Name ?? string.Empty,
                Description = i.Description ?? string.Empty,
                Value = 0,
            };
        }
        catch
        {
            return new IntentionSnapshot();
        }
    }

    #endregion
}
