using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Extensions.Logging;
using NetworkPlugin.Configuration;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.Room;
using NetworkPlugin.Utils;
using NetworkPlugin.Network.Server.Core;
using NetworkPlugin.Network.Utils;
using NetworkPlugin.Network.Security;

namespace NetworkPlugin.Network.Server;

public class RelayServer : BaseGameServer
{
    #region 私有字段

        private readonly ILogger<RelayServer> _logger;
        private readonly ConfigManager _configManager;

        private object _lock => SyncRoot;

        private readonly Dictionary<string, NetworkRoom> _rooms = [];

        private Dictionary<NetPeer, PlayerSession> _sessionsByPeer => SessionsByPeer;

        private Dictionary<string, PlayerSession> _sessionsByPlayerId => SessionsByPlayerId;

        private readonly Dictionary<string, NetworkConnection> _connectionsByPlayerId = [];

        private Dictionary<string, DateTime> _disconnectedAtByPlayerId => DisconnectedAtByPlayerId;

        private readonly TimeSpan _reconnectGracePeriod = TimeSpan.FromSeconds(60);

        private IServerCore _core => Core;

    #endregion

    #region 构造函数

    public RelayServer(ILogger<RelayServer> logger, IServiceProvider serviceProvider, ConfigManager configManager)
        : base(CreateCore(configManager, logger))
    {
        _logger = logger;
        _configManager = configManager;
    }

    internal RelayServer(ILogger<RelayServer> logger, IServiceProvider serviceProvider, ConfigManager configManager, IServerCore core)
        : base(core)
    {
        _logger = logger;
        _configManager = configManager;
    }

    internal void AddSessionForTest(NetPeer peer, PlayerSession session)
    {
        lock (_lock)
        {
            SessionsByPeer[peer] = session;
            SessionsByPlayerId[session.PlayerId] = session;
        }
    }

    internal Dictionary<string, NetworkRoom> GetRoomsForTest() => _rooms;
    internal Dictionary<string, NetworkConnection> GetConnectionsForTest() => _connectionsByPlayerId;
    internal void AddPeerMappingForTest(NetPeer peer, PlayerSession session)
    {
        lock (_lock)
        {
            SessionsByPeer[peer] = session;
        }
    }

    #endregion

    #region 生命周期

        public override void Start()
    {

        _core.Start();
        _logger.LogInformation($"[RelayServer] Server started on port {_configManager.RelayServerPort.Value}");
    }

        public override void Stop()
    {
        _logger.LogInformation("[RelayServer] Stopping server...");
        _core.Stop();

        lock (_lock)
        {
            _connectionsByPlayerId.Clear();
            _sessionsByPlayerId.Clear();
            _sessionsByPeer.Clear();
            _disconnectedAtByPlayerId.Clear();
            _rooms.Clear();
        }

        _logger.LogInformation("[RelayServer] Server stopped");
    }

    #endregion

    #region 消息路由与房间协议（Relay）

    internal void ProcessMessage(NetPeer fromPeer, NetworkMessage message, DeliveryMethod deliveryMethod)
    {
        lock (_lock)
        {

            if (!_sessionsByPeer.TryGetValue(fromPeer, out var session))
            {
                return;
            }

            session.UpdateMessageTime();

            switch (message.Type)
            {
                case NetworkMessageTypes.CreateRoom:

                    HandleCreateRoom(session, message);
                    return;
                case NetworkMessageTypes.JoinRoom:

                    HandleJoinRoom(session, message);
                    return;
                case NetworkMessageTypes.LeaveRoom:

                    HandleLeaveRoom(session);
                    return;
                case NetworkMessageTypes.RoomMessage:

                    HandleRoomMessage(session, message, deliveryMethod);
                    return;
                case NetworkMessageTypes.DirectMessage:

                    HandleDirectMessage(session, message, deliveryMethod);
                    return;
                case NetworkMessageTypes.Heartbeat:

                    HandleHeartbeat(session, fromPeer);
                    return;
                case NetworkMessageTypes.GetRoomList:

                    HandleGetRoomList(fromPeer);
                    return;
                case NetworkMessageTypes.KickPlayer:

                    HandleKickPlayer(session, message);
                    return;
                case NetworkMessageTypes.Reconnect_REQUEST:

                    HandleReconnectRequest(fromPeer, message);
                    return;

                case NetworkMessageTypes.NatInfoReport:

                    HandleNatInfoReport(session, message);
                    return;
                case NetworkMessageTypes.NatInfoRequest:

                    HandleNatInfoRequest(session, message);
                    return;

                case NetworkMessageTypes.PlayerJoined:

                    HandlePlayerJoined(session, message);
                    return;
                case NetworkMessageTypes.GetSelf_REQUEST:

                    HandleGetSelfRequest(session, fromPeer);
                    return;
                case NetworkMessageTypes.UpdatePlayerLocation:

                    HandleUpdatePlayerLocation(session, message);
                    return;
                case NetworkMessageTypes.PlayerReadyChanged:

                    HandlePlayerReadyChanged(session, message);
                    return;

                case NetworkMessageTypes.FullStateSyncRequest:

                    HandleFullStateSyncRequest(session, message, deliveryMethod);
                    return;

                case NetworkMessageTypes.FullStateSyncResponse:

                    HandleFullStateSyncResponse(session, message, deliveryMethod);
                    return;

                case NetworkMessageTypes.RoomStateRequest:
                    HandleRoomStateRequest(session, message, deliveryMethod);
                    return;

                case NetworkMessageTypes.RoomStateUpload:
                    HandleRoomStateUpload(session, message, deliveryMethod);
                    return;

                case NetworkMessageTypes.RoomStateResponse:
                    HandleRoomStateResponse(session, message, deliveryMethod);
                    return;
            }

            if (IsGameEvent(message.Type))
            {
                ForwardGameEventToRoom(session, message.Type, message.Payload, deliveryMethod);
                return;
            }

            _logger.LogWarning($"[RelayServer] Unknown message type: {message.Type}");
        }
    }

