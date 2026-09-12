using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Configuration;
using NetworkPlugin.Core;
using NetworkPlugin.Network.RoomSync;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.MidGameJoin;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Network.Reconnection;
using NetworkPlugin.Core.Trade;
using UnityEngine;
using System.Diagnostics;

namespace NetworkPlugin;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
[BepInProcess("LBoL.exe")]
public class Plugin : BaseUnityPlugin
{
        internal static new ManualLogSource Logger;

    private static readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private static readonly object _syncProbeLock = new();
    private static bool _syncWiringLogged;
    private static readonly HashSet<string> _syncPatchScopesLogged = new(StringComparer.Ordinal);

    internal static int MainThreadId { get; private set; }

    private static int _mainThreadQueueWarningCount;

    internal static void RunOnMainThread(Action action)
    {
        if (action == null)
        {
            return;
        }

        if (_mainThreadActions.Count >= Network.NetworkConstants.MaxMainThreadQueueSize)
        {
            if (Interlocked.Increment(ref _mainThreadQueueWarningCount) % 100 == 1)
            {
                Logger?.LogWarning($"[Plugin] 主线程任务调度队列已达上限 ({Network.NetworkConstants.MaxMainThreadQueueSize})，已拒绝积压任务");
            }
            return;
        }

        _mainThreadActions.Enqueue(action);
    }

    internal static void FlushMainThreadActionsForTest(int maxCount = -1)
    {
        int count = 0;
        while ((maxCount < 0 || count < maxCount) && _mainThreadActions.TryDequeue(out Action a))
        {
            count++;
            try
            {
                a?.Invoke();
            }
            catch (Exception ex)
            {
                Logger?.LogDebug($"[Plugin] Main thread test action failed: {ex.Message}");
            }
        }
    }

        public static ConfigManager ConfigManager { get; private set; }

        private ServiceProvider _serviceProvider;

        private static readonly Harmony harmony = PluginInfo.harmony;

    private float _lastCatchUpPumpAtRealtime;

        private void Awake()
    {

        Logger = base.Logger;
        MainThreadId = Thread.CurrentThread.ManagedThreadId;
        Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

        TryLogAssemblyFingerprint();

        ConfigManager = new ConfigManager(Config);
        Logger.LogInfo("配置管理器已初始化");

        ServiceCollection services = new ServiceCollection();

        services.AddSingleton(ConfigManager);

        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        ModService.ServiceProvider = _serviceProvider;

        LogSynchronizationManagerWiringOnce(_serviceProvider);

        try
        {
            _serviceProvider.GetService<ReconnectionManager>()?.Initialize();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning($"[Plugin] Failed to initialize ReconnectionManager: {ex.Message}");
        }

        try
        {
            _serviceProvider.GetService<MidGameJoinManager>()?.Initialize();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning($"[Plugin] Failed to initialize MidGameJoinManager: {ex.Message}");
        }

        try
        {
            _serviceProvider.GetService<TradeSettlementCoordinator>();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning($"[Plugin] Failed to initialize TradeSettlementCoordinator: {ex.Message}");
        }

        if (gameObject == null)
        {
            Logger.LogError("GameObject is null, cannot call DontDestroyOnLoad.");
            return;
        }

        DontDestroyOnLoad(gameObject);

        ApplyHarmonyPatchesSafely(harmony);
        Logger.LogInfo("补丁已加载");

        LogCurrentConfig();
    }

    private static void TryLogAssemblyFingerprint()
    {
        try
        {
            var asm = typeof(Plugin).Assembly;
            var asmPath = asm.Location ?? string.Empty;

            if (string.IsNullOrWhiteSpace(asmPath) || !File.Exists(asmPath))
            {
                Logger?.LogInfo($"[Build] AssemblyLocation unavailable (Location='{asmPath ?? ""}')");
                return;
            }

            FileInfo fi = null;
            if (!string.IsNullOrWhiteSpace(asmPath))
            {
                try { fi = new FileInfo(asmPath); }
                catch (Exception ex) { Logger?.LogWarning($"无法获取 Assembly 文件信息: {asmPath}, {ex.Message}"); }
            }

            var size = fi != null ? fi.Length : -1;
            var lastWriteUtc = fi != null ? fi.LastWriteTimeUtc.ToString("O") : "unknown";
            var fnv64 = TryComputeFnv1a64Hex(asmPath);

            Logger?.LogInfo($"[Build] Assembly='{asmPath}', LastWriteUtc='{lastWriteUtc}', Size={size}, FNV64={fnv64}");
        }
        catch
        {

        }
    }

