using System;
using System.Collections.Generic;
using System.Text.Json;
using HarmonyLib;
using LBoL.Base;
using LBoL.Core;
using LBoL.Core.Battle;
using LBoL.Core.Battle.BattleActions;
using LBoL.Core.Cards;
using LBoL.Core.Units;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Patch.Network;

[HarmonyPatch]
public static class CardActionCapturePatch
{
    private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

    private static INetworkClient TryGetClient()
        => ServiceProvider?.GetService<INetworkClient>();

    private static bool TryPrepareClient(out INetworkClient client, out bool isHost, out string selfPlayerId)
    {
        client = TryGetClient();
        if (client == null || !client.IsConnected)
        {
            isHost = false;
            selfPlayerId = null;
            return false;
        }

        NetworkIdentityTracker.EnsureSubscribed(client);
        isHost = NetworkIdentityTracker.GetSelfIsHost();
        selfPlayerId = NetworkIdentityTracker.GetSelfPlayerId();
        if (string.IsNullOrWhiteSpace(selfPlayerId))
        {
            selfPlayerId = client.GetSelf()?.playerId;
        }
        return !string.IsNullOrWhiteSpace(selfPlayerId);
    }

    private static string ResolveSelfPlayerName()
    {
        try
        {
            string selfId = NetworkIdentityTracker.GetSelfPlayerId();
            string name = OtherPlayersOverlayPatch.ResolveDisplayName(selfId, null, isLocal: true);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
            return selfId ?? "Player";
        }
        catch
        {
            return NetworkIdentityTracker.GetSelfPlayerId() ?? "Player";
        }
    }

    #region Card.GetActions 拦截与动作收集

    [HarmonyPatch(typeof(Card), "GetActions")]
    [HarmonyPostfix]
    public static void Card_GetActions_Postfix(
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
            if (__result == null)
            {
                return;
            }

            if (RemoteCardUsePatch.IsInRemoteCardPipeline)
            {
                return;
            }

            if (!TryPrepareClient(out _, out _, out _))
            {
                return;
            }

            if (__instance?.Battle?.Player == null)
            {
                return;
            }

            string cardId = __instance.Id;
            string cardName = __instance.Name;
            string playId = Guid.NewGuid().ToString("N");

            __result = WrapActionsStream(__result, cardId, cardName, isUs: false, selector, playId);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[CardActionCapture] Card_GetActions_Postfix error: {ex.Message}");
        }
    }

    #endregion

    #region UltimateSkill.GetActions 拦截与动作收集

    [HarmonyPatch(typeof(UltimateSkill), "GetActions")]
    [HarmonyPostfix]
    public static void UltimateSkill_GetActions_Postfix(
        UltimateSkill __instance,
        UnitSelector selector,
        IList<DamageAction> damageActions,
        ref IEnumerable<BattleAction> __result)
    {
        try
        {
            if (__result == null)
            {
                return;
            }

            if (RemoteCardUsePatch.IsInRemoteCardPipeline)
            {
                return;
            }

            if (!TryPrepareClient(out _, out _, out _))
            {
                return;
            }

            string usId = __instance?.Id ?? "UnknownUs";
            string usName = __instance?.Name ?? __instance?.DebugName ?? "符卡";
            string playId = Guid.NewGuid().ToString("N");

            __result = WrapActionsStream(__result, usId, usName, isUs: true, selector, playId);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[CardActionCapture] UltimateSkill_GetActions_Postfix error: {ex.Message}");
        }
    }

    #endregion

    #region 动作蓝图按序流式发送

    internal static IEnumerable<BattleAction> WrapActionsStream(
        IEnumerable<BattleAction> source,
        string cardOrUsId,
        string cardOrUsName,
        bool isUs,
        UnitSelector selector,
        string playId)
    {
        if (source == null)
        {
            yield break;
        }

        using IEnumerator<BattleAction> enumerator = source.GetEnumerator();
        int actionIndex = 0;

        while (true)
        {
            BattleAction current;
            try
            {
                if (!enumerator.MoveNext())
                {
                    break;
                }
                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[CardActionCapture] 获取下一个动作异常 ({cardOrUsName}): {ex.Message}");
                throw;
            }

            if (current != null)
            {
                try
                {
                    SendCapturedSingleAction(cardOrUsId, cardOrUsName, isUs, selector, current, playId, actionIndex);
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogError($"[CardActionCapture] 动作序列化或上报失败 (PlayId={playId}, Index={actionIndex}): {ex.Message}");
                }
                actionIndex++;
            }

            yield return current;
        }
    }

    internal static void SendCapturedSingleAction(
        string cardOrUsId,
        string cardOrUsName,
        bool isUs,
        UnitSelector selector,
        BattleAction action,
        string playId,
        int actionIndex)
    {
        try
        {
            if (!TryPrepareClient(out INetworkClient client, out bool isHost, out string selfPlayerId))
            {
                return;
            }

            object[] actionBlueprint = Array.Empty<object>();
            if (action != null)
            {
                actionBlueprint = RemoteCardUsePatch.BuildActionBlueprint(new[] { action });
            }

            string eventType;
            if (isHost)
            {
                eventType = isUs
                    ? NetworkMessageTypes.BattlePlayerUsUsedBroadcast
                    : NetworkMessageTypes.BattlePlayerCardUsedBroadcast;
            }
            else
            {
                eventType = isUs
                    ? NetworkMessageTypes.BattlePlayerUsUsedReport
                    : NetworkMessageTypes.BattlePlayerCardUsedReport;
            }

            var payload = new
            {
                Timestamp = DateTime.Now.Ticks,
                PlayerId = selfPlayerId,
                PlayerName = ResolveSelfPlayerName(),
                IsHost = isHost,
                PlayId = playId,
                ActionIndex = actionIndex,
                CardName = cardOrUsName,
                CardId = cardOrUsId,
                UsName = isUs ? cardOrUsName : null,
                IsUs = isUs,
                Actions = actionBlueprint,
            };

            client.SendGameEventData(eventType, payload);
            Plugin.Logger?.LogInfo($"[CardActionCapture] 已发送出牌动作事件: {eventType} PlayId={playId}, Index={actionIndex}, CardName={cardOrUsName}, ActionCount={actionBlueprint.Length}");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[CardActionCapture] SendCapturedSingleAction error: {ex.Message}");
            throw;
        }
    }

    internal static IEnumerable<BattleAction> WrapActionsStreamForTest(
        IEnumerable<BattleAction> source,
        string cardOrUsId,
        string cardOrUsName,
        bool isUs,
        UnitSelector selector,
        string playId)
        => WrapActionsStream(source, cardOrUsId, cardOrUsName, isUs, selector, playId);

    internal static void SendCapturedSingleActionForTest(
        string cardOrUsId,
        string cardOrUsName,
        bool isUs,
        UnitSelector selector,
        BattleAction action,
        string playId,
        int actionIndex)
        => SendCapturedSingleAction(cardOrUsId, cardOrUsName, isUs, selector, action, playId, actionIndex);

    #endregion
}
