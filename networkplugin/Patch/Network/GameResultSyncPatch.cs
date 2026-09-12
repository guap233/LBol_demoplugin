using System;
using System.Text.Json;
using HarmonyLib;
using LBoL.Core;
using LBoL.Presentation;
using LBoL.Presentation.UI.Panels;
using LBoL.Presentation.Units;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Patch.Network;

[HarmonyPatch]
public static class GameResultSyncPatch
{
    public static bool SyncOnResult = true;
    public static GameResultType? LastLocalResult;
    public static GameResultType? LastRemoteResult;

    private static long _suppressUntilTicks;

    private static bool IsVictoryResult(GameResultType resultType)
        => resultType != GameResultType.Failure;

    private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

    private static bool _subscribed;
    private static INetworkClient _subscribedClient;
    private static readonly Action<string, object> _onGameEventReceived = OnGameEventReceived;
    private static readonly Action<bool> _onConnectionStateChanged = OnConnectionStateChanged;

        [HarmonyPatch(typeof(GameDirector), "Update")]
    private static class SubscribeHook
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            INetworkClient client = NetworkEventHelper.TryGetNetworkClient();
            if (client == null)
            {
                return;
            }

            EnsureSubscribed(client);
            TickSuppression();
        }
    }

    private static void TickSuppression()
    {
        if (SyncOnResult)
        {
            return;
        }

        if (_suppressUntilTicks <= 0)
        {
            return;
        }

        if (DateTime.Now.Ticks < _suppressUntilTicks)
        {
            return;
        }

        SyncOnResult = true;
        _suppressUntilTicks = 0;
    }

    private static void EnsureSubscribed(INetworkClient client)
    {
        if (_subscribed && ReferenceEquals(_subscribedClient, client))
        {
            return;
        }

        try
        {
            if (_subscribedClient != null)
            {
                _subscribedClient.OnGameEventReceived -= _onGameEventReceived;
                _subscribedClient.OnConnectionStateChanged -= _onConnectionStateChanged;
            }
        }
        catch
        {

        }

        try
        {
            client.OnGameEventReceived += _onGameEventReceived;
            client.OnConnectionStateChanged += _onConnectionStateChanged;
            _subscribedClient = client;
            _subscribed = true;
        }
        catch
        {
            _subscribedClient = null;
            _subscribed = false;
        }
    }

    private static void OnConnectionStateChanged(bool connected)
    {
        if (connected)
        {
            return;
        }

        SyncOnResult = true;
        LastLocalResult = null;
        LastRemoteResult = null;
        _suppressUntilTicks = 0;
    }

    private static void OnGameEventReceived(string eventType, object payload)
    {
        if (!string.Equals(eventType, NetworkMessageTypes.OnGameRunResult, StringComparison.Ordinal))
        {
            return;
        }

        if (NetworkIdentityTracker.GetSelfIsHost())
        {
            Plugin.Logger?.LogWarning("[GameResultSync] Host 收到远端 OnGameRunResult，直接忽略（Host 拥有权威发布权）");
            return;
        }

        if (!TryGetJsonElement(payload, out JsonElement root))
        {
            Plugin.Logger?.LogWarning("[GameResultSync] Received OnGameRunResult but payload is not JSON.");
            return;
        }

        string resultTypeString = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ResultType", out JsonElement resultTypeElement))
        {
            resultTypeString = resultTypeElement.ValueKind == JsonValueKind.String
                ? resultTypeElement.GetString()
                : resultTypeElement.GetRawText();
        }

        if (string.IsNullOrWhiteSpace(resultTypeString) || !Enum.TryParse(resultTypeString, out GameResultType resultType))
        {
            Plugin.Logger?.LogWarning($"[GameResultSync] Invalid ResultType in payload: '{resultTypeString ?? "<null>"}'");
            return;
        }

        if (!IsVictoryResult(resultType))
        {

            return;
        }

        SyncOnResult = false;
        _suppressUntilTicks = DateTime.Now.AddSeconds(3).Ticks;
        LastRemoteResult = resultType;
        Plugin.Logger?.LogInfo($"[GameResultSync] Remote game result received: {resultType}");

        TryLeaveGameRunFromRemoteResult(resultType);
    }

    private static void TryLeaveGameRunFromRemoteResult(GameResultType resultType)
    {
        try
        {
            if (!IsVictoryResult(resultType))
            {
                return;
            }

            if (Singleton<GameMaster>.Instance?.CurrentGameRun == null)
            {
                return;
            }

            GameMaster.LeaveGameRun();
            Plugin.Logger?.LogInfo("[GameResultSync] LeaveGameRun triggered by remote victory result.");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[GameResultSync] Failed to LeaveGameRun on remote victory result: {ex.Message}");
        }
    }

    internal static void HandleLocalGameResult(GameResultType resultType)
    {
        LastLocalResult = resultType;

        INetworkClient client = NetworkEventHelper.TryGetNetworkClient();
        if (client?.IsConnected != true)
        {
            return;
        }

        if (!NetworkIdentityTracker.GetSelfIsHost())
        {
            Plugin.Logger?.LogWarning("[GameResultSync] 非 Host 成员尝试发布战斗结果，已拦截");
            return;
        }

        if (!SyncOnResult)
        {

            SyncOnResult = true;
            _suppressUntilTicks = 0;
            return;
        }

        if (!IsVictoryResult(resultType))
        {

            return;
        }

        try
        {
            string resultTypeName = Enum.GetName(typeof(GameResultType), resultType) ?? string.Empty;
            var payload = new
            {
                Sender = client.GetSelf()?.userName,
                ResultType = resultTypeName,
                IsTrueEnd = resultType == GameResultType.TrueEnd,
                Timestamp = DateTime.Now.Ticks
            };

            client.SendRequest(NetworkMessageTypes.OnGameRunResult, JsonCompat.Serialize(payload));
            Plugin.Logger?.LogInfo($"[GameResultSync] Broadcast game result: {resultType}");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[GameResultSync] Failed to broadcast game result: {ex.Message}");
        }
    }

        [HarmonyPatch(typeof(GameResultPanel), "OnShowing")]
    private static class GameResultPanel_OnShowing_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GameResultData payload)
        {
            if (payload == null)
            {
                return;
            }

            HandleLocalGameResult(payload.Type);
        }
    }

    private static bool TryGetJsonElement(object payload, out JsonElement root)
        => NetworkEventHelper.TryGetJsonElement(payload, out root);
}
