using System;
using LiteNetLib;
using LiteNetLib.Utils;
using NetworkPlugin.Network.Room;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Network.Messages;

public class NetworkConnection
{
    #region 公共属性

        public string CurrentRoomId { get; internal set; }

        public string PlayerId { get; internal set; }

        public NetPeer Peer { get; internal set; }

    #endregion

    #region 私有字段

        private readonly Action<NetPeer, string, string, DeliveryMethod>? _sendRaw;

    #endregion

    #region 构造函数

        public NetworkConnection(NetPeer peer, string playerId, string currentRoomId = "", Action<NetPeer, string, string, DeliveryMethod>? sendRaw = null)
    {

        Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        PlayerId = playerId ?? throw new ArgumentNullException(nameof(playerId));

        CurrentRoomId = currentRoomId ?? string.Empty;
        _sendRaw = sendRaw;
    }

    #endregion

    #region 公共方法

        public void SendMessage(NetworkMessage message, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered)
    {

        if (message == null)
        {
            return;
        }

        string payloadJson = message.Payload switch
        {
            null => string.Empty,
            string s => s,
            _ => JsonCompat.Serialize(message.Payload),
        };

        if (_sendRaw != null)
        {
            _sendRaw(Peer, message.Type, payloadJson, deliveryMethod);
            return;
        }

        try
        {
            NetDataWriter writer = new();
            writer.Put(message.Type);
            writer.Put(payloadJson);
            Peer.Send(writer, deliveryMethod);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[NetworkConnection] Send message failed: {ex.Message}");
        }
    }

    #endregion
}