    #endregion

    #region 心跳与大厅

        private void HandleHeartbeat(PlayerSession session, NetPeer peer)
    {

        session.UpdateHeartbeat();

        SendMessageToPeer(peer, new NetworkMessage
        {
            Type = NetworkMessageTypes.HeartbeatResponse,
            Payload = new
            {
                Timestamp = DateTime.UtcNow.Ticks,
                Ping = session.Ping
            },
            SenderPlayerId = "SERVER"
        }, DeliveryMethod.ReliableOrdered);
    }

        private void HandleGetRoomList(NetPeer peer)
    {
        List<RoomStatus> rooms = _rooms.Values.Select(r =>
        {
            var status = r.GetStatus();
            if (r.Config != null && !r.Config.IsPublic)
            {
                // Mask sensitive member details for private rooms
                status.PlayerIds = new List<string>();
                status.HostPlayerId = "[Protected]";
            }
            return status;
        }).ToList();

        SendMessageToPeer(peer, new NetworkMessage
        {
            Type = NetworkMessageTypes.RoomList,
            Payload = new { Rooms = rooms },
            SenderPlayerId = "SERVER"
        }, DeliveryMethod.ReliableOrdered);
    }

    #endregion

    #region 房间管理

    private void HandleCreateRoom(PlayerSession session, NetworkMessage message)
    {
        if (!string.IsNullOrEmpty(session.CurrentRoomId))
        {
            HandleLeaveRoom(session);
        }

        var relayConfig = _configManager.GetRelayServerConfig();
        if (_rooms.Count >= relayConfig.MaxRooms)
        {
            SendErrorMessage(session.Peer, "CreateRoomFailed", $"Relay server max rooms limit ({relayConfig.MaxRooms}) reached");
            return;
        }

        RoomConfig roomConfig = message.GetRoomConfigPayload() ?? RoomConfig.Default();
        int maxAllowed = relayConfig.MaxPlayersPerRoom > 0 ? relayConfig.MaxPlayersPerRoom : 8;
        if (roomConfig.MaxPlayers < 1 || roomConfig.MaxPlayers > maxAllowed)
        {
            SendErrorMessage(session.Peer, "CreateRoomFailed", $"MaxPlayers must be between 1 and {maxAllowed}");
            return;
        }

        string roomId = GenerateRoomId();
        NetworkRoom room = new(roomId, roomConfig, _logger);
        _rooms[roomId] = room;

        if (!_connectionsByPlayerId.TryGetValue(session.PlayerId, out var connection))
        {
            connection = new NetworkConnection(session.Peer, session.PlayerId);
            _connectionsByPlayerId[session.PlayerId] = connection;
        }

        var joinResult = room.AddPlayer(session.PlayerId, connection);
        if (!joinResult.IsSuccess)
        {
            // Atomic rollback
            _rooms.Remove(roomId);
            SendErrorMessage(session.Peer, "CreateRoomFailed", joinResult.ErrorMessage ?? "Unknown error");
            return;
        }

        session.CurrentRoomId = roomId;
        connection.CurrentRoomId = roomId;

        SendMessageToPeer(session.Peer, new NetworkMessage
        {
            Type = NetworkMessageTypes.RoomCreated,
            Payload = new
            {
                RoomId = roomId,
                RoomConfig = roomConfig,
                PlayerId = session.PlayerId
            },
            SenderPlayerId = "SERVER"
        }, DeliveryMethod.ReliableOrdered);

        BroadcastPlayerList(room);
        _logger.LogInformation($"[RelayServer] Room created: {roomId} by player {session.PlayerId}");
    }

    private void HandleJoinRoom(PlayerSession session, NetworkMessage message)
    {
        string roomId = TryGetStringProperty(message.Payload, "RoomId") ?? session.CurrentRoomId;
        if (string.IsNullOrEmpty(roomId))
        {
            SendErrorMessage(session.Peer, "JoinRoomFailed", "Missing RoomId");
            return;
        }

        if (!_rooms.TryGetValue(roomId, out var room))
        {
            SendErrorMessage(session.Peer, "JoinRoomFailed", $"Room not found: {roomId}");
            return;
        }

        if (room.IsFull)
        {
            SendErrorMessage(session.Peer, "JoinRoomFailed", "Room is full");
            return;
        }

        string password = TryGetStringProperty(message.Payload, "Password");
        if (!room.Config.IsPublic && string.IsNullOrWhiteSpace(password) && !string.Equals(room.HostPlayerId, session.PlayerId, StringComparison.Ordinal))
        {
            SendErrorMessage(session.Peer, "JoinRoomFailed", "Cannot join private room without authorization");
            return;
        }

        if (!string.IsNullOrEmpty(room.Config.Password))
        {
            if (string.IsNullOrEmpty(password) || !string.Equals(room.Config.Password, password, StringComparison.Ordinal))
            {
                SendErrorMessage(session.Peer, "JoinRoomFailed", "Invalid room password");
                return;
            }
        }

        if (!string.IsNullOrEmpty(session.CurrentRoomId))
        {
            HandleLeaveRoom(session);
        }

        if (!_connectionsByPlayerId.TryGetValue(session.PlayerId, out var connection))
        {
            connection = new NetworkConnection(session.Peer, session.PlayerId);
            _connectionsByPlayerId[session.PlayerId] = connection;
        }

        var joinResult = room.AddPlayer(session.PlayerId, connection);
        if (!joinResult.IsSuccess)
        {
            SendErrorMessage(session.Peer, "JoinRoomFailed", joinResult.ErrorMessage ?? "Unknown error");
            return;
        }

        session.CurrentRoomId = roomId;
        connection.CurrentRoomId = roomId;

        SendMessageToPeer(session.Peer, new NetworkMessage
        {
            Type = NetworkMessageTypes.RoomJoined,
            Payload = new { RoomId = roomId, PlayerId = session.PlayerId },
            SenderPlayerId = "SERVER"
        }, DeliveryMethod.ReliableOrdered);

        BroadcastPlayerList(room);
    }

