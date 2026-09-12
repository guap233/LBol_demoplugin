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

public partial class NetworkServer : BaseGameServer
{
    #region 私有字段

        private IServerCore _core => Core;

        private int _port;

        private int _maxConnections;

        private string _connectionKey;

        private readonly ManualLogSource _logger;

        private Dictionary<string, PlayerSession> _sessionsByPlayerId => SessionsByPlayerId;
    private readonly Dictionary<int, string> _playerIdByPeerId = new();
    private Dictionary<string, DateTime> _disconnectedAtByPlayerId => DisconnectedAtByPlayerId;
    private readonly TimeSpan _reconnectGracePeriod = TimeSpan.FromSeconds(60);
    private readonly Dictionary<int, PlayerSession> _playerSessions = new();
    private readonly HashSet<string> _routeProbeOnceKeys = new(StringComparer.Ordinal);

    #endregion

    #region 公共事件

        public delegate void GameEventHandler(string eventType, object eventData, PlayerSession sender);

        public event GameEventHandler OnGameEventReceived;

    #endregion

    #region 构造函数

    internal Action<NetPeer, string, string, DeliveryMethod>? MessageSentForTest { get; set; }

    internal NetworkServer(IServerCore core, ManualLogSource logger = null)
        : base(core)
    {
        _logger = logger;
    }

    public NetworkServer(int port, int maxConnections, string connectionKey, ManualLogSource logger, string listenAddress = "0.0.0.0")
        : base(CreateCore(port, maxConnections, connectionKey, logger, listenAddress))
    {
        _port = port;
        _maxConnections = maxConnections;
        _connectionKey = connectionKey;

        _logger = logger;
    }

    internal PlayerSession GetPlayerSessionByPeerForTest(NetPeer peer)
    {
        lock (SyncRoot)
        {
            SessionsByPeer.TryGetValue(peer, out var session);
            return session;
        }
    }

    internal void ProcessRawInboundForTest(NetPeer peer, string messageType, string json)
    {
        ProcessInboundMessageForTest(new ServerInboundMessage(peer, messageType, json, DeliveryMethod.ReliableOrdered, MessagePriority.Normal));
    }

    #endregion

    #region 公共方法

        public IReadOnlyCollection<PlayerSession> GetPlayerSessions()
    {
        lock (SyncRoot)
        {
            return SessionsByPeer.Values.ToList();
        }
    }

        public PlayerSession GetPlayerSession(int peerId)
    {
        lock (SyncRoot)
        {
            if (_playerSessions.TryGetValue(peerId, out var session))
            {
                return session;
            }
            return SessionsByPeer.Values.FirstOrDefault(s => s.Peer != null && s.Peer.Id == peerId);
        }
    }

        public int PlayerCount
    {
        get
        {
            lock (SyncRoot)
            {
                return SessionsByPeer.Count;
            }
        }
    }

