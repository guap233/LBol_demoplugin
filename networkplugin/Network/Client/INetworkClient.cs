using System;
using System.Net;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.NetworkPlayer;

namespace NetworkPlugin.Network.Client;

public interface INetworkClient
{
    #region 基本连接管理

        void Start();

        INetworkPlayer GetSelf();

        void ConnectToServer(string host, int port);

        void PollEvents();

        void Stop();

        void ClearSessionCredentials();

        bool IsConnected { get; }

        string LastDisconnectReason { get; }

        bool IsConnecting { get; }

        int Ping { get; }

        IPEndPoint LocalEndPoint { get; }

        IPEndPoint RemoteEndPoint { get; }

    #endregion

    #region 数据传输方法

        void SendGameEventData(string eventType, object eventData);

        void SendGameEventData(string eventType, object eventData, NetworkEventOptions options);

        void BroadcastState(string eventType, object eventData);

        void BroadcastAction(string eventType, object eventData);

        void SendDirect(string targetPlayerId, string eventType, object eventData);

        void SendRequest<T>(string requestHeader, T requestData);

    #endregion

    #region 网络事件

        event Action<string> OnConnected;

        event Action<string, string> OnDisconnected;

        event Action<string, object> OnGameEventReceived;

        event Action<string, object> OnResponseReceived;

        event Action<bool> OnConnectionStateChanged;

    #endregion

    #region 高级功能

        object GetConnectionStats();

        void SetConnectionTimeout(int timeoutMs);

        void EnableAutoReconnect(bool enabled, int retryInterval = NetworkConstants.ReconnectIntervalMs);

    #endregion
}
