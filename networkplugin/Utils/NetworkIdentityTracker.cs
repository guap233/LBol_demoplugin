using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;

namespace NetworkPlugin.Utils;

public static class NetworkIdentityTracker
{
    private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

    private static readonly object SyncLock = new();
    private static bool _subscribed;
    private static INetworkClient _subscribedClient;

    private static string _selfPlayerId;
    private static bool _selfIsHost;
    private static string _sessionNonce;
    private static readonly HashSet<string> _playerIds = new(StringComparer.Ordinal);
    private static bool _hasOnlineHost;
    private static bool _isHostInGracePeriod;

    private static readonly Action<string, object> OnGameEventReceivedHandler = OnGameEventReceived;
    private static readonly Action<bool> OnConnectionStateChangedHandler = OnConnectionStateChanged;

    public static INetworkClient TryGetClient()
        => ServiceProvider?.GetService<INetworkClient>();

    public static void EnsureSubscribed(INetworkClient client)
    {
        if (client == null)
        {
            return;
        }

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
            {
                _subscribedClient.OnGameEventReceived -= OnGameEventReceivedHandler;
                _subscribedClient.OnConnectionStateChanged -= OnConnectionStateChangedHandler;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[NetworkIdentityTracker] Unsubscribe failed: {ex.Message}");
        }

        try
        {
            client.OnGameEventReceived += OnGameEventReceivedHandler;
            client.OnConnectionStateChanged += OnConnectionStateChangedHandler;
            lock (SyncLock)
            {
                _subscribedClient = client;
                _subscribed = true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[NetworkIdentityTracker] Subscribe failed: {ex.Message}");
            lock (SyncLock)
            {
                _subscribedClient = null;
                _subscribed = false;
            }
        }
    }

    public static string GetSelfPlayerId()
    {
        lock (SyncLock)
        {
            return _selfPlayerId;
        }
    }

    public static bool GetSelfIsHost()
    {
        lock (SyncLock)
        {
            return _selfIsHost;
        }
    }

    public static bool HasOnlineHost()
    {
        lock (SyncLock)
        {
            return _hasOnlineHost || _selfIsHost;
        }
    }

    public static bool IsHostInGracePeriod()
    {
        lock (SyncLock)
        {
            return _isHostInGracePeriod && !_selfIsHost && !_hasOnlineHost;
        }
    }

    public static string GetSessionNonce()
    {
        lock (SyncLock)
        {
            return _sessionNonce;
        }
    }

    public static HashSet<string> GetPlayerIdsSnapshot()
    {
        lock (SyncLock)
        {
            var snapshot = new HashSet<string>(_playerIds, StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(_selfPlayerId))
            {
                snapshot.Add(_selfPlayerId);
            }
            return snapshot;
        }
    }

    private static void OnConnectionStateChanged(bool connected)
    {
        if (connected)
        {
            return;
        }

        lock (SyncLock)
        {
            _selfPlayerId = null;
            _selfIsHost = false;
            _sessionNonce = null;
            _playerIds.Clear();
            _hasOnlineHost = false;
            _isHostInGracePeriod = false;
        }
    }

    internal static void ResetForTest()
    {
        OnConnectionStateChanged(false);
    }

    private static void OnGameEventReceived(string eventType, object payload)
    {
        if (!TryGetJsonElement(payload, out JsonElement root))
        {
            return;
        }

        switch (eventType)
        {
            case NetworkMessageTypes.Welcome:
                HandleWelcome(root);
                return;
            case NetworkMessageTypes.HostChanged:
                HandleHostChanged(root);
                return;
            case NetworkMessageTypes.PlayerListUpdate:
                HandlePlayerListUpdate(root);
                return;
            case NetworkMessageTypes.PlayerJoined:
                HandlePlayerJoined(root);
                return;
            case NetworkMessageTypes.PlayerLeft:
                HandlePlayerLeft(root);
                return;
            case NetworkMessageTypes.Reconnect_RESPONSE:
                HandleReconnectResponse(root);
                return;
        }
    }

    internal static void HandleWelcome(JsonElement root)
    {
        try
        {
            string playerId = GetString(root, "PlayerId");
            bool isHost = GetBool(root, "IsHost");
            string sessionNonce = GetString(root, "SessionNonce");

            JsonElement listElem;
            bool hasList = root.TryGetProperty("Players", out listElem) && listElem.ValueKind == JsonValueKind.Array;
            if (!hasList)
            {
                hasList = root.TryGetProperty("PlayerList", out listElem) && listElem.ValueKind == JsonValueKind.Array;
            }

            lock (SyncLock)
            {
                _selfPlayerId = playerId;
                _selfIsHost = isHost;
                _sessionNonce = sessionNonce;
                _playerIds.Clear();
                bool onlineHostFound = isHost;
                bool graceHostFound = false;

                if (hasList)
                {
                    foreach (JsonElement p in listElem.EnumerateArray())
                    {
                        string id = GetString(p, "PlayerId");
                        bool hasConnectedField = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("IsConnected", out _);
                        bool isConnected = !hasConnectedField || GetBool(p, "IsConnected");
                        bool pHost = GetBool(p, "IsHost");

                        if (isConnected && !string.IsNullOrWhiteSpace(id))
                        {
                            _playerIds.Add(id);
                        }

                        if (pHost)
                        {
                            if (isConnected) onlineHostFound = true;
                            else graceHostFound = true;
                        }
                    }
                }

                _hasOnlineHost = onlineHostFound;
                _isHostInGracePeriod = graceHostFound && !onlineHostFound;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[NetworkIdentityTracker] HandleWelcome error: {ex.Message}");
        }
    }

    private static void HandleHostChanged(JsonElement root)
    {
        try
        {
            string newHostId = GetString(root, "NewHostId");
            if (string.IsNullOrWhiteSpace(newHostId))
            {
                return;
            }

            lock (SyncLock)
            {
                _selfIsHost = string.Equals(_selfPlayerId, newHostId, StringComparison.Ordinal);
                _hasOnlineHost = true;
                _isHostInGracePeriod = false;
            }
        }
        catch
        {

        }
    }

    internal static void HandlePlayerListUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("Players", out JsonElement playersElem) || playersElem.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        lock (SyncLock)
        {
            _playerIds.Clear();
            bool foundSelf = false;
            bool selfIsHost = _selfIsHost;
            bool onlineHostFound = _selfIsHost;
            bool graceHostFound = false;

            foreach (JsonElement p in playersElem.EnumerateArray())
            {
                string id = GetString(p, "PlayerId");
                bool hasConnectedField = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("IsConnected", out _);
                bool isConnected = !hasConnectedField || GetBool(p, "IsConnected");
                bool pHost = GetBool(p, "IsHost");

                if (isConnected && !string.IsNullOrWhiteSpace(id))
                {
                    _playerIds.Add(id);
                }

                if (!foundSelf && !string.IsNullOrWhiteSpace(_selfPlayerId) &&
                    string.Equals(id, _selfPlayerId, StringComparison.Ordinal))
                {
                    foundSelf = true;
                    selfIsHost = pHost;
                }

                if (pHost)
                {
                    if (isConnected) onlineHostFound = true;
                    else graceHostFound = true;
                }
            }

            if (foundSelf)
            {
                _selfIsHost = selfIsHost;
                if (selfIsHost) onlineHostFound = true;
            }

            _hasOnlineHost = onlineHostFound;
            _isHostInGracePeriod = graceHostFound && !onlineHostFound;
        }
    }

    private static void HandlePlayerJoined(JsonElement root)
    {
        string id = GetString(root, "PlayerId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        bool isHost = GetBool(root, "IsHost");

        lock (SyncLock)
        {
            _playerIds.Add(id);
            if (isHost)
            {
                _hasOnlineHost = true;
                _isHostInGracePeriod = false;
            }
        }
    }

    private static void HandlePlayerLeft(JsonElement root)
    {
        string id = GetString(root, "PlayerId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (SyncLock)
        {
            _playerIds.Remove(id);
        }
    }

    public static void HandleReconnectResponse(JsonElement root)
    {
        try
        {
            if (root.TryGetProperty("Success", out var sElem) && sElem.GetBoolean())
            {
                string playerId = GetString(root, "PlayerId");
                bool isHost = GetBool(root, "IsHost");
                string sessionNonce = GetString(root, "SessionNonce");

                lock (SyncLock)
                {
                    if (!string.IsNullOrWhiteSpace(playerId))
                    {
                        _selfPlayerId = playerId;
                        _playerIds.Add(playerId);
                    }
                    _selfIsHost = isHost;
                    if (isHost)
                    {
                        _hasOnlineHost = true;
                    }
                    if (!string.IsNullOrWhiteSpace(sessionNonce))
                    {
                        _sessionNonce = sessionNonce;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[NetworkIdentityTracker] HandleReconnectResponse error: {ex.Message}");
        }
    }

    private static bool TryGetJsonElement(object payload, out JsonElement root)
        => NetworkEventHelper.TryGetJsonElement(payload, out root);

    private static string GetString(JsonElement elem, string property)
    {
        try
        {
            if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
            {
                return null;
            }

            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    private static bool GetBool(JsonElement elem, string property)
    {
        try
        {
            if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
            {
                return false;
            }

            return p.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(p.GetString(), out bool b) && b,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}