        private void HandleSystemMessageCore(PlayerSession senderSession, string messageType, string jsonPayload)
    {
        try
        {
            if (TryRouteControlledMessage(senderSession, messageType, jsonPayload))
            {
                return;
            }

            switch (messageType)
            {
                case NetworkMessageTypes.PlayerJoined:
                    HandlePlayerJoined(senderSession.Peer, jsonPayload);
                    return;
                case NetworkMessageTypes.Heartbeat:
                    HandleHeartbeat(senderSession.Peer);
                    return;
                case NetworkMessageTypes.GetSelf_REQUEST:
                    HandleGetSelfRequest(senderSession.Peer);
                    return;
                case NetworkMessageTypes.UpdatePlayerLocation:
                    HandleUpdatePlayerLocation(senderSession.Peer, jsonPayload);
                    return;
                case NetworkMessageTypes.Reconnect_REQUEST:
                    HandleReconnectRequest(senderSession.Peer, jsonPayload);
                    return;
                case NetworkMessageTypes.DirectMessage:
                    HandleDirectMessage(senderSession, jsonPayload);
                    return;
                case NetworkMessageTypes.PlayerReadyChanged:
                    HandlePlayerReadyChanged(senderSession, jsonPayload);
                    return;
                case NetworkMessageTypes.LeaveRoom:
                    HandleLeaveRoom(senderSession);
                    return;
                default:
                    Plugin.Logger?.LogInfo($"[服务器] 未知系统消息类型: {messageType}, 来自 {senderSession.Peer.EndPoint}");
                    _logger?.LogWarning($"[服务器] 未知系统消息类型: {messageType}, 来自 {senderSession.Peer.EndPoint}");
                    return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[服务器] 处理系统消息异常: type={messageType}, from={senderSession.Peer.EndPoint}, err={ex.Message}");
            _logger?.LogError($"[服务器] 处理系统消息异常: type={messageType}, from={senderSession.Peer.EndPoint}, err={ex.Message}");
        }
    }

        private void HandleDirectMessage(PlayerSession senderSession, string jsonPayload)
    {
        try
        {
            JsonElement root = JsonSerializer.Deserialize<JsonElement>(jsonPayload);
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string targetPlayerId = TryGetJsonStringProperty(root, "TargetPlayerId");

            if (!TryGetConnectedTargetSession(targetPlayerId, out PlayerSession targetSession))
            {
                return;
            }

            string innerType = TryGetJsonStringProperty(root, "Type") ?? NetworkMessageTypes.DirectMessage;

            LogRouteProbeOnce($"DirectMessage/{innerType}",
                $"sender={senderSession.PlayerId}, target={targetPlayerId}");

            string innerJson = GetNestedPayloadJson(root);
            if (TryRouteControlledMessage(senderSession, innerType, innerJson))
            {
                return;
            }

            SendRawJsonToPeer(targetSession.Peer, innerType, innerJson);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 处理 DirectMessage 异常: {ex.Message}");
        }
    }

        private void HandlePlayerReadyChanged(PlayerSession senderSession, string jsonPayload)
    {
        try
        {
            JsonElement root = JsonSerializer.Deserialize<JsonElement>(jsonPayload);
            if (root.ValueKind != JsonValueKind.Object) return;

            bool isReady = false;
            if (root.TryGetProperty("IsReady", out var readyProp))
            {
                isReady = readyProp.ValueKind == JsonValueKind.True;
            }

            senderSession.Metadata["Ready"] = isReady;
            Plugin.Logger?.LogInfo($"[服务器] 玩家 {senderSession.PlayerId} 准备状态变更: {(isReady ? "已就绪" : "取消准备")}");

            BroadcastPlayerList();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[服务器] 处理 PlayerReadyChanged 失败: {ex.Message}");
        }
    }

        private void HandleGameEventCore(PlayerSession session, string eventType, string jsonPayload)
    {
        try
        {
            if (string.Equals(eventType, NetworkMessageTypes.LeaveRoom, StringComparison.OrdinalIgnoreCase))
            {
                HandleLeaveRoom(session);
                return;
            }

            object eventData = JsonSerializer.Deserialize<object>(jsonPayload);
            string summary = NetLogHelper.BuildSummary(eventType, jsonPayload);
            Plugin.Logger?.LogInfo($"[服务器] 收到游戏事件: type={eventType}, from={session.PlayerId} ({summary})");

            session.UpdateMessageTime();

            OnGameEventReceived?.Invoke(eventType, eventData, session);

            if (TryRouteControlledMessage(session, eventType, jsonPayload))
            {
                return;
            }

            if (TryRouteHostRequest(session, eventType, jsonPayload))
            {
                return;
            }

            BroadcastGameEvent(eventType, jsonPayload, session.Peer.Id);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[服务器] 处理游戏事件异常: {ex.Message}");
            _logger?.LogError($"[服务器] 处理游戏事件异常: {ex.Message}");
        }
    }

    private void HandleLeaveRoom(PlayerSession senderSession)
    {
        if (senderSession == null) return;
        lock (SyncRoot)
        {
            senderSession.IsConnected = false;
            senderSession.Metadata["Left"] = true;
            _disconnectedAtByPlayerId[senderSession.PlayerId] = DateTime.UtcNow;
            if (senderSession.Peer != null)
            {
                _playerIdByPeerId.Remove(senderSession.Peer.Id);
                _playerSessions.Remove(senderSession.Peer.Id);
                SessionsByPeer.Remove(senderSession.Peer);
            }

            Plugin.Logger?.LogInfo($"[服务器] 玩家主动离开房间: {senderSession.PlayerId}");

            BroadcastMessage(NetworkMessageTypes.PlayerLeft, new
            {
                PlayerId = senderSession.PlayerId
            });

            BroadcastPlayerList();
        }
    }

    private void HandlePlayerJoined(NetPeer fromPeer, string jsonPayload)
    {
        try
        {
            var playerInfo = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonPayload);
            if (!SessionsByPeer.TryGetValue(fromPeer, out var session))
            {
                return;
            }

            session.Metadata["IsConfirmed"] = true;
            session.Metadata.Remove("Left");

            if (playerInfo != null)
            {
                if (playerInfo.TryGetValue("PlayerName", out object nameObj) && nameObj != null)
                {
                    session.PlayerName = nameObj.ToString();
                }

                if (playerInfo.TryGetValue("CharacterId", out object charObj) && charObj != null)
                {
                    session.Metadata["CharacterId"] = charObj.ToString();
                }
            }

            Plugin.Logger?.LogInfo($"[服务器] 玩家加入: {session.PlayerName} ({session.PlayerId})");

            BroadcastMessage(NetworkMessageTypes.PlayerJoined, new
            {
                PlayerId = session.PlayerId,
                PlayerName = session.PlayerName,
                IsHost = session.IsHost,
                CharacterId = session.Metadata.TryGetValue("CharacterId", out var cid) ? cid?.ToString() : null
            }, excludePeerId: fromPeer.Id);

            BroadcastPlayerList();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[服务器] 处理 PlayerJoined 异常: {ex.Message}");
            _logger?.LogError($"[服务器] 处理 PlayerJoined 异常: {ex.Message}");
        }
    }

