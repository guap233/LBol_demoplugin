using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NetworkPlugin.Configuration;
using NetworkPlugin.Core;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Network.Client;

#region 网络客户端：连接管理、事件路由与自动重连

public class NetworkClient : INetworkClient
{
    #region 字段与常量

        private EventBasedNetListener _listener;

        private NetManager _netManager;

        private NetPeer _serverPeer;

        private string _connectionKey;

        private DateTime _lastHeartbeatSentUtc = DateTime.MinValue;
        private int _heartbeatIntervalMs = 5_000;

        private INetworkManager _networkManager;
        private INetworkPlayer _networkPlayer;
        private INetworkPlayer _fallbackSelf;

        private readonly ISynchronizationManager _synchronizationManager;

    #endregion

    #region 公共事件

        public event Action<string> OnConnected;

        public event Action<string, string> OnDisconnected;

        public event Action<string, object> OnGameEventReceived;

        public event Action<string, object> OnResponseReceived;

        public event Action<bool> OnConnectionStateChanged;

    #endregion

    #region 自动重连配置

        private volatile bool _autoReconnectEnabled = false;
        private volatile bool _isStopped;
        private int _retryInterval = NetworkConstants.ReconnectIntervalMs;
        private int _maxReconnectAttempts = NetworkConstants.DefaultMaxReconnectAttempts;
        private int _reconnectAttemptCount = 0;
        private int _connectionTimeout = NetworkConstants.DefaultNetworkTimeoutSeconds * 1000;
        private string _lastConnectHost;
        private int _lastConnectPort;
        private readonly object _reconnectLock = new();
        private Timer _reconnectTimer;

    #endregion

    #region 会话与重连凭证

        private string _lastSessionPlayerId;
        private string _lastSessionReconnectToken;
        private string _lastSessionNonce;
        private string _lastSessionEndpoint;
        private volatile bool _isAwaitingReconnectResponse;

        public void ClearSessionCredentials()
        {
            _lastSessionPlayerId = null;
            _lastSessionReconnectToken = null;
            _lastSessionNonce = null;
            _lastSessionEndpoint = null;
            _isAwaitingReconnectResponse = false;
            Plugin.Logger?.LogInfo("[客户端] 会话重连凭证已清除。");
        }

        internal void SetSessionCredentialsForTest(string playerId, string token, string endpoint)
        {
            _lastSessionPlayerId = playerId;
            _lastSessionReconnectToken = token;
            _lastSessionEndpoint = endpoint;
        }

        internal string LastSessionPlayerIdForTest => _lastSessionPlayerId;
        internal string LastSessionReconnectTokenForTest => _lastSessionReconnectToken;

    #endregion

    #region Payload 日志去重

        private readonly object _payloadPreviewLock = new();
        private readonly HashSet<string> _payloadPreviewLogged = new(StringComparer.Ordinal);

    #endregion

    #region 构造函数

        private ConfigManager _configManager;

        public NetworkClient(ConfigManager configManager, ISynchronizationManager synchronizationManager)
        : this(configManager?.RelayServerConnectionKey?.Value ?? NetworkPlugin.Network.Security.SecurityUtils.GenerateSecureKey(), null, null, synchronizationManager)
    {
        _configManager = configManager;
        try
        {
            if (configManager != null)
            {

                _connectionTimeout = Math.Max(5_000, configManager.NetworkTimeoutSeconds.Value * 1000);
                _netManager.DisconnectTimeout = _connectionTimeout;

                _heartbeatIntervalMs = Math.Max(1_000, Math.Min(10_000, _connectionTimeout / 3));
            }
        }
        catch
        {

        }
    }

        public NetworkClient(string connectionKey, INetworkManager networkManager, INetworkPlayer networkPlayer, ISynchronizationManager synchronizationManager = null)
    {
        _networkManager = networkManager;
        _connectionKey = connectionKey;
        _listener = new EventBasedNetListener();
        _netManager = new NetManager(_listener);
        _networkPlayer = networkPlayer;
        _synchronizationManager = synchronizationManager;
        RegisterEvents();
    }

