#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using LiteNetLib;
using NetworkPlugin.Network.Messages;

namespace NetworkPlugin.Network.Server.Core;

public sealed class ServerCore : IServerCore
{
    private readonly ServerMessageQueue _messageQueue = new ServerMessageQueue();
    private readonly Dictionary<NetPeer, CorePeerSession> _sessions = new Dictionary<NetPeer, CorePeerSession>();
    private readonly object _lock = new object();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

        public ServerOptions Options { get; }
        public IServerLogger Logger { get; }
        public IServerMessageCodec Codec { get; }

        public EventBasedNetListener Listener { get; }
        public NetManager NetManager { get; }

        public event Action<NetPeer>? PeerConnected;
        public event Action<NetPeer, DisconnectInfo>? PeerDisconnected;
        public event Action<NetPeer, int>? PeerLatencyUpdated;
        public event Action<ServerInboundMessage>? MessageReceived;

        public ServerCore(ServerOptions options, IServerLogger? logger = null, IServerMessageCodec? codec = null)
    {
        Options = options ?? new ServerOptions();
        Logger = logger ?? new NullServerLogger();
        Codec = codec ?? new DefaultServerMessageCodec();

        Listener = new EventBasedNetListener();
        NetManager = new NetManager(Listener)
        {
            UnsyncedEvents = true,
            AutoRecycle = true,
            DisconnectTimeout = Options.DisconnectTimeoutMs,
            PingInterval = Options.PingIntervalMs,
        };

        RegisterCoreEvents();
    }

