using System;
using System.Collections.Generic;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Network.Snapshot;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Utils;
using UnityEngine;

namespace NetworkPlugin.Patch.UI;

public static partial class OtherPlayersOverlayPatch
{
    private static readonly Dictionary<string, VirtualAiBattleStateEntry> _virtualAiBattleStates;

    internal struct RemoteBattleState
    {
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public int Shield { get; set; }
        public int Block { get; set; }
        public int CurrentPower { get; set; }
        public int PowerPerLevel { get; set; }
        public int MaxPowerLevel { get; set; }
        public bool HasFreshBattleState { get; set; }
    }

    private sealed class VirtualAiBattleStateEntry
    {
        public RemoteBattleState State { get; set; }
        public float NextRefreshTime { get; set; }
    }

    private static bool TryGetRemoteBattleState(PlayerSummary playerSummary, out RemoteBattleState state)
    {
        state = default;
        if (playerSummary == null || string.IsNullOrWhiteSpace(playerSummary.PlayerId))
        {
            return false;
        }

        if (IsVirtualAiDebugPlayerId(playerSummary.PlayerId) && TryGetVirtualAiDebugBattleState(playerSummary.PlayerId, out state))
        {
            return true;
        }

        INetworkPlayer networkPlayer = TryGetNetworkManager()?.GetPlayer(playerSummary.PlayerId);
        PlayerStateSnapshot snapshot = TryGetLatestRemoteBattleSnapshot(playerSummary.PlayerId);

        bool hasSnapshot = snapshot != null;
        bool hasRuntime = networkPlayer != null &&
            (networkPlayer.maxHP > 0 || networkPlayer.HP > 0 || networkPlayer.block > 0 || networkPlayer.shield > 0 ||
             networkPlayer.GetCurrentPowerSafe() > 0 || networkPlayer.GetPowerPerLevelSafe() > 0 || networkPlayer.GetMaxPowerLevelSafe() > 0);

        if (!hasSnapshot && !hasRuntime)
        {
            if (playerSummary.MaxHp <= 0)
            {
                return false;
            }

            int defaultMaxHp = playerSummary.MaxHp;
            int defaultHp = playerSummary.Hp >= 0 ? Math.Min(playerSummary.Hp, defaultMaxHp) : defaultMaxHp;
            state = new RemoteBattleState
            {
                Health = defaultHp,
                MaxHealth = defaultMaxHp,
                Shield = 0,
                Block = 0,
                CurrentPower = 0,
                PowerPerLevel = 1,
                MaxPowerLevel = 3,
                HasFreshBattleState = false,
            };
            return true;
        }

        bool useRuntime = networkPlayer != null && networkPlayer.maxHP > 0;

        int health = useRuntime ? networkPlayer.HP : (hasSnapshot ? snapshot.Health : (playerSummary.Hp >= 0 ? playerSummary.Hp : 0));
        int maxHealth = useRuntime ? networkPlayer.maxHP : (hasSnapshot ? snapshot.MaxHealth : (playerSummary.MaxHp > 0 ? playerSummary.MaxHp : 0));
        int shield = useRuntime ? networkPlayer.shield : (hasSnapshot ? snapshot.Shield : 0);
        int block = useRuntime ? networkPlayer.block : (hasSnapshot ? snapshot.Block : 0);
        int currentPower = networkPlayer?.GetCurrentPowerSafe() ?? 0;
        int powerPerLevel = networkPlayer?.GetPowerPerLevelSafe() ?? 0;
        int maxPowerLevel = networkPlayer?.GetMaxPowerLevelSafe() ?? 0;

        if (maxHealth <= 0)
        {
            maxHealth = playerSummary.MaxHp > 0 ? playerSummary.MaxHp : 0;
            if (maxHealth <= 0)
            {
                return false;
            }
        }

        state = new RemoteBattleState
        {
            Health = Math.Max(0, health),
            MaxHealth = Math.Max(1, maxHealth),
            Shield = Math.Max(0, shield),
            Block = Math.Max(0, block),
            CurrentPower = Math.Max(0, currentPower),
            PowerPerLevel = Math.Max(0, powerPerLevel),
            MaxPowerLevel = Math.Max(0, maxPowerLevel),
            HasFreshBattleState = hasSnapshot,
        };

        return true;
    }

    private static int GetDefaultMaxHpForCharacter(string characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId)) return 80;
        return characterId switch
        {
            "Reimu" => 80,
            "Marisa" => 75,
            "Sakuya" => 80,
            "Cirno" => 70,
            "Koishi" => 95,
            "Youmu" => 80,
            _ => 80,
        };
    }

    private static bool TryGetVirtualAiDebugBattleState(string playerId, out RemoteBattleState state)
    {
        state = default;
        if (!IsVirtualAiDefaultEnabled() || !IsVirtualAiDebugPlayerId(playerId))
        {
            return false;
        }

        if (!_virtualAiBattleStates.TryGetValue(playerId, out VirtualAiBattleStateEntry entry))
        {
            entry = new VirtualAiBattleStateEntry();
            _virtualAiBattleStates[playerId] = entry;
        }

        float now = (float)(DateTime.UtcNow.Ticks / 10_000_000.0);
        if (now >= entry.NextRefreshTime)
        {
            var rng = new System.Random();
            int variant = string.Equals(playerId, "aidefault3", StringComparison.Ordinal) ? 2 : (string.Equals(playerId, "aidefault2", StringComparison.Ordinal) ? 1 : 0);
            int maxHealth = rng.Next(48 + variant * 10, 92 + variant * 10);
            int healthMin = Math.Max(8, maxHealth / 3);
            int health = rng.Next(healthMin, maxHealth + 1);
            int shield = rng.Next(0, 19 + variant * 12);
            int block = rng.Next(0, 17 + variant * 10);
            int powerPerLevel = 100;
            int maxPowerLevel = 3;
            int currentPower = rng.Next(0, powerPerLevel * maxPowerLevel + 1);

            entry.State = new RemoteBattleState
            {
                Health = health,
                MaxHealth = maxHealth,
                Shield = shield,
                Block = block,
                CurrentPower = currentPower,
                PowerPerLevel = powerPerLevel,
                MaxPowerLevel = maxPowerLevel,
                HasFreshBattleState = true,
            };
            entry.NextRefreshTime = now + (float)(0.75 + rng.NextDouble() * 0.85);
        }

        state = entry.State;
        return true;
    }

    private static PlayerStateSnapshot TryGetLatestRemoteBattleSnapshot(string playerId)
    {
        PlayerStateSnapshot latest = null;

        if (TurnBoundaryReceivePatch.TryGetLastSnapshot(playerId, out TurnBoundarySnapshot snapshot) && snapshot?.PlayerState != null)
        {
            latest = snapshot.PlayerState;
        }

        return latest;
    }
}
