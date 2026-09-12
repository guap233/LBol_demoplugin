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
    #region 消息发送与广播

        private void BroadcastGameEvent(string eventType, string jsonPayload, int excludePeerId)
    {
        string json = jsonPayload;

        foreach (var session in SessionsByPeer.Values)
        {
            if (session.Peer.Id != excludePeerId && session.IsConnected)
            {
                try
                {
                    NetDataWriter writer = new NetDataWriter();
                    writer.Put(eventType);
                    writer.Put(json);
                    session.Peer.Send(writer, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogError($"[服务器] 广播游戏事件异常: to={session.PlayerId}, err={ex.Message}");
                    _logger?.LogError($"[服务器] 广播游戏事件异常: to={session.PlayerId}, err={ex.Message}");
                }
            }
        }
    }

        private void BroadcastMessage(string messageType, object data, int? excludePeerId = null)
    {
        foreach (var session in SessionsByPeer.Values)
        {
            if (excludePeerId.HasValue && session.Peer.Id == excludePeerId.Value)
            {
                continue;
            }

            if (session.IsConnected)
            {
                SendMessage(session.Peer, messageType, data);
            }
        }
    }

        private void SendMessage(NetPeer peer, string messageType, object data)
    {
        try
        {
            string json = JsonCompat.Serialize(data);
            MessageSentForTest?.Invoke(peer, messageType, json, DeliveryMethod.ReliableOrdered);
            NetDataWriter writer = new NetDataWriter();
            writer.Put(messageType);
            writer.Put(json);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[服务器] 发送消息异常: to={peer.EndPoint}, err={ex.Message}");
            _logger?.LogError($"[服务器] 发送消息异常: to={peer.EndPoint}, err={ex.Message}");
        }
    }

        private static int? TryGetMetadataInt(Dictionary<string, object> metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out object value) || value == null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            float f => (int)f,
            double d => (int)d,
            string s when int.TryParse(s, out int i) => i,
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out int i) => i,
            JsonElement je when je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out int i) => i,
            _ => null
        };
    }

        private static string TryGetMetadataString(Dictionary<string, object> metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out object value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => value.ToString()
        };
    }

        private void SendWelcomeMessage(NetPeer peer, PlayerSession session)
    {
        var welcomeData = new
        {
            Message = "Welcome to the server!",
            PlayerId = session.PlayerId,
            IsHost = session.IsHost,
            ReconnectToken = TryGetMetadataString(session.Metadata, "ReconnectToken"),
            SessionNonce = session.SessionNonce,
            PlayerList = _sessionsByPlayerId.Values
                .Where(s => !s.Metadata.ContainsKey("Left") && (s.IsHost || (s.Metadata.TryGetValue("IsConfirmed", out var c) && c is bool b && b)))
                .Select(s => new
            {
                PlayerId = s.PlayerId,
                PlayerName = s.PlayerName,
                IsHost = s.IsHost,
                IsConnected = s.IsConnected,
                CharacterId = TryGetMetadataString(s.Metadata, "CharacterId"),
                LocationX = TryGetMetadataInt(s.Metadata, "LocationX") ?? -1,
                LocationY = TryGetMetadataInt(s.Metadata, "LocationY") ?? -1,
                Stage = TryGetMetadataInt(s.Metadata, "Stage") ?? -1,
                LocationName = TryGetMetadataString(s.Metadata, "LocationName"),
                Ready = TryGetMetadataBool(s.Metadata, "Ready") ?? false
            }).ToList()
        };

        SendMessage(peer, NetworkMessageTypes.Welcome, welcomeData);
    }

        private void BroadcastPlayerList()
    {
        var playerList = _sessionsByPlayerId.Values
            .Where(s => !s.Metadata.ContainsKey("Left") && (s.IsHost || (s.Metadata.TryGetValue("IsConfirmed", out var c) && c is bool b && b)))
            .Select(s => new
        {
            PlayerId = s.PlayerId,
            PlayerName = s.PlayerName,
            IsHost = s.IsHost,
            IsConnected = s.IsConnected,
            CharacterId = TryGetMetadataString(s.Metadata, "CharacterId"),
            LocationX = TryGetMetadataInt(s.Metadata, "LocationX") ?? -1,
            LocationY = TryGetMetadataInt(s.Metadata, "LocationY") ?? -1,
            Stage = TryGetMetadataInt(s.Metadata, "Stage") ?? -1,
            LocationName = TryGetMetadataString(s.Metadata, "LocationName"),
            Ready = TryGetMetadataBool(s.Metadata, "Ready") ?? false,
            Hp = TryGetMetadataInt(s.Metadata, "Hp") ?? -1,
            MaxHp = TryGetMetadataInt(s.Metadata, "MaxHp") ?? -1,
        }).ToList();

        BroadcastMessage(NetworkMessageTypes.PlayerListUpdate, new { Players = playerList });
    }

        private void CleanupDisconnectedSessions()
    {
        if (_disconnectedAtByPlayerId.Count == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        List<string> expired = null;

        foreach (var kvp in _disconnectedAtByPlayerId)
        {
            if (now - kvp.Value > _reconnectGracePeriod)
            {
                expired ??= new List<string>();
                expired.Add(kvp.Key);
            }
        }

        if (expired == null)
        {
            return;
        }

        bool hostRemoved = false;
        foreach (string playerId in expired)
        {
            if (_sessionsByPlayerId.TryGetValue(playerId, out var session))
            {
                hostRemoved |= session.IsHost;
                _sessionsByPlayerId.Remove(playerId);
            }

            _disconnectedAtByPlayerId.Remove(playerId);
        }

        if (hostRemoved)
        {
            foreach (var s in _sessionsByPlayerId.Values)
            {
                s.IsHost = false;
            }

            var newHost = SessionsByPeer.Values.FirstOrDefault(s => s.IsConnected);
            if (newHost != null)
            {
                newHost.IsHost = true;
                BroadcastMessage(NetworkMessageTypes.HostChanged, new { NewHostId = newHost.PlayerId });
                Plugin.Logger?.LogInfo($"[服务器] 房主已变更为 {newHost.PlayerId}");
            }
        }

        BroadcastPlayerList();
    }

        private static bool? TryGetMetadataBool(Dictionary<string, object> metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out var value) || value == null)
            return null;
        return value switch
        {
            bool b => b,
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            _ => null
        };
    }

    #endregion
}
