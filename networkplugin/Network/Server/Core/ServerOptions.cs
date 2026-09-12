namespace NetworkPlugin.Network.Server.Core;

public sealed class ServerOptions
{
        public int Port { get; set; }
        public string ListenAddress { get; set; } = "0.0.0.0";
        public int MaxConnections { get; set; } = 32;
        public string ConnectionKey { get; set; } = string.Empty;

        public int DisconnectTimeoutMs { get; set; } = 30_000;
        public int PingIntervalMs { get; set; } = 1_000;

        public int MaxQueueSize { get; set; } = 2_000;
        public int MaxMessagesPerTick { get; set; } = 512;

        public bool UseBackgroundThread { get; set; } = false;
        public int BackgroundThreadSleepMs { get; set; } = 15;
}
