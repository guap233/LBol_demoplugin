using System.Collections.Generic;
using NetworkPlugin.Network.NetworkPlayer;

namespace NetworkPlugin.Network.Client;

public interface INetworkManager
{
        INetworkPlayer GetSelf();

        void RegisterPlayer(INetworkPlayer player);

        void RemovePlayer(string id);

        INetworkPlayer GetPlayer(string id);

        INetworkPlayer GetPlayerByPeerId(int peerId);

        IEnumerable<INetworkPlayer> GetAllPlayers();

        int GetPlayerCount();

        void ClearAllPlayers();

        bool IsConnected => GetPlayerCount() > 0;
}
