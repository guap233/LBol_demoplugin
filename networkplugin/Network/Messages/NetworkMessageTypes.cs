using System;
using System.Collections.Generic;

namespace NetworkPlugin.Network.Messages
{
        public static class NetworkMessageTypes
    {

                public const string PlayerJoined = "PlayerJoined";

                public const string PlayerLeft = "PlayerLeft";

                public const string PlayerListUpdate = "PlayerListUpdate";

                public const string Welcome = "Welcome";

                public const string Heartbeat = "Heartbeat";

                public const string HeartbeatResponse = "HeartbeatResponse";

                public const string HostChanged = "HostChanged";

                public const string GetSelf_REQUEST = "GetSelf_REQUEST";

                public const string GetSelf_RESPONSE = "GetSelf_RESPONSE";

                public const string DirectMessage = "DirectMessage";

                public const string UpdatePlayerLocation = "UpdatePlayerLocation";

                public const string Reconnect_REQUEST = "Reconnect_REQUEST";

                public const string Reconnect_RESPONSE = "Reconnect_RESPONSE";

                public const string CreateRoom = "CreateRoom";

                public const string JoinRoom = "JoinRoom";

                public const string LeaveRoom = "LeaveRoom";

                public const string RoomMessage = "RoomMessage";

                public const string GetRoomList = "GetRoomList";

                public const string RoomList = "RoomList";

                public const string RoomCreated = "RoomCreated";

                public const string RoomJoined = "RoomJoined";

                public const string KickPlayer = "KickPlayer";

                public const string Error = "Error";

                public const string OnCardPlayStart = "OnCardPlayStart";

                public const string OnCardPlayComplete = "OnCardPlayComplete";

                public const string OnCardDraw = "OnCardDraw";

                public const string OnCardDiscard = "OnCardDiscard";

                public const string OnCardExile = "OnCardExile";

                public const string OnCardUpgrade = "OnCardUpgrade";

                public const string OnCardRemove = "OnCardRemove";

                public const string OnRemoteCardUse = "OnRemoteCardUse";
                public const string OnRemoteCardResolved = "OnRemoteCardResolved";

                public const string ManaConsumeStarted = "ManaConsumeStarted";

                public const string ManaConsumeCompleted = "ManaConsumeCompleted";

                public const string ManaRegain = "ManaRegain";

                public const string TurnManaCalculated = "TurnManaCalculated";

                public const string MaxManaChange = "MaxManaChange";

                public const string OnDamageDealt = "OnDamageDealt";

                public const string OnDamageReceived = "OnDamageReceived";

                public const string OnBlockGained = "OnBlockGained";

                public const string OnShieldGained = "OnShieldGained";

                public const string OnHealingReceived = "OnHealingReceived";

                public const string OnStatusEffectApplied = "OnStatusEffectApplied";

                public const string OnStatusEffectRemoved = "OnStatusEffectRemoved";

                public const string OnMoodEffectLoopStarted = "OnMoodEffectLoopStarted";
                public const string OnMoodEffectLoopEnded = "OnMoodEffectLoopEnded";
                public const string OnMoodEffectStateSync = "OnMoodEffectStateSync";

                public const string OnPlayerStateUpdate = "OnPlayerStateUpdate";

                public const string OnEnemyAttackPlayerVisual = "OnEnemyAttackPlayerVisual";

                public const string BattlePlayerDamageReport = "BattlePlayerDamageReport";

                public const string BattlePlayerDamageBroadcast = "BattlePlayerDamageBroadcast";

                public const string BattlePlayerHealReport = "BattlePlayerHealReport";

                public const string BattlePlayerHealBroadcast = "BattlePlayerHealBroadcast";

                public const string BattlePlayerStatusEffectsDeltaReport = "BattlePlayerStatusEffectsDeltaReport";

                public const string BattlePlayerStatusEffectsDeltaBroadcast = "BattlePlayerStatusEffectsDeltaBroadcast";

                public const string BattlePlayerStatusEffectsFullReport = "BattlePlayerStatusEffectsFullReport";

                public const string BattlePlayerStatusEffectsFullBroadcast = "BattlePlayerStatusEffectsFullBroadcast";

                public const string BattlePlayerUsUsedReport = "BattlePlayerUsUsedReport";

                public const string BattlePlayerUsUsedBroadcast = "BattlePlayerUsUsedBroadcast";

                public const string BattlePlayerCardUsedReport = "BattlePlayerCardUsedReport";

                public const string BattlePlayerCardUsedBroadcast = "BattlePlayerCardUsedBroadcast";

                public const string OnTurnStart = "OnTurnStart";

