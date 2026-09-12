using System;
using System.Collections.Generic;
using System.Text.Json;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.Snapshot;
using NetworkPlugin.Utils;
using NetworkPlugin.Network.NetworkPlayer;
using HarmonyLib;
using UnityEngine;

namespace NetworkPlugin.Patch.UI;

public static partial class OtherPlayersOverlayPatch
{
    #region 网络客户端操作

    private static INetworkClient TryGetNetworkClient()
        => ServiceProvider?.GetService(typeof(INetworkClient)) as INetworkClient;

    private static void EnsureSubscribed(INetworkClient client)
    {
        if (_subscribed && ReferenceEquals(_subscribedClient, client))
        {
            return;
        }

        if (_subscribedClient != null)
        {
            _subscribedClient.OnGameEventReceived -= _onGameEventReceived;
            _subscribedClient.OnConnectionStateChanged -= _onConnectionStateChanged;
        }

        try
        {
            client.OnGameEventReceived += _onGameEventReceived;
            client.OnConnectionStateChanged += _onConnectionStateChanged;
            _subscribedClient = client;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayPatch] 订阅网络事件失败: {ex.Message}");
            _subscribed = false;
            _subscribedClient = null;
        }
    }

    #endregion

    #region 网络事件处理

    private static void OnConnectionStateChanged(bool connected)
    {
        if (connected)
        {
            return;
        }

        lock (_syncLock)
        {
            _players.Clear();
        }

        _selfPlayerId = null;
        ClearRemoteCharacters();
        ClearMapIcons();
        MarkOverlayUiDirty();
    }

    internal static void OnGameEventReceived(string eventType, object payload)
    {
        try
        {
            if (!TryGetJsonElement(payload, out JsonElement root))
            {
                return;
            }

            switch (eventType)
            {
                case NetworkMessageTypes.Welcome:
                    HandleWelcome(root);
                    break;
                case NetworkMessageTypes.PlayerListUpdate:
                    HandlePlayerListUpdate(root);
                    break;
                case NetworkMessageTypes.PlayerJoined:
                    HandlePlayerJoined(root);
                    break;
                case NetworkMessageTypes.PlayerLeft:
                    HandlePlayerLeft(root);
                    break;
                case NetworkMessageTypes.HostChanged:
                    HandleHostChanged(root);
                    break;
                case NetworkMessageTypes.OnPlayerStateUpdate:
                    HandlePlayerStateUpdate(root);
                    break;
                case NetworkMessageTypes.BattlePlayerUsUsedBroadcast:
                case NetworkMessageTypes.BattlePlayerUsUsedReport:
                    HandlePlayerUsUsed(root);
                    break;
                case NetworkMessageTypes.BattlePlayerCardUsedBroadcast:
                case NetworkMessageTypes.BattlePlayerCardUsedReport:
                    HandlePlayerCardUsed(root);
                    break;
                case NetworkMessageTypes.BattlePlayerStatusEffectsDeltaBroadcast:
                case NetworkMessageTypes.BattlePlayerStatusEffectsFullBroadcast:
                    HandlePlayerStatusEffectsBroadcast(root);
                    break;
                case NetworkMessageTypes.BattlePlayerDamageBroadcast:
                case NetworkMessageTypes.BattlePlayerDamageReport:
                case NetworkMessageTypes.BattlePlayerHealBroadcast:
                case NetworkMessageTypes.BattlePlayerHealReport:
                    HandleBattlePlayerDamageOrHealBroadcast(root);
                    break;
                case NetworkMessageTypes.OnEnemyAttackPlayerVisual:
                    HandleEnemyAttackPlayerVisual(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[OtherPlayersOverlayPatch] 处理网络事件失败: {eventType}, {ex.Message}");
        }
    }

    private static string CurrentSelfPlayerId => _selfPlayerId ?? NetworkIdentityTracker.GetSelfPlayerId();

    private static void HandleEnemyAttackPlayerVisual(JsonElement root)
    {
        try
        {
            string playerId = GetString(root, "PlayerId");
            if (string.IsNullOrWhiteSpace(playerId) || string.Equals(playerId, CurrentSelfPlayerId, StringComparison.Ordinal))
            {
                return;
            }

            string enemyId = GetString(root, "EnemyId");
            string gunName = GetString(root, "GunName") ?? "Instant";
            string gunTypeStr = GetString(root, "GunType");
            LBoL.Core.Cards.GunType gunType = Enum.TryParse(gunTypeStr, out LBoL.Core.Cards.GunType parsed) ? parsed : LBoL.Core.Cards.GunType.Single;
            bool isGrazed = GetBool(root, "IsGrazed");
            bool isAccuracy = GetBool(root, "IsAccuracy");
            int damage = GetInt(root, "Damage", 0);

            TriggerRemoteEnemyAttackVisual(playerId, enemyId, gunName, gunType, isGrazed, isAccuracy, damage);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayEventBridge] HandleEnemyAttackPlayerVisual 异常: {ex.Message}");
        }
    }

    private static readonly HashSet<string> _playedActions = new(StringComparer.Ordinal);
    private static readonly Queue<string> _playedActionOrder = new();
    private const int MaxPlayedActionHistory = 1000;

    private static bool TryRecordPlayback(string key)
    {
        lock (_syncLock)
        {
            if (_playedActions.Contains(key))
            {
                return false;
            }
            _playedActions.Add(key);
            _playedActionOrder.Enqueue(key);
            while (_playedActionOrder.Count > MaxPlayedActionHistory)
            {
                string old = _playedActionOrder.Dequeue();
                _playedActions.Remove(old);
            }
            return true;
        }
    }

    private static void HandlePlayerUsUsed(JsonElement root)
    {
        try
        {
            string playerId = GetString(root, "PlayerId");
            string usName = GetString(root, "UsName") ?? GetString(root, "CardName") ?? "符卡";
            if (!string.IsNullOrWhiteSpace(playerId) && !string.Equals(playerId, CurrentSelfPlayerId, StringComparison.Ordinal))
            {
                string playId = GetString(root, "PlayId");
                int actionIndex = GetInt(root, "ActionIndex", -1);
                if (!string.IsNullOrWhiteSpace(playId) && actionIndex >= 0)
                {
                    string dedupKey = $"us|{playerId}|{playId}|{actionIndex}";
                    if (!TryRecordPlayback(dedupKey))
                    {
                        return;
                    }
                }

                JsonElement? actions = root.TryGetProperty("Actions", out JsonElement actionsEl) && actionsEl.ValueKind == JsonValueKind.Array ? actionsEl : null;
                TriggerRemoteCharacterCardUseEffect(playerId, usName, isUs: true, actions);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayEventBridge] HandlePlayerUsUsed 异常: {ex.Message}");
        }
    }

    private static void HandlePlayerCardUsed(JsonElement root)
    {
        try
        {
            string playerId = GetString(root, "PlayerId");
            string cardName = GetString(root, "CardName") ?? "卡牌";
            if (!string.IsNullOrWhiteSpace(playerId) && !string.Equals(playerId, CurrentSelfPlayerId, StringComparison.Ordinal))
            {
                string playId = GetString(root, "PlayId");
                int actionIndex = GetInt(root, "ActionIndex", -1);
                if (!string.IsNullOrWhiteSpace(playId) && actionIndex >= 0)
                {
                    string dedupKey = $"card|{playerId}|{playId}|{actionIndex}";
                    if (!TryRecordPlayback(dedupKey))
                    {
                        return;
                    }
                }

                JsonElement? actions = root.TryGetProperty("Actions", out JsonElement actionsEl) && actionsEl.ValueKind == JsonValueKind.Array ? actionsEl : null;
                TriggerRemoteCharacterCardUseEffect(playerId, cardName, isUs: false, actions);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayEventBridge] HandlePlayerCardUsed 异常: {ex.Message}");
        }
    }

    private static void HandlePlayerStatusEffectsBroadcast(JsonElement root)
    {
        try
        {
            string playerId = GetString(root, "PlayerId");
            if (string.IsNullOrWhiteSpace(playerId) || string.Equals(playerId, _selfPlayerId, StringComparison.Ordinal))
            {
                return;
            }

            List<RemoteStatusEffectInfo> statusEffects = null;
            if (root.TryGetProperty("StatusEffects", out JsonElement seElem) && seElem.ValueKind == JsonValueKind.Array)
            {
                statusEffects = ParsePlayerStatusEffectsArray(seElem);
            }

            if (statusEffects != null)
            {
                ApplyStatusEffectsToRemotePlayer(playerId, statusEffects);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayEventBridge] HandlePlayerStatusEffectsBroadcast 异常: {ex.Message}");
        }
    }

    private static List<RemoteStatusEffectInfo> ParsePlayerStatusEffectsArray(JsonElement seArrayElem)
    {
        List<RemoteStatusEffectInfo> list = new();
        foreach (JsonElement elem in seArrayElem.EnumerateArray())
        {
            if (elem.ValueKind == JsonValueKind.Object)
            {
                string id = GetString(elem, "Id");
                string type = GetString(elem, "Type") ?? id;
                int level = GetInt(elem, "Level", 0);
                int duration = GetInt(elem, "Duration", 0);
                list.Add(new RemoteStatusEffectInfo
                {
                    Id = id,
                    Type = type,
                    Level = level,
                    Duration = duration,
                });
            }
            else if (elem.ValueKind == JsonValueKind.String)
            {
                string str = elem.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    string[] parts = str.Split(':', '|');
                    string type = parts[0];
                    int level = parts.Length > 1 && int.TryParse(parts[1], out int l) ? l : 1;
                    int duration = parts.Length > 2 && int.TryParse(parts[2], out int d) ? d : 0;
                    list.Add(new RemoteStatusEffectInfo
                    {
                        Id = type,
                        Type = type,
                        Level = level,
                        Duration = duration,
                    });
                }
            }
        }
        return list;
    }

    private static void HandlePlayerStateUpdate(JsonElement root)
    {
        try
        {
            string playerId = GetString(root, "PlayerId");
            if (string.IsNullOrWhiteSpace(playerId))
            {
                return;
            }

            if (!root.TryGetProperty("Player", out JsonElement playerElem) || playerElem.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            int hp = GetInt(playerElem, "Hp", 0);
            int maxHp = GetInt(playerElem, "MaxHp", 0);
            int block = GetInt(playerElem, "Block", 0);
            int shield = GetInt(playerElem, "Shield", 0);
            int power = GetInt(playerElem, "Power", 0);
            int powerPerLevel = GetInt(playerElem, "PowerPerLevel", 100);
            int maxPowerLevel = GetInt(playerElem, "MaxPowerLevel", 3);

            INetworkManager manager = TryGetNetworkManager();
            if (manager != null)
            {
                INetworkPlayer networkPlayer = manager.GetPlayer(playerId);
                if (networkPlayer != null)
                {
                    networkPlayer.HP = hp;
                    networkPlayer.maxHP = maxHp;
                    networkPlayer.block = block;
                    networkPlayer.shield = shield;

                    if (networkPlayer is RemoteNetworkPlayer remotePlayer)
                    {
                        remotePlayer.Power = power;
                        remotePlayer.PowerPerLevel = powerPerLevel;
                        remotePlayer.MaxPowerLevel = maxPowerLevel;
                    }
                    else
                    {
                        Traverse.Create(networkPlayer).Property("Power").SetValue(power);
                        Traverse.Create(networkPlayer).Property("PowerPerLevel").SetValue(powerPerLevel);
                        Traverse.Create(networkPlayer).Property("MaxPowerLevel").SetValue(maxPowerLevel);
                    }

                    MarkOverlayUiDirty();
                }
            }

            lock (_syncLock)
            {
                if (_players.TryGetValue(playerId, out PlayerSummary summary))
                {
                    if (maxHp > 0) summary.MaxHp = maxHp;
                    if (hp >= 0) summary.Hp = hp;
                }
            }

            if (playerElem.TryGetProperty("StatusEffects", out JsonElement seElem) && seElem.ValueKind == JsonValueKind.Array)
            {
                var statusEffects = ParsePlayerStatusEffectsArray(seElem);
                if (statusEffects != null && !string.Equals(playerId, _selfPlayerId, StringComparison.Ordinal))
                {
                    ApplyStatusEffectsToRemotePlayer(playerId, statusEffects);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayPatch] HandlePlayerStateUpdate 失败: {ex.Message}");
        }
    }

    private static void HandlePlayerListUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("Players", out JsonElement playersElem) || playersElem.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        ReplacePlayersFromArray(playersElem);
    }

    private static void HandlePlayerJoined(JsonElement root)
    {
        string playerId = GetString(root, "PlayerId");
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        bool hasConnectedField = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("IsConnected", out _);

        lock (_syncLock)
        {
            string charId = ResolveCharacterId(root);

            if (!string.IsNullOrWhiteSpace(_selfPlayerId) && string.Equals(playerId, _selfPlayerId, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(charId))
            {
                charId = GetFallbackCharacterId();
            }

            int hp = GetInt(root, "Hp", -1);
            int maxHp = GetInt(root, "MaxHp", -1);
            if (maxHp <= 0 && _players.TryGetValue(playerId, out PlayerSummary oldSummary) && oldSummary.MaxHp > 0)
            {
                hp = oldSummary.Hp;
                maxHp = oldSummary.MaxHp;
            }

            _players[playerId] = new PlayerSummary
            {
                PlayerId = playerId,
                PlayerName = GetString(root, "PlayerName") ?? playerId,
                IsHost = GetBool(root, "IsHost"),
                IsConnected = hasConnectedField ? GetBool(root, "IsConnected") : true,
                CharacterId = charId,
                LocationX = GetInt(root, "LocationX", -1),
                LocationY = GetInt(root, "LocationY", -1),
                Stage = GetInt(root, "Stage", -1),
                LocationName = GetString(root, "LocationName"),
                Hp = hp,
                MaxHp = maxHp,
                LastUpdateTime = Time.unscaledTime,
            };

            if (maxHp > 0)
            {
                INetworkManager manager = TryGetNetworkManager();
                INetworkPlayer networkPlayer = manager?.GetPlayer(playerId);
                if (networkPlayer != null)
                {
                    if (hp >= 0) networkPlayer.HP = hp;
                    networkPlayer.maxHP = maxHp;
                }
            }
        }

        MarkOverlayUiDirty();
        ForceRefreshRemoteCharacters();
    }

    private static void HandleHostChanged(JsonElement root)
    {
        string newHostId = GetString(root, "NewHostId");
        if (string.IsNullOrWhiteSpace(newHostId))
        {
            return;
        }

        lock (_syncLock)
        {
            foreach (KeyValuePair<string, PlayerSummary> kv in _players)
            {
                kv.Value.IsHost = kv.Key == newHostId;
            }
        }

        MarkOverlayUiDirty();
    }

    private static void HandleWelcome(JsonElement root)
    {
        string selfId = GetString(root, "PlayerId");
        if (!string.IsNullOrWhiteSpace(selfId))
        {
            _selfPlayerId = selfId;
        }

        if (root.TryGetProperty("PlayerList", out JsonElement playersElem) && playersElem.ValueKind == JsonValueKind.Array)
        {
            ReplacePlayersFromArray(playersElem);
        }
    }

    private static void HandlePlayerLeft(JsonElement root)
    {
        string playerId = GetString(root, "PlayerId");
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        lock (_syncLock)
        {
            _players.Remove(playerId);
        }

        Plugin.RunOnMainThread(() =>
        {
            if (_ui?.Entries != null && _ui.Entries.TryGetValue(playerId, out var entryUi))
            {
                if (entryUi?.Root != null)
                {
                    UnityEngine.Object.Destroy(entryUi.Root);
                }
                _ui.Entries.Remove(playerId);
            }
        });

        RemoveRemoteCharacter(playerId);
        HideMapIcon(playerId);
        MarkOverlayUiDirty();
    }

    private static void ReplacePlayersFromArray(JsonElement playersElem)
    {
        Dictionary<string, PlayerSummary> incoming = new Dictionary<string, PlayerSummary>();
        foreach (JsonElement p in playersElem.EnumerateArray())
        {
            string playerId = GetString(p, "PlayerId");
            if (string.IsNullOrWhiteSpace(playerId))
            {
                continue;
            }

            bool hasConnectedField = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("IsConnected", out _);
            string playerName = GetString(p, "PlayerName");
            string charId = ResolveCharacterId(p);

            if (!string.IsNullOrWhiteSpace(_selfPlayerId) && string.Equals(playerId, _selfPlayerId, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(charId))
            {
                charId = GetFallbackCharacterId();
            }

            int hp = GetInt(p, "Hp", -1);
            int maxHp = GetInt(p, "MaxHp", -1);
            if (maxHp <= 0)
            {
                lock (_syncLock)
                {
                    if (_players.TryGetValue(playerId, out PlayerSummary oldSummary) && oldSummary.MaxHp > 0)
                    {
                        hp = oldSummary.Hp;
                        maxHp = oldSummary.MaxHp;
                    }
                }
            }

            incoming[playerId] = new PlayerSummary
            {
                PlayerId = playerId,
                PlayerName = string.IsNullOrWhiteSpace(playerName) ? playerId : playerName,
                IsHost = GetBool(p, "IsHost"),
                IsConnected = hasConnectedField ? GetBool(p, "IsConnected") : true,
                CharacterId = charId,
                LocationX = GetInt(p, "LocationX", -1),
                LocationY = GetInt(p, "LocationY", -1),
                Stage = GetInt(p, "Stage", -1),
                LocationName = GetString(p, "LocationName"),
                Hp = hp,
                MaxHp = maxHp,
                LastUpdateTime = Time.unscaledTime,
            };

            if (maxHp > 0)
            {
                INetworkManager manager = TryGetNetworkManager();
                INetworkPlayer networkPlayer = manager?.GetPlayer(playerId);
                if (networkPlayer != null)
                {
                    if (hp >= 0) networkPlayer.HP = hp;
                    networkPlayer.maxHP = maxHp;
                }
            }
        }

        lock (_syncLock)
        {
            _players.Clear();
            foreach (var kv in incoming)
            {
                _players[kv.Key] = kv.Value;
            }
        }

        MarkOverlayUiDirty();
        ForceRefreshRemoteCharacters();
    }

    private static void HandleBattlePlayerDamageOrHealBroadcast(JsonElement root)
    {
        try
        {
            string playerId = GetString(root, "PlayerId");
            if (string.IsNullOrWhiteSpace(playerId))
            {
                return;
            }

            if (!root.TryGetProperty("TargetState", out JsonElement targetStateElem) || targetStateElem.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            int hp = GetInt(targetStateElem, "Hp", 0);
            int maxHp = GetInt(targetStateElem, "MaxHp", 0);
            int block = GetInt(targetStateElem, "Block", 0);
            int shield = GetInt(targetStateElem, "Shield", 0);

            INetworkManager manager = TryGetNetworkManager();
            if (manager != null)
            {
                INetworkPlayer networkPlayer = manager.GetPlayer(playerId);
                if (networkPlayer != null)
                {
                    networkPlayer.HP = hp;
                    if (maxHp > 0)
                    {
                        networkPlayer.maxHP = maxHp;
                    }
                    networkPlayer.block = block;
                    networkPlayer.shield = shield;

                    MarkOverlayUiDirty();
                }
            }

            lock (_syncLock)
            {
                if (_players.TryGetValue(playerId, out PlayerSummary summary))
                {
                    if (maxHp > 0) summary.MaxHp = maxHp;
                    if (hp >= 0) summary.Hp = hp;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayEventBridge] HandleBattlePlayerDamageOrHealBroadcast 异常: {ex.Message}");
        }
    }

    #endregion

    #region JSON工具

    private static string ResolveCharacterId(JsonElement elem)
    {
        string value = GetString(elem, "CharacterId");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = GetString(elem, "ModelName");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = GetString(elem, "CharacterName");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = GetString(elem, "PlayerModel");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = GetString(elem, "chara");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return GetString(elem, "Chara");
    }

    private static bool TryGetJsonElement(object payload, out JsonElement element)
    {
        if (payload is JsonElement je)
        {
            element = je;
            return true;
        }

        if (payload is string s && !string.IsNullOrWhiteSpace(s))
        {
            using JsonDocument doc = JsonDocument.Parse(s);
            element = doc.RootElement.Clone();
            return true;
        }

        if (payload != null)
        {
            string json = JsonCompat.Serialize(payload);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                element = doc.RootElement.Clone();
                return true;
            }
        }

        element = default;
        return false;
    }

    private static string GetString(JsonElement elem, string property)
    {
        if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool GetBool(JsonElement elem, string property)
    {
        if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
        {
            return false;
        }

        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => p.TryGetInt32(out int v) && v != 0,
            JsonValueKind.String => bool.TryParse(p.GetString(), out bool b) && b,
            _ => false,
        };
    }

    private static int GetInt(JsonElement elem, string property, int defaultValue = 0)
    {
        if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
        {
            return defaultValue;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out int v) ? v : defaultValue,
            JsonValueKind.String => int.TryParse(p.GetString(), out int v) ? v : defaultValue,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => defaultValue,
        };
    }

    #endregion
}
