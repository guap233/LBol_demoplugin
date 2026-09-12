using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using LBoL.Base;
using LBoL.Base.Extensions;
using LBoL.Core;
using LBoL.Core.Battle;
using LBoL.Core.Battle.BattleActions;
using LBoL.Core.Cards;
using LBoL.Core.StatusEffects;
using LBoL.Core.Units;
using LBoL.Presentation.UI.Panels;
using LBoL.Presentation.Units;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Patch.Network;

public static partial class RemoteCardUsePatch
{
    #region Execution (replay + resolved state)

    private static void ApplyRemoteResolvedStateToView(string playerId, JsonElement root)
    {
        if (!OtherPlayersOverlayPatch.TryGetRemoteCharacterUnitView(playerId, out UnitView view))
        {
            return;
        }

        Unit unit = view.Unit;
        if (unit == null)
        {
            return;
        }

        try
        {
            int? maxHp = GetInt(root, "MaxHp");
            int? hp = GetInt(root, "Hp");
            int? block = GetInt(root, "Block");
            int? shield = GetInt(root, "Shield");

            if (maxHp != null) TrySetProperty(unit, "MaxHp", maxHp.Value);
            if (hp != null) TrySetProperty(unit, "Hp", hp.Value);
            if (block != null) TrySetProperty(unit, "Block", block.Value);
            if (shield != null) TrySetProperty(unit, "Shield", shield.Value);
        }
        catch
        {

        }

        try
        {
            if (root.TryGetProperty("StatusEffects", out JsonElement effectsEl) && effectsEl.ValueKind == JsonValueKind.Array)
            {
                ReplaceStatusEffects(unit, effectsEl);
            }
        }
        catch
        {

        }

        try
        {
            unit.NotifyChanged();
        }
        catch
        {

        }

        try
        {
            if (root.TryGetProperty("Effects", out JsonElement effectsEl) && effectsEl.ValueKind == JsonValueKind.Object)
            {
                bool hasDamage = GetBool(effectsEl, "HasDamage") ?? false;
                bool hasStatus = GetBool(effectsEl, "HasStatus") ?? false;
                bool hasHeal = GetBool(effectsEl, "HasHeal") ?? false;

                if (hasDamage)
                {
                    view.PlayAnimation("hit");
                }
                else if (hasStatus || hasHeal)
                {
                    view.PlayAnimation("spell");
                }
            }
        }
        catch
        {

        }
    }

    private static void ReplaceStatusEffects(Unit owner, JsonElement effectsEl)
    {
        try
        {
            OrderedList<StatusEffect> list = Traverse.Create(owner).Field("_statusEffects").GetValue<OrderedList<StatusEffect>>();
            list?.Clear();

            foreach (JsonElement item in effectsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string effectId = GetString(item, "Id");
                if (string.IsNullOrWhiteSpace(effectId))
                {
                    continue;
                }

                StatusEffect effect = Library.TryCreateStatusEffect(effectId);
                if (effect == null)
                {
                    continue;
                }

                bool hasLevel = GetBool(item, "HasLevel") ?? false;
                bool hasDuration = GetBool(item, "HasDuration") ?? false;
                bool hasCount = GetBool(item, "HasCount") ?? false;

                int level = GetInt(item, "Level") ?? 0;
                int duration = GetInt(item, "Duration") ?? 0;
                int count = GetInt(item, "Count") ?? 0;
                int limit = GetInt(item, "Limit") ?? 0;

                bool autoDecreasing = GetBool(item, "IsAutoDecreasing") ?? true;
                TrySetProperty(effect, "IsAutoDecreasing", autoDecreasing);

                if (hasLevel)
                {
                    TryCall(effect, "SetInitLevel", level);
                }

                if (hasDuration)
                {
                    TryCall(effect, "SetInitDuration", duration);
                }

                if (hasCount)
                {
                    TryCall(effect, "SetInitCount", count);
                }

                if (limit > 0)
                {
                    TrySetProperty(effect, "Limit", limit);
                }

                AddStatusEffectNoStack(owner, effect);
            }
        }
        catch
        {

        }
    }

