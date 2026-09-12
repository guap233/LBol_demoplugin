using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.Server.Core;
using NetworkPlugin.Utils;
namespace NetworkPlugin.Network.Server;

public partial class NetworkServer
{
    #region 消息处理

        private bool IsGameEvent(string messageType)
    {
        return NetworkMessageTypes.IsGameEvent(messageType, NetworkMessageTypes.GameEventRoute.HostServer);
    }

        private void LogRouteProbeOnce(string routeKey, string details)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
        {
            return;
        }

        try
        {
            bool shouldLog;
            lock (_routeProbeOnceKeys)
            {
                shouldLog = _routeProbeOnceKeys.Add(routeKey);
            }

            if (!shouldLog)
            {
                return;
            }

            _logger?.LogInfo($"[RouteProbe] {routeKey}: {details}");
        }
        catch (Exception ex) { _logger?.LogWarning($"[RouteProbe] LogRouteProbeOnce error: {ex.Message}"); }
    }

        private PlayerSession GetConnectedHostSession()
    {
        lock (SyncRoot)
        {
            return SessionsByPeer.Values.FirstOrDefault(session => session.IsHost && session.IsConnected);
        }
    }

        private bool TryGetConnectedTargetSession(string targetPlayerId, out PlayerSession targetSession)
    {
        targetSession = null;
        return !string.IsNullOrWhiteSpace(targetPlayerId) &&
               TryGetSession(targetPlayerId, out targetSession) &&
               targetSession.IsConnected;
    }

        private static string TryGetJsonStringProperty(JsonElement root, params string[] propertyNames)
    {
        for (int index = 0; index < propertyNames.Length; index++)
        {
            string propertyName = propertyNames[index];
            if (!root.TryGetProperty(propertyName, out JsonElement propertyValue) || propertyValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string value = propertyValue.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

        private static string GetNestedPayloadJson(JsonElement root)
    {
        return root.TryGetProperty("Payload", out JsonElement payloadElement)
            ? payloadElement.GetRawText()
            : "{}";
    }

        private bool TryRouteControlledMessage(PlayerSession senderSession, string messageType, string jsonPayload)
    {
        switch (messageType)
        {
            case NetworkMessageTypes.RoomStateRequest:
                RouteRoomStateRequest(senderSession, jsonPayload);
                return true;
            case NetworkMessageTypes.RoomStateUpload:
                RouteRoomStateUpload(senderSession, jsonPayload);
                return true;
            case NetworkMessageTypes.RoomStateResponse:
                RouteRoomStateResponse(senderSession, jsonPayload);
                return true;
            case NetworkMessageTypes.FullStateSyncRequest:
                RouteFullStateSyncRequest(senderSession, jsonPayload);
                return true;
            case NetworkMessageTypes.FullStateSyncResponse:
                RouteFullStateSyncResponse(senderSession, jsonPayload);
                return true;
            default:
                return false;
        }
    }

        private bool TryRouteHostRequest(PlayerSession senderSession, string messageType, string jsonPayload)
    {
        if (!NetworkMessageTypes.IsHostRequest(messageType))
        {
            return false;
        }

        try
        {
            PlayerSession hostSession = GetConnectedHostSession();
            if (hostSession == null)
            {
                Plugin.Logger?.LogWarning($"[服务器] 收到 Host 请求 {messageType}，但未找到已连接的 Host 会话（来自 {senderSession?.PlayerId}）");
                return true;
            }

            if (!string.IsNullOrWhiteSpace(jsonPayload) && IsImpersonating(senderSession, jsonPayload, out string claimedId))
            {
                string warn = $"[安全拦截] 客户端 {senderSession?.PlayerId} 企图在 {messageType} 中冒充 {claimedId}，已拦截";
                Plugin.Logger?.LogWarning(warn);
                _logger?.LogWarning(warn);
                return true;
            }

            LogRouteProbeOnce($"HostRequest/{messageType}",
                $"sender={senderSession?.PlayerId}, host={hostSession.PlayerId}");

            SendRawJsonToPeer(hostSession.Peer, messageType, jsonPayload);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 路由 Host 请求异常: type={messageType}, err={ex.Message}");
            return true;
        }
    }

    internal static bool IsImpersonating(PlayerSession senderSession, string jsonPayload, out string claimedId)
    {
        claimedId = string.Empty;
        if (senderSession == null || string.IsNullOrWhiteSpace(senderSession.PlayerId))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonPayload);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string[] senderIdProperties = { "RequesterPlayerId", "RequesterId", "PlayerId", "VoterId", "ClientPlayerId", "SenderPlayerId", "UploaderId", "OwnerPlayerId" };
            foreach (string prop in senderIdProperties)
            {
                if (root.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String)
                {
                    string val = el.GetString();
                    if (!string.IsNullOrWhiteSpace(val) && !string.Equals(val, senderSession.PlayerId, StringComparison.Ordinal))
                    {
                        claimedId = val;
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[NetworkServer] IsImpersonating check failed: {ex.Message}");
        }

        return false;
    }

        private void RouteRoomStateRequest(PlayerSession senderSession, string jsonPayload)
    {
        try
        {
            PlayerSession hostSession = GetConnectedHostSession();
            if (hostSession == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(jsonPayload) && IsImpersonating(senderSession, jsonPayload, out string claimedId))
            {
                string warn = $"[安全拦截] 客户端 {senderSession?.PlayerId} 企图在 RoomStateRequest 中冒充 {claimedId}，已拦截";
                Plugin.Logger?.LogWarning(warn);
                _logger?.LogWarning(warn);
                return;
            }

            LogRouteProbeOnce(NetworkMessageTypes.RoomStateRequest,
                $"sender={senderSession?.PlayerId}, host={hostSession.PlayerId}");

            SendRawJsonToPeer(hostSession.Peer, NetworkMessageTypes.RoomStateRequest, jsonPayload);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 路由 RoomStateRequest 异常: {ex.Message}");
        }
    }

        private void RouteRoomStateUpload(PlayerSession senderSession, string jsonPayload)
    {
        try
        {
            PlayerSession hostSession = GetConnectedHostSession();
            if (hostSession == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(jsonPayload) && IsImpersonating(senderSession, jsonPayload, out string claimedId))
            {
                string warn = $"[安全拦截] 客户端 {senderSession?.PlayerId} 企图在 RoomStateUpload 中冒充 {claimedId}，已拦截";
                Plugin.Logger?.LogWarning(warn);
                _logger?.LogWarning(warn);
                return;
            }

            LogRouteProbeOnce(NetworkMessageTypes.RoomStateUpload,
                $"sender={senderSession?.PlayerId}, host={hostSession.PlayerId}");

            SendRawJsonToPeer(hostSession.Peer, NetworkMessageTypes.RoomStateUpload, jsonPayload);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 路由 RoomStateUpload 异常: {ex.Message}");
        }
    }

        private void RouteRoomStateResponse(PlayerSession senderSession, string jsonPayload)
    {
        try
        {
            if (!senderSession.IsHost)
            {
                return;
            }

            JsonElement root = JsonSerializer.Deserialize<JsonElement>(jsonPayload);
            string targetPlayerId = TryGetJsonStringProperty(root, "TargetPlayerId", "RequesterId");

            if (string.IsNullOrWhiteSpace(targetPlayerId))
            {
                return;
            }

            if (!TryGetConnectedTargetSession(targetPlayerId, out PlayerSession targetSession))
            {
                return;
            }

            LogRouteProbeOnce(NetworkMessageTypes.RoomStateResponse,
                $"host={senderSession?.PlayerId}, target={targetPlayerId}");

            SendRawJsonToPeer(targetSession.Peer, NetworkMessageTypes.RoomStateResponse, jsonPayload);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 路由 RoomStateResponse 异常: {ex.Message}");
        }
    }

        private void RouteFullStateSyncRequest(PlayerSession senderSession, string jsonPayload)
    {
        try
        {

            PlayerSession hostSession = GetConnectedHostSession();
            if (hostSession == null)
            {
                return;
            }

            LogRouteProbeOnce(NetworkMessageTypes.FullStateSyncRequest,
                $"sender={senderSession?.PlayerId}, host={hostSession.PlayerId}");

            JsonElement root = JsonSerializer.Deserialize<JsonElement>(jsonPayload);
            string targetPlayerId = TryGetJsonStringProperty(root, "TargetPlayerId");
            if (!string.IsNullOrWhiteSpace(targetPlayerId) && !string.Equals(targetPlayerId, senderSession.PlayerId, StringComparison.Ordinal))
            {
                return;
            }

            SendRawJsonToPeer(hostSession.Peer, NetworkMessageTypes.FullStateSyncRequest, jsonPayload);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 路由 FullStateSyncRequest 异常: {ex.Message}");
        }
    }

        private void RouteFullStateSyncResponse(PlayerSession senderSession, string jsonPayload)
    {
        try
        {
            if (!senderSession.IsHost)
            {
                return;
            }

            JsonElement root = JsonSerializer.Deserialize<JsonElement>(jsonPayload);
            string targetPlayerId = TryGetJsonStringProperty(root, "TargetPlayerId");
            if (string.IsNullOrWhiteSpace(targetPlayerId))
            {
                return;
            }

            if (!TryGetConnectedTargetSession(targetPlayerId, out PlayerSession targetSession))
            {
                return;
            }

            LogRouteProbeOnce(NetworkMessageTypes.FullStateSyncResponse,
                $"host={senderSession?.PlayerId}, target={targetPlayerId}");

            SendRawJsonToPeer(targetSession.Peer, NetworkMessageTypes.FullStateSyncResponse, jsonPayload);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 路由 FullStateSyncResponse 异常: {ex.Message}");
        }
    }

        private void SendRawJsonToPeer(NetPeer peer, string messageType, string jsonPayload)
    {
        try
        {
            NetDataWriter writer = new();
            writer.Put(messageType);
            writer.Put(jsonPayload ?? string.Empty);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 发送原始消息异常: type={messageType}, err={ex.Message}");
        }
    }

        private void HandleHeartbeat(NetPeer fromPeer)
    {
        if (TryGetSession(fromPeer, out var session))
        {
            session.UpdateHeartbeat();

            SendMessage(fromPeer, NetworkMessageTypes.HeartbeatResponse, new
            {
                Timestamp = DateTime.UtcNow.Ticks,
                Ping = session.Ping
            });
        }
    }

    #endregion
}