        private void HandleGetSelfRequest(NetPeer fromPeer)
    {
        if (SessionsByPeer.TryGetValue(fromPeer, out var session))
        {
            var responseData = new
            {
                PlayerId = session.PlayerId,
                PlayerName = session.PlayerName,
                IsHost = session.IsHost,
                ConnectedAt = session.ConnectedAt.Ticks,
                ReconnectToken = TryGetMetadataString(session.Metadata, "ReconnectToken"),
            };

            SendMessage(fromPeer, NetworkMessageTypes.GetSelf_RESPONSE, responseData);
        }
    }

        private sealed class ReconnectRequest
    {
                public string PlayerId { get; set; } = string.Empty;
                public string ReconnectToken { get; set; } = string.Empty;
    }

        private void HandleReconnectRequest(NetPeer fromPeer, string jsonPayload)
    {
        try
        {
            ReconnectRequest request = JsonSerializer.Deserialize<ReconnectRequest>(jsonPayload);
            if (request == null || string.IsNullOrWhiteSpace(request.PlayerId) || string.IsNullOrWhiteSpace(request.ReconnectToken))
            {
                SendMessage(fromPeer, NetworkMessageTypes.Reconnect_RESPONSE, new { Success = false, Error = "Invalid request" });
                return;
            }

            PlayerSession targetSession;
            lock (SyncRoot)
            {
                if (!_sessionsByPlayerId.TryGetValue(request.PlayerId, out targetSession!))
                {
                    SendMessage(fromPeer, NetworkMessageTypes.Reconnect_RESPONSE, new { Success = false, Error = "Unknown playerId" });
                    return;
                }

                if (targetSession.IsConnected && targetSession.Peer == fromPeer)
                {
                    SendMessage(fromPeer, NetworkMessageTypes.Reconnect_RESPONSE, new { Success = false, Error = "Already connected" });
                    return;
                }

                string expectedToken = TryGetMetadataString(targetSession.Metadata, "ReconnectToken");
                if (!string.Equals(expectedToken, request.ReconnectToken, StringComparison.Ordinal))
                {
                    SendMessage(fromPeer, NetworkMessageTypes.Reconnect_RESPONSE, new { Success = false, Error = "Invalid token" });
                    return;
                }

                if (_disconnectedAtByPlayerId.TryGetValue(request.PlayerId, out var disconnectedAt))
                {
                    if (DateTime.UtcNow - disconnectedAt > _reconnectGracePeriod)
                    {
                        SendMessage(fromPeer, NetworkMessageTypes.Reconnect_RESPONSE, new { Success = false, Error = "Reconnect window expired" });
                        return;
                    }
                }

                if (_playerIdByPeerId.TryGetValue(fromPeer.Id, out var currentPlayerId) &&
                    !string.Equals(currentPlayerId, request.PlayerId, StringComparison.Ordinal))
                {
                    _playerIdByPeerId.Remove(fromPeer.Id);
                    _playerSessions.Remove(fromPeer.Id);

                    _sessionsByPlayerId.Remove(currentPlayerId);
                    _disconnectedAtByPlayerId.Remove(currentPlayerId);
                    SessionsByPeer.Remove(fromPeer);
                }

                if (targetSession.Peer != null && targetSession.Peer != fromPeer)
                {
                    SessionsByPeer.Remove(targetSession.Peer);
                    _playerIdByPeerId.Remove(targetSession.Peer.Id);
                    _playerSessions.Remove(targetSession.Peer.Id);
                }

                targetSession.Peer = fromPeer;
                targetSession.IsConnected = true;
                targetSession.Metadata["IsConfirmed"] = true;
                targetSession.Metadata.Remove("Left");
                targetSession.UpdateHeartbeat();
                targetSession.UpdateMessageTime();

                SessionsByPeer[fromPeer] = targetSession;
                _playerIdByPeerId[fromPeer.Id] = targetSession.PlayerId;
                _playerSessions[fromPeer.Id] = targetSession;
                _disconnectedAtByPlayerId.Remove(targetSession.PlayerId);

                string newToken = GenerateReconnectToken();
                targetSession.Metadata["ReconnectToken"] = newToken;

                SendMessage(fromPeer, NetworkMessageTypes.Reconnect_RESPONSE, new
                {
                    Success = true,
                    PlayerId = targetSession.PlayerId,
                    IsHost = targetSession.IsHost,
                    ConnectedAt = targetSession.ConnectedAt.Ticks,
                    ReconnectToken = newToken,
                    SessionNonce = targetSession.SessionNonce
                });

                Plugin.Logger?.LogInfo($"[服务器] 玩家 {targetSession.PlayerId} 重连成功并已合并会话，轮换新凭证。");
            }

            BroadcastPlayerList();
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[服务器] 处理 {NetworkMessageTypes.Reconnect_REQUEST} 异常: {ex.Message}");
            SendMessage(fromPeer, NetworkMessageTypes.Reconnect_RESPONSE, new { Success = false, Error = "Server error" });
        }
    }