    private static void TryExecuteRemoteCardUse(JsonElement root)
    {
        try
        {
            GameRunController run = GameStateUtils.GetCurrentGameRun();
            BattleController battle = run?.Battle;
            if (battle == null || battle.Player == null)
            {
                return;
            }

            if (!TryGetActionSourceCard(root, battle, out Card actionSourceCard))
            {
                actionSourceCard = null;
            }

            PlayerUnit caster = TryCreateRemoteCaster(root, battle) ?? battle.Player;

            Unit targetUnit = ResolveTargetUnit(root, battle);

            using (EnterRemotePipelineScope())
            {
                List<BattleAction> actions = BuildReplayActions(root, battle, caster, targetUnit);
                if (actions.Count == 0)
                {
                    Plugin.Logger?.LogWarning("[RemoteCardUse] BuildReplayActions 返回空列表，跳过执行");
                    return;
                }

                Plugin.Logger?.LogInfo($"[RemoteCardUse] 准备执行: caster={(caster == null ? "null" : caster.GetType().Name)}, target={(targetUnit == null ? "null" : targetUnit.GetType().Name)}, actions={actions.Count}, card={(actionSourceCard == null ? "null" : actionSourceCard.Id)}");
                InvokeBattleReact(battle, actions, actionSourceCard, () =>
                {
                    TryBroadcastResolvedState(root, battle);
                });
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[RemoteCardUse] Execute failed: {ex.Message}");
        }
    }

    private sealed class CallbackAction : LBoL.Core.Battle.SimpleAction
    {
        private readonly Action _callback;
        public CallbackAction(Action callback) => _callback = callback;
        protected override void ResolvePhase() => _callback?.Invoke();
    }

    private static Unit ResolveTargetUnit(JsonElement root, BattleController battle)
    {
        string targetUnitKind = NetworkEventHelper.GetString(root, "TargetUnitKind");
        if (!string.Equals(targetUnitKind, "Enemy", StringComparison.OrdinalIgnoreCase))
        {
            return battle.Player;
        }

        string targetId = NetworkEventHelper.GetString(root, "TargetPlayerId");
        if (string.IsNullOrWhiteSpace(targetId) || battle.EnemyGroup == null)
        {
            return battle.Player;
        }

        foreach (EnemyUnit enemy in battle.EnemyGroup)
        {
            if (enemy != null && !string.IsNullOrWhiteSpace(enemy.Id) &&
                string.Equals(enemy.Id, targetId, StringComparison.Ordinal))
            {
                return enemy;
            }
        }

        if (int.TryParse(targetId, out int rootIndex))
        {
            EnemyUnit byIndex = battle.GetEnemyByRootIndex(rootIndex);
            if (byIndex != null)
            {
                return byIndex;
            }
        }

        Plugin.Logger?.LogWarning($"[RemoteCardUse] Enemy target not found: {targetId}, fallback to battle.Player.");
        return battle.Player;
    }

    private static void InvokeBattleReact(BattleController battle, List<BattleAction> actions, Card actionSourceCard, Action onComplete = null)
    {
        if (battle == null || actions == null || actions.Count == 0)
        {
            return;
        }

        try
        {

            string recordPrefix = "RemoteCard";
            int i = 0;
            foreach (BattleAction action in actions)
            {
                if (action == null)
                {
                    continue;
                }

                action.SetSource(actionSourceCard).SetCause(ActionCause.Card);

                string recordName = $"{recordPrefix}:{action.GetType().Name}:{i}";
                battle.RequestDebugAction(action, recordName);
                i++;
            }

            if (onComplete != null)
            {
                battle.RequestDebugAction(new CallbackAction(onComplete), $"{recordPrefix}:ResolvedCallback");
            }

            Plugin.Logger?.LogInfo($"[RemoteCardUse] 已入队 {i} 个 debug action 等待战斗协程执行");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[RemoteCardUse] RequestDebugAction enqueue failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void TryBroadcastResolvedState(JsonElement originalRoot, BattleController battle)
    {
        try
        {
            if (battle?.Player == null)
            {
                return;
            }

            string requestId = GetString(originalRoot, "RequestId");
            long resolveSeq = Interlocked.Increment(ref _resolvedSeq);

            string selfId;
            lock (_syncLock)
            {
                selfId = _selfPlayerId;
            }

            if (string.IsNullOrWhiteSpace(selfId))
            {
                return;
            }

            if (!IsConnected(out INetworkClient client))
            {
                return;
            }

            bool hasDamage = false;
            bool hasHeal = false;
            bool hasStatus = false;
            try
            {
                if (originalRoot.TryGetProperty("Actions", out JsonElement actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in actionsEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string kind = GetString(item, "Kind");
                        if (kind == "Damage")
                        {
                            hasDamage = true;
                        }
                        else if (kind == "Heal")
                        {
                            hasHeal = true;
                        }
                        else if (kind == "ApplyStatusEffect")
                        {
                            hasStatus = true;
                        }
                    }
                }
            }
            catch
            {

            }

            var payload = new
            {
                Timestamp = DateTime.Now.Ticks,
                RequestId = requestId,
                ResolveSeq = resolveSeq,
                EventType = NetworkMessageTypes.OnRemoteCardResolved,
                TargetPlayerId = selfId,
                Hp = battle.Player.Hp,
                MaxHp = battle.Player.MaxHp,
                Block = battle.Player.Block,
                Shield = battle.Player.Shield,
                Status = battle.Player.Status.ToString(),
                StatusEffects = SnapshotStatusEffects(battle.Player),
                Effects = new
                {
                    HasDamage = hasDamage,
                    HasHeal = hasHeal,
                    HasStatus = hasStatus
                }
            };

            client.SendGameEventData(NetworkMessageTypes.OnRemoteCardResolved, payload);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[RemoteCardUse] Resolve send failed: {ex.Message}");
        }
    }

    private static bool TryGetActionSourceCard(JsonElement root, BattleController battle, out Card card)
    {
        card = null;
        try
        {
            if (!root.TryGetProperty("Card", out JsonElement cardEl) || cardEl.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string cardId = GetString(cardEl, "CardId");
            if (string.IsNullOrWhiteSpace(cardId))
            {
                return false;
            }

            bool upgraded = GetBool(cardEl, "IsUpgraded") ?? false;
            int? upgradeCounter = GetInt(cardEl, "UpgradeCounter");

            Card created = Library.TryCreateCard(cardId, upgraded, upgradeCounter);
            if (created == null)
            {
                return false;
            }

            TrySetGameRun(created, battle.GameRun);
            TryEnterBattle(created, battle);

            try
            {
                created.PendingTarget = battle.Player;
            }
            catch
            {

            }

            card = created;
            return true;
        }
        catch
        {
            card = null;
            return false;
        }
    }

    private static PlayerUnit TryCreateRemoteCaster(JsonElement root, BattleController battle)
    {
        try
        {
            string senderId = NetworkEventHelper.GetString(root, "SenderPlayerId");
            if (!string.IsNullOrWhiteSpace(senderId))
            {
                if (OtherPlayersOverlayPatch.TryGetRemoteCharacterUnitView(senderId, out UnitView casterView) && casterView?.Unit is PlayerUnit playerUnit)
                {
                    if (root.TryGetProperty("SenderStatusEffects", out JsonElement effectsEl2) && effectsEl2.ValueKind == JsonValueKind.Array)
                    {
                        ApplyStatusEffectSnapshot(playerUnit, battle, effectsEl2);
                    }
                    return playerUnit;
                }
            }

            string characterId = GetString(root, "SenderCharacterId");
            if (string.IsNullOrWhiteSpace(characterId))
            {
                characterId = battle.Player.Id;
            }

            PlayerUnit unit = Library.TryCreatePlayerUnit(characterId);
            if (unit == null)
            {
                return null;
            }

            TrySetGameRun(unit, battle.GameRun);
            TryEnterBattle(unit, battle);

            if (root.TryGetProperty("SenderStatusEffects", out JsonElement effectsEl) && effectsEl.ValueKind == JsonValueKind.Array)
            {
                ApplyStatusEffectSnapshot(unit, battle, effectsEl);
            }

            return unit;
        }
        catch
        {
            return null;
        }
    }

    internal static List<BattleAction> BuildReplayActions(JsonElement root, BattleController battle, PlayerUnit caster, Unit targetUnit)
    {
        List<BattleAction> list = new List<BattleAction>();
        try
        {
            if (!root.TryGetProperty("Actions", out JsonElement actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (JsonElement item in actionsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string kind = GetString(item, "Kind");
                if (string.IsNullOrWhiteSpace(kind))
                {
                    continue;
                }

                switch (kind)
                {
                    case ActionBlueprintConstants.KindDamage:
                        TryAddReplayDamage(list, item, caster, targetUnit, battle);
                        break;
                    case ActionBlueprintConstants.KindBlockShield:
                        TryAddReplayBlockShield(list, item, caster, targetUnit, battle);
                        break;
                    case ActionBlueprintConstants.KindHeal:
                        TryAddReplayHeal(list, item, caster, targetUnit, battle);
                        break;
                    case ActionBlueprintConstants.KindApplyStatusEffect:
                        TryAddReplayStatus(list, item, targetUnit, battle);
                        break;
                    case ActionBlueprintConstants.KindPerformAction:
                        var pa = ReconstructPerformAction(item, battle);
                        if (pa != null)
                        {
                            list.Add(pa);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] BuildReplayActions error: {ex.Message}");
        }

        return list;
    }

    public const float MaxPlausibleDamage = 500f;
    public const int MaxPlausibleHeal = 100;
    public const int MaxPlausibleStatusLevel = 999;
    public const int MaxPlausibleStatusDuration = 99;
    public const int MaxPlausibleStatusCount = 999;
    public const int MaxPlausibleStatusLimit = 999;

    private static void TryAddReplayDamage(List<BattleAction> list, JsonElement item, Unit caster, Unit target, BattleController battle)
    {
        try
        {
            float? dmg = GetFloat(item, "Damage") ?? GetFloat(item, "Amount");
            if (dmg == null || dmg.Value < 0f || dmg.Value > MaxPlausibleDamage)
            {
                if (dmg != null && (dmg.Value > MaxPlausibleDamage || dmg.Value < 0f))
                {
                    Plugin.Logger?.LogWarning($"[RemoteCardUse] 拒绝执行异常伤害值: {dmg.Value} (上限: {MaxPlausibleDamage})");
                }
                return;
            }

            string typeStr = GetString(item, "DamageType");
            if (!Enum.TryParse(typeStr, out DamageType damageType))
            {
                return;
            }

            bool isAccuracy = GetBool(item, "IsAccuracy") ?? false;
            bool dontBreakPerfect = GetBool(item, "DontBreakPerfect") ?? false;

            DamageInfo info = damageType switch
            {
                DamageType.Attack => DamageInfo.Attack(dmg.Value, isAccuracy),
                DamageType.HpLose => DamageInfo.HpLose(dmg.Value, dontBreakPerfect),
                DamageType.Reaction => DamageInfo.Reaction(dmg.Value, dontBreakPerfect),
                _ => DamageInfo.Attack(dmg.Value, isAccuracy)
            };

            string gunName = GetString(item, "GunName") ?? "Instant";
            string gunTypeStr = GetString(item, "GunType");
            GunType gunType = Enum.TryParse(gunTypeStr, out GunType parsed) ? parsed : GunType.Single;

            Unit actionCaster = ResolveUnit(item.TryGetProperty("Caster", out JsonElement cEl) ? cEl : default, battle) ?? caster;
            var targets = new List<Unit>();
            if (item.TryGetProperty("Targets", out JsonElement targetsEl) && targetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tEl in targetsEl.EnumerateArray())
                {
                    Unit t = ResolveUnit(tEl, battle);
                    if (t != null)
                    {
                        targets.Add(t);
                    }
                }
            }
            if (targets.Count == 0)
            {
                targets.Add(target);
            }

            list.Add(new DamageAction(actionCaster, targets, info, gunName, gunType));
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] TryAddReplayDamage error: {ex.Message}");
        }
    }

    private static void TryAddReplayBlockShield(List<BattleAction> list, JsonElement item, Unit caster, Unit target, BattleController battle)
    {
        try
        {
            float? block = GetFloat(item, "Block");
            float? shield = GetFloat(item, "Shield");
            if (block == null && shield == null)
            {
                return;
            }

            string typeStr = GetString(item, "Type");
            BlockShieldType type = Enum.TryParse(typeStr, out BlockShieldType parsed) ? parsed : BlockShieldType.Normal;
            bool cast = GetBool(item, "Cast") ?? true;

            Unit actionSource = ResolveUnit(item.TryGetProperty("Source", out JsonElement sEl) ? sEl : default, battle) ?? caster;
            Unit actionTarget = ResolveUnit(item.TryGetProperty("Target", out JsonElement tEl) ? tEl : default, battle) ?? target ?? caster;

            int b = Math.Max(0, (int)Math.Round(block ?? 0f));
            int s = Math.Max(0, (int)Math.Round(shield ?? 0f));
            if (b > (int)MaxPlausibleDamage || s > (int)MaxPlausibleDamage)
            {
                Plugin.Logger?.LogWarning($"[RemoteCardUse] 拒绝执行异常格挡/护盾值: b={b}, s={s} (上限: {MaxPlausibleDamage})");
                return;
            }
            list.Add(new CastBlockShieldAction(actionSource, actionTarget, b, s, type, cast));
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] TryAddReplayBlockShield error: {ex.Message}");
        }
    }

    private static void TryAddReplayHeal(List<BattleAction> list, JsonElement item, Unit caster, Unit target, BattleController battle)
    {
        try
        {
            int? amount = GetInt(item, "Amount");
            if (amount == null)
            {
                float? f = GetFloat(item, "Amount");
                if (f != null)
                {
                    amount = (int)Math.Round(f.Value);
                }
            }

            if (amount == null || amount.Value < 0 || amount.Value > MaxPlausibleHeal)
            {
                if (amount != null && (amount.Value > MaxPlausibleHeal || amount.Value < 0))
                {
                    Plugin.Logger?.LogWarning($"[RemoteCardUse] 拒绝执行异常治疗值: {amount.Value} (上限: {MaxPlausibleHeal})");
                }
                return;
            }

            string typeStr = GetString(item, "HealType");
            HealType healType = Enum.TryParse(typeStr, out HealType parsed) ? parsed : HealType.Normal;

            float waitTime = GetFloat(item, "WaitTime") ?? 0.2f;

            Unit actionCaster = ResolveUnit(item.TryGetProperty("Caster", out JsonElement cEl) ? cEl : default, battle) ?? caster;
            Unit actionTarget = ResolveUnit(item.TryGetProperty("Target", out JsonElement tEl) ? tEl : default, battle) ?? target ?? caster;

            list.Add(new HealAction(actionCaster, actionTarget, amount.Value, healType, waitTime));
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] TryAddReplayHeal error: {ex.Message}");
        }
    }

    private static void TryAddReplayStatus(List<BattleAction> list, JsonElement item, Unit target, BattleController battle)
    {
        try
        {
            string effectId = GetString(item, "EffectId");
            if (string.IsNullOrWhiteSpace(effectId))
            {
                return;
            }

            StatusEffect effect = Library.TryCreateStatusEffect(effectId);
            if (effect == null)
            {
                return;
            }

            int? level = GetInt(item, "Level");
            int? duration = GetInt(item, "Duration");
            int? count = GetInt(item, "Count");
            int? limit = GetInt(item, "Limit");

            if (level != null && (level.Value < 0 || level.Value > MaxPlausibleStatusLevel))
            {
                Plugin.Logger?.LogWarning($"[RemoteCardUse] 拒绝执行异常状态层数: {level.Value} (上限: {MaxPlausibleStatusLevel})");
                return;
            }
            if (duration != null && (duration.Value < 0 || duration.Value > MaxPlausibleStatusDuration))
            {
                Plugin.Logger?.LogWarning($"[RemoteCardUse] 拒绝执行异常状态持续时间: {duration.Value} (上限: {MaxPlausibleStatusDuration})");
                return;
            }
            if (count != null && (count.Value < 0 || count.Value > MaxPlausibleStatusCount))
            {
                Plugin.Logger?.LogWarning($"[RemoteCardUse] 拒绝执行异常状态计数: {count.Value} (上限: {MaxPlausibleStatusCount})");
                return;
            }
            if (limit != null && (limit.Value < 0 || limit.Value > MaxPlausibleStatusLimit))
            {
                Plugin.Logger?.LogWarning($"[RemoteCardUse] 拒绝执行异常状态上限: {limit.Value} (上限: {MaxPlausibleStatusLimit})");
                return;
            }

            float waitTime = GetFloat(item, "WaitTime") ?? 0f;
            bool startAutoDecreasing = GetBool(item, "StartAutoDecreasing") ?? true;

            Unit actionTarget = ResolveUnit(item.TryGetProperty("Target", out JsonElement tEl) ? tEl : default, battle) ?? target;

            list.Add(new ApplyStatusEffectAction(effect.GetType(), actionTarget, level, duration, count, limit, waitTime, startAutoDecreasing));
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] TryAddReplayStatus error: {ex.Message}");
        }
    }

    private static void ApplyStatusEffectSnapshot(Unit owner, BattleController battle, JsonElement effectsEl)
    {
        try
        {
            foreach (JsonElement item in effectsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string effectId = GetString(item, "Id");
                if (string.IsNullOrWhiteSpace(effectId))
                {
                    continue;
                }

                StatusEffect effect = Library.TryCreateStatusEffect(effectId);
                if (effect == null)
                {
                    continue;
                }

                TrySetGameRun(effect, battle.GameRun);

                bool hasLevel = GetBool(item, "HasLevel") ?? false;
                bool hasDuration = GetBool(item, "HasDuration") ?? false;
                bool hasCount = GetBool(item, "HasCount") ?? false;

                int level = GetInt(item, "Level") ?? 0;
                int duration = GetInt(item, "Duration") ?? 0;
                int count = GetInt(item, "Count") ?? 0;
                int limit = GetInt(item, "Limit") ?? 0;

                if (level < 0 || level > MaxPlausibleStatusLevel ||
                    duration < 0 || duration > MaxPlausibleStatusDuration ||
                    count < 0 || count > MaxPlausibleStatusCount ||
                    limit < 0 || limit > MaxPlausibleStatusLimit)
                {
                    Plugin.Logger?.LogWarning($"[RemoteCardUse] 状态效果快照越界丢弃: effect={effectId}, level={level}, dur={duration}, cnt={count}, lim={limit}");
                    continue;
                }

                bool autoDecreasing = GetBool(item, "IsAutoDecreasing") ?? true;
                TrySetProperty(effect, "IsAutoDecreasing", autoDecreasing);

                if (hasLevel)
                {
                    TryCall(effect, "SetInitLevel", level);
                }

                if (hasDuration)
                {
                    TryCall(effect, "SetInitDuration", duration);
                }

                if (hasCount)
                {
                    TryCall(effect, "SetInitCount", count);
                }

                if (limit > 0)
                {
                    TrySetProperty(effect, "Limit", limit);
                }

                AddStatusEffectNoStack(owner, effect);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] ApplyStatusEffectSnapshot error: {ex.Message}");
        }
    }

    private static void AddStatusEffectNoStack(Unit owner, StatusEffect effect)
    {
        try
        {
            if (owner == null || effect == null)
            {
                return;
            }

            if (owner.HasStatusEffect(effect.GetType()))
            {
                return;
            }

            TrySetProperty(effect, "Owner", owner);
            TryCall(effect, "TriggerAdding", owner);
            TryCall(effect, "ClampMax");

            OrderedList<StatusEffect> list = Traverse.Create(owner).Field("_statusEffects").GetValue<OrderedList<StatusEffect>>();
            list?.Add(effect);

            TryCall(effect, "TriggerAdded", owner);
        }
        catch
        {

        }
    }

    private static void TrySetGameRun(GameEntity entity, GameRunController gameRun)
    {
        try
        {
            Traverse.Create(entity).Property("GameRun").SetValue(gameRun);
        }
        catch
        {

        }
    }

    private static void TryEnterBattle(object entity, BattleController battle)
    {
        try
        {
            Traverse.Create(entity).Method("EnterBattle", battle).GetValue();
        }
        catch
        {

        }
    }

    private static void TryCall(object instance, string methodName, params object[] args)
    {
        try
        {
            Traverse.Create(instance).Method(methodName, args).GetValue();
        }
        catch
        {

        }
    }

    private static void TrySetProperty(object instance, string propertyName, object value)
    {
        try
        {
            Traverse.Create(instance).Property(propertyName).SetValue(value);
        }
        catch
        {

        }
    }

    internal static Unit ResolveUnit(JsonElement elem, BattleController battle, string defaultPlayerId = null)
    {
        if (elem.ValueKind != JsonValueKind.Object) return null;
        string kind = GetString(elem, "Kind");
        if (kind == "Player")
        {
            string playerId = GetString(elem, "PlayerId");
            if (string.IsNullOrWhiteSpace(playerId) || playerId == "__local__")
            {
                if (!string.IsNullOrWhiteSpace(defaultPlayerId))
                {
                    playerId = defaultPlayerId;
                }
            }
            if (string.IsNullOrWhiteSpace(playerId)) return battle?.Player;

            string selfId;
            lock (_syncLock)
            {
                selfId = _selfPlayerId;
            }
            selfId ??= NetworkIdentityTracker.GetSelfPlayerId();
            if (string.Equals(playerId, selfId, StringComparison.Ordinal) || playerId == "__local__")
            {
                return battle?.Player;
            }

            if (OtherPlayersOverlayPatch.TryGetRemoteCharacterUnitView(playerId, out UnitView view) && view?.Unit != null)
            {
                return view.Unit;
            }

            return battle?.Player;
        }
        else if (kind == "Enemy")
        {
            string enemyId = GetString(elem, "EnemyId");
            int? rootIndex = GetInt(elem, "RootIndex");
            if (battle?.EnemyGroup == null) return null;

            foreach (EnemyUnit enemy in battle.EnemyGroup)
            {
                if (enemy != null && string.Equals(enemy.Id, enemyId, StringComparison.Ordinal))
                {
                    if (rootIndex == null || enemy.RootIndex == rootIndex.Value)
                    {
                        return enemy;
                    }
                }
            }
            if (rootIndex != null)
            {
                EnemyUnit byIndex = battle?.GetEnemyByRootIndex(rootIndex.Value);
                if (byIndex != null) return byIndex;
            }
            return null;
        }
        return null;
    }

    private static PerformAction ReconstructPerformAction(JsonElement elem, BattleController battle)
    {
        string type = GetString(elem, "Type");
        switch (type)
        {
            case "ViewCard":
                {
                    string cardId = GetString(elem, "CardId");
                    string zoneStr = GetString(elem, "Zone");
                    Card card = Library.TryCreateCard(cardId, false, null);
                    if (card != null && Enum.TryParse(zoneStr, out CardZone zone))
                    {
                        TrySetGameRun(card, battle.GameRun);
                        TryEnterBattle(card, battle);
                        return PerformAction.ViewCard(card);
                    }
                    break;
                }
            case "Gun":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    Unit target = ResolveUnit(elem.GetProperty("Target"), battle);
                    string gunId = GetString(elem, "GunId");
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    if (source != null && target != null)
                    {
                        return PerformAction.Gun(source, target, gunId, waitTime);
                    }
                    break;
                }
            case "Doll":
                {
                    Unit target = ResolveUnit(elem.GetProperty("Target"), battle);
                    string gunId = GetString(elem, "GunId");
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    string debugString = GetString(elem, "DebugString") ?? "";
                    if (target != null)
                    {
                        return PerformAction.Doll(null, target, gunId, waitTime, debugString);
                    }
                    break;
                }
            case "Animation":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    string animationName = GetString(elem, "AnimationName");
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    string sfxId = GetString(elem, "SfxId");
                    float sfxDelay = GetFloat(elem, "SfxDelay") ?? 0f;
                    int shakeLevel = GetInt(elem, "ShakeLevel") ?? -1;
                    if (source != null)
                    {
                        return PerformAction.Animation(source, animationName, waitTime, sfxId, sfxDelay, shakeLevel);
                    }
                    break;
                }
            case "Sfx":
                {
                    string id = GetString(elem, "Id");
                    float delay = GetFloat(elem, "Delay") ?? 0f;
                    return PerformAction.Sfx(id, delay);
                }
            case "UiSound":
                {
                    string id = GetString(elem, "Id");
                    return PerformAction.UiSound(id);
                }
            case "Chat":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    string content = GetString(elem, "Content");
                    float chatTime = GetFloat(elem, "ChatTime") ?? 2f;
                    float delay = GetFloat(elem, "Delay") ?? 0f;
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    bool talk = GetBool(elem, "Talk") ?? true;
                    if (source != null)
                    {
                        return PerformAction.Chat(source, content, chatTime, delay, waitTime, talk);
                    }
                    break;
                }
            case "Spell":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    string spellName = GetString(elem, "SpellName");
                    if (source != null)
                    {
                        return PerformAction.Spell(source, spellName);
                    }
                    break;
                }
            case "Effect":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    string effectName = GetString(elem, "EffectName");
                    float delay = GetFloat(elem, "Delay") ?? 0f;
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    string sfxId = GetString(elem, "SfxId");
                    float sfxDelay = GetFloat(elem, "SfxDelay") ?? 0f;
                    string effectTypeStr = GetString(elem, "EffectType");
                    if (source != null && Enum.TryParse(effectTypeStr, out PerformAction.EffectBehavior effectType))
                    {
                        return PerformAction.Effect(source, effectName, delay, sfxId, sfxDelay, effectType, waitTime);
                    }
                    break;
                }
            case "EffectMessage":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    string effectName = GetString(elem, "EffectName");
                    string message = GetString(elem, "Message");
                    if (source != null)
                    {
                        return PerformAction.EffectMessage(source, effectName, message, null);
                    }
                    break;
                }
            case "SePop":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    string popContent = GetString(elem, "PopContent");
                    if (source != null)
                    {
                        return PerformAction.SePop(source, popContent);
                    }
                    break;
                }
            case "SummonFriend":
                {
                    string cardId = GetString(elem, "CardId");
                    Card card = Library.TryCreateCard(cardId, false, null);
                    if (card != null)
                    {
                        TrySetGameRun(card, battle.GameRun);
                        TryEnterBattle(card, battle);
                        return PerformAction.SummonFriend(card);
                    }
                    break;
                }
            case "TransformModel":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    string modelName = GetString(elem, "ModelName");
                    if (source != null)
                    {
                        return PerformAction.TransformModel(source, modelName);
                    }
                    break;
                }
            case "DeathAnimation":
                {
                    Unit source = ResolveUnit(elem.GetProperty("Source"), battle);
                    if (source != null)
                    {
                        return PerformAction.DeathAnimation(source);
                    }
                    break;
                }
            case "Wait":
                {
                    float time = GetFloat(elem, "Time") ?? 0f;
                    bool unscale = GetBool(elem, "Unscale") ?? false;
                    return PerformAction.Wait(time, unscale);
                }
        }
        return null;
    }

    internal static bool IsSelfPlayer(string playerId, string defaultPlayerId = null)
    {
        if (string.IsNullOrWhiteSpace(playerId) || playerId == "__local__")
        {
            if (!string.IsNullOrWhiteSpace(defaultPlayerId))
            {
                playerId = defaultPlayerId;
            }
            else
            {
                return true;
            }
        }
        string selfId;
        lock (_syncLock)
        {
            selfId = _selfPlayerId;
        }
        selfId ??= NetworkIdentityTracker.GetSelfPlayerId();
        return !string.IsNullOrWhiteSpace(selfId) && string.Equals(playerId, selfId, StringComparison.Ordinal);
    }

    internal static bool SafeIsNull(UnitView view)
    {
        if (ReferenceEquals(view, null))
        {
            return true;
        }
        try
        {
            return view == null;
        }
        catch
        {
            return ReferenceEquals(view, null);
        }
    }

    private static UnitView SafeGetLocalPlayerUnitView()
    {
        try
        {
            return Singleton<GameDirector>.Instance?.PlayerUnitView;
        }
        catch
        {
            return null;
        }
    }

    internal static object SafeWaitForSeconds(float seconds)
    {
        try
        {
            return new UnityEngine.WaitForSeconds(seconds);
        }
        catch (Exception)
        {
            return seconds;
        }
    }

    internal static UnitView ResolveUnitView(Unit unit, JsonElement elem, BattleController battle, string defaultPlayerId = null)
    {
        try
        {
            if (unit != null)
            {
                UnitView localView = SafeGetLocalPlayerUnitView();
                if (localView != null && unit == localView.Unit)
                {
                    if (string.IsNullOrWhiteSpace(defaultPlayerId) || IsSelfPlayer(defaultPlayerId))
                    {
                        return localView;
                    }
                }

                if (unit is EnemyUnit eu)
                {
                    UnitView ev = GameDirector.GetEnemy(eu);
                    if (ev != null) return ev;
                }

                foreach (UnitView remote in OtherPlayersOverlayPatch.SnapshotRemoteCharacterUnitViews())
                {
                    if (remote != null && remote.Unit == unit)
                    {
                        return remote;
                    }
                }
            }

            if (elem.ValueKind == JsonValueKind.Object)
            {
                string kind = GetString(elem, ActionBlueprintConstants.KeyKind);
                if (kind == ActionBlueprintConstants.UnitPlayer)
                {
                    string playerId = GetString(elem, ActionBlueprintConstants.KeyPlayerId);
                    if (string.IsNullOrWhiteSpace(playerId) || playerId == "__local__")
                    {
                        if (!string.IsNullOrWhiteSpace(defaultPlayerId))
                        {
                            playerId = defaultPlayerId;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(playerId))
                    {
                        if (IsSelfPlayer(playerId))
                        {
                            return SafeGetLocalPlayerUnitView();
                        }

                        if (OtherPlayersOverlayPatch.TryGetRemoteCharacterUnitView(playerId, out UnitView remoteView) && remoteView != null)
                        {
                            return remoteView;
                        }

                        // 明确为远端玩家且未找到视图时，严格返回 null，绝不误回退至本地玩家
                        return null;
                    }
                }
                else if (kind == ActionBlueprintConstants.UnitEnemy)
                {
                    string enemyId = GetString(elem, ActionBlueprintConstants.KeyEnemyId);
                    int? rootIndex = GetInt(elem, ActionBlueprintConstants.KeyRootIndex);
                    if (rootIndex != null)
                    {
                        UnitView ev = GameDirector.GetEnemyByRootIndex(rootIndex.Value);
                        if (ev != null) return ev;
                    }
                    if (battle?.EnemyGroup != null && !string.IsNullOrWhiteSpace(enemyId))
                    {
                        foreach (EnemyUnit e in battle.EnemyGroup)
                        {
                            if (e != null && e.Id == enemyId)
                            {
                                UnitView ev = GameDirector.GetEnemy(e);
                                if (ev != null) return ev;
                            }
                        }
                    }
                    return null;
                }
            }

            if (!string.IsNullOrWhiteSpace(defaultPlayerId))
            {
                if (IsSelfPlayer(defaultPlayerId))
                {
                    return SafeGetLocalPlayerUnitView();
                }

                if (OtherPlayersOverlayPatch.TryGetRemoteCharacterUnitView(defaultPlayerId, out UnitView remoteView) && remoteView != null)
                {
                    return remoteView;
                }

                // 明确为远端玩家且未找到视图时，严格返回 null
                return null;
            }

            return SafeGetLocalPlayerUnitView();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] ResolveUnitView failed: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(defaultPlayerId) && !IsSelfPlayer(defaultPlayerId))
            {
                return null;
            }
            return SafeGetLocalPlayerUnitView();
        }
    }

    private static UnitView ResolveTargetUnitView(Unit targetUnit, JsonElement targetElem, BattleController battle, UnitView sourceView)
    {
        try
        {
            UnitView tv = ResolveUnitView(targetUnit, targetElem, battle, null);
            if (tv != null && tv != sourceView)
            {
                return tv;
            }

            if (GameDirector.Enemies != null)
            {
                foreach (UnitView enemyView in GameDirector.Enemies)
                {
                    if (enemyView != null && enemyView.Unit != null && enemyView.Unit.IsAlive && enemyView.gameObject.activeInHierarchy)
                    {
                        return enemyView;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] ResolveTargetUnitView failed: {ex.Message}");
            return null;
        }
    }

    internal static void EnsureUnitIdle(UnitView view, bool forceReset = false)
    {
        if (SafeIsNull(view))
        {
            return;
        }

        try
        {
            var trav = Traverse.Create(view);

            // 检查内部射击状态（_status 与 _gunInShooting）
            int statusInt = 0;
            try
            {
                object statusObj = trav.Field("_status").GetValue();
                if (statusObj != null)
                {
                    statusInt = Convert.ToInt32(statusObj);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[RemoteCardUse] Read _status failed: {ex.Message}");
            }

            bool hasGun = false;
            try
            {
                object gunObj = trav.Field("_gunInShooting").GetValue();
                hasGun = gunObj != null;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[RemoteCardUse] Read _gunInShooting failed: {ex.Message}");
            }

            bool isShooting = statusInt != 0 || hasGun;

            if (isShooting || forceReset)
            {
                // 销毁残留 Shooter 粒子发射器
                try
                {
                    var destroyShooterMethod = trav.Method("DestroyShooter");
                    if (destroyShooterMethod.MethodExists())
                    {
                        destroyShooterMethod.GetValue();
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"[RemoteCardUse] DestroyShooter failed: {ex.Message}");
                }

                // 清理并复位射击相关状态与计时器
                try
                {
                    trav.Field("_gunInShooting").SetValue(null);
                    trav.Field("_complexFirstGun").SetValue(null);
                    trav.Field("_shootCounting").SetValue(false);
                    trav.Field("_shooterCounting").SetValue(false);
                    trav.Field("_shootTime").SetValue(0f);
                    trav.Field("_shooterTime").SetValue(0f);
                    trav.Field("_status").SetValue(0); // ShootStatus.Idle
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"[RemoteCardUse] Reset shoot status fields failed: {ex.Message}");
                }

                // 强制打断射击循环轨道（SpineIdle(false)）
                try
                {
                    bool spineLoaded = false;
                    try
                    {
                        object spineLoadedObj = trav.Property("SpineLoaded").GetValue();
                        if (spineLoadedObj is bool b)
                        {
                            spineLoaded = b;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger?.LogDebug($"[RemoteCardUse] Check SpineLoaded failed: {ex.Message}");
                        spineLoaded = false;
                    }

                    if (spineLoaded)
                    {
                        var spineIdleMethod = trav.Method("SpineIdle", false);
                        if (spineIdleMethod.MethodExists())
                        {
                            spineIdleMethod.GetValue();
                        }
                        else
                        {
                            SafePlayAnimation(view, ActionBlueprintConstants.AnimIdle);
                        }
                    }
                    else
                    {
                        SafePlayAnimation(view, ActionBlueprintConstants.AnimIdle);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"[RemoteCardUse] Force idle animation failed: {ex.Message}");
                }
            }
            else
            {
                // 非射击循环（例如防御、施法、技能、Buff）：
                // 严禁调用 SpineIdle(false) 暴力截断动画！
                // 仅调用 SpineIdle(true)，即 AddAnimation(0, "idle", true, 0f)，确保动作自然播放完毕后平滑回归待机。
                try
                {
                    bool spineLoaded = false;
                    try
                    {
                        object spineLoadedObj = trav.Property("SpineLoaded").GetValue();
                        if (spineLoadedObj is bool b)
                        {
                            spineLoaded = b;
                        }
                    }
                    catch
                    {
                        spineLoaded = false;
                    }

                    if (spineLoaded)
                    {
                        var spineIdleAddMethod = trav.Method("SpineIdle", true);
                        if (spineIdleAddMethod.MethodExists())
                        {
                            spineIdleAddMethod.GetValue();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"[RemoteCardUse] SpineIdle(true) queue fallback: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] EnsureUnitIdle failed: {ex.Message}");
        }
    }

    private static void TrackCasterUnitView(JsonElement actionEl, BattleController battle, string defaultSenderPlayerId, HashSet<UnitView> views)
    {
        if (views == null || actionEl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        try
        {
            if (actionEl.TryGetProperty("Caster", out JsonElement cEl))
            {
                UnitView cv = ResolveUnitView(null, cEl, battle, defaultSenderPlayerId);
                if (cv != null) views.Add(cv);
            }
            if (actionEl.TryGetProperty("Source", out JsonElement sEl))
            {
                UnitView sv = ResolveUnitView(null, sEl, battle, defaultSenderPlayerId);
                if (sv != null) views.Add(sv);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] TrackCasterUnitView failed: {ex.Message}");
        }
    }

    public static System.Collections.IEnumerator PlayVisualsCoroutine(JsonElement actionsEl, BattleController battle, bool skipStateVisuals, string defaultSenderPlayerId = null)
    {
        RemoteCardPlaybackTracker.NotifyCardVisualStarted();
        HashSet<UnitView> affectedViews = new HashSet<UnitView>();
        try
        {
            if (actionsEl.ValueKind != JsonValueKind.Array)
            {
                if (!skipStateVisuals)
                {
                    yield return PlayGenericCastVisual(battle, defaultSenderPlayerId);
                }
                yield break;
            }

            bool playedAnyVisual = false;
            foreach (JsonElement actionEl in actionsEl.EnumerateArray())
            {
                if (actionEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                TrackCasterUnitView(actionEl, battle, defaultSenderPlayerId, affectedViews);

                string kind = GetString(actionEl, ActionBlueprintConstants.KeyKind);
                if (kind == ActionBlueprintConstants.KindPerformAction)
                {
                    playedAnyVisual = true;
                    string type = GetString(actionEl, ActionBlueprintConstants.KeyType);
                    yield return PlayPerformActionVisual(type, actionEl, battle, defaultSenderPlayerId);
                }
                else if (kind == ActionBlueprintConstants.KindDamage && !skipStateVisuals)
                {
                    playedAnyVisual = true;
                    yield return PlayDamageVisual(actionEl, battle, defaultSenderPlayerId);
                }
                else if (kind == ActionBlueprintConstants.KindBlockShield && !skipStateVisuals)
                {
                    playedAnyVisual = true;
                    yield return PlayBlockShieldVisual(actionEl, battle, defaultSenderPlayerId);
                }
                else if (kind == ActionBlueprintConstants.KindHeal && !skipStateVisuals)
                {
                    playedAnyVisual = true;
                    yield return PlayHealVisual(actionEl, battle, defaultSenderPlayerId);
                }
                else if (kind == ActionBlueprintConstants.KindApplyStatusEffect && !skipStateVisuals)
                {
                    playedAnyVisual = true;
                    yield return PlayStatusEffectVisual(actionEl, battle, defaultSenderPlayerId);
                }
            }

            if (!playedAnyVisual && !skipStateVisuals)
            {
                yield return PlayGenericCastVisual(battle, defaultSenderPlayerId);
            }
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(defaultSenderPlayerId) &&
                    OtherPlayersOverlayPatch.TryGetRemoteCharacterUnitView(defaultSenderPlayerId, out UnitView senderView) &&
                    senderView != null)
                {
                    EnsureUnitIdle(senderView);
                }
                else
                {
                    // 仅清理施法者自身的玩家动画，不干扰其他角色或受击目标的动画
                    foreach (UnitView view in affectedViews)
                    {
                        if (view != null && view.Unit is PlayerUnit)
                        {
                            EnsureUnitIdle(view);
                        }
                    }
                }
            }
            finally
            {
                RemoteCardPlaybackTracker.NotifyCardVisualEnded();
            }
        }
    }

    private static System.Collections.IEnumerator PlayPerformActionVisual(string type, JsonElement elem, BattleController battle, string defaultSenderPlayerId = null)
    {
        switch (type)
        {
            case "ViewCard":
                {
                    string cardId = GetString(elem, "CardId");
                    string zoneStr = GetString(elem, "Zone");
                    Card card = Library.TryCreateCard(cardId, false, null);
                    if (card != null && Enum.TryParse(zoneStr, out CardZone zone))
                    {
                        yield return LBoL.Presentation.UI.UiManager.GetPanel<LBoL.Presentation.UI.Panels.PlayBoard>().CardUi.ViewCardFromZone(card, zone);
                    }
                    break;
                }
            case "Gun":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    Unit target = elem.TryGetProperty("Target", out JsonElement tEl) ? ResolveUnit(tEl, battle) : null;
                    string gunId = GetString(elem, "GunId");
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    UnitView targetView = ResolveTargetUnitView(target, tEl, battle, sourceView);
                    if (sourceView != null && !string.IsNullOrEmpty(gunId) && gunId != "Empty")
                    {
                        if (targetView != null)
                        {
                            sourceView.Target = targetView;
                            sourceView.Targets.Clear();
                            sourceView.Targets.Add(targetView);
                            targetView.ComingDamage = DamageInfo.Attack(0f, false);
                        }
                        sourceView.PerformShoot(gunId);
                        yield return new UnityEngine.WaitForSeconds(waitTime);
                    }
                    break;
                }
            case "Doll":
                {
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    yield return new UnityEngine.WaitForSeconds(waitTime);
                    break;
                }
            case "Animation":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    string animationName = GetString(elem, "AnimationName");
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    string sfxId = GetString(elem, "SfxId");
                    float sfxDelay = GetFloat(elem, "SfxDelay") ?? 0f;
                    int shakeLevel = GetInt(elem, "ShakeLevel") ?? -1;

                    if (!string.IsNullOrEmpty(sfxId))
                    {
                        LBoL.Presentation.AudioManager.PlaySfxDelay(sfxId, sfxDelay);
                    }
                    if (shakeLevel >= 0)
                    {
                        GameDirector.Shake(shakeLevel, true);
                    }
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null && !string.IsNullOrEmpty(animationName))
                    {
                        SafePlayAnimation(sourceView, animationName, ActionBlueprintConstants.AnimSpell);
                        float actualWait = waitTime;
                        if ((string.Equals(animationName, "skill", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(animationName, "spell", StringComparison.OrdinalIgnoreCase)) &&
                            actualWait <= 0.2f)
                        {
                            actualWait = 0.4f;
                        }
                        yield return SafeWaitForSeconds(actualWait);
                    }
                    break;
                }
            case "Sfx":
                {
                    string sfxId = GetString(elem, "Id");
                    float delay = GetFloat(elem, "Delay") ?? 0f;
                    if (!string.IsNullOrEmpty(sfxId))
                    {
                        LBoL.Presentation.AudioManager.PlaySfxDelay(sfxId, delay);
                    }
                    break;
                }
            case "UiSound":
                {
                    string sfxId = GetString(elem, "Id");
                    if (!string.IsNullOrEmpty(sfxId))
                    {
                        LBoL.Presentation.AudioManager.PlayUi(sfxId, false);
                    }
                    break;
                }
            case "Chat":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    string content = GetString(elem, "Content");
                    float chatTime = GetFloat(elem, "ChatTime") ?? 2f;
                    float delay = GetFloat(elem, "Delay") ?? 0f;
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    bool talk = GetBool(elem, "Talk") ?? true;
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null && sourceView.Unit != null && sourceView.Unit.IsAlive && !sourceView.IsHidden)
                    {
                        sourceView.Chat(content, chatTime, talk ? ((sourceView == GameDirector.Player) ? LBoL.Presentation.UI.Widgets.ChatWidget.CloudType.LeftTalk : LBoL.Presentation.UI.Widgets.ChatWidget.CloudType.RightTalk) : ((sourceView == GameDirector.Player) ? LBoL.Presentation.UI.Widgets.ChatWidget.CloudType.LeftThink : LBoL.Presentation.UI.Widgets.ChatWidget.CloudType.RightThink), delay);
                        yield return new UnityEngine.WaitForSecondsRealtime(waitTime);
                    }
                    break;
                }
            case "Spell":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    string spellName = GetString(elem, "SpellName");
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null && !string.IsNullOrEmpty(spellName))
                    {
                        yield return SafeSpellDeclare(sourceView, spellName);
                    }
                    break;
                }
            case "Effect":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    string effectName = GetString(elem, "EffectName");
                    float delay = GetFloat(elem, "Delay") ?? 0f;
                    float waitTime = GetFloat(elem, "WaitTime") ?? 0f;
                    string sfxId = GetString(elem, "SfxId");
                    float sfxDelay = GetFloat(elem, "SfxDelay") ?? 0f;
                    string effectTypeStr = GetString(elem, "EffectType");
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null && !string.IsNullOrEmpty(effectName))
                    {
                        if (Enum.TryParse(effectTypeStr, out PerformAction.EffectBehavior effectType))
                        {
                            switch (effectType)
                            {
                                case PerformAction.EffectBehavior.PlayOneShot:
                                    sourceView.PlayEffectOneShot(effectName, delay);
                                    break;
                                case PerformAction.EffectBehavior.Add:
                                    sourceView.PlayEffectLoop(effectName);
                                    break;
                                case PerformAction.EffectBehavior.Remove:
                                    sourceView.EndEffectLoop(effectName, true);
                                    break;
                                case PerformAction.EffectBehavior.DieOut:
                                    sourceView.EndEffectLoop(effectName, false);
                                    break;
                            }
                        }
                        if (!string.IsNullOrEmpty(sfxId))
                        {
                            LBoL.Presentation.AudioManager.PlaySfxDelay(sfxId, sfxDelay);
                        }
                        yield return new UnityEngine.WaitForSeconds(waitTime);
                    }
                    break;
                }
            case "EffectMessage":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    string effectName = GetString(elem, "EffectName");
                    string message = GetString(elem, "Message");
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null && !string.IsNullOrEmpty(effectName))
                    {
                        sourceView.SendEffectMessage(effectName, message, null);
                    }
                    break;
                }
            case "SePop":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    string popContent = GetString(elem, "PopContent");
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null && sourceView.Unit != null && sourceView.Unit.IsAlive && !string.IsNullOrEmpty(popContent))
                    {
                        sourceView.ShowSePopup(popContent, StatusEffectType.Positive, 0, LBoL.Presentation.UI.Widgets.UnitInfoWidget.SePopType.Amulet);
                    }
                    break;
                }
            case "SummonFriend":
                {
                    string cardId = GetString(elem, "CardId");
                    Card card = Library.TryCreateCard(cardId, false, null);
                    if (card != null)
                    {
                        LBoL.Presentation.UI.UiManager.GetPanel<LBoL.Presentation.UI.Panels.PlayBoard>().CardUi.PlaySummonEffect(card);
                        yield return new UnityEngine.WaitForSeconds(0.5f);
                    }
                    break;
                }
            case "TransformModel":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    string modelName = GetString(elem, "ModelName");
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null && !string.IsNullOrEmpty(modelName))
                    {
                        yield return sourceView.LoadUnitModelAsync(modelName, false, null);
                    }
                    break;
                }
            case "DeathAnimation":
                {
                    Unit source = elem.TryGetProperty("Source", out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
                    UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);
                    if (sourceView != null)
                    {
                        sourceView.DeathAnimation();
                    }
                    break;
                }
            case "Wait":
                {
                    float time = GetFloat(elem, "Time") ?? 0f;
                    bool unscale = GetBool(elem, "Unscale") ?? false;
                    if (unscale)
                    {
                        yield return new UnityEngine.WaitForSecondsRealtime(time);
                    }
                    else
                    {
                        yield return new UnityEngine.WaitForSeconds(time);
                    }
                    break;
                }
        }
    }

    private static System.Collections.IEnumerator PlayDamageVisual(JsonElement actionEl, BattleController battle, string defaultSenderPlayerId = null)
    {
        Unit source = actionEl.TryGetProperty("Caster", out JsonElement cEl) ? ResolveUnit(cEl, battle) : null;
        UnitView sourceView = ResolveUnitView(source, cEl, battle, defaultSenderPlayerId);
        if (sourceView == null)
        {
            yield break;
        }

        var targets = new List<Unit>();
        if (actionEl.TryGetProperty("Targets", out JsonElement targetsEl) && targetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tEl in targetsEl.EnumerateArray())
            {
                Unit t = ResolveUnit(tEl, battle);
                if (t != null) targets.Add(t);
            }
        }

        string gunName = GetString(actionEl, "GunName") ?? "Instant";
        string gunTypeStr = GetString(actionEl, "GunType");
        GunType gunType = Enum.TryParse(gunTypeStr, out GunType parsed) ? parsed : GunType.Single;

        var targetViews = new List<UnitView>();
        foreach (Unit t in targets)
        {
            UnitView tv = ResolveUnitView(t, default, battle, null);
            if (tv != null && (tv.Unit == null || tv.Unit.IsAlive))
            {
                targetViews.Add(tv);
            }
        }

        if (targetViews.Count == 0)
        {
            UnitView fallbackTarget = ResolveTargetUnitView(null, default, battle, sourceView);
            if (fallbackTarget != null)
            {
                targetViews.Add(fallbackTarget);
            }
        }

        if (targetViews.Count == 0)
        {
            yield break;
        }

        // 注册特定攻击与受击目标，支持逐目标去重
        string attackId = GetString(actionEl, "AttackId") ?? GetString(actionEl, "PlayId") ?? Guid.NewGuid().ToString("N");
        var targetIds = new List<string>();
        foreach (Unit t in targets)
        {
            if (t != null)
            {
                if (!string.IsNullOrEmpty(t.Id)) targetIds.Add(t.Id);
                if (t is EnemyUnit eu && SpawnedEnemySyncPatch.TryGetSpawnId(eu, out string sid))
                {
                    targetIds.Add(sid);
                }
            }
        }

        RemoteCardPlaybackTracker.NotifyAttackStarted(attackId, targetIds);

        try
        {
            float blocked = GetFloat(actionEl, "DamageBlocked") ?? GetFloat(actionEl, "Blocked") ?? GetFloat(actionEl, "Block") ?? 0f;
            float shielded = GetFloat(actionEl, "DamageShielded") ?? GetFloat(actionEl, "Shielded") ?? GetFloat(actionEl, "Shield") ?? 0f;
            float actualDamage = GetFloat(actionEl, "ActualDamage") ?? GetFloat(actionEl, "HpDamage") ?? (GetFloat(actionEl, ActionBlueprintConstants.KeyDamage) ?? GetFloat(actionEl, ActionBlueprintConstants.KeyAmount) ?? 0f);
            string damageTypeStr = GetString(actionEl, ActionBlueprintConstants.KeyDamageType);
            DamageType damageType = Enum.TryParse(damageTypeStr, out DamageType parsedDt) ? parsedDt : DamageType.Attack;
            bool isAccuracy = GetBool(actionEl, ActionBlueprintConstants.KeyIsAccuracy) ?? false;
            bool dontBreakPerfect = GetBool(actionEl, ActionBlueprintConstants.KeyDontBreakPerfect) ?? false;
            bool isGrazed = GetBool(actionEl, "IsGrazed") ?? GetBool(actionEl, "IsGraze") ?? false;
            bool isCanceled = GetBool(actionEl, "IsCanceled") ?? false;

            float displayHpDamage = (isCanceled || isGrazed) ? 0f : actualDamage;
            DamageInfo realDamageInfo = damageType switch
            {
                DamageType.HpLose => DamageInfo.HpLose(displayHpDamage, dontBreakPerfect),
                DamageType.Reaction => DamageInfo.Reaction(displayHpDamage, dontBreakPerfect),
                _ => DamageInfo.Attack(displayHpDamage, isAccuracy)
            };
            realDamageInfo.DontBreakPerfect = dontBreakPerfect;
            realDamageInfo.DamageBlocked = isCanceled ? 0f : blocked;
            realDamageInfo.DamageShielded = isCanceled ? 0f : shielded;
            realDamageInfo.IsGrazed = isGrazed;

            if (!string.IsNullOrEmpty(gunName) && gunName != "Empty" && gunName != "Instant")
            {
                var pairs = new List<ValueTuple<UnitView, DamageInfo>>();
                foreach (UnitView tv in targetViews)
                {
                    pairs.Add(new ValueTuple<UnitView, DamageInfo>(tv, realDamageInfo));
                }

                bool isBusy = false;
                try
                {
                    object statusObj = Traverse.Create(sourceView).Field("_status").GetValue();
                    int statusInt = statusObj != null ? Convert.ToInt32(statusObj) : 0;
                    if (gunType == GunType.Middle || gunType == GunType.Last)
                    {
                        if (statusInt != ActionBlueprintConstants.ShootStatusComplex)
                        {
                            isBusy = true;
                        }
                    }
                    else
                    {
                        if (statusInt != ActionBlueprintConstants.ShootStatusIdle)
                        {
                            EnsureUnitIdle(sourceView, forceReset: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"[RemoteCardUse] Check sourceView _status failed: {ex.Message}");
                    isBusy = false;
                }

                if (!isBusy)
                {
                    bool shootOk = false;
                    IEnumerator shootEnumerator = null;
                    try
                    {
                        var method = Traverse.Create(typeof(GameDirector)).Method("GunShootAction", sourceView, pairs, gunName, gunType);
                        if (method.MethodExists())
                        {
                            shootEnumerator = (System.Collections.IEnumerator)method.GetValue();
                            shootOk = shootEnumerator != null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger?.LogWarning($"[RemoteCardUse] GunShootAction call failed: {ex.Message}");
                        shootOk = false;
                    }

                    if (shootOk)
                    {
                        // 绝不覆盖原游戏的全局 _gunHitArgs，防止破坏本地玩家或其他角色的并发命中回调
                        yield return shootEnumerator;
                    }
                    else
                    {
                        PerformSafeShoot(sourceView, targetViews, gunName, realDamageInfo);
                    }
                }
                else
                {
                    PerformSafeShoot(sourceView, targetViews, gunName, realDamageInfo);
                }
            }
            else
            {
                foreach (UnitView tv in targetViews)
                {
                    SafePlayAnimation(tv, ActionBlueprintConstants.AnimHit);
                    if (!isCanceled && !isGrazed && (!realDamageInfo.ZeroDamage || isAccuracy) && PopupHud.Instance != null && tv != null)
                    {
                        try
                        {
                            PopupHud.Instance.DamagePopupFromScene(realDamageInfo, tv.transform.position, sourceIsPlayer: true);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger?.LogDebug($"[RemoteCardUse] PlayDamageVisual fallback popup failed: {ex.Message}");
                        }
                    }
                }
            }
        }
        finally
        {
            RemoteCardPlaybackTracker.NotifyAttackEnded(attackId);
        }
    }

    private static void PerformSafeShoot(UnitView sourceView, List<UnitView> targetViews, string gunName, DamageInfo damageInfo)
    {
        try
        {
            sourceView.Targets = targetViews;
            sourceView.Target = targetViews.Count > 0 ? targetViews[0] : null;
            foreach (UnitView tv in targetViews)
            {
                if (tv != null)
                {
                    tv.ComingDamage = damageInfo;
                }
            }
            sourceView.PerformShoot(gunName);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] PerformShoot {gunName} failed: {ex.Message}");
            foreach (UnitView tv in targetViews)
            {
                SafePlayAnimation(tv, ActionBlueprintConstants.AnimHit);
                if (damageInfo.Damage > 0f && PopupHud.Instance != null && tv != null)
                {
                    try
                    {
                        PopupHud.Instance.DamagePopupFromScene(damageInfo, tv.transform.position, sourceIsPlayer: true);
                    }
                    catch (Exception pEx)
                    {
                        Plugin.Logger?.LogDebug($"[RemoteCardUse] PerformSafeShoot fallback popup failed: {pEx.Message}");
                    }
                }
            }
        }
    }

    private static System.Collections.IEnumerator PlayHealVisual(JsonElement actionEl, BattleController battle, string defaultSenderPlayerId = null)
    {
        Unit target = actionEl.TryGetProperty("Target", out JsonElement tEl) ? ResolveUnit(tEl, battle) : null;
        UnitView targetView = ResolveUnitView(target, tEl, battle, defaultSenderPlayerId);
        if (SafeIsNull(targetView))
        {
            yield break;
        }

        // 若施法者存在，联动触发施法手势
        UnitView casterView = ResolveUnitView(null, default, battle, defaultSenderPlayerId);
        if (!SafeIsNull(casterView) && (casterView.Unit == null || casterView.Unit.IsAlive))
        {
            SafePlayAnimation(casterView, ActionBlueprintConstants.AnimSpell, ActionBlueprintConstants.AnimCast);
        }

        int amount = GetInt(actionEl, "Amount") ?? 0;
        bool large = amount > 12;

        try
        {
            LBoL.Presentation.Effect.EffectManager.CreateEffect(large ? "UnitHealLarge" : "UnitHeal", targetView.EffectRoot, true);
            LBoL.Presentation.AudioManager.PlayUi(large ? "HealLarge" : "Heal", false);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] PlayHealVisual effect/audio failed: {ex.Message}");
        }
        yield return SafeWaitForSeconds(0.35f);
    }

    private static System.Collections.IEnumerator PlayStatusEffectVisual(JsonElement actionEl, BattleController battle, string defaultSenderPlayerId = null)
    {
        Unit target = actionEl.TryGetProperty("Target", out JsonElement tEl) ? ResolveUnit(tEl, battle) : null;
        UnitView targetView = ResolveUnitView(target, tEl, battle, defaultSenderPlayerId);
        if (SafeIsNull(targetView))
        {
            yield break;
        }

        string effectId = GetString(actionEl, "EffectId");
        if (string.IsNullOrEmpty(effectId))
        {
            yield break;
        }

        StatusEffect effect = Library.TryCreateStatusEffect(effectId);
        if (effect == null)
        {
            yield break;
        }

        // 若施法者存在，联动触发施法手势
        UnitView casterView = ResolveUnitView(null, default, battle, defaultSenderPlayerId);
        if (!SafeIsNull(casterView) && (casterView.Unit == null || casterView.Unit.IsAlive))
        {
            SafePlayAnimation(casterView, ActionBlueprintConstants.AnimSpell, ActionBlueprintConstants.AnimCast);
        }

        int level = GetInt(actionEl, "Level") ?? 0;
        int duration = GetInt(actionEl, "Duration") ?? 0;
        int count = GetInt(actionEl, "Count") ?? 0;
        int num = 0;
        if (effect.HasLevel) num = level;
        else if (effect.HasDuration) num = duration;
        else if (effect.HasCount) num = count;

        try
        {
            targetView.ShowSePopup(effect.Name, effect.Type, num, LBoL.Presentation.UI.Widgets.UnitInfoWidget.SePopType.Add);

            if (string.IsNullOrEmpty(effect.Config.SFX) || effect.Config.SFX == "Default")
            {
                switch (effect.Config.Type)
                {
                    case StatusEffectType.Positive:
                        LBoL.Presentation.AudioManager.PlaySfx("Buff", -1f);
                        break;
                    case StatusEffectType.Negative:
                        LBoL.Presentation.AudioManager.PlaySfx("Debuff", -1f);
                        break;
                }
            }
            else
            {
                LBoL.Presentation.AudioManager.PlaySfx(effect.Config.SFX, -1f);
            }

            if (!string.IsNullOrEmpty(effect.Config.VFX) && effect.Config.VFX != "Default")
            {
                LBoL.Presentation.Effect.EffectManager.CreateEffect(effect.Config.VFX, targetView.EffectRoot, 0f, null, false, true);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] PlayStatusEffectVisual failed: {ex.Message}");
        }

        yield return SafeWaitForSeconds(0.35f);
    }

    internal static void SafePlayAnimation(UnitView view, string primaryAnimation, string fallbackAnimation = null)
    {
        if (view is null || string.IsNullOrEmpty(primaryAnimation))
        {
            return;
        }

        try
        {
            view.PlayAnimation(primaryAnimation);
        }
        catch (Exception exPrimary)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] PlayAnimation '{primaryAnimation}' failed: {exPrimary.Message}");
            if (!string.IsNullOrEmpty(fallbackAnimation) && !string.Equals(primaryAnimation, fallbackAnimation, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    view.PlayAnimation(fallbackAnimation);
                }
                catch (Exception exFallback)
                {
                    Plugin.Logger?.LogDebug($"[RemoteCardUse] Fallback animation '{fallbackAnimation}' failed: {exFallback.Message}");
                }
            }
        }
    }

    private static readonly System.Reflection.MethodInfo _createLocalShieldEffectMethod =
        AccessTools.Method(typeof(UnitView), "CreateLocalShieldEffect", new[] { typeof(string), typeof(bool) });

    internal static void PlayShieldVfx(UnitView targetView, bool isShield)
    {
        if (targetView is null)
        {
            return;
        }

        string effectName = isShield ? ActionBlueprintConstants.VfxCastShield : ActionBlueprintConstants.VfxCastBlock;
        bool invokedReflectedMethod = false;

        if (_createLocalShieldEffectMethod != null)
        {
            try
            {
                _createLocalShieldEffectMethod.Invoke(targetView, new object[] { effectName, isShield });
                invokedReflectedMethod = true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[RemoteCardUse] CreateLocalShieldEffect invoke failed: {ex.Message}");
            }
        }

        if (!invokedReflectedMethod)
        {
            try
            {
                LBoL.Presentation.Effect.EffectManager.CreateEffect(effectName, targetView.EffectRoot, true);
            }
            catch (Exception exFallback)
            {
                Plugin.Logger?.LogDebug($"[RemoteCardUse] CreateEffect fallback failed: {exFallback.Message}");
            }
        }
    }

    internal static System.Collections.IEnumerator PlayBlockShieldVisual(JsonElement actionEl, BattleController battle, string defaultSenderPlayerId = null)
    {
        Unit source = actionEl.TryGetProperty(ActionBlueprintConstants.KeySource, out JsonElement sEl) ? ResolveUnit(sEl, battle) : null;
        UnitView sourceView = ResolveUnitView(source, sEl, battle, defaultSenderPlayerId);

        Unit target = actionEl.TryGetProperty(ActionBlueprintConstants.KeyTarget, out JsonElement tEl) ? ResolveUnit(tEl, battle) : null;
        UnitView targetView = ResolveUnitView(target, tEl, battle, defaultSenderPlayerId) ?? sourceView;

        // 阶段 1: 起手防御姿态（0.2s 蓄力等待）
        if (!SafeIsNull(sourceView))
        {
            SafePlayAnimation(sourceView, ActionBlueprintConstants.AnimDefend, ActionBlueprintConstants.AnimSpell);
        }

        yield return SafeWaitForSeconds(0.2f);

        // 阶段 2: 护盾/格挡光效与音效爆发（0.2s 扩散等待）
        if (!SafeIsNull(targetView))
        {
            float shield = GetFloat(actionEl, ActionBlueprintConstants.KeyShield) ?? 0f;
            float block = GetFloat(actionEl, ActionBlueprintConstants.KeyBlock) ?? 0f;
            bool isShield = shield > 0f;

            PlayShieldVfx(targetView, isShield);

            try
            {
                LBoL.Presentation.AudioManager.PlaySfx(ActionBlueprintConstants.SfxShieldCast, -1f);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[RemoteCardUse] PlaySfx ShieldCast failed: {ex.Message}");
            }
        }

        yield return SafeWaitForSeconds(0.2f);

        // 阶段 3: 后摇缓冲（0.1s）
        yield return SafeWaitForSeconds(0.1f);
    }

    internal static System.Collections.IEnumerator PlayGenericCastVisual(BattleController battle, string defaultSenderPlayerId = null)
    {
        UnitView casterView = ResolveUnitView(null, default, battle, defaultSenderPlayerId);
        if (SafeIsNull(casterView))
        {
            yield break;
        }

        SafePlayAnimation(casterView, ActionBlueprintConstants.AnimSpell, ActionBlueprintConstants.AnimCast);

        try
        {
            casterView.PlayEffectOneShot(ActionBlueprintConstants.VfxCardCast, 0f);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] PlayEffectOneShot CardCast failed: {ex.Message}");
        }

        try
        {
            LBoL.Presentation.AudioManager.PlaySfx(ActionBlueprintConstants.SfxBuff, -1f);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[RemoteCardUse] PlaySfx Buff failed: {ex.Message}");
        }

        yield return SafeWaitForSeconds(0.45f);
    }

    internal static System.Collections.IEnumerator SafeSpellDeclare(UnitView view, string spellName)
    {
        if (view == null || string.IsNullOrEmpty(spellName))
        {
            yield break;
        }

        SafePlayAnimation(view, ActionBlueprintConstants.AnimSpell);

        bool ok = false;
        IEnumerator inner = null;
        try
        {
            inner = view.SpellDeclare(spellName);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[RemoteCardUse] SpellDeclare call failed: {ex.Message}");
        }

        if (inner != null)
        {
            while (true)
            {
                bool hasNext = false;
                try
                {
                    hasNext = inner.MoveNext();
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"[RemoteCardUse] SpellDeclare MoveNext failed: {ex.Message}");
                    break;
                }

                if (!hasNext)
                {
                    ok = true;
                    break;
                }

                yield return inner.Current;
            }
        }

        if (!ok)
        {
            SafePlayAnimation(view, ActionBlueprintConstants.AnimSpell);

            try
            {
                LBoL.Presentation.AudioManager.PlaySfx("SpellDeclare", -1f);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[RemoteCardUse] Fallback SpellDeclare SFX failed: {ex.Message}");
            }
            yield return new UnityEngine.WaitForSeconds(0.5f);
        }
    }

    internal static System.Collections.IEnumerator PlayUsSequenceCoroutine(
        UnitView view,
        string spellName,
        JsonElement? actions,
        BattleController battle,
        string playerId)
    {
        RemoteCardPlaybackTracker.NotifyCardVisualStarted();
        try
        {
            yield return SafeSpellDeclare(view, spellName);
            if (actions.HasValue && actions.Value.ValueKind == JsonValueKind.Array && actions.Value.GetArrayLength() > 0)
            {
                yield return PlayVisualsCoroutine(actions.Value, battle, skipStateVisuals: false, defaultSenderPlayerId: playerId);
            }
        }
        finally
        {
            try
            {
                if (view != null)
                {
                    EnsureUnitIdle(view);
                }
            }
            finally
            {
                RemoteCardPlaybackTracker.NotifyCardVisualEnded();
            }
        }
    }

    #endregion
}