    #endregion

    #region 连接管理

        public void Start()
    {

        _isStopped = false;

        _netManager.DisconnectTimeout = _connectionTimeout;

        if (_netManager.Start())
        {
            Plugin.Logger?.LogInfo("[客户端] 网络客户端已启动。");
        }
        else
        {
            Plugin.Logger?.LogError("[客户端] 网络客户端启动失败。");
            throw new Exception("Failed to start NetworkClient");
        }
    }

        public INetworkPlayer GetSelf()
    {
        try
        {

            INetworkPlayer p = _networkManager?.GetSelf();
            if (p != null)
            {
                return p;
            }

            if (_networkPlayer != null)
            {
                return _networkPlayer;
            }

            return _fallbackSelf ??= new LocalNetworkPlayer(this);
        }
        catch
        {

            return _fallbackSelf ??= new LocalNetworkPlayer(this);
        }
    }

        private void RegisterEvents()
    {
        _listener.PeerConnectedEvent += peer =>
        {
            Plugin.Logger?.LogInfo($"[客户端] 已连接到服务器: {peer.EndPoint}");

            _serverPeer = peer;
            _lastHeartbeatSentUtc = DateTime.UtcNow;

            StopAutoReconnectTimer_NoThrow();

            OnConnected?.Invoke(peer.EndPoint.ToString());
            OnConnectionStateChanged?.Invoke(true);

            try
            {
                _synchronizationManager?.OnConnectionRestored();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"[客户端] 通知同步管理器失败: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(_lastSessionPlayerId) && !string.IsNullOrWhiteSpace(_lastSessionReconnectToken))
            {
                Plugin.Logger?.LogInfo($"[客户端] 检测到历史会话凭证，尝试恢复身份: playerId={_lastSessionPlayerId}");
                _isAwaitingReconnectResponse = true;
                SendRawJson(NetworkMessageTypes.Reconnect_REQUEST, JsonCompat.Serialize(new
                {
                    PlayerId = _lastSessionPlayerId,
                    ReconnectToken = _lastSessionReconnectToken
                }));
            }
            else
            {
                SendPlayerJoined();
            }
        };

        _listener.PeerDisconnectedEvent += (peer, disconnectInfo) =>
        {
            Plugin.Logger?.LogWarning($"[客户端] 已从服务器断开: {peer.EndPoint}, 原因: {disconnectInfo.Reason}");
            LastDisconnectReason = disconnectInfo.Reason.ToString();

            _serverPeer = null;

            _lastHeartbeatSentUtc = DateTime.MinValue;

            OnDisconnected?.Invoke(peer.EndPoint.ToString(), disconnectInfo.Reason.ToString());
            OnConnectionStateChanged?.Invoke(false);

            try
            {
                _synchronizationManager?.OnConnectionLost();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"[客户端] 通知同步管理器失败: {ex.Message}");
            }

            if (_autoReconnectEnabled && !_isStopped)
            {
                Plugin.Logger?.LogInfo($"[客户端] 已启用自动重连，将在 {_retryInterval}ms 后重试");
                StartAutoReconnectTimer_NoThrow();
            }
        };

        _listener.NetworkReceiveEvent += (fromPeer, dataReader, deliveryMethod) =>
        {
            try
            {
                string messageType = dataReader.GetString();

                if (IsGameEvent(messageType))
                {
                    HandleGameEvent(messageType, dataReader);
                }
                else if (string.Equals(messageType, NetworkMessageTypes.HeartbeatResponse, StringComparison.Ordinal))
                {
                    _ = dataReader.GetString();
                }
                else if (string.Equals(messageType, NetworkMessageTypes.GetSelf_RESPONSE, StringComparison.Ordinal))
                {
                    HandleRequestResponse(fromPeer, dataReader);
                }
                else if (string.Equals(messageType, NetworkMessageTypes.Reconnect_RESPONSE, StringComparison.Ordinal))
                {
                    HandleReconnectResponse(fromPeer, dataReader);
                }
                else
                {
                    Plugin.Logger?.LogWarning($"[客户端] 未知消息类型: {messageType}，来自 {fromPeer.EndPoint}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[客户端] 处理来自 {fromPeer.EndPoint} 的数据失败: {ex.Message}");
            }
            finally
            {
                dataReader.Recycle();
            }
        };
    }

    private void SendPlayerJoined()
    {
        string playerName = null;
        try
        {
            playerName = _networkManager?.GetSelf()?.userName;
            if (string.IsNullOrWhiteSpace(playerName))
            {
                playerName = _networkPlayer?.userName;
            }
            if (string.IsNullOrWhiteSpace(playerName))
            {
                playerName = GetSelf()?.userName;
            }
        }
        catch
        {

        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            playerName = "Player";
        }

        string characterId = null;
        try
        {
            var startGamePanel = LBoL.Presentation.UI.UiManager.GetPanel<LBoL.Presentation.UI.Panels.StartGamePanel>();
            if (startGamePanel != null)
            {
                var playerUnit = HarmonyLib.Traverse.Create(startGamePanel).Field("_player").GetValue<LBoL.Core.Units.PlayerUnit>();
                if (playerUnit != null)
                {
                    characterId = playerUnit.Id;
                }
            }
            if (string.IsNullOrEmpty(characterId))
            {
                characterId = GameStateUtils.GetCurrentPlayer()?.ModelName;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[NetworkClient] CharacterId retrieval error: {ex.Message}");
        }

        var playerInfo = new
        {
            PlayerName = playerName,
            CharacterId = characterId,
            ConnectionTime = DateTime.Now.Ticks
        };

        SendGameEventData(NetworkMessageTypes.PlayerJoined, playerInfo);
    }

    private void HandleReconnectResponse(NetPeer fromPeer, NetDataReader dataReader)
    {
        try
        {
            string jsonPayload = dataReader.GetString();
            using var doc = System.Text.Json.JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;
            bool success = root.TryGetProperty("Success", out var sElem) && sElem.GetBoolean();
            if (success)
            {
                string playerId = root.TryGetProperty("PlayerId", out var pElem) ? pElem.GetString() : _lastSessionPlayerId;
                string newToken = root.TryGetProperty("ReconnectToken", out var tElem) ? tElem.GetString() : null;
                string nonce = root.TryGetProperty("SessionNonce", out var nElem) ? nElem.GetString() : null;
                bool isHost = root.TryGetProperty("IsHost", out var hElem) && hElem.GetBoolean();

                _lastSessionPlayerId = playerId;
                if (!string.IsNullOrWhiteSpace(newToken))
                {
                    _lastSessionReconnectToken = newToken;
                }
                if (!string.IsNullOrWhiteSpace(nonce))
                {
                    _lastSessionNonce = nonce;
                }

                _isAwaitingReconnectResponse = false;
                Plugin.Logger?.LogInfo($"[客户端] 重连身份恢复成功: playerId={playerId}, isHost={isHost}");

                OnGameEventReceived?.Invoke(NetworkMessageTypes.Reconnect_RESPONSE, jsonPayload);
            }
            else
            {
                string err = root.TryGetProperty("Error", out var eElem) ? eElem.GetString() : "Unknown";
                Plugin.Logger?.LogWarning($"[客户端] 重连恢复失败 ({err})，回退为全新加入流程");
                ClearSessionCredentials();
                _isAwaitingReconnectResponse = false;
                SendPlayerJoined();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[客户端] 处理重连响应异常: {ex.Message}");
            ClearSessionCredentials();
            _isAwaitingReconnectResponse = false;
            SendPlayerJoined();
        }
    }

    private void SendRawJson(string messageType, string json)
    {
        if (!IsConnected || _serverPeer == null)
        {
            return;
        }

        NetDataWriter writer = new NetDataWriter();
        writer.Put(messageType);
        writer.Put(json ?? string.Empty);
        _serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

        private bool IsGameEvent(string messageType)
    {
        return NetworkMessageTypes.IsGameEvent(messageType, NetworkMessageTypes.GameEventRoute.Client);
    }

        public void DispatchGameEventLocally(string eventType, string jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        try
        {
            LogPayloadPreviewOnce(eventType, jsonPayload);

            if (_configManager?.ShouldLog(2) == true)
            {
                Plugin.Logger?.LogInfo($"[客户端] (本地回环) 收到游戏事件: {eventType}");
            }
            Plugin.Logger?.LogDebug($"[客户端] (本地回环) 收到游戏事件: {eventType}");

            OnGameEventReceived?.Invoke(eventType, jsonPayload);

            _synchronizationManager?.ProcessEventFromNetwork(new
            {
                EventType = eventType,
                Payload = (object)jsonPayload,
                Timestamp = DateTime.Now.Ticks
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[客户端] 本地回环分发游戏事件失败: type={eventType}, err={ex.Message}");
        }
    }

        public void InjectLocalGameEvent(string eventType, object payload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        try
        {
            string json = payload is string s ? s : JsonCompat.Serialize(payload);
            DispatchGameEventLocally(eventType, json);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[客户端] 注入本地游戏事件失败: type={eventType}, err={ex.Message}");
        }
    }

        private void HandleGameEvent(string eventType, NetDataReader dataReader)
    {
        try
        {

            string jsonPayload = dataReader.GetString();

            object eventData = jsonPayload;

            LogPayloadPreviewOnce(eventType, jsonPayload);

            if (_configManager?.ShouldLog(2) == true)
            {
                Plugin.Logger?.LogInfo($"[客户端] 收到游戏事件: {eventType}");
            }

            Plugin.Logger?.LogDebug($"[客户端] 收到游戏事件: {eventType}");

            OnGameEventReceived?.Invoke(eventType, eventData);

            if (string.Equals(eventType, NetworkMessageTypes.Welcome, StringComparison.Ordinal))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonPayload);
                    var root = doc.RootElement;
                    string pid = root.TryGetProperty("PlayerId", out var pElem) ? pElem.GetString() : null;
                    string token = root.TryGetProperty("ReconnectToken", out var tElem) ? tElem.GetString() : null;
                    string nonce = root.TryGetProperty("SessionNonce", out var nElem) ? nElem.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(pid))
                    {
                        _lastSessionPlayerId = pid;
                    }
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        _lastSessionReconnectToken = token;
                    }
                    if (!string.IsNullOrWhiteSpace(nonce))
                    {
                        _lastSessionNonce = nonce;
                    }

                    Plugin.Logger?.LogInfo($"[客户端] 已记录当前会话凭证: playerId={_lastSessionPlayerId}");
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"[NetworkClient] Welcome credentials parse error: {ex.Message}");
                }
            }

            _synchronizationManager?.ProcessEventFromNetwork(new
            {
                EventType = eventType,
                Payload = eventData,
                Timestamp = DateTime.Now.Ticks
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[客户端] 处理游戏事件失败: {ex.Message}");
        }
    }

        private void LogPayloadPreviewOnce(string eventType, string jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            eventType = "<unknown>";
        }

        lock (_payloadPreviewLock)
        {

            if (!_payloadPreviewLogged.Add(eventType))
            {
                return;
            }
        }

        string summary = NetLogHelper.BuildSummary(eventType, jsonPayload);
        try
        {
            Plugin.Logger?.LogInfo($"[客户端] Payload 预览(仅一次): event={eventType}, {summary}");
        }
        catch
        {

        }
    }

        public void ConnectToServer(string host, int port)
    {
        string currentEndpoint = $"{host}:{port}";
        if (!string.IsNullOrWhiteSpace(_lastSessionEndpoint) &&
            !string.Equals(currentEndpoint, _lastSessionEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            ClearSessionCredentials();
        }
        _lastSessionEndpoint = currentEndpoint;

        _lastConnectHost = host;
        _lastConnectPort = port;

        string keyToUse = _connectionKey;
        try
        {
            var provider = NetworkPlugin.Network.Services.ModService.ServiceProvider;
            if (provider != null)
            {
                var config = provider.GetService(typeof(NetworkPlugin.Configuration.ConfigManager)) as NetworkPlugin.Configuration.ConfigManager;
                if (config != null)
                {
                    if (NetworkPlugin.Patch.UI.MainMenuMultiplayerEntryPatch.IsLocalServerRunning)
                    {
                        keyToUse = config.HostConnectionKey?.Value ?? NetworkPlugin.Network.Security.SecurityUtils.GenerateSecureKey();
                    }
                    else
                    {
                        keyToUse = config.HostConnectionKey?.Value ?? config.RelayServerConnectionKey?.Value ?? NetworkPlugin.Network.Security.SecurityUtils.GenerateSecureKey();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[NetworkClient] GetConfig error: {ex.Message}");
        }

        Plugin.Logger?.LogInfo($"[客户端] 正在连接服务器 {host}:{port}（密钥: <已隐藏>）...");
        NetDataWriter connectData = new();

        connectData.Put(keyToUse ?? string.Empty);
        connectData.Put(NetworkConstants.ProtocolVersion);

        try
        {
            if (!_netManager.IsRunning)
            {
                Start();
            }
            _netManager.Connect(host, port, connectData);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[客户端] 发起连接异常: {ex.Message}");
        }
    }

        public void PollEvents()
    {

        _netManager.PollEvents();

        SendHeartbeatIfNeeded_NoThrow();
    }

        private void SendHeartbeatIfNeeded_NoThrow()
    {
        try
        {

            if (!IsConnected || _serverPeer == null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;

            if (_lastHeartbeatSentUtc != DateTime.MinValue &&
                (now - _lastHeartbeatSentUtc).TotalMilliseconds < _heartbeatIntervalMs)
            {
                return;
            }

            NetDataWriter writer = new();
            writer.Put(NetworkMessageTypes.Heartbeat);

            _serverPeer.Send(writer, DeliveryMethod.Unreliable);
            _lastHeartbeatSentUtc = now;
        }
        catch
        {

        }
    }

        public void Stop()
    {

        _isStopped = true;
        _autoReconnectEnabled = false;
        StopAutoReconnectTimer_NoThrow();
        _netManager.Stop();
        _serverPeer = null;
        Plugin.Logger?.LogInfo("[客户端] 网络客户端服务已停止。");
    }

        private void StartAutoReconnectTimer_NoThrow()
    {
        try
        {

            if (!_autoReconnectEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastConnectHost) || _lastConnectPort <= 0)
            {
                Plugin.Logger?.LogInfo("[客户端] 自动重连跳过：缺少上次连接的地址信息");
                return;
            }

            lock (_reconnectLock)
            {

                _reconnectTimer?.Dispose();
                _reconnectTimer = new Timer(_ =>
                {
                    try
                    {
                        if (IsConnected || IsConnecting)
                        {
                            return;
                        }

                        if (_reconnectAttemptCount >= _maxReconnectAttempts)
                        {
                            Plugin.Logger?.LogWarning($"[客户端] 自动重连达到最大尝试次数 ({_maxReconnectAttempts})，已停止。");
                            StopAutoReconnectTimer_NoThrow();
                            return;
                        }

                        _reconnectAttemptCount++;
                        Plugin.Logger?.LogInfo($"[客户端] 自动重连尝试 ({_reconnectAttemptCount}/{_maxReconnectAttempts})：{_lastConnectHost}:{_lastConnectPort}");
                        ConnectToServer(_lastConnectHost, _lastConnectPort);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger?.LogError($"[客户端] 自动重连异常: {ex.Message}");
                    }
                }, null, _retryInterval, _retryInterval);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[客户端] 启动自动重连计时器失败: {ex.Message}");
        }
    }

        private void StopAutoReconnectTimer_NoThrow()
    {
        try
        {
            lock (_reconnectLock)
            {
                _reconnectAttemptCount = 0;
                _reconnectTimer?.Dispose();
                _reconnectTimer = null;
            }
        }
        catch
        {

        }
    }

    #endregion

    #region 连接属性

        public bool IsConnected => _serverPeer != null && _serverPeer.ConnectionState == ConnectionState.Connected;

        public string LastDisconnectReason { get; private set; }

        public bool IsConnecting => !IsConnected && _netManager?.FirstPeer != null;

        public int Ping => _serverPeer?.Ping ?? 9999;

        public IPEndPoint LocalEndPoint
    {
        get
        {
            try
            {
                if (_netManager == null || _netManager.LocalPort <= 0)
                {
                    return null;
                }

                return new IPEndPoint(IPAddress.Any, _netManager.LocalPort);
            }
            catch
            {
                return null;
            }
        }
    }

        public IPEndPoint RemoteEndPoint => _serverPeer?.EndPoint;

    #endregion

    #region 数据发送与响应处理

        public void SendGameEventData(string eventType, object eventData)
    {
        SendGameEventData(eventType, eventData, NetworkEventOptions.ActionBroadcast);
    }

        public void BroadcastState(string eventType, object eventData)
    {
        SendGameEventData(eventType, eventData, NetworkEventOptions.StateBroadcast);
    }

        public void BroadcastAction(string eventType, object eventData)
    {
        SendGameEventData(eventType, eventData, NetworkEventOptions.ActionBroadcast);
    }

        public void SendDirect(string targetPlayerId, string eventType, object eventData)
    {
        SendGameEventData(eventType, eventData, NetworkEventOptions.Targeted(targetPlayerId));
    }

        public void SendGameEventData(string eventType, object eventData, NetworkEventOptions options)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        try
        {
            string json = JsonCompat.Serialize(eventData);

            if (!string.IsNullOrWhiteSpace(options.TargetPlayerId))
            {
                string selfPlayerId = GetSelf()?.playerId ?? NetworkIdentityTracker.GetSelfPlayerId();
                bool isTargetSelf = !string.IsNullOrWhiteSpace(selfPlayerId) &&
                                    string.Equals(options.TargetPlayerId, selfPlayerId, StringComparison.Ordinal);

                if (isTargetSelf)
                {

                    Plugin.RunOnMainThread(() => DispatchGameEventLocally(eventType, json));
                    return;
                }

                if (!IsConnected)
                {
                    Plugin.Logger?.LogWarning($"[客户端] 未连接到服务器，无法向 {options.TargetPlayerId} 发送定向事件: {eventType}");
                    return;
                }

                var directEnvelope = new
                {
                    TargetPlayerId = options.TargetPlayerId,
                    Type = eventType,
                    Payload = eventData
                };

                string directJson = JsonCompat.Serialize(directEnvelope);
                NetDataWriter directWriter = new();
                directWriter.Put(NetworkMessageTypes.DirectMessage);
                directWriter.Put(directJson);
                _serverPeer.Send(directWriter, options.DeliveryMethod);

                string directSummary = NetLogHelper.BuildSummary(eventType, json);
                Plugin.Logger?.LogDebug($"[客户端] 已向 {options.TargetPlayerId} 发送定向事件: {eventType} ({directSummary})");
                return;
            }

            if (options.IncludeSelf)
            {
                Plugin.RunOnMainThread(() => DispatchGameEventLocally(eventType, json));
            }

            if (!IsConnected)
            {
                if (!options.IncludeSelf)
                {
                    Plugin.Logger?.LogWarning($"[客户端] 未连接到服务器，无法发送事件: {eventType}");
                }
                return;
            }

            NetDataWriter writer = new();
            writer.Put(eventType);
            writer.Put(json);
            _serverPeer.Send(writer, options.DeliveryMethod);

            string summary = NetLogHelper.BuildSummary(eventType, json);
            Plugin.Logger?.LogDebug($"[客户端] 已发送游戏事件: {eventType} ({summary})");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[客户端] 发送游戏事件失败: {eventType}, 错误: {ex.Message}");
        }
    }

        public void SendRequest<T>(string requestHeader, T requestData)
    {

        if (!IsConnected)
        {
            Plugin.Logger?.LogInfo("[客户端] 未连接到服务器，无法发送请求。");
            return;
        }

        NetDataWriter writer = new();
        writer.Put(requestHeader);

        string payloadForLog = null;

        if (typeof(T).IsPrimitive || typeof(T) == typeof(string))
        {
            switch (requestData)
            {
                case float f: writer.Put(f); break;
                case double d: writer.Put(d); break;
                case long l: writer.Put(l); break;
                case int i: writer.Put(i); break;
                case string s: writer.Put(s); break;
                case bool b: writer.Put(b); break;
                default: throw new NotSupportedException($"Type {typeof(T)} is not supported by NetDataWriter.Put");
            }

            try
            {
                payloadForLog = requestData?.ToString();
            }
            catch
            {
                payloadForLog = string.Empty;
            }
        }
        else
        {

            string json = JsonCompat.Serialize(requestData);
            writer.Put(json);
            payloadForLog = json;
        }

        _serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);
        payloadForLog ??= string.Empty;

        string summary = NetLogHelper.BuildSummary(requestHeader, payloadForLog);
        Plugin.Logger?.LogDebug($"[客户端] 已发送请求: {requestHeader} ({summary})");
    }

        private void HandleRequestResponse(NetPeer fromPeer, NetDataReader dataReader)
    {
        try
        {

            string responseHeader = dataReader.GetString();
            Plugin.Logger?.LogInfo($"[客户端] 收到响应: type='{responseHeader}', from={fromPeer.EndPoint}");

            string responseData = dataReader.GetString();
            Plugin.Logger?.LogInfo($"[客户端] 响应消息: {responseData}");

            OnResponseReceived?.Invoke(responseHeader, responseData);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[客户端] 处理响应失败: {ex.Message}");
        }
    }

    #endregion

    #region 高级功能实现

        public object GetConnectionStats()
    {

        if (_serverPeer == null)
        {
            return new { Status = "Not Connected" };
        }

        return new
        {
            Status = "Connected",
            Ping = _serverPeer.Ping,
            Mtu = _serverPeer.Mtu,
            ConnectionTime = DateTime.Now.Ticks,
            Address = _serverPeer.EndPoint.ToString()
        };
    }

        public void SetConnectionTimeout(int timeoutMs)
    {
        _connectionTimeout = timeoutMs;

        if (_netManager != null) _netManager.DisconnectTimeout = timeoutMs;
        Plugin.Logger?.LogInfo($"[客户端] 连接超时已设置为 {timeoutMs}ms");
    }

        public void EnableAutoReconnect(bool enabled, int retryInterval = NetworkConstants.ReconnectIntervalMs)
    {
        _autoReconnectEnabled = enabled;
        _retryInterval = retryInterval;
        Plugin.Logger?.LogInfo($"[客户端] 自动重连{(enabled ? "已启用" : "已禁用")}, 重试间隔: {retryInterval}ms");

        if (!enabled)
        {
            StopAutoReconnectTimer_NoThrow();
        }
    }

    #endregion
}

#endregion