        private void HandleUpdatePlayerLocation(NetPeer fromPeer, string jsonPayload)
    {
        try
        {
            if (!SessionsByPeer.TryGetValue(fromPeer, out var session))
            {
                return;
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(jsonPayload);
            }
            catch
            {
                Plugin.Logger?.LogError($"[服务器] UpdatePlayerLocation payload 无效: from={session.PlayerId}");
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("LocationX", out JsonElement xElem) && xElem.TryGetInt32(out int x))
            {
                session.Metadata["LocationX"] = x;
            }
            if (root.TryGetProperty("LocationY", out JsonElement yElem) && yElem.TryGetInt32(out int y))
            {
                session.Metadata["LocationY"] = y;
            }
            if (root.TryGetProperty("Stage", out JsonElement stageElem) && stageElem.TryGetInt32(out int stage))
            {
                session.Metadata["Stage"] = stage;
            }
            if (root.TryGetProperty("LocationName", out JsonElement nameElem) && nameElem.ValueKind == JsonValueKind.String)
            {
                session.Metadata["LocationName"] = nameElem.GetString();
            }
            if (root.TryGetProperty("CharacterId", out JsonElement charElem) && charElem.ValueKind == JsonValueKind.String)
            {
                session.Metadata["CharacterId"] = charElem.GetString();
            }
            if (root.TryGetProperty("Hp", out JsonElement hpElem) && hpElem.TryGetInt32(out int hp))
            {
                session.Metadata["Hp"] = hp;
            }
            if (root.TryGetProperty("MaxHp", out JsonElement maxHpElem) && maxHpElem.TryGetInt32(out int maxHp))
            {
                session.Metadata["MaxHp"] = maxHp;
            }

            BroadcastPlayerList();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[服务器] 处理 UpdatePlayerLocation 异常: {ex.Message}");
            _logger?.LogError($"[服务器] 处理 UpdatePlayerLocation 异常: {ex.Message}");
        }
    }

