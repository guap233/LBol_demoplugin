using BepInEx.Configuration;
using NetworkPlugin.Network;

namespace NetworkPlugin.Configuration;

public partial class ConfigManager
{
        #region 网络参数

        public ConfigEntry<string> PlayerIdOverride { get; private set; }

        public ConfigEntry<string> PlayerNameOverride { get; private set; }

        public ConfigEntry<int> HostServerPort { get; private set; }

        public ConfigEntry<string> HostPlayerNameOverride { get; private set; }

        public ConfigEntry<int> HostMaxConnections { get; private set; }

        public ConfigEntry<string> HostConnectionKey { get; private set; }

        public ConfigEntry<string> HostListenAddress { get; private set; }

        public ConfigEntry<int> ServerPort { get; private set; }

        public ConfigEntry<string> ServerIP { get; private set; }

        public ConfigEntry<int> LogVerbosity { get; private set; }

        public ConfigEntry<int> RelayServerPort { get; private set; }

        public ConfigEntry<int> RelayServerMaxConnections { get; private set; }

        public ConfigEntry<string> RelayServerConnectionKey { get; private set; }

        public ConfigEntry<int> RelayServerMaxRooms { get; private set; }

        public ConfigEntry<int> RelayServerMaxPlayersPerRoom { get; private set; }

    #endregion

        private void BindNetworkSettings(ConfigFile configFile)
    {

        PlayerIdOverride = configFile.Bind(
            "Network",
            "PlayerIdOverride",
            "",
            "玩家自定义 PlayerId（可选；为空则使用服务器下发的 PlayerId）。"
        );

        PlayerNameOverride = configFile.Bind(
            "Network",
            "PlayerNameOverride",
            "",
            "玩家自定义联机昵称（覆盖存档中的名字，为空则使用游戏存档名）。"
        );

        HostServerPort = configFile.Bind(
            "Network",
            "HostServerPort",
            NetworkConstants.DefaultPort,
            "做房主专用监听的端口号"
        );

        HostPlayerNameOverride = configFile.Bind(
            "Network",
            "HostPlayerNameOverride",
            "",
            "做房主专用联机昵称（为空则使用游戏存档名）。"
        );

        HostMaxConnections = configFile.Bind(
            "Network",
            "HostMaxConnections",
            4,
            "做房主本地服务器最大允许的连接数"
        );

        HostConnectionKey = configFile.Bind(
            "Network",
            "HostConnectionKey",
            Network.Security.SecurityUtils.GenerateSecureKey(),
            "做房主本地服务器连接密钥（默认启动时动态生成高熵随机凭证）。"
        );

        HostListenAddress = configFile.Bind(
            "Network",
            "HostListenAddress",
            "0.0.0.0",
            "做房主本地服务器物理监听地址（如 0.0.0.0 监听所有接口，或 127.0.0.1 仅本机回环）。"
        );

        ServerPort = configFile.Bind(
            "Network",
            "ServerPort",
            NetworkConstants.DefaultPort,
            "联机服务器监听的端口号"
        );

        ServerIP = configFile.Bind(
            "Network",
            "ServerIP",
            "127.0.0.1",
            "连接服务器的IP地址"
        );

        LogVerbosity = configFile.Bind(
            "Network",
            "LogVerbosity",
            2,
            "日志详细程度，0 = 仅错误，1 = 错误和警告，2 = 全部"
        );

        RelayServerPort = configFile.Bind(
            "RelayServer",
            "Port",
            NetworkConstants.RelayPort,
            "中继服务器监听端口"
        );

        RelayServerMaxConnections = configFile.Bind(
            "RelayServer",
            "MaxConnections",
            1000,
            "中继服务器最大连接数"
        );

        RelayServerConnectionKey = configFile.Bind(
            "RelayServer",
            "ConnectionKey",
            Network.Security.SecurityUtils.GenerateSecureKey(),
            "中继服务器连接密钥（默认启动时动态生成高熵随机凭证）。"
        );

        RelayServerMaxRooms = configFile.Bind(
            "RelayServer",
            "MaxRooms",
            100,
            "中继服务器最大房间数"
        );

        RelayServerMaxPlayersPerRoom = configFile.Bind(
            "RelayServer",
            "MaxPlayersPerRoom",
            4,
            "中继服务器每个房间最大玩家数"
        );
    }

        public NetworkPlugin.Network.Server.RelayServerConfig GetRelayServerConfig()
    {
        return new NetworkPlugin.Network.Server.RelayServerConfig
        {
            Port = RelayServerPort?.Value ?? NetworkConstants.RelayPort,
            MaxConnections = RelayServerMaxConnections?.Value ?? 1000,
            ConnectionKey = RelayServerConnectionKey?.Value ?? Network.Security.SecurityUtils.GenerateSecureKey(),
            MaxRooms = RelayServerMaxRooms?.Value ?? NetworkPlugin.Network.Server.ServerConstants.MaxRoomCount,
            MaxPlayersPerRoom = RelayServerMaxPlayersPerRoom?.Value ?? NetworkPlugin.Network.Server.ServerConstants.DefaultMaxPlayersPerRoom
        };
    }

        public bool ShouldLog(int level)
    {
        int verbosity = LogVerbosity?.Value ?? 2;
        return verbosity >= level;
    }
}