                public const string OnTurnEnd = "OnTurnEnd";

                public const string EndTurnRequest = "EndTurnRequest";
                public const string EndTurnStatus = "EndTurnStatus";
                public const string EndTurnConfirm = "EndTurnConfirm";

                public const string OnBattleStart = "OnBattleStart";

                public const string OnBattleEnd = "OnBattleEnd";

                public const string BattleEnemyIntentChanged = "BattleEnemyIntentChanged";

                public const string BattleEnemyStateChanged = "BattleEnemyStateChanged";

                public const string BattleEnemySpawned = "BattleEnemySpawned";

                public const string EnemyStateUpdate = "EnemyStateUpdate";

                public const string EnemySpawned = "EnemySpawned";

                public const string OnMapNodeEnter = "OnMapNodeEnter";

                public const string OnMapNodeComplete = "OnMapNodeComplete";

                public const string OnMapNodeMarkChanged = "OnMapNodeMarkChanged";

                public const string OnMapNodeVoteCast = "OnMapNodeVoteCast";

                public const string OnMapNodeVoteResult = "OnMapNodeVoteResult";

                public const string OnEventStart = "OnEventStart";

                public const string OnEventSelection = "OnEventSelection";

                public const string OnEventResult = "OnEventResult";

                public const string OnDialogText = "OnDialogText";

                public const string OnDialogOptions = "OnDialogOptions";

                public const string OnEventVoteCast = "OnEventVoteCast";

                public const string OnEventVotingResult = "OnEventVotingResult";

                public const string OnDebutBonusRolled = "OnDebutBonusRolled";

                public const string OnBossRewardSelection = "OnBossRewardSelection";

                public const string OnShopEvent = "OnShopEvent";

                public const string OnTreasureEvent = "OnTreasureEvent";

                public const string OnTradeStartRequest = "OnTradeStartRequest";

                public const string OnTradeOfferUpdateRequest = "OnTradeOfferUpdateRequest";

                public const string OnTradeConfirmRequest = "OnTradeConfirmRequest";

                public const string OnTradeCancelRequest = "OnTradeCancelRequest";

                public const string OnTradeStateUpdate = "OnTradeStateUpdate";

                public const string OnTradeSnapshotRequest = "OnTradeSnapshotRequest";

                public const string OnTradePrepareResultRequest = "OnTradePrepareResultRequest";

                public const string OnTradeCommitResultRequest = "OnTradeCommitResultRequest";

                public const string OnTradeCompensationResultRequest = "OnTradeCompensationResultRequest";

                public const string OnPlayerDeathStatusChanged = "OnPlayerDeathStatusChanged";

                public const string OnResurrectRequest = "OnResurrectRequest";

                public const string OnResurrectFailed = "OnResurrectFailed";

                public const string OnPlayerResurrected = "OnPlayerResurrected";

                public const string OnGapHealRequest = "OnGapHealRequest";

                public const string OnGapHealFailed = "OnGapHealFailed";

                public const string OnGapPlayerHealed = "OnGapPlayerHealed";

                public const string GapStationEntered = "GapStationEntered";

                public const string DrinkTeaStarted = "DrinkTeaStarted";

                public const string DrinkTeaCompleted = "DrinkTeaCompleted";

                public const string GapOptionsUpgradeSelected = "GapOptionsUpgradeSelected";

                public const string GapOptionsRemoveCard = "GapOptionsRemoveCard";

                public const string OnExhibitObtained = "OnExhibitObtained";

                public const string OnExhibitRemoved = "OnExhibitRemoved";

                public const string OnToolCardUsed = "OnToolCardUsed";

                public const string OnShopPurchase = "OnShopPurchase";

                public const string OnConnectionEstablished = "OnConnectionEstablished";

                public const string OnConnectionLost = "OnConnectionLost";

                public const string OnReconnectionAttempt = "OnReconnectionAttempt";

                public const string StateSyncRequest = "StateSyncRequest";

                public const string StateSyncResponse = "StateSyncResponse";

                public const string FullStateSyncRequest = "FullStateSyncRequest";

                public const string FullStateSyncResponse = "FullStateSyncResponse";

                public const string RoomStateRequest = "RoomStateRequest";

                public const string RoomStateResponse = "RoomStateResponse";

                public const string RoomStateUpload = "RoomStateUpload";

                public const string RoomStateBroadcast = "RoomStateBroadcast";

                public const string OnGameStart = "OnGameStart";

                public const string OnGameEnd = "OnGameEnd";

                public const string OnGameRunResult = "OnGameRunResult";

