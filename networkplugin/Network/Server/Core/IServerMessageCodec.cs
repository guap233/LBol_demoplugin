using LiteNetLib;
using LiteNetLib.Utils;

namespace NetworkPlugin.Network.Server.Core;

public interface IServerMessageCodec
{
        bool TryDecode(NetDataReader reader, out string messageType, out string jsonPayload);
        void Encode(NetDataWriter writer, string messageType, string jsonPayload);
}