    private static string TryComputeFnv1a64Hex(string path)
    {
        try
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[64 * 1024];
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        hash ^= buf[i];
                        hash *= prime;
                    }
                }
            }

            return $"0x{hash:x16}";
        }
        catch
        {
            return "(unavailable)";
        }
    }

    internal static void LogSynchronizationManagerResolveFromPatch(string scope, IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(scope) || serviceProvider == null)
        {
            return;
        }

        try
        {
            lock (_syncProbeLock)
            {
                if (_syncPatchScopesLogged.Contains(scope))
                {
                    return;
                }

                _syncPatchScopesLogged.Add(scope);
            }

            ISynchronizationManager syncByInterface = serviceProvider.GetService<ISynchronizationManager>();
            SynchronizationManager syncByConcrete = serviceProvider.GetService<SynchronizationManager>();

            Logger?.LogInfo(
                $"[SyncProbe] PatchResolve[{scope}]: aliasSame={ReferenceEquals(syncByInterface, syncByConcrete)}, " +
                $"interfaceHash={syncByInterface?.GetHashCode() ?? 0}, concreteHash={syncByConcrete?.GetHashCode() ?? 0}");
        }
        catch (Exception ex)
        {
            Logger?.LogWarning($"[SyncProbe] PatchResolve[{scope}] failed: {ex.Message}");
        }
    }

    private static void LogSynchronizationManagerWiringOnce(IServiceProvider serviceProvider)
    {
        if (serviceProvider == null)
        {
            return;
        }

        try
        {
            lock (_syncProbeLock)
            {
                if (_syncWiringLogged)
                {
                    return;
                }

                _syncWiringLogged = true;
            }

            ISynchronizationManager syncByInterface = serviceProvider.GetService<ISynchronizationManager>();
            SynchronizationManager syncByConcrete = serviceProvider.GetService<SynchronizationManager>();
            INetworkClient networkClient = serviceProvider.GetService<INetworkClient>();

            bool networkClientFieldReadable = false;
            bool networkClientUsesAlias = false;
            int networkClientSyncHash = 0;

            if (networkClient is NetworkClient typedClient)
            {
                FieldInfo syncField = typeof(NetworkClient).GetField("_synchronizationManager", BindingFlags.Instance | BindingFlags.NonPublic);
                if (syncField != null)
                {
                    networkClientFieldReadable = true;
                    object clientSyncManager = syncField.GetValue(typedClient);
                    networkClientUsesAlias = ReferenceEquals(clientSyncManager, syncByInterface);
                    networkClientSyncHash = clientSyncManager?.GetHashCode() ?? 0;
                }
            }

            Logger?.LogInfo(
                $"[SyncProbe] StartupWiring: aliasSame={ReferenceEquals(syncByInterface, syncByConcrete)}, " +
                $"interfaceHash={syncByInterface?.GetHashCode() ?? 0}, concreteHash={syncByConcrete?.GetHashCode() ?? 0}, " +
                $"clientFieldReadable={networkClientFieldReadable}, clientSameAsAlias={networkClientUsesAlias}, " +
                $"clientSyncHash={networkClientSyncHash}");
        }
        catch (Exception ex)
        {
            Logger?.LogWarning($"[SyncProbe] StartupWiring failed: {ex.Message}");
        }
    }

        private void ConfigureServices(IServiceCollection services)
    {

        services.AddSingleton(Logger);

        services.AddSingleton<LocalNetworkPlayer>();
        services.AddSingleton<INetworkPlayer>(sp => sp.GetRequiredService<LocalNetworkPlayer>());
        services.AddSingleton<INetworkManager, NetworkManager>();
        services.AddSingleton<NetworkAvailabilityTracker>();
        services.AddSingleton<SynchronizationManager>();
        services.AddSingleton<ISynchronizationManager>(sp => sp.GetRequiredService<SynchronizationManager>());

        services.AddSingleton<INetworkClient>(sp => new NetworkClient(sp.GetRequiredService<ConfigManager>(), null));
        services.AddSingleton<RoomSyncManager>();

        services.AddSingleton(sp => new ReconnectionManager(
            new ReconnectionConfig(), null,
            sp.GetRequiredService<INetworkClient>(),
            sp.GetRequiredService<INetworkManager>(),
            Logger));

        services.AddSingleton(new MidGameJoinConfig());
        services.AddSingleton<MidGameJoinManager>();

        services.AddSingleton(sp => new MapCatchUpOrchestrator(Logger, sp.GetRequiredService<RoomSyncManager>()));

        services.AddSingleton<ITradeInventory, GameRunTradeInventory>();
        services.AddSingleton<TradeSettlementCoordinator>();
    }

        void Update()
    {

        try
        {
            while (_mainThreadActions.TryDequeue(out Action a))
            {
                try
                {
                    a?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"[MainThread] Callback degraded: {ex.Message}");
                }
            }
        }
        catch
        {

        }

        try
        {
            if (_serviceProvider == null)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - _lastCatchUpPumpAtRealtime < 0.25f)
            {
                return;
            }

            _lastCatchUpPumpAtRealtime = now;
            _serviceProvider.GetService<MapCatchUpOrchestrator>()?.PumpMainThread();

            if (Time.frameCount % 30 == 0)
            {
                NetworkPlugin.Patch.UI.MainMenuMultiplayerEntryPatch.ForceEnsureButtonInCurrentScene();
            }
        }
        catch
        {

        }
    }

        void OnDestroy()
    {

        _serviceProvider?.Dispose();

        Logger?.LogInfo("Plugin has been destroyed and resources cleaned up.");
    }

    private void ApplyHarmonyPatchesSafely(Harmony harmony)
    {
        int ok = 0;
        int failed = 0;

        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        {

            if (!Attribute.IsDefined(type, typeof(HarmonyPatch), inherit: true))
            {
                continue;
            }

            try
            {
                harmony.CreateClassProcessor(type).Patch();
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Logger?.LogError($"[Harmony] Failed to patch {type.FullName}: {ex}");
            }
        }

        Logger?.LogInfo($"[Harmony] Patch result: ok={ok}, failed={failed}");
    }

        private void LogCurrentConfig()
    {
        Logger.LogInfo("=== 当前配置信息 ===");
        Logger.LogInfo($"功能开关:");
        Logger.LogInfo($"  卡牌同步: {ConfigManager.EnableCardSync.Value}");
        Logger.LogInfo($"  法力同步: {ConfigManager.EnableManaSync.Value}");
        Logger.LogInfo($"  战斗同步: {ConfigManager.EnableBattleSync.Value}");
        Logger.LogInfo($"  地图同步: {ConfigManager.EnableMapSync.Value}");
        Logger.LogInfo($"  Overlay调试日志: {ConfigManager.DebugOtherPlayersOverlay.Value}");
        Logger.LogInfo($"  UI控件边界调试: {ConfigManager.DebugShowControlBounds.Value}");
        if (ConfigManager.EnableSaveLoadSync.Value)
        {

            Logger.LogWarning("  存档/读档同步: true (Deprecated) -> 已强制关闭：联机不再同步存档 bytes。将使用 FullSnapshot+checkpoint 追赶。");
            try
            {
                ConfigManager.EnableSaveLoadSync.Value = false;
            }
            catch
            {

            }
        }
        else
        {
            Logger.LogInfo("  存档/读档同步: false (Deprecated)");
        }
        Logger.LogInfo($"性能参数:");
        Logger.LogInfo($"  最大队列大小: {ConfigManager.MaxQueueSize.Value}");
        Logger.LogInfo($"  缓存过期时间: {ConfigManager.StateCacheExpiryMinutes.Value} 分钟");
        Logger.LogInfo($"  网络超时时间: {ConfigManager.NetworkTimeoutSeconds.Value} 秒");
        Logger.LogInfo($"  最大重连尝试: {ConfigManager.MaxReconnectAttempts.Value}");
        Logger.LogInfo($"网络参数:");
        Logger.LogInfo($"  PlayerIdOverride: {ConfigManager.PlayerIdOverride.Value}");
        Logger.LogInfo($"  服务器IP: {ConfigManager.ServerIP.Value}");
        Logger.LogInfo($"  服务器端口: {ConfigManager.ServerPort.Value}");
        Logger.LogInfo($"  日志详细程度: {ConfigManager.LogVerbosity.Value}");
        Logger.LogInfo("===================");
    }
}