                public const string OnGameSave = "OnGameSave";

                public const string OnGameLoad = "OnGameLoad";

                public const string OnError = "OnError";

                public const string MidGameJoinRequest = "MidGameJoinRequest";

                public const string MidGameJoinResponse = "MidGameJoinResponse";

                public const string GameStateTransfer = "GameStateTransfer";

                public const string OnShopEnter = "OnShopEnter";

                public const string OnShopExit = "OnShopExit";

                public const string ChatMessage = "ChatMessage";

                public const string PlayerReadyChanged = "PlayerReadyChanged";

        public const string OnLobbyResumeGame = "OnLobbyResumeGame";

        public const string OnLobbyResumeInfo = "OnLobbyResumeInfo";

                public const string HandSyncRequest = "HandSyncRequest";

                public const string HandSyncResponse = "HandSyncResponse";

                public const string DeckSyncRequest = "DeckSyncRequest";

                public const string DeckSyncResponse = "DeckSyncResponse";

                public const string DiscardSyncRequest = "DiscardSyncRequest";

                public const string DiscardSyncResponse = "DiscardSyncResponse";

                public const string DeckOperation = "DeckOperation";

                public const string CardStateChanged = "CardStateChanged";

                public const string ExhibitActivationChanged = "ExhibitActivationChanged";

                public const string ExhibitCounterChanged = "ExhibitCounterChanged";

                public const string OnToolCardObtained = "OnToolCardObtained";

                public const string OnToolCardRemoved = "OnToolCardRemoved";

                public const string OnToolCardEffectApplied = "OnToolCardEffectApplied";

                public const string SaveSyncRequest = "SaveSyncRequest";

                public const string SaveSyncResponse = "SaveSyncResponse";

                public const string QuickSaveSync = "QuickSaveSync";

                public const string NatInfoReport = "NatInfoReport";

                public const string NatInfoRequest = "NatInfoRequest";

                public const string NatInfoResponse = "NatInfoResponse";

                public const string NatError = "NatError";

                public enum GameEventRoute
        {
                        Client,
                        HostServer,
                        Relay,
        }

                private static readonly HashSet<string> ExplicitGameEvents = new(StringComparer.Ordinal)
        {
            // Cards & Decks
            OnCardPlayStart,
            OnCardPlayComplete,
            OnCardDraw,
            OnCardDiscard,
            OnCardExile,
            OnCardUpgrade,
            OnCardRemove,
            OnRemoteCardUse,
            OnRemoteCardResolved,
            CardStateChanged,
            DeckOperation,
            HandSyncRequest,
            HandSyncResponse,
            DeckSyncRequest,
            DeckSyncResponse,
            DiscardSyncRequest,
            DiscardSyncResponse,

            // Mana
            ManaConsumeStarted,
            ManaConsumeCompleted,
            ManaRegain,
            TurnManaCalculated,
            MaxManaChange,

            // Combat & Damage & Vitals
            OnDamageDealt,
            OnDamageReceived,
            OnBlockGained,
            OnShieldGained,
            OnHealingReceived,
            OnStatusEffectApplied,
            OnStatusEffectRemoved,
            OnMoodEffectLoopStarted,
            OnMoodEffectLoopEnded,
            OnMoodEffectStateSync,
            OnPlayerStateUpdate,
            OnEnemyAttackPlayerVisual,
            BattlePlayerDamageReport,
            BattlePlayerDamageBroadcast,
            BattlePlayerHealReport,
            BattlePlayerHealBroadcast,
            BattlePlayerStatusEffectsDeltaReport,
            BattlePlayerStatusEffectsDeltaBroadcast,
            BattlePlayerStatusEffectsFullReport,
            BattlePlayerStatusEffectsFullBroadcast,
            BattlePlayerUsUsedReport,
            BattlePlayerUsUsedBroadcast,
            BattlePlayerCardUsedReport,
            BattlePlayerCardUsedBroadcast,
            OnTurnStart,
            OnTurnEnd,
            EndTurnRequest,
            EndTurnStatus,
            EndTurnConfirm,
            "EndTurnCancel",
            OnBattleStart,
            OnBattleEnd,
            BattleEnemyIntentChanged,
            BattleEnemyStateChanged,
            BattleEnemySpawned,
            EnemyStateUpdate,
            EnemySpawned,

            // Map & Events & Voting
            OnMapNodeEnter,
            OnMapNodeComplete,
            OnMapNodeMarkChanged,
            OnMapNodeVoteCast,
            OnMapNodeVoteResult,
            OnEventStart,
            OnEventSelection,
            OnEventResult,
            OnDialogText,
            OnDialogOptions,
            OnEventVoteCast,
            OnEventVotingResult,
            OnDebutBonusRolled,
            OnBossRewardSelection,
            OnShopEvent,
            OnShopEnter,
            OnShopExit,
            OnShopPurchase,
            OnTreasureEvent,

            // Trade
            OnTradeStartRequest,
            OnTradeOfferUpdateRequest,
            OnTradeConfirmRequest,
            OnTradeCancelRequest,
            OnTradeStateUpdate,
            OnTradeSnapshotRequest,
            OnTradePrepareResultRequest,
            OnTradeCommitResultRequest,
            OnTradeCompensationResultRequest,

            // Death & Resurrect & Gap
            OnPlayerDeathStatusChanged,
            OnResurrectRequest,
            OnResurrectFailed,
            OnPlayerResurrected,
            OnGapHealRequest,
            OnGapHealFailed,
            OnGapPlayerHealed,
            GapStationEntered,
            DrinkTeaStarted,
            DrinkTeaCompleted,
            GapOptionsUpgradeSelected,
            GapOptionsRemoveCard,

            // Exhibits & Tools
            OnExhibitObtained,
            OnExhibitRemoved,
            ExhibitActivationChanged,
            ExhibitCounterChanged,
            OnToolCardUsed,
            OnToolCardObtained,
            OnToolCardRemoved,
            OnToolCardEffectApplied,

            // Connection & Game Lifecycle
            OnConnectionEstablished,
            OnConnectionLost,
            OnReconnectionAttempt,
            OnGameStart,
            OnGameEnd,
            OnGameRunResult,
            OnGameSave,
            OnGameLoad,
            OnError,
            OnLobbyResumeGame,
            OnLobbyResumeInfo,

            // Chat
            ChatMessage,
        };

