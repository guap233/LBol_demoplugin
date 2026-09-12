namespace NetworkPlugin.Network.Server;

public class RelayServerConfig
{
        public int Port { get; set; } = ServerConstants.DefaultRelayPort;

        public int MaxConnections { get; set; } = ServerConstants.DefaultMaxConnections;

        public int MaxRooms { get; set; } = ServerConstants.MaxRoomCount;

        public int MaxPlayersPerRoom { get; set; } = ServerConstants.DefaultMaxPlayersPerRoom;

        public int DisconnectTimeoutSeconds { get; set; } = ServerConstants.DisconnectTimeoutSeconds;

        public string ServerName { get; set; } = "LBoL Relay Server";

    public string ConnectionKey { get; set; } = Security.SecurityUtils.GenerateSecureKey();

        public bool EnableNatPunchthrough { get; set; } = true;
}