        public void Start()
    {
        if (_isRunning)
        {
            Logger.Warn("[ServerCore] Server is already running");
            return;
        }

        if (!string.IsNullOrWhiteSpace(Options.ListenAddress) &&
            System.Net.IPAddress.TryParse(Options.ListenAddress, out var ip) &&
            !System.Net.IPAddress.Any.Equals(ip))
        {
            NetManager.Start(ip, System.Net.IPAddress.IPv6Any, Options.Port);
        }
        else
        {
            NetManager.Start(Options.Port);
        }
        _isRunning = true;
        Logger.Info($"[ServerCore] Server started on port {Options.Port}, bind={Options.ListenAddress}");

        if (Options.UseBackgroundThread)
        {
            _cts = new CancellationTokenSource();
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "ServerCore Main Loop"
            };
            _thread.Start();
        }
    }

        public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException ex) { Logger?.Debug($"[ServerCore] Cts already disposed: {ex.Message}"); }
        catch (Exception ex) { Logger?.Warn($"[ServerCore] 取消 CancellationTokenSource 失败: {ex.Message}"); }

        try
        {
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(500);
            }
        }
        catch (Exception ex)
        {
            Logger?.Debug($"[ServerCore] Thread join error: {ex.Message}");
        }

        NetManager.Stop();

        lock (_lock)
        {
            _sessions.Clear();
        }

        Logger.Info("[ServerCore] Server stopped");
    }

        public void PollEvents()
    {
        NetManager.PollEvents();
        DrainMessages();
        CleanupTimeoutConnections();
    }

        public bool TryGetSession(NetPeer peer, out CorePeerSession? session)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(peer, out session);
        }
    }

        public void MarkPeerSeen(NetPeer peer)
    {
        if (peer == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(peer, out var session))
            {
                session.MarkSeen(DateTime.UtcNow);
            }
        }
    }

    public bool ValidateConnection(LiteNetLib.Utils.NetDataReader? reader, out string? rejectionReason)
    {
        if (NetManager.PeersCount >= Options.MaxConnections)
        {
            rejectionReason = "MaxConnectionsReached";
            return false;
        }

        string providedKey = reader != null && reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;
        if (!string.IsNullOrEmpty(Options.ConnectionKey))
        {
            if (!string.Equals(providedKey, Options.ConnectionKey, StringComparison.Ordinal))
            {
                rejectionReason = "InvalidKey";
                return false;
            }
        }

        if (reader == null || reader.AvailableBytes < 4)
        {
            rejectionReason = "MissingProtocolVersion";
            return false;
        }

        int clientProtocolVersion = reader.GetInt();
        if (clientProtocolVersion < NetworkConstants.ProtocolVersion)
        {
            rejectionReason = $"IncompatibleProtocolVersion:{clientProtocolVersion}";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    private void RegisterCoreEvents()
    {
        Listener.ConnectionRequestEvent += request =>
        {
            try
            {
                if (!ValidateConnection(request.Data, out string? rejectionReason))
                {
                    request.Reject();
                    Logger.Warn($"[ServerCore] Rejecting connection: {rejectionReason}");
                    return;
                }

                request.Accept();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[ServerCore] Error handling connection request");
                try { request.Reject(); }
                catch (ObjectDisposedException odEx) { Logger?.Debug($"[ServerCore] Request already disposed: {odEx.Message}"); }
                catch (Exception rejectEx) { Logger?.Warn($"[ServerCore] 拒绝连接请求失败: {rejectEx.Message}"); }
            }
        };

        Listener.PeerConnectedEvent += peer =>
        {
            lock (_lock)
            {
                CorePeerSession session = new CorePeerSession
                {
                    Peer = peer,
                    ConnectedAt = DateTime.UtcNow
                };
                session.MarkSeen(DateTime.UtcNow);
                _sessions[peer] = session;
            }

            PeerConnected?.Invoke(peer);
        };

        Listener.PeerDisconnectedEvent += (peer, disconnectInfo) =>
        {
            lock (_lock)
            {
                _sessions.Remove(peer);
            }

            PeerDisconnected?.Invoke(peer, disconnectInfo);
        };

        Listener.NetworkLatencyUpdateEvent += (peer, latency) =>
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(peer, out var session))
                {
                    session.Ping = latency;
                }
            }

            PeerLatencyUpdated?.Invoke(peer, latency);
        };

        Listener.NetworkReceiveEvent += (fromPeer, reader, deliveryMethod) =>
        {
            try
            {
                if (!Codec.TryDecode(reader, out string messageType, out string jsonPayload))
                {
                    return;
                }

                MarkPeerSeen(fromPeer);

                var priority = MessagePriorities.GetPriority(messageType);
                _messageQueue.Enqueue(
                    new ServerInboundMessage(fromPeer, messageType, jsonPayload, deliveryMethod, priority),
                    Options.MaxQueueSize);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[ServerCore] Error receiving message");
            }
            finally
            {
                reader.Recycle();
            }
        };
    }

        private void DrainMessages()
    {
        int handled = 0;
        while (handled < Options.MaxMessagesPerTick && _messageQueue.TryDequeueHighest(out var message))
        {
            try
            {
                MessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[ServerCore] Error processing message: {message.Type}");
            }
            finally
            {
                handled++;
            }
        }
    }

        private void CleanupTimeoutConnections()
    {
        const int defaultTimeoutSeconds = 30;
        int timeoutSeconds = Math.Max(1, Options.DisconnectTimeoutMs / 1000);

        List<NetPeer>? timeoutPeers = null;
        DateTime now = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session.IsTimeout(timeoutSeconds <= 0 ? defaultTimeoutSeconds : timeoutSeconds, now))
                {
                    if (timeoutPeers == null)
                    {
                        timeoutPeers = new List<NetPeer>();
                    }

                    timeoutPeers.Add(kvp.Key);
                }
            }
        }

        if (timeoutPeers == null)
        {
            return;
        }

        foreach (var peer in timeoutPeers)
        {
            try
            {
                Logger.Warn($"[ServerCore] Disconnecting timeout peer: {peer.EndPoint}");
                peer.Disconnect();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[ServerCore] Failed to disconnect timeout peer");
            }
        }
    }

        private void RunLoop()
    {
        CancellationToken token = _cts != null ? _cts.Token : CancellationToken.None;
        while (_isRunning && !token.IsCancellationRequested)
        {
            try
            {
                PollEvents();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[ServerCore] Error in server loop");
            }

            Thread.Sleep(Math.Max(1, Options.BackgroundThreadSleepMs));
        }
    }
}