                private static readonly HashSet<string> ClientOnlyGameEvents = new(StringComparer.Ordinal)
        {
            StateSyncResponse,
            FullStateSyncRequest,
            FullStateSyncResponse,
            RoomStateRequest,
            RoomStateResponse,
            RoomStateUpload,
            RoomStateBroadcast,
            MidGameJoinRequest,
            MidGameJoinResponse,
            Welcome,
            PlayerJoined,
            PlayerLeft,
            PlayerListUpdate,
            HostChanged,
        };

                private static readonly HashSet<string> HostServerOnlyGameEvents = new(StringComparer.Ordinal)
        {
            StateSyncRequest,
            FullStateSyncRequest,
            FullStateSyncResponse,
            RoomStateRequest,
            RoomStateResponse,
            RoomStateUpload,
            RoomStateBroadcast,
        };

                private static readonly HashSet<string> RelayOnlyGameEvents = new(StringComparer.Ordinal)
        {
            StateSyncRequest,
            RoomStateBroadcast,
        };

                private static readonly HashSet<string> HostRequestEvents = new(StringComparer.Ordinal)
        {
            OnTradeStartRequest,
            OnTradeOfferUpdateRequest,
            OnTradeConfirmRequest,
            OnTradeCancelRequest,
            OnTradeSnapshotRequest,
            OnTradePrepareResultRequest,
            OnTradeCommitResultRequest,
            OnTradeCompensationResultRequest,
            OnResurrectRequest,
            OnGapHealRequest,
            OnMapNodeVoteCast,
            OnEventVoteCast,
            MidGameJoinRequest,
            BattlePlayerCardUsedReport,
            BattlePlayerUsUsedReport,
            BattlePlayerDamageReport,
            BattlePlayerHealReport,
            BattlePlayerStatusEffectsDeltaReport,
            BattlePlayerStatusEffectsFullReport,
            EndTurnRequest,
            "EndTurnCancel",
        };

                public static bool IsHostRequest(string messageType)
        {
            return !string.IsNullOrWhiteSpace(messageType) && HostRequestEvents.Contains(messageType);
        }

                public static bool IsGameEvent(string messageType, GameEventRoute route)
        {
            if (string.IsNullOrWhiteSpace(messageType))
            {
                return false;
            }

            if (ExplicitGameEvents.Contains(messageType))
            {
                return true;
            }

            return route switch
            {
                GameEventRoute.Client => ClientOnlyGameEvents.Contains(messageType),
                GameEventRoute.HostServer => HostServerOnlyGameEvents.Contains(messageType),
                GameEventRoute.Relay => RelayOnlyGameEvents.Contains(messageType),
                _ => false,
            };
        }
    }

        public enum MessagePriority
    {
                Low = 0,
                Normal = 1,
                High = 2,
                Critical = 3
    }
}
