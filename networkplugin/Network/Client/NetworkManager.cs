using System;
using System.Collections.Generic;
using System.Text.Json;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Core.Trade;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Network.Client;

public class NetworkManager : INetworkManager
{
    #region 私有字段

        private readonly INetworkClient _networkClient;

        private readonly object _playersLock = new();

        private readonly Dictionary<string, INetworkPlayer> _players = [];

        private INetworkPlayer _selfPlayer;

        private string _selfKey = "self";

        private INetworkPlayer[] _playersSnapshot = Array.Empty<INetworkPlayer>();

        private int _playersSnapshotRevision = -1;

        private int _playersRevision;

        public NetworkManager(INetworkClient networkClient, LocalNetworkPlayer selfPlayer = null)
    {
        _networkClient = networkClient;
        _selfPlayer = selfPlayer ?? new LocalNetworkPlayer(networkClient);
        lock (_playersLock)
        {
            _players[_selfKey] = _selfPlayer;
            MarkPlayersDirty_NoLock();
        }

        try
        {
            NetworkIdentityTracker.EnsureSubscribed(_networkClient);

            ResurrectSyncPatch.EnsureSubscribed(_networkClient);

            GapOptionsSyncPatch.EnsureSubscribed(_networkClient);

            TradeSyncPatch.EnsureSubscribed(_networkClient);
            TradeSettlementCoordinator.EnsureInitialized();

            _networkClient.OnGameEventReceived += OnGameEventReceived;
            _networkClient.OnConnectionStateChanged += OnConnectionStateChanged;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[NetworkManager] Subscription initialization error: {ex.Message}");
        }
    }

    #endregion

    #region INetworkManager 实现

        public bool IsConnected => _networkClient?.IsConnected == true;

        public IEnumerable<INetworkPlayer> GetAllPlayers()
    {

        SyncPlayersFromIdentityTracker();
        return GetPlayersSnapshot();
    }

        public INetworkPlayer GetPlayer(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        SyncPlayersFromIdentityTracker();
        lock (_playersLock)
        {
            _players.TryGetValue(id, out var player);
            return player;
        }
    }

        public int GetPlayerCount()
    {

        if (_networkClient?.IsConnected != true)
        {
            return 1;
        }

        try
        {
            HashSet<string> ids = NetworkIdentityTracker.GetPlayerIdsSnapshot();
            if (ids.Count > 0)
            {
                return ids.Count;
            }
        }
        catch
        {

        }

        SyncPlayersFromIdentityTracker();
        lock (_playersLock)
        {
            return Math.Max(1, _players.Count);
        }
    }

        public INetworkPlayer GetSelf()
    {
        return _selfPlayer;
    }

        public void RegisterPlayer(INetworkPlayer player)
    {
        if (player == null)
        {
            throw new ArgumentNullException(nameof(player));
        }

        string id = player.userName;
        if (string.IsNullOrWhiteSpace(id))
        {

            id = Guid.NewGuid().ToString("N");
        }

        lock (_playersLock)
        {
            bool changed = !_players.TryGetValue(id, out INetworkPlayer existing) || !ReferenceEquals(existing, player);
            _players[id] = player;
            if (ReferenceEquals(player, _selfPlayer))
            {
                _selfPlayer = player;
                _selfKey = id;
            }

            if (changed)
            {
                MarkPlayersDirty_NoLock();
            }
        }
    }

        public void RemovePlayer(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (_playersLock)
        {

            if (string.Equals(id, _selfKey, StringComparison.Ordinal))
            {
                return;
            }

            if (_players.Remove(id))
            {
                MarkPlayersDirty_NoLock();
            }
        }
    }

    #endregion

    #region 内部方法

        internal void SetSelfPlayer(INetworkPlayer player)
    {
        _selfPlayer = player;
    }

    public void ClearAllPlayers()
    {
        lock (_playersLock)
        {
            _players.Clear();

            _selfKey = "self";
            _players[_selfKey] = _selfPlayer;
            MarkPlayersDirty_NoLock();
        }
    }

        public INetworkPlayer GetPlayerByPeerId(int peerId)
    {

        return null;
    }

    #endregion

    #region 事件订阅与同步

        private void OnConnectionStateChanged(bool connected)
    {

        if (connected)
        {
            return;
        }

        ClearAllPlayers();
    }

        private void OnGameEventReceived(string eventType, object payload)
    {

        if (eventType != NetworkMessageTypes.Welcome &&
            eventType != NetworkMessageTypes.PlayerListUpdate &&
            eventType != NetworkMessageTypes.PlayerJoined &&
            eventType != NetworkMessageTypes.PlayerLeft &&
            eventType != NetworkMessageTypes.HostChanged)
        {
            return;
        }

        SyncPlayersFromIdentityTracker();
        TryUpdatePlayersFromPayload(eventType, payload);
    }