        private void HandleLeaveRoom(PlayerSession session)
    {

        if (string.IsNullOrEmpty(session.CurrentRoomId))
        {
            return;
        }

        if (_rooms.TryGetValue(session.CurrentRoomId, out var room))
        {
            room.RemovePlayer(session.PlayerId);

            room.BroadcastMessage(new NetworkMessage
            {
                Type = NetworkMessageTypes.PlayerLeft,
                Payload = new { PlayerId = session.PlayerId },
                SenderPlayerId = "SERVER"
            }, excludePlayerId: session.PlayerId);

            BroadcastPlayerList(room);

            if (room.PlayerCount == 0)
            {

                _rooms.Remove(room.RoomId);
                _logger.LogInformation($"[RelayServer] Room {room.RoomId} destroyed (empty)");
            }
        }

        if (_connectionsByPlayerId.TryGetValue(session.PlayerId, out var connection))
        {
            connection.CurrentRoomId = string.Empty;
        }

        session.CurrentRoomId = string.Empty;
    }

        private void HandleRoomMessage(PlayerSession session, NetworkMessage message, DeliveryMethod deliveryMethod)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId) || !_rooms.TryGetValue(session.CurrentRoomId, out var room))
        {
            SendErrorMessage(session.Peer, "RoomMessageFailed", "Not in room");
            return;
        }

        if (!room.ContainsPlayer(session.PlayerId))
        {
            SendErrorMessage(session.Peer, "RoomMessageFailed", "Not in room");
            return;
        }

        string innerType = TryGetStringProperty(message.Payload, "Type") ?? NetworkMessageTypes.RoomMessage;
        object innerPayload = TryGetObjectProperty(message.Payload, "Payload") ?? message.Payload;

        room.BroadcastMessage(new NetworkMessage
        {
            Type = innerType,
            Payload = innerPayload,
            SenderPlayerId = session.PlayerId
        }, excludePlayerId: session.PlayerId);
    }

    private void HandleDirectMessage(PlayerSession session, NetworkMessage message, DeliveryMethod deliveryMethod)
    {
        string targetPlayerId = TryGetStringProperty(message.Payload, "TargetPlayerId");
        string innerType = TryGetStringProperty(message.Payload, "Type") ?? NetworkMessageTypes.DirectMessage;
        object innerPayload = TryGetObjectProperty(message.Payload, "Payload") ?? message.Payload;

        if (string.IsNullOrEmpty(session.CurrentRoomId) || !_rooms.TryGetValue(session.CurrentRoomId, out var room) || !room.ContainsPlayer(session.PlayerId))
        {
            SendErrorMessage(session.Peer, "DirectMessageFailed", "Sender not in room");
            return;
        }

        if (string.IsNullOrEmpty(targetPlayerId) || !room.ContainsPlayer(targetPlayerId) || !_connectionsByPlayerId.TryGetValue(targetPlayerId, out var connection))
        {
            SendErrorMessage(session.Peer, "DirectMessageFailed", "Target not in same room");
            return;
        }

        if (!NetworkMessagePolicy.IsRegistered(innerType))
        {
            SendErrorMessage(session.Peer, "DirectMessageFailed", $"Unregistered inner message type: {innerType}");
            return;
        }

        if (string.Equals(innerType, NetworkMessageTypes.FullStateSyncRequest, StringComparison.Ordinal))
        {
            HandleFullStateSyncRequest(session, new NetworkMessage
            {
                Type = innerType,
                Payload = innerPayload,
                SenderPlayerId = session.PlayerId
            }, deliveryMethod);
            return;
        }

        if (string.Equals(innerType, NetworkMessageTypes.FullStateSyncResponse, StringComparison.Ordinal))
        {
            HandleFullStateSyncResponse(session, new NetworkMessage
            {
                Type = innerType,
                Payload = innerPayload,
                SenderPlayerId = session.PlayerId
            }, deliveryMethod);
            return;
        }

        connection.SendMessage(new NetworkMessage
        {
            Type = innerType,
            Payload = innerPayload,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

    private void HandleKickPlayer(PlayerSession session, NetworkMessage message)
    {
        if (string.IsNullOrEmpty(session.CurrentRoomId) || !_rooms.TryGetValue(session.CurrentRoomId, out var room))
        {
            SendErrorMessage(session.Peer, "KickPlayerFailed", "Not in room");
            return;
        }

        if (!string.Equals(room.HostPlayerId, session.PlayerId, StringComparison.Ordinal))
        {
            SendErrorMessage(session.Peer, "KickPlayerFailed", "Only host can kick");
            return;
        }

        string targetPlayerId = TryGetStringProperty(message.Payload, "TargetPlayerId");
        if (string.IsNullOrEmpty(targetPlayerId) || !room.ContainsPlayer(targetPlayerId))
        {
            SendErrorMessage(session.Peer, "KickPlayerFailed", "Target not in room");
            return;
        }

        room.RemovePlayer(targetPlayerId);

        // Atomic cleanup of kicked player's room session and connection
        if (_sessionsByPlayerId.TryGetValue(targetPlayerId, out var targetSession))
        {
            targetSession.CurrentRoomId = string.Empty;
        }

        if (_connectionsByPlayerId.TryGetValue(targetPlayerId, out var targetConnection))
        {
            targetConnection.CurrentRoomId = string.Empty;
            SendMessageToPeer(targetConnection.Peer, new NetworkMessage
            {
                Type = NetworkMessageTypes.KickPlayer,
                Payload = new { Reason = "KickedByHost", RoomId = room.RoomId },
                SenderPlayerId = "SERVER"
            }, DeliveryMethod.ReliableOrdered);
        }

        BroadcastPlayerList(room);
    }

    #endregion

    #region 重连与 NAT 穿透

        private sealed class ReconnectRequest
    {
        public string PlayerId { get; set; } = string.Empty;
        public string ReconnectToken { get; set; } = string.Empty;
    }

        private void HandleReconnectRequest(NetPeer fromPeer, NetworkMessage message)
    {
        try
        {
            string playerId = TryGetStringProperty(message.Payload, "PlayerId");
            string reconnectToken = TryGetStringProperty(message.Payload, "ReconnectToken");
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(reconnectToken))
            {
                SendMessageToPeer(fromPeer, new NetworkMessage { Type = NetworkMessageTypes.Reconnect_RESPONSE, Payload = new { Success = false, Error = "Invalid request" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            lock (_lock)
            {
                if (!_sessionsByPlayerId.TryGetValue(playerId, out var session))
                {
                    SendMessageToPeer(fromPeer, new NetworkMessage { Type = NetworkMessageTypes.Reconnect_RESPONSE, Payload = new { Success = false, Error = "Unknown playerId" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                if (session.IsConnected && session.Peer == fromPeer)
                {
                    SendMessageToPeer(fromPeer, new NetworkMessage { Type = NetworkMessageTypes.Reconnect_RESPONSE, Payload = new { Success = false, Error = "Already connected" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                string expectedToken = TryGetMetadataString(session.Metadata, "ReconnectToken");
                if (!string.Equals(expectedToken, reconnectToken, StringComparison.Ordinal))
                {
                    SendMessageToPeer(fromPeer, new NetworkMessage { Type = NetworkMessageTypes.Reconnect_RESPONSE, Payload = new { Success = false, Error = "Invalid token" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                if (_disconnectedAtByPlayerId.TryGetValue(playerId, out var disconnectedAt) &&
                    DateTime.UtcNow - disconnectedAt > _reconnectGracePeriod)
                {
                    SendMessageToPeer(fromPeer, new NetworkMessage { Type = NetworkMessageTypes.Reconnect_RESPONSE, Payload = new { Success = false, Error = "Reconnect window expired" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                if (session.Peer != null && session.Peer != fromPeer)
                {
                    _sessionsByPeer.Remove(session.Peer);
                }

                session.Peer = fromPeer;
                session.IsConnected = true;
                session.UpdateHeartbeat();
                session.UpdateMessageTime();

                _sessionsByPeer[fromPeer] = session;

                var conn = new NetworkConnection(fromPeer, session.PlayerId, session.CurrentRoomId);
                _connectionsByPlayerId[session.PlayerId] = conn;

                if (!string.IsNullOrEmpty(session.CurrentRoomId) && _rooms.TryGetValue(session.CurrentRoomId, out var room))
                {
                    if (!room.ContainsPlayer(session.PlayerId))
                    {
                        room.AddPlayer(session.PlayerId, conn);
                    }
                    BroadcastPlayerList(room);
                }

                _disconnectedAtByPlayerId.Remove(session.PlayerId);

                string newToken = GenerateReconnectToken();
                session.Metadata["ReconnectToken"] = newToken;

                SendMessageToPeer(fromPeer, new NetworkMessage
                {
                    Type = NetworkMessageTypes.Reconnect_RESPONSE,
                    Payload = new
                    {
                        Success = true,
                        PlayerId = session.PlayerId,
                        PlayerName = session.PlayerName,
                        CurrentRoomId = session.CurrentRoomId,
                        ReconnectToken = newToken
                    },
                    SenderPlayerId = "SERVER"
                }, DeliveryMethod.ReliableOrdered);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[RelayServer] Error handling {NetworkMessageTypes.Reconnect_REQUEST}");
            SendMessageToPeer(fromPeer, new NetworkMessage { Type = NetworkMessageTypes.Reconnect_RESPONSE, Payload = new { Success = false, Error = "Server error" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
        }
    }

        private void HandleNatInfoReport(PlayerSession session, NetworkMessage message)
    {
        try
        {

            JsonElement root = GetJsonElement(message.Payload);
            if (root.ValueKind != JsonValueKind.Object)
            {

                SendMessageToPeer(session.Peer, new NetworkMessage { Type = NetworkMessageTypes.NatError, Payload = new { Error = "Invalid payload" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            NatTraversal.NatInfo natInfo = JsonSerializer.Deserialize<NatTraversal.NatInfo>(root.GetRawText());
            if (natInfo == null)
            {
                SendMessageToPeer(session.Peer, new NetworkMessage { Type = NetworkMessageTypes.NatError, Payload = new { Error = "Invalid natInfo" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            NatTraversal.RegisterPeerNatInfo(session.PlayerId, natInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RelayServer] Error handling NatInfoReport");
        }
    }

        private void HandleNatInfoRequest(PlayerSession session, NetworkMessage message)
    {
        try
        {

            string targetPlayerId = TryGetStringProperty(message.Payload, "TargetPlayerId");
            if (string.IsNullOrWhiteSpace(targetPlayerId))
            {
                SendMessageToPeer(session.Peer, new NetworkMessage { Type = NetworkMessageTypes.NatError, Payload = new { Error = "Missing TargetPlayerId" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            if (string.IsNullOrEmpty(session.CurrentRoomId) || !_rooms.TryGetValue(session.CurrentRoomId, out var room) || !room.ContainsPlayer(session.PlayerId))
            {
                SendMessageToPeer(session.Peer, new NetworkMessage { Type = NetworkMessageTypes.NatError, Payload = new { Error = "Sender not in room" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            if (!room.ContainsPlayer(targetPlayerId))
            {
                SendMessageToPeer(session.Peer, new NetworkMessage { Type = NetworkMessageTypes.NatError, Payload = new { Error = "Target not in same room" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            NatTraversal.NatInfo natInfo = NatTraversal.GetPeerNatInfo(targetPlayerId);
            if (natInfo == null)
            {
                SendMessageToPeer(session.Peer, new NetworkMessage { Type = NetworkMessageTypes.NatError, Payload = new { Error = "Nat info not found" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            SendMessageToPeer(session.Peer, new NetworkMessage
            {
                Type = NetworkMessageTypes.NatInfoResponse,
                Payload = new { TargetPlayerId = targetPlayerId, NatInfo = natInfo },
                SenderPlayerId = "SERVER"
            }, DeliveryMethod.ReliableOrdered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RelayServer] Error handling NatInfoRequest");
            SendMessageToPeer(session.Peer, new NetworkMessage { Type = NetworkMessageTypes.NatError, Payload = new { Error = "Server error" }, SenderPlayerId = "SERVER" }, DeliveryMethod.ReliableOrdered);
        }
    }

    #endregion

    #region 玩家信息与状态（兼容消息）

        private void HandlePlayerJoined(PlayerSession session, NetworkMessage message)
    {
        try
        {

            JsonElement root = GetJsonElement(message.Payload);
            if (root.ValueKind == JsonValueKind.Object)
            {

                if (root.TryGetProperty("PlayerName", out var nameElem) && nameElem.ValueKind == JsonValueKind.String)
                {
                    session.PlayerName = nameElem.GetString();
                }

                if (root.TryGetProperty("CharacterId", out var charElem) && charElem.ValueKind == JsonValueKind.String)
                {
                    session.Metadata["CharacterId"] = charElem.GetString();
                }
            }

            if (!string.IsNullOrEmpty(session.CurrentRoomId) && _rooms.TryGetValue(session.CurrentRoomId, out var room))
            {

                room.BroadcastMessage(new NetworkMessage
                {
                    Type = NetworkMessageTypes.PlayerJoined,
                    Payload = new
                    {
                        PlayerId = session.PlayerId,
                        PlayerName = session.PlayerName,
                        IsHost = string.Equals(room.HostPlayerId, session.PlayerId, StringComparison.Ordinal),
                        CharacterId = session.Metadata.TryGetValue("CharacterId", out var cid) ? cid?.ToString() : null
                    },
                    SenderPlayerId = "SERVER"
                }, excludePlayerId: session.PlayerId);

                BroadcastPlayerList(room);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RelayServer] Error handling PlayerJoined");
        }
    }

        private void HandleGetSelfRequest(PlayerSession session, NetPeer peer)
    {

        SendMessageToPeer(peer, new NetworkMessage
        {
            Type = NetworkMessageTypes.GetSelf_RESPONSE,
            Payload = new
            {
                PlayerId = session.PlayerId,
                PlayerName = session.PlayerName,
                IsHost = IsRoomHost(session),
                ConnectedAt = session.ConnectedAt.Ticks
            },
            SenderPlayerId = "SERVER"
        }, DeliveryMethod.ReliableOrdered);
    }

        private void HandleUpdatePlayerLocation(PlayerSession session, NetworkMessage message)
    {
        try
        {

            JsonElement root = GetJsonElement(message.Payload);
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("LocationX", out var xElem) && xElem.TryGetInt32(out int x))
            {
                session.Metadata["LocationX"] = x;
            }
            if (root.TryGetProperty("LocationY", out var yElem) && yElem.TryGetInt32(out int y))
            {
                session.Metadata["LocationY"] = y;
            }
            if (root.TryGetProperty("Stage", out var stageElem) && stageElem.TryGetInt32(out int stage))
            {
                session.Metadata["Stage"] = stage;
            }
            if (root.TryGetProperty("LocationName", out var nameElem) && nameElem.ValueKind == JsonValueKind.String)
            {
                session.Metadata["LocationName"] = nameElem.GetString();
            }
            if (root.TryGetProperty("CharacterId", out var charElem) && charElem.ValueKind == JsonValueKind.String)
            {
                session.Metadata["CharacterId"] = charElem.GetString();
            }
            if (root.TryGetProperty("Hp", out var hpElem) && hpElem.TryGetInt32(out int hp))
            {
                session.Metadata["Hp"] = hp;
            }
            if (root.TryGetProperty("MaxHp", out var maxHpElem) && maxHpElem.TryGetInt32(out int maxHp))
            {
                session.Metadata["MaxHp"] = maxHp;
            }

            if (!string.IsNullOrEmpty(session.CurrentRoomId) && _rooms.TryGetValue(session.CurrentRoomId, out var room))
            {

                BroadcastPlayerList(room);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RelayServer] Error handling UpdatePlayerLocation");
        }
    }

        private void HandlePlayerReadyChanged(PlayerSession session, NetworkMessage message)
    {
        try
        {
            JsonElement root = GetJsonElement(message.Payload);
            if (root.ValueKind != JsonValueKind.Object) return;

            bool isReady = root.TryGetProperty("IsReady", out var readyProp) && readyProp.ValueKind == JsonValueKind.True;
            session.Metadata["Ready"] = isReady;

            _logger.LogInformation("[RelayServer] Player {PlayerId} ready state: {IsReady}", session.PlayerId, isReady);

            if (!string.IsNullOrEmpty(session.CurrentRoomId) && _rooms.TryGetValue(session.CurrentRoomId, out var room))
            {
                BroadcastPlayerList(room);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RelayServer] Error handling PlayerReadyChanged");
        }
    }

    #endregion

    #region 游戏事件转发与成员列表广播

        private void ForwardGameEventToRoom(PlayerSession session, string eventType, object payload, DeliveryMethod deliveryMethod)
    {

        if (string.IsNullOrEmpty(session.CurrentRoomId) || !_rooms.TryGetValue(session.CurrentRoomId, out var room))
        {
            return;
        }

        room.BroadcastMessage(new NetworkMessage
        {
            Type = eventType,
            Payload = payload,
            SenderPlayerId = session.PlayerId
        }, excludePlayerId: session.PlayerId);
    }

        private void BroadcastPlayerList(NetworkRoom room)
    {

        var players = room.GetAllPlayerIds()
            .Select(pid => _sessionsByPlayerId.TryGetValue(pid, out var s) ? s : null)
            .Where(s => s != null)
            .Select(s => new
            {
                PlayerId = s.PlayerId,
                PlayerName = s.PlayerName,
                IsHost = string.Equals(room.HostPlayerId, s.PlayerId, StringComparison.Ordinal),
                IsConnected = s.IsConnected,
                CharacterId = s.Metadata.TryGetValue("CharacterId", out var cid) ? cid?.ToString() : null,
                LocationX = TryGetMetadataInt(s.Metadata, "LocationX") ?? -1,
                LocationY = TryGetMetadataInt(s.Metadata, "LocationY") ?? -1,
                Stage = TryGetMetadataInt(s.Metadata, "Stage") ?? -1,
                LocationName = TryGetMetadataString(s.Metadata, "LocationName"),
                Ready = s.Metadata.TryGetValue("Ready", out var rdy) && rdy is bool b && b,
                Hp = TryGetMetadataInt(s.Metadata, "Hp") ?? -1,
                MaxHp = TryGetMetadataInt(s.Metadata, "MaxHp") ?? -1,
            })
            .ToList();

        room.BroadcastMessage(new NetworkMessage
        {
            Type = NetworkMessageTypes.PlayerListUpdate,
            Payload = new { Players = players },
            SenderPlayerId = "SERVER"
        });
    }

        private bool IsRoomHost(PlayerSession session)
    {
        return !string.IsNullOrEmpty(session.CurrentRoomId)
               && _rooms.TryGetValue(session.CurrentRoomId, out var room)
               && string.Equals(room.HostPlayerId, session.PlayerId, StringComparison.Ordinal);
    }

    #endregion

    #region 消息发送与协议辅助

    internal Action<NetPeer, NetworkMessage, DeliveryMethod>? MessageSentForTest { get; set; }

    private void SendMessageToPeer(NetPeer peer, NetworkMessage message, DeliveryMethod deliveryMethod)
    {
        MessageSentForTest?.Invoke(peer, message, deliveryMethod);
        try
        {
            NetDataWriter writer = new();

            writer.Put(message.Type);
            writer.Put(JsonCompat.Serialize(message.Payload));

            peer.Send(writer, deliveryMethod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RelayServer] Failed to send message to peer");
        }
    }

        private void SendErrorMessage(NetPeer peer, string errorType, string errorMessage)
    {

        SendMessageToPeer(peer, new NetworkMessage
        {
            Type = NetworkMessageTypes.Error,
            Payload = new { ErrorType = errorType, Message = errorMessage },
            SenderPlayerId = "SERVER"
        }, DeliveryMethod.ReliableOrdered);
    }

        private string GetPlayerId(NetPeer peer)
    {
        return _sessionsByPeer.TryGetValue(peer, out var session) ? session.PlayerId : "unknown";
    }

        private static bool IsGameEvent(string messageType)
    {
        return NetworkMessageTypes.IsGameEvent(messageType, NetworkMessageTypes.GameEventRoute.Relay);
    }

        private void HandleRoomStateRequest(PlayerSession session, NetworkMessage message, DeliveryMethod deliveryMethod)
    {
        string roomId = TryGetStringProperty(message.Payload, "RoomId") ?? session.CurrentRoomId;
        if (string.IsNullOrWhiteSpace(roomId) || !_rooms.TryGetValue(roomId, out var room))
        {
            SendErrorMessage(session.Peer, "RoomStateRequestFailed", "Room not found");
            return;
        }

        if (!string.Equals(session.CurrentRoomId, roomId, StringComparison.Ordinal) || !room.ContainsPlayer(session.PlayerId))
        {
            SendErrorMessage(session.Peer, "RoomStateRequestFailed", "Not in room");
            return;
        }

        string requesterId = TryGetStringProperty(message.Payload, "RequesterId") ?? TryGetStringProperty(message.Payload, "TargetPlayerId");
        if (!string.IsNullOrWhiteSpace(requesterId) && !string.Equals(requesterId, session.PlayerId, StringComparison.Ordinal))
        {
            _logger?.LogWarning($"[RelayServer] 客户端 {session.PlayerId} 企图在 RoomStateRequest 中冒充 {requesterId}，已拒绝");
            SendErrorMessage(session.Peer, "RoomStateRequestFailed", "Requester identity mismatch");
            return;
        }

        string hostPlayerId = room.HostPlayerId;
        if (string.IsNullOrWhiteSpace(hostPlayerId) || !_connectionsByPlayerId.TryGetValue(hostPlayerId, out var hostConnection))
        {
            SendErrorMessage(session.Peer, "RoomStateRequestFailed", "Host not available");
            return;
        }

        object payloadToHost = EnsureRoomIdInPayload(message.Payload, roomId);

        hostConnection.SendMessage(new NetworkMessage
        {
            Type = NetworkMessageTypes.RoomStateRequest,
            Payload = payloadToHost,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

        private void HandleRoomStateUpload(PlayerSession session, NetworkMessage message, DeliveryMethod deliveryMethod)
    {
        string roomId = TryGetStringProperty(message.Payload, "RoomId") ?? session.CurrentRoomId;
        if (string.IsNullOrWhiteSpace(roomId) || !_rooms.TryGetValue(roomId, out var room))
        {
            SendErrorMessage(session.Peer, "RoomStateUploadFailed", "Room not found");
            return;
        }

        if (!string.Equals(session.CurrentRoomId, roomId, StringComparison.Ordinal) || !room.ContainsPlayer(session.PlayerId))
        {
            SendErrorMessage(session.Peer, "RoomStateUploadFailed", "Not in room");
            return;
        }

        string uploaderId = TryGetStringProperty(message.Payload, "UploaderId") ?? TryGetStringProperty(message.Payload, "OwnerPlayerId");
        if (!string.IsNullOrWhiteSpace(uploaderId) && !string.Equals(uploaderId, session.PlayerId, StringComparison.Ordinal))
        {
            _logger?.LogWarning($"[RelayServer] 客户端 {session.PlayerId} 企图在 RoomStateUpload 中冒充 {uploaderId}，已拒绝");
            SendErrorMessage(session.Peer, "RoomStateUploadFailed", "Uploader identity mismatch");
            return;
        }

        string hostPlayerId = room.HostPlayerId;
        if (string.IsNullOrWhiteSpace(hostPlayerId) || !_connectionsByPlayerId.TryGetValue(hostPlayerId, out var hostConnection))
        {
            SendErrorMessage(session.Peer, "RoomStateUploadFailed", "Host not available");
            return;
        }

        object payloadToHost = EnsureRoomIdInPayload(message.Payload, roomId);

        hostConnection.SendMessage(new NetworkMessage
        {
            Type = NetworkMessageTypes.RoomStateUpload,
            Payload = payloadToHost,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

        private void HandleRoomStateResponse(PlayerSession session, NetworkMessage message, DeliveryMethod deliveryMethod)
    {
        string roomId = TryGetStringProperty(message.Payload, "RoomId") ?? session.CurrentRoomId;
        if (string.IsNullOrWhiteSpace(roomId) || !_rooms.TryGetValue(roomId, out var room))
        {
            SendErrorMessage(session.Peer, "RoomStateResponseFailed", "Room not found");
            return;
        }

        if (!string.Equals(room.HostPlayerId, session.PlayerId, StringComparison.Ordinal))
        {
            SendErrorMessage(session.Peer, "RoomStateResponseFailed", "Not host");
            return;
        }

        string targetPlayerId = TryGetStringProperty(message.Payload, "TargetPlayerId") ?? TryGetStringProperty(message.Payload, "RequesterId");
        if (string.IsNullOrWhiteSpace(targetPlayerId) || !_connectionsByPlayerId.TryGetValue(targetPlayerId, out var targetConnection))
        {
            SendErrorMessage(session.Peer, "RoomStateResponseFailed", "Target not available");
            return;
        }

        object payloadToTarget = EnsureRoomIdInPayload(message.Payload, roomId);

        targetConnection.SendMessage(new NetworkMessage
        {
            Type = NetworkMessageTypes.RoomStateResponse,
            Payload = payloadToTarget,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

        private void HandleFullStateSyncRequest(PlayerSession session, NetworkMessage message, DeliveryMethod deliveryMethod)
    {

        string roomId = TryGetStringProperty(message.Payload, "RoomId") ?? session.CurrentRoomId;
        if (string.IsNullOrWhiteSpace(roomId) || !_rooms.TryGetValue(roomId, out var room))
        {
            SendErrorMessage(session.Peer, "FullStateSyncRequestFailed", "Room not found");
            return;
        }

        if (!string.Equals(session.CurrentRoomId, roomId, StringComparison.Ordinal) || !room.ContainsPlayer(session.PlayerId))
        {
            SendErrorMessage(session.Peer, "FullStateSyncRequestFailed", "Not in room");
            return;
        }

        string targetPlayerId = TryGetStringProperty(message.Payload, "TargetPlayerId");
        if (!string.IsNullOrWhiteSpace(targetPlayerId) && !string.Equals(targetPlayerId, session.PlayerId, StringComparison.Ordinal))
        {
            SendErrorMessage(session.Peer, "FullStateSyncRequestFailed", "Target mismatch");
            return;
        }

        string hostPlayerId = room.HostPlayerId;
        if (string.IsNullOrWhiteSpace(hostPlayerId) || !_connectionsByPlayerId.TryGetValue(hostPlayerId, out var hostConnection))
        {
            SendErrorMessage(session.Peer, "FullStateSyncRequestFailed", "Host not available");
            return;
        }

        object payloadToHost = EnsureRoomIdInPayload(message.Payload, roomId);

        hostConnection.SendMessage(new NetworkMessage
        {
            Type = NetworkMessageTypes.FullStateSyncRequest,
            Payload = payloadToHost,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

        private void HandleFullStateSyncResponse(PlayerSession session, NetworkMessage message, DeliveryMethod deliveryMethod)
    {

        string roomId = TryGetStringProperty(message.Payload, "RoomId") ?? session.CurrentRoomId;
        if (string.IsNullOrWhiteSpace(roomId) || !_rooms.TryGetValue(roomId, out var room))
        {
            SendErrorMessage(session.Peer, "FullStateSyncResponseFailed", "Room not found");
            return;
        }

        if (!string.Equals(room.HostPlayerId, session.PlayerId, StringComparison.Ordinal))
        {
            SendErrorMessage(session.Peer, "FullStateSyncResponseFailed", "Only host can respond");
            return;
        }

        string targetPlayerId = TryGetStringProperty(message.Payload, "TargetPlayerId");
        if (string.IsNullOrWhiteSpace(targetPlayerId))
        {
            SendErrorMessage(session.Peer, "FullStateSyncResponseFailed", "Missing TargetPlayerId");
            return;
        }

        if (!room.ContainsPlayer(targetPlayerId) || !_connectionsByPlayerId.TryGetValue(targetPlayerId, out var targetConnection))
        {
            SendErrorMessage(session.Peer, "FullStateSyncResponseFailed", "Target not in room");
            return;
        }

        targetConnection.SendMessage(new NetworkMessage
        {
            Type = NetworkMessageTypes.FullStateSyncResponse,
            Payload = message.Payload,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

        private static object EnsureRoomIdInPayload(object payload, string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return payload;
        }

        try
        {

            string existingRoomId = TryGetStringProperty(payload, "RoomId");
            if (!string.IsNullOrWhiteSpace(existingRoomId))
            {
                return payload;
            }

            string json = payload is string s ? s : JsonCompat.Serialize(payload);
            var dict = JsonCompat.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
            dict["RoomId"] = roomId;
            return dict;
        }
        catch
        {

            return payload;
        }
    }

    #endregion

    #region JSON/Metadata 工具

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

        private static string TryGetStringProperty(object payload, string propertyName)
    {
        JsonElement root = GetJsonElement(payload);
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

        private static object TryGetObjectProperty(object payload, string propertyName)
    {
        JsonElement root = GetJsonElement(payload);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => value,
            JsonValueKind.Array => value,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out long l) ? l : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

        private static JsonElement GetJsonElement(object payload)
    {
        try
        {

            return JsonCompat.ToJsonElement(payload);
        }
        catch
        {

            return default;
        }
    }

    #endregion

    #region ServerCore 创建

        private static IServerCore CreateCore(ConfigManager configManager, ILogger<RelayServer> logger)
    {
        var relayConfig = configManager.GetRelayServerConfig();

        return new ServerCore(
            new ServerOptions
            {
                Port = relayConfig.Port,
                MaxConnections = relayConfig.MaxConnections,
                ConnectionKey = relayConfig.ConnectionKey,
                DisconnectTimeoutMs = configManager.NetworkTimeoutSeconds.Value * 1000,
                PingIntervalMs = 1000,
                MaxQueueSize = 2000,
                UseBackgroundThread = true,
                BackgroundThreadSleepMs = 15,
            },
            new MicrosoftServerLogger(logger));
    }

    #endregion

    #region BaseGameServer 覆写（核心行为）

        protected override TimeSpan ReconnectGracePeriod => _reconnectGracePeriod;

        protected override string CreatePlayerId(NetPeer peer) => GeneratePlayerId();

        protected override PlayerSession CreateSession(NetPeer peer, string playerId)
    {

        return new PlayerSession
        {
            Peer = peer,
            PlayerId = playerId,
            PlayerName = $"Player_{playerId[..6]}",
            ConnectedAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow,
            LastMessageAt = DateTime.UtcNow,
            IsConnected = true,
            IsHost = false,
            CurrentRoomId = string.Empty
        };
    }

        protected override bool IsGameEventType(string messageType) => IsGameEvent(messageType);

        protected override void HandleGameEvent(PlayerSession session, string eventType, string jsonPayload, DeliveryMethod deliveryMethod)
    {

        ProcessMessage(session.Peer, new NetworkMessage
        {
            Type = eventType,
            Payload = jsonPayload,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

        protected override void HandleSystemMessage(PlayerSession session, string messageType, string jsonPayload, DeliveryMethod deliveryMethod)
    {
        ProcessMessage(session.Peer, new NetworkMessage
        {
            Type = messageType,
            Payload = jsonPayload,
            SenderPlayerId = session.PlayerId
        }, deliveryMethod);
    }

        protected override void OnSessionConnected(PlayerSession session)
    {
        lock (_lock)
        {

            _connectionsByPlayerId[session.PlayerId] = new NetworkConnection(session.Peer, session.PlayerId);
        }

        SendMessageToPeer(session.Peer, new NetworkMessage
        {
            Type = NetworkMessageTypes.Welcome,
            Payload = new
            {
                PlayerId = session.PlayerId,
                ReconnectToken = TryGetMetadataString(session.Metadata, "ReconnectToken"),
                SessionNonce = session.SessionNonce,
                ServerTime = DateTime.UtcNow.Ticks
            },
            SenderPlayerId = "SERVER"
        }, DeliveryMethod.ReliableOrdered);

        _logger.LogInformation($"[RelayServer] Client connected: {session.Peer.EndPoint}, PlayerId={session.PlayerId}");
    }

        protected override void OnSessionDisconnected(PlayerSession session, DisconnectInfo disconnectInfo)
    {
        lock (_lock)
        {
            _logger.LogInformation($"[RelayServer] Client disconnected: {session.Peer.EndPoint}, Reason: {disconnectInfo.Reason}, PlayerId={session.PlayerId}");

            if (!string.IsNullOrEmpty(session.CurrentRoomId) && _rooms.TryGetValue(session.CurrentRoomId, out var room))
            {

                room.RemovePlayer(session.PlayerId);
                BroadcastPlayerList(room);

                if (room.PlayerCount == 0)
                {

                    _rooms.Remove(room.RoomId);
                    _logger.LogInformation($"[RelayServer] Room {room.RoomId} destroyed (empty)");
                }
            }

            _connectionsByPlayerId.Remove(session.PlayerId);
        }
    }

    #endregion

    #region ID/Token 生成

        private static string GeneratePlayerId()
    {
        return Guid.NewGuid().ToString("N");
    }

        private static new string GenerateReconnectToken()
    {
        return Guid.NewGuid().ToString("N");
    }

        [ThreadStatic]
    private static Random? _threadRandom;

        private static string GenerateRoomId()
    {
        _threadRandom ??= new Random();
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, 6).Select(s => s[_threadRandom.Next(s.Length)]).ToArray());
    }

    #endregion
}
