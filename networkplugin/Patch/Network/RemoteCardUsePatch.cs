using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using LBoL.Base;
using LBoL.Core;
using LBoL.Core.Battle;
using LBoL.Core.Battle.BattleActions;
using LBoL.Core.Cards;
using LBoL.Core.StatusEffects;
using LBoL.Core.Units;
using LBoL.Presentation.UI;
using LBoL.Presentation.UI.Panels;
using LBoL.Presentation.Units;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.Patch.EnemyUnits;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Patch.Network;

public static partial class RemoteCardUsePatch
{
        private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

        internal static bool IsInRemoteCardPipeline => Volatile.Read(ref _remotePipelineDepth) > 0;

        private static int _remotePipelineDepth;

        private sealed class RemotePipelineScope : IDisposable
    {
                private bool _disposed;

                public RemotePipelineScope()
        {
            Interlocked.Increment(ref _remotePipelineDepth);
        }

                public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Decrement(ref _remotePipelineDepth);
        }
    }

        private static IDisposable EnterRemotePipelineScope() => new RemotePipelineScope();

        private static long _resolvedSeq;
        private static readonly object _resolvedLock = new();
        private static readonly Dictionary<string, long> _lastResolvedSeqByTarget = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _lastResolvedTimestampByTarget = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<string>> _processedResolvedRequestIdsByTarget = new(StringComparer.Ordinal);

        [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    [HarmonyPatch(typeof(Card), "GetActions")]
    private static IEnumerable<BattleAction> Card_GetActions_Original(
        Card __instance,
        UnitSelector selector,
        ManaGroup consumingMana,
        Interaction precondition,
        bool kicker,
        bool summoning,
        IList<DamageAction> damageActions)

        => throw new NotImplementedException("Harmony reverse patch stub");

        private static INetworkClient TryGetClient()
        => ServiceProvider?.GetService<INetworkClient>();

        private static bool IsConnected(out INetworkClient client)
    {
        client = TryGetClient();
        return client != null && client.IsConnected;
    }

        private static IEnumerable<BattleAction> FailFallbackActions(Card card, ManaGroup consumingMana, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            ShowTopMessage(message);
        }

        BattleController battle = card?.Battle;

        if (card != null)
        {
            card.PendingManaUsage = null;
            card.PendingTarget = null;
            card.KickerPlaying = false;
        }

            BattleAction refundMoneyAction = null;
            try
            {
                int? moneyCost = card?.Config?.MoneyCost;

                if (battle != null && card != null && moneyCost != null && moneyCost.Value > 0 &&
                    (card.Zone == CardZone.PlayArea || card.Zone == CardZone.FollowArea))
                {
                    refundMoneyAction = new GainMoneyAction(moneyCost.Value);
                }
            }
            catch
            {

            }

            if (refundMoneyAction != null)
            {
                yield return refundMoneyAction;
            }

            BattleAction refundAction = null;
            try
            {
                if (battle != null && card != null && card.Zone == CardZone.PlayArea && consumingMana.Total > 0)
            {
                refundAction = new GainManaAction(consumingMana);
            }
        }
        catch
        {

        }

        if (refundAction != null)
        {
            yield return refundAction;
        }

        BattleAction moveAction = null;
        try
        {
            if (battle != null && card != null)
            {
                bool canReturnToHand = false;
                try
                {
                    canReturnToHand = battle.HandZone != null && battle.HandZone.Count < battle.MaxHand;
                }
                catch
                {
                    canReturnToHand = false;
                }

                moveAction = new MoveCardAction(card, canReturnToHand ? CardZone.Hand : CardZone.Discard);
            }
        }
        catch
        {

        }

        if (moveAction != null)
        {
            yield return moveAction;
        }
    }

        private static object SnapshotMana(ManaGroup mana)
        => new
        {
            Red = mana.Red,
            Blue = mana.Blue,
            Green = mana.Green,
            White = mana.White,
            Colorless = mana.Colorless,
            Philosophy = mana.Philosophy,
            Any = mana.Any,
            Hybrid = mana.Hybrid,
            HybridColor = mana.HybridColor.ToString(),
            Total = mana.Total
        };

        private static object SnapshotStatusEffects(Unit unit)
    {
        try
        {
            if (unit?.StatusEffects == null)
            {
                return Array.Empty<object>();
            }

            List<object> list = new List<object>();
            foreach (StatusEffect se in unit.StatusEffects)
            {
                if (se == null)
                {
                    continue;
                }

                int? level = null;
                int? count = null;
                int? duration = null;
                int? limit = null;
                try
                {
                    if (se.HasLevel) level = se.Level;
                    if (se.HasCount) count = se.Count;
                    if (se.HasDuration) duration = se.Duration;
                    limit = se.Limit;
                }
                catch (Exception ex) { Plugin.Logger?.LogDebug($"[RemoteCardUse] 获取 StatusEffect 属性失败: Id={se?.Id}, {ex.Message}"); }

                list.Add(new
                {
                    Id = se.Id,
                    TypeName = se.GetType().FullName,
                    Name = se.Name,
                    SeType = se.Type.ToString(),
                    IsAutoDecreasing = SafeGetAutoDecreasing(se),
                    HasLevel = SafeBool(() => se.HasLevel),
                    Level = level,
                    HasCount = SafeBool(() => se.HasCount),
                    Count = count,
                    HasDuration = SafeBool(() => se.HasDuration),
                    Duration = duration,
                    Limit = limit
                });
            }

            return list.ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

        private static bool SafeBool(Func<bool> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return false;
        }
    }

        private static bool SafeGetAutoDecreasing(StatusEffect effect)
    {
        try
        {
            return Traverse.Create(effect).Property("IsAutoDecreasing").GetValue<bool>();
        }
        catch
        {
            return true;
        }
    }

        private static void ShowTopMessage(string message)
    {
        try
        {
            if (!UiManager.IsInitialized)
            {
                return;
            }

            UiManager.GetPanel<TopMessagePanel>().ShowMessage(message);
        }
        catch
        {

        }
    }

        #region Send (patch Card.GetActions)

        [HarmonyPatch(typeof(BattleController), "RequestUseCard", typeof(Card), typeof(UnitSelector), typeof(ManaGroup), typeof(bool))]
        [HarmonyPrefix]
        private static bool BattleController_RequestUseCard_Prefix(Card card, UnitSelector selector)
        {
            try
            {
                if (card == null || selector == null)
                {
                    return true;
                }

                if (selector.Type != TargetType.SingleEnemy)
                {
                    return true;
                }

                if (selector.SelectedEnemy is not RemotePlayerProxyEnemy proxy || string.IsNullOrWhiteSpace(proxy.RemotePlayerId))
                {
                    return true;
                }

                if (IsConnected(out _))
                {
                    return true;
                }

                ShowTopMessage("未连接，无法对队友出牌。");
                return false;
            }
            catch
            {
                return true;
            }
        }

        [HarmonyPatch(typeof(Card), "GetActions")]
    [HarmonyPrefix]
    private static bool Card_GetActions_Prefix(
        Card __instance,
        UnitSelector selector,
        ManaGroup consumingMana,
        Interaction precondition,
        bool kicker,
        bool summoning,
        IList<DamageAction> damageActions,
        ref IEnumerable<BattleAction> __result)
    {
        try
        {
            if (selector == null)
            {
                return true;
            }

            if (selector.Type != TargetType.SingleEnemy)
            {
                return true;
            }

            if (selector.SelectedEnemy is not RemotePlayerProxyEnemy proxy || string.IsNullOrWhiteSpace(proxy.RemotePlayerId))
            {
                return true;
            }

            if (!IsConnected(out INetworkClient client))
            {
                __result = FailFallbackActions(__instance, consumingMana, "未连接，无法对队友出牌。");
                return false;
            }

            __result = RemoteOnlyActions(__instance, selector, proxy, consumingMana, precondition, kicker, summoning, client);
            return false;
        }
        catch
        {
            return true;
        }
    }

        private static IEnumerable<BattleAction> RemoteOnlyActions(Card card, UnitSelector selector, RemotePlayerProxyEnemy proxy, ManaGroup consumingMana, Interaction precondition, bool kicker, bool summoning, INetworkClient client)
    {
        bool hasDamage = false;
        bool hasHeal = false;
        bool hasStatus = false;
        object[] actionBlueprint = Array.Empty<object>();
        bool sendOk = false;

        try
        {
            string senderNetworkId;
            lock (_syncLock)
            {
                senderNetworkId = _selfPlayerId;
            }
            senderNetworkId ??= "unknown";

            string senderName = GameStateUtils.GetCurrentPlayer()?.Name;
            string senderCharacterId = card?.Battle?.Player?.Id;

            try
            {
                using (EnterRemotePipelineScope())
                {
                    IEnumerable<BattleAction> original = Card_GetActions_Original(card, selector, consumingMana, precondition, kicker, summoning, new List<DamageAction>());
                    actionBlueprint = BuildActionBlueprint(original, out hasDamage, out hasHeal, out hasStatus);
                }
            }
            catch
            {
                actionBlueprint = Array.Empty<object>();
            }

            string requestId = Guid.NewGuid().ToString("N");

            var payload = new
            {
                Timestamp = DateTime.Now.Ticks,
                RequestId = requestId,
                EventType = NetworkMessageTypes.OnRemoteCardUse,
                SenderPlayerId = senderNetworkId,
                SenderName = senderName,
                SenderCharacterId = senderCharacterId,
                TargetPlayerId = proxy.RemotePlayerId,
                TargetName = proxy.RemotePlayerName,
                Card = new
                {
                    CardId = card.Id,
                    InstanceId = card.InstanceId,
                    CardName = card.Name,
                    CardType = card.CardType.ToString(),
                    IsUpgraded = card.IsUpgraded,
                    UpgradeCounter = card.UpgradeCounter,
                },
                ConsumingMana = SnapshotMana(consumingMana),
                Kicker = kicker,
                SenderStatusEffects = SnapshotStatusEffects(card?.Battle?.Player),
                Actions = actionBlueprint
            };

            client.SendGameEventData(NetworkMessageTypes.OnRemoteCardUse, payload);
            sendOk = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[RemoteCardUse] Send failed: {ex.Message}");
        }

        if (!sendOk)
        {
            foreach (BattleAction a in FailFallbackActions(card, consumingMana, "网络发送失败，已取消对队友出牌。"))
            {
                yield return a;
            }
            yield break;
        }

        card.PendingManaUsage = consumingMana;
        card.PendingTarget = proxy;
        card.KickerPlaying = kicker;

        try
        {
            if (card.Battle != null && actionBlueprint != null && actionBlueprint.Length > 0)
            {
                string serializedActions = JsonSerializer.Serialize(actionBlueprint);
                using (JsonDocument doc = JsonDocument.Parse(serializedActions))
                {
                    Singleton<GameDirector>.Instance?.StartCoroutine(PlayVisualsCoroutine(doc.RootElement.Clone(), card.Battle, false));
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[RemoteCardUse] Caster play visuals failed: {ex.Message}");
        }

        yield return PerformAction.Wait(0.4f);

        card.PendingManaUsage = null;
        card.PendingTarget = null;
        card.KickerPlaying = false;
    }

    #endregion

    #region Shared Blueprint + JSON Helpers

    private static object SerializeUnit(Unit unit)
    {
        if (unit == null) return null;
        if (unit is PlayerUnit pu)
        {
            string pid = OtherPlayersOverlayPatch.GetPlayerIdFromUnit(pu);
            if (string.IsNullOrWhiteSpace(pid))
            {
                pid = NetworkIdentityTracker.GetSelfPlayerId();
                if (string.IsNullOrWhiteSpace(pid))
                {
                    pid = TryGetClient()?.GetSelf()?.playerId;
                }
                pid ??= "__local__";
            }
            return new { Kind = ActionBlueprintConstants.UnitPlayer, PlayerId = pid };
        }
        if (unit is EnemyUnit eu)
        {
            return new { Kind = ActionBlueprintConstants.UnitEnemy, EnemyId = eu.Id, RootIndex = eu.RootIndex };
        }
        return new { Kind = ActionBlueprintConstants.UnitUnknown, Id = unit.Id };
    }

    internal static object[] BuildActionBlueprint(IEnumerable<BattleAction> actions)
        => BuildActionBlueprint(actions, out _, out _, out _);

    internal static object[] BuildActionBlueprint(IEnumerable<BattleAction> actions, out bool hasDamage, out bool hasHeal, out bool hasStatus)
    {
        hasDamage = false;
        hasHeal = false;
        hasStatus = false;

        if (actions == null)
        {
            return Array.Empty<object>();
        }

        try
        {
            List<object> list = new List<object>();
            foreach (BattleAction action in actions)
            {
                if (action == null)
                {
                    continue;
                }

                switch (action)
                {
                    case DamageAction da:
                        {
                            hasDamage = true;
                            DamageInfo info = da.DealingArgs.DamageInfo;
                            var targetsList = new List<object>();
                            if (da.DealingArgs.Targets != null)
                            {
                                foreach (var t in da.DealingArgs.Targets)
                                {
                                    targetsList.Add(SerializeUnit(t));
                                }
                            }
                            list.Add(new
                            {
                                Kind = ActionBlueprintConstants.KindDamage,
                                Caster = SerializeUnit(da.DealingArgs.Source),
                                Targets = targetsList.ToArray(),
                                Damage = info.Damage,
                                DamageType = info.DamageType.ToString(),
                                IsAccuracy = info.IsAccuracy,
                                DontBreakPerfect = info.DontBreakPerfect,
                                DamageBlocked = info.DamageBlocked,
                                DamageShielded = info.DamageShielded,
                                IsGrazed = info.IsGrazed,
                                IsCanceled = da.IsCanceled,
                                GunName = da.GunName,
                                GunType = da.GunType.ToString(),
                            });
                            break;
                        }
                    case HealAction ha:
                        {
                            hasHeal = true;
                            list.Add(new
                            {
                                Kind = ActionBlueprintConstants.KindHeal,
                                Caster = SerializeUnit(ha.Args.Source),
                                Target = SerializeUnit(ha.Args.Target),
                                Amount = ha.Args.Amount,
                                HealType = ha.Args.HealType.ToString(),
                                WaitTime = ha.WaitTime
                            });
                            break;
                        }
                    case ApplyStatusEffectAction sa:
                        {
                            StatusEffectApplyEventArgs args = sa.Args;
                            StatusEffect effect = args?.Effect;
                            if (effect == null)
                            {
                                break;
                            }

                            hasStatus = true;
                            list.Add(new
                            {
                                Kind = ActionBlueprintConstants.KindApplyStatusEffect,
                                Target = SerializeUnit(args.Unit),
                                EffectId = effect.Id,
                                Level = args.Level,
                                Duration = args.Duration,
                                Count = args.Count,
                                Limit = effect.Limit,
                                WaitTime = args.WaitTime,
                                StartAutoDecreasing = SafeGetAutoDecreasing(effect)
                            });
                            break;
                        }
                    case CastBlockShieldAction cba:
                        {
                            var args = cba.Args;
                            list.Add(new
                            {
                                Kind = ActionBlueprintConstants.KindBlockShield,
                                Source = SerializeUnit(args?.Source),
                                Target = SerializeUnit(args?.Target),
                                Block = args?.Block ?? 0f,
                                Shield = args?.Shield ?? 0f,
                                Cast = cba.Cast,
                                Type = args?.Type.ToString() ?? "Normal"
                            });
                            break;
                        }
                    case PerformAction pa:
                        {
                            var paArgs = pa.Args;
                            if (paArgs is PerformAction.ViewCardArgs vca)
                            {
                                list.Add(new {
                                    Kind = ActionBlueprintConstants.KindPerformAction,
                                    Type = "ViewCard",
                                    CardId = vca.Card?.Id,
                                    Zone = vca.Zone.ToString()
                                });
                            }
                            else if (paArgs is PerformAction.GunArgs ga)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Gun",
                                    Source = SerializeUnit(ga.Source),
                                    Target = SerializeUnit(ga.Target),
                                    GunId = ga.GunId,
                                    WaitTime = ga.WaitTime
                                });
                            }
                            else if (paArgs is PerformAction.DollArgs daArg)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Doll",
                                    Target = SerializeUnit(daArg.Target),
                                    GunId = daArg.GunId,
                                    WaitTime = daArg.WaitTime,
                                    DebugString = daArg.DebugString
                                });
                            }
                            else if (paArgs is PerformAction.AnimationArgs aa)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Animation",
                                    Source = SerializeUnit(aa.Source),
                                    AnimationName = aa.AnimationName,
                                    WaitTime = aa.WaitTime,
                                    SfxId = aa.SfxId,
                                    SfxDelay = aa.SfxDelay,
                                    ShakeLevel = aa.ShakeLevel
                                });
                            }
                            else if (paArgs is PerformAction.SfxArgs sfx)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Sfx",
                                    Id = sfx.Id,
                                    Delay = sfx.Delay
                                });
                            }
                            else if (paArgs is PerformAction.UiSoundArgs uiSound)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "UiSound",
                                    Id = uiSound.Id
                                });
                            }
                            else if (paArgs is PerformAction.ChatArgs chat)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Chat",
                                    Source = SerializeUnit(chat.Source),
                                    Content = chat.Content,
                                    ChatTime = chat.ChatTime,
                                    Delay = chat.Delay,
                                    WaitTime = chat.WaitTime,
                                    Talk = chat.Talk
                                });
                            }
                            else if (paArgs is PerformAction.SpellArgs spell)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Spell",
                                    Source = SerializeUnit(spell.Source),
                                    SpellName = spell.SpellName
                                });
                            }
                            else if (paArgs is PerformAction.EffectArgs effect)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Effect",
                                    Source = SerializeUnit(effect.Source),
                                    EffectName = effect.EffectName,
                                    Delay = effect.Delay,
                                    WaitTime = effect.WaitTime,
                                    SfxId = effect.SfxId,
                                    SfxDelay = effect.SfxDelay,
                                    EffectType = effect.EffectType.ToString()
                                });
                            }
                            else if (paArgs is PerformAction.EffectMessageArgs ema)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "EffectMessage",
                                    Source = SerializeUnit(ema.Source),
                                    EffectName = ema.EffectName,
                                    Message = ema.Message
                                });
                            }
                            else if (paArgs is PerformAction.SePopArgs sePop)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "SePop",
                                    Source = SerializeUnit(sePop.Source),
                                    PopContent = sePop.PopContent
                                });
                            }
                            else if (paArgs is PerformAction.SummonFriendArgs sfa)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "SummonFriend",
                                    CardId = sfa.Card?.Id
                                });
                            }
                            else if (paArgs is PerformAction.TransformModelArgs tma)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "TransformModel",
                                    Source = SerializeUnit(tma.Source),
                                    ModelName = tma.ModelName
                                });
                            }
                            else if (paArgs is PerformAction.DeathAnimationArgs daa)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "DeathAnimation",
                                    Source = SerializeUnit(daa.Source)
                                });
                            }
                            else if (paArgs is PerformAction.WaitArgs wait)
                            {
                                list.Add(new {
                                    Kind = "PerformAction",
                                    Type = "Wait",
                                    Time = wait.Time,
                                    Unscale = wait.Unscale
                                });
                            }
                            break;
                        }
                }
            }

            return list.ToArray();
        }
        catch
        {
            hasDamage = false;
            hasHeal = false;
            hasStatus = false;
            return Array.Empty<object>();
        }
    }

    private static bool TryGetJsonElement(object payload, out JsonElement root)
        => NetworkEventHelper.TryGetJsonElement(payload, out root);

    private static string GetString(JsonElement root, string name)
        => NetworkEventHelper.GetString(root, name);

    private static int? GetInt(JsonElement elem, string property)
    {
        if (NetworkEventHelper.TryGetInt(elem, property, out int v))
            return v;
        return null;
    }

    private static long? GetLong(JsonElement elem, string property)
    {
        if (NetworkEventHelper.TryGetLong(elem, property, out long v))
            return v;
        return null;
    }

    private static float? GetFloat(JsonElement elem, string property)
    {
        if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetSingle(out float f))
            return f;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d))
            return (float)d;
        if (p.ValueKind == JsonValueKind.String && float.TryParse(p.GetString(), out float s))
            return s;
        return null;
    }

    private static bool? GetBool(JsonElement elem, string property)
    {
        if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(p.GetString(), out bool b) ? b : null,
            _ => null,
        };
    }

    #endregion
}
