using System;
using LiteNetLib;

namespace NetworkPlugin.Network.Server.Core;

public interface IServerCore
{
    ServerOptions Options { get; }

    EventBasedNetListener Listener { get; }
    NetManager NetManager { get; }

    event Action<NetPeer>? PeerConnected;
    event Action<NetPeer, DisconnectInfo>? PeerDisconnected;
    event Action<NetPeer, int>? PeerLatencyUpdated;
    event Action<ServerInboundMessage>? MessageReceived;

    void Start();
    void Stop();
    void PollEvents();
    void MarkPeerSeen(NetPeer peer);
    bool TryGetSession(NetPeer peer, out CorePeerSession? session);
    bool ValidateConnection(LiteNetLib.Utils.NetDataReader? reader, out string? rejectionReason);
}
