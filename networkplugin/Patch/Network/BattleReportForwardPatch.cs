using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Patch.Network;

[HarmonyPatch]
public static class BattleReportForwardPatch
{
	private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

	private static readonly object SyncLock = new();
	private static bool _subscribed;
	private static INetworkClient _subscribedClient;

	private static readonly Dictionary<string, long> _lastForwardedTicksByKey = new(StringComparer.Ordinal);
	private const int MaxForwardedKeys = 256;

	private static INetworkClient TryGetNetworkClient()
	    => ServiceProvider?.GetService<INetworkClient>();

	private static bool ShouldForwardReportEvent(string eventType)
	{
		return !string.IsNullOrWhiteSpace(eventType)
		    && eventType.EndsWith("Report", StringComparison.Ordinal)
		    && eventType.StartsWith("BattlePlayer", StringComparison.Ordinal);
	}

	private static bool TryReserveForwardKey(string eventType, string playerId, string targetId, long timestamp, string playId, int actionIndex)
	{
		string key;
		if (!string.IsNullOrEmpty(playId))
		{
			key = $"{eventType}|{playerId}|{playId}|{actionIndex}";
		}
		else
		{
			string suffix = actionIndex >= 0 ? actionIndex.ToString() : timestamp.ToString();
			key = $"{eventType}|{playerId}|{targetId}|{suffix}";
		}

		lock (SyncLock)
		{
			if (_lastForwardedTicksByKey.TryGetValue(key, out long lastTimestamp) && lastTimestamp == timestamp && string.IsNullOrEmpty(playId))
			{
				return false;
			}

			if (!string.IsNullOrEmpty(playId) && _lastForwardedTicksByKey.ContainsKey(key))
			{
				return false;
			}

			_lastForwardedTicksByKey[key] = timestamp;
			if (_lastForwardedTicksByKey.Count > MaxForwardedKeys)
			{
				_lastForwardedTicksByKey.Clear();
			}
		}

		return true;
	}

	[HarmonyTargetMethod]
	private static MethodBase TargetMethod()
	{

		Type t = AccessTools.TypeByName("LBoL.Presentation.Units.GameDirector")
		         ?? AccessTools.TypeByName("LBoL.Presentation.GameDirector")
		         ?? AccessTools.TypeByName("LBoL.Presentation.UI.GameDirector");
		if (t == null)
		{
			return null;
		}

		return AccessTools.DeclaredMethod(t, "Update");
	}

	[HarmonyPrepare]
	private static bool Prepare()
	{

		return TargetMethod() != null;
	}

	[HarmonyPostfix]
	private static void Postfix()
	{
		INetworkClient client = TryGetNetworkClient();
		if (client == null)
		{
			return;
		}

		EnsureSubscribed(client);
	}

	private static void EnsureSubscribed(INetworkClient client)
	{
		lock (SyncLock)
		{
			if (_subscribed && ReferenceEquals(_subscribedClient, client))
			{
				return;
			}
		}

		try
		{
			if (_subscribedClient != null)
				_subscribedClient.OnGameEventReceived -= OnGameEventReceived;
		}
		catch
		{

		}

		try
		{
			client.OnGameEventReceived += OnGameEventReceived;
			lock (SyncLock)
			{
				_subscribedClient = client;
				_subscribed = true;
			}
		}
		catch
		{
			lock (SyncLock)
			{
				_subscribedClient = null;
				_subscribed = false;
			}
		}
	}

	private static void OnGameEventReceived(string eventType, object payload)
	{
		if (!ShouldForwardReportEvent(eventType))
		{
			return;
		}

		INetworkClient client;
		lock (SyncLock)
		{
			client = _subscribedClient;
		}

		if (client?.IsConnected != true)
		{
			return;
		}

		if (!NetworkIdentityTracker.GetSelfIsHost())
		{
			return;
		}

		string broadcastType = MapToBroadcastType(eventType);
		if (string.IsNullOrWhiteSpace(broadcastType))
		{
			return;
		}

		if (!TryGetJsonElement(payload, out JsonElement root))
		{
			return;
		}

		long timestamp = 0;
		string playerId = string.Empty;
		string targetId = string.Empty;
		string playId = string.Empty;
		int actionIndex = -1;
		if (root.ValueKind == JsonValueKind.Object)
		{
			if (root.TryGetProperty("Timestamp", out JsonElement timestampElement))
			{
				if (timestampElement.ValueKind == JsonValueKind.Number)
				{
					timestampElement.TryGetInt64(out timestamp);
				}
				else if (timestampElement.ValueKind == JsonValueKind.String)
				{
					long.TryParse(timestampElement.GetString(), out timestamp);
				}
			}

			if (root.TryGetProperty("PlayerId", out JsonElement playerElement) && playerElement.ValueKind == JsonValueKind.String)
			{
				playerId = playerElement.GetString() ?? string.Empty;
			}

			if (root.TryGetProperty("TargetId", out JsonElement targetElement) && targetElement.ValueKind == JsonValueKind.String)
			{
				targetId = targetElement.GetString() ?? string.Empty;
			}

			if (root.TryGetProperty("PlayId", out JsonElement playElement) && playElement.ValueKind == JsonValueKind.String)
			{
				playId = playElement.GetString() ?? string.Empty;
			}

			if (root.TryGetProperty("ActionIndex", out JsonElement indexElement) && indexElement.ValueKind == JsonValueKind.Number)
			{
				indexElement.TryGetInt32(out actionIndex);
			}
		}

		if (!TryReserveForwardKey(eventType, playerId, targetId, timestamp, playId, actionIndex))
		{
			return;
		}

		client.SendGameEventData(broadcastType, payload);
	}

	private static string MapToBroadcastType(string reportType)
	{

		return reportType switch
		{
			NetworkMessageTypes.BattlePlayerDamageReport => NetworkMessageTypes.BattlePlayerDamageBroadcast,
			NetworkMessageTypes.BattlePlayerHealReport => NetworkMessageTypes.BattlePlayerHealBroadcast,
			NetworkMessageTypes.BattlePlayerStatusEffectsDeltaReport => NetworkMessageTypes.BattlePlayerStatusEffectsDeltaBroadcast,
			NetworkMessageTypes.BattlePlayerStatusEffectsFullReport => NetworkMessageTypes.BattlePlayerStatusEffectsFullBroadcast,
			NetworkMessageTypes.BattlePlayerUsUsedReport => NetworkMessageTypes.BattlePlayerUsUsedBroadcast,
			NetworkMessageTypes.BattlePlayerCardUsedReport => NetworkMessageTypes.BattlePlayerCardUsedBroadcast,
			_ => null,
		};
	}

	private static bool TryGetJsonElement(object payload, out JsonElement root)
		=> NetworkEventHelper.TryGetJsonElement(payload, out root);
}