        private void SyncPlayersFromIdentityTracker()
    {

        if (_networkClient?.IsConnected != true)
        {
            return;
        }

        try
        {

            NetworkIdentityTracker.EnsureSubscribed(_networkClient);
            TradeSyncPatch.EnsureSubscribed(_networkClient);
            TradeSettlementCoordinator.EnsureInitialized();

            HashSet<string> ids = NetworkIdentityTracker.GetPlayerIdsSnapshot();

            if (ids.Count == 0)
            {
                return;
            }

            string selfId = NetworkIdentityTracker.GetSelfPlayerId();

            lock (_playersLock)
            {

                bool changed = false;

                if (!string.IsNullOrWhiteSpace(selfId) && !string.Equals(_selfKey, selfId, StringComparison.Ordinal))
                {

                    if (_players.Remove(_selfKey))
                    {
                        changed = true;
                    }

                    _selfKey = selfId;

                    if (!_players.TryGetValue(_selfKey, out INetworkPlayer existing) || !ReferenceEquals(existing, _selfPlayer))
                    {
                        _players[_selfKey] = _selfPlayer;
                        changed = true;
                    }
                }

                foreach (string id in ids)
                {

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    if (string.Equals(id, _selfKey, StringComparison.Ordinal))
                    {

                        if (!_players.TryGetValue(id, out INetworkPlayer existing) || !ReferenceEquals(existing, _selfPlayer))
                        {
                            _players[id] = _selfPlayer;
                            changed = true;
                        }
                        continue;
                    }

                    if (!_players.ContainsKey(id))
                    {
                        _players[id] = new RemoteNetworkPlayer(id);
                        changed = true;
                    }
                }

                List<string> toRemove = null;

                foreach (string key in _players.Keys)
                {

                    if (string.Equals(key, _selfKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!ids.Contains(key))
                    {
                        (toRemove ??= new List<string>()).Add(key);
                    }
                }

                if (toRemove != null)
                {
                    foreach (string key in toRemove)
                    {

                        if (_players.Remove(key))
                        {
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    MarkPlayersDirty_NoLock();
                }
            }
        }
        catch
        {

        }
    }

        private void TryUpdatePlayersFromPayload(string eventType, object payload)
    {
        if (!TryGetJsonElement(payload, out JsonElement root))
        {
            return;
        }

        try
        {
            switch (eventType)
            {
                case NetworkMessageTypes.Welcome:
                    if ((root.TryGetProperty("Players", out JsonElement list) && list.ValueKind == JsonValueKind.Array) ||
                        (root.TryGetProperty("PlayerList", out list) && list.ValueKind == JsonValueKind.Array))
                    {
                        UpdatePlayersFromArray(list);
                    }

                    return;
                case NetworkMessageTypes.PlayerListUpdate:
                    if (root.TryGetProperty("Players", out JsonElement players) && players.ValueKind == JsonValueKind.Array)
                    {
                        UpdatePlayersFromArray(players);
                    }

                    return;
                case NetworkMessageTypes.PlayerJoined:
                    UpdateSinglePlayer(root);
                    return;
                case NetworkMessageTypes.PlayerLeft:
                    string leftId = GetString(root, "PlayerId");
                    if (!string.IsNullOrWhiteSpace(leftId))
                    {
                        RemovePlayer(leftId);
                    }

                    return;
            }
        }
        catch
        {

        }
    }

    internal void UpdatePlayersFromArray(JsonElement playersArray)
    {
        if (playersArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        HashSet<string> incomingOnlineIds = new(StringComparer.Ordinal);

        foreach (JsonElement p in playersArray.EnumerateArray())
        {
            string playerId = GetString(p, "PlayerId") ?? GetString(p, "Id");
            if (string.IsNullOrWhiteSpace(playerId))
            {
                continue;
            }

            bool hasConnectedField = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("IsConnected", out _);
            bool isConnected = !hasConnectedField || GetBool(p, "IsConnected", true);

            if (isConnected)
            {
                incomingOnlineIds.Add(playerId);
                UpdateSinglePlayer(p);
            }
        }

        lock (_playersLock)
        {
            List<string> toRemove = null;
            foreach (string key in _players.Keys)
            {
                if (string.Equals(key, _selfKey, StringComparison.Ordinal) || string.Equals(key, "self", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!incomingOnlineIds.Contains(key))
                {
                    (toRemove ??= new List<string>()).Add(key);
                }
            }

            if (toRemove != null)
            {
                foreach (string key in toRemove)
                {
                    _players.Remove(key);
                }
                MarkPlayersDirty_NoLock();
            }
        }
    }

        private void UpdateSinglePlayer(JsonElement playerObj)
    {
        string playerId = GetString(playerObj, "PlayerId") ?? GetString(playerObj, "Id");
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        string playerName = GetString(playerObj, "PlayerName") ?? GetString(playerObj, "Name");
        string characterId = GetString(playerObj, "CharacterId") ?? GetString(playerObj, "Chara");

        int locX = GetInt(playerObj, "LocationX", -1);
        int locY = GetInt(playerObj, "LocationY", -1);
        int stage = GetInt(playerObj, "Stage", -1);
        string locName = GetString(playerObj, "LocationName");
        int hp = GetInt(playerObj, "Hp", -1);
        int maxHp = GetInt(playerObj, "MaxHp", -1);

        lock (_playersLock)
        {
            bool changed = false;
            if (string.Equals(playerId, _selfKey, StringComparison.Ordinal))
            {
                try
                {
                    _selfPlayer.playerId = playerId;
                }
                catch
                {

                }

                if (string.IsNullOrWhiteSpace(_selfPlayer?.userName) && !string.IsNullOrWhiteSpace(playerName))
                {
                    _selfPlayer.userName = playerName;
                }

                if (stage >= 0)
                {
                    _selfPlayer.stage = stage;
                }

                if (maxHp > 0 && _selfPlayer != null)
                {
                    _selfPlayer.maxHP = maxHp;
                    if (hp >= 0)
                    {
                        _selfPlayer.HP = hp;
                    }
                }

                return;
            }

            if (!_players.TryGetValue(playerId, out INetworkPlayer existing) || existing == null)
            {
                existing = new RemoteNetworkPlayer(playerId, playerName);
                _players[playerId] = existing;
                changed = true;
            }

            try
            {
                existing.playerId = playerId;
            }
            catch
            {

            }

            if (!string.IsNullOrWhiteSpace(playerName))
            {
                existing.userName = playerName;
            }

            if (!string.IsNullOrWhiteSpace(characterId))
            {
                existing.chara = characterId;
            }

            if (!string.IsNullOrWhiteSpace(locName))
            {
                existing.location = locName;
            }

            if (locX >= 0)
            {
                existing.location_X = locX;
            }

            if (locY >= 0)
            {
                existing.location_Y = locY;
            }

            if (stage >= 0)
            {
                existing.stage = stage;
            }

            if (maxHp > 0)
            {
                if (existing.maxHP != maxHp || (hp >= 0 && existing.HP != hp))
                {
                    changed = true;
                }
                existing.maxHP = maxHp;
                if (hp >= 0)
                {
                    existing.HP = hp;
                }
            }
            else if (hp >= 0)
            {
                if (existing.HP != hp)
                {
                    changed = true;
                }
                existing.HP = hp;
            }

            if (changed)
            {
                MarkPlayersDirty_NoLock();
            }
        }
    }

        private static bool TryGetJsonElement(object payload, out JsonElement root)
    {
        try
        {

            if (payload is JsonElement je)
            {
                root = je;
                return true;
            }

            if (payload is string s)
            {
                using JsonDocument doc = JsonDocument.Parse(s);
                root = doc.RootElement.Clone();
                return true;
            }
        }
        catch
        {

        }

        root = default;
        return false;
    }

        private static string GetString(JsonElement elem, string property)
    {
        try
        {
            if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
            {
                return null;
            }

            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
        }
        catch
        {
            return null;
        }
    }

        private static int GetInt(JsonElement elem, string property, int defaultValue)
    {
        try
        {
            if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
            {
                return defaultValue;
            }

            return p.ValueKind switch
            {
                JsonValueKind.Number => p.TryGetInt32(out int i) ? i : defaultValue,
                JsonValueKind.String => int.TryParse(p.GetString(), out int i) ? i : defaultValue,
                _ => defaultValue,
            };
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool GetBool(JsonElement elem, string property, bool defaultValue = false)
    {
        try
        {
            if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(property, out JsonElement p))
            {
                return defaultValue;
            }

            return p.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(p.GetString(), out bool b) ? b : defaultValue,
                _ => defaultValue,
            };
        }
        catch
        {
            return defaultValue;
        }
    }

    private IEnumerable<INetworkPlayer> GetPlayersSnapshot()
    {
        lock (_playersLock)
        {

            if (_playersSnapshotRevision == _playersRevision)
            {
                return _playersSnapshot;
            }

            int count = _players.Count;
            if (count <= 0)
            {

                _playersSnapshot = Array.Empty<INetworkPlayer>();
            }
            else
            {
                INetworkPlayer[] arr = new INetworkPlayer[count];
                int i = 0;
                foreach (INetworkPlayer p in _players.Values)
                {
                    arr[i++] = p;
                }

                _playersSnapshot = arr;
            }

            _playersSnapshotRevision = _playersRevision;
            return _playersSnapshot;
        }
    }

        private void MarkPlayersDirty_NoLock()
    {
        unchecked
        {
            _playersRevision++;
        }
    }

    #endregion
}