        public override void Start()
    {
        _core.Start();
        Plugin.Logger?.LogInfo($"[服务器] 已启动，监听端口 {_port}。");
        _logger?.LogInfo($"[服务器] 已启动，监听端口 {_port}。");
    }

        public override void PollEvents()
    {
        _core.PollEvents();
        CleanupDisconnectedSessions();
    }

        public override void Stop()
    {
        _core.Stop();
        lock (SyncRoot)
        {
            _playerSessions.Clear();
            _playerIdByPeerId.Clear();
            _sessionsByPlayerId.Clear();
            _disconnectedAtByPlayerId.Clear();
            SessionsByPeer.Clear();
            SessionsByPlayerId.Clear();
        }
        Plugin.Logger?.LogInfo("[服务器] 已停止。");
        _logger?.LogInfo("[服务器] 已停止。");
    }

    private static IServerCore CreateCore(int port, int maxConnections, string connectionKey, ManualLogSource logger, string listenAddress = "0.0.0.0")
    {
        return new ServerCore(
            new ServerOptions
            {
                Port = port,
                ListenAddress = listenAddress,
                MaxConnections = maxConnections,
                ConnectionKey = connectionKey,
                DisconnectTimeoutMs = ServerConstants.DisconnectTimeoutSeconds * 1000,
                PingIntervalMs = 1_000,
                UseBackgroundThread = false,
            },
            new BepInExServerLogger(logger));
    }

    protected override TimeSpan ReconnectGracePeriod => _reconnectGracePeriod;

    protected override string CreatePlayerId(NetPeer peer) => $"Player_{peer.Id}";

    protected override PlayerSession CreateSession(NetPeer peer, string playerId)
    {
        bool isHost = _sessionsByPlayerId.Values.All(s => !s.IsHost);
        return new PlayerSession
        {
            Peer = peer,
            PlayerId = playerId,
            ConnectedAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow,
            LastMessageAt = DateTime.UtcNow,
            IsConnected = true,
            IsHost = isHost
        };
    }

    protected override bool IsGameEventType(string messageType) => IsGameEvent(messageType);

    protected override void HandleGameEvent(PlayerSession session, string eventType, string jsonPayload, DeliveryMethod deliveryMethod)
    {
        HandleGameEventCore(session, eventType, jsonPayload);
    }

    protected override void HandleSystemMessage(PlayerSession session, string messageType, string jsonPayload, DeliveryMethod deliveryMethod)
    {
        HandleSystemMessageCore(session, messageType, jsonPayload);
    }

    protected override void OnSessionConnected(PlayerSession session)
    {
        _playerIdByPeerId[session.Peer.Id] = session.PlayerId;
        _playerSessions[session.Peer.Id] = session;

        Plugin.Logger?.LogInfo($"[服务器] 客户端已连接: {session.Peer.EndPoint}");
        _logger?.LogInfo($"[服务器] 客户端已连接: {session.Peer.EndPoint}");

        if (session.IsHost)
        {
            session.Metadata["IsConfirmed"] = true;
            session.Metadata.Remove("Left");
            BroadcastPlayerList();
        }
        else
        {
            session.Metadata["IsConfirmed"] = false;
        }

        SendWelcomeMessage(session.Peer, session);
    }

    protected override void OnSessionDisconnected(PlayerSession session, DisconnectInfo disconnectInfo)
    {
        Plugin.Logger?.LogInfo($"[服务器] 客户端已断开: {session.Peer.EndPoint}, 原因: {disconnectInfo.Reason}");
        _logger?.LogInfo($"[服务器] 客户端已断开: {session.Peer.EndPoint}, 原因: {disconnectInfo.Reason}");

        _playerIdByPeerId.Remove(session.Peer.Id);
        _playerSessions.Remove(session.Peer.Id);

        BroadcastPlayerList();
    }

    #endregion

}
