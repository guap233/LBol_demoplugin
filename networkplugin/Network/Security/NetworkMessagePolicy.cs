using System;
using System.Collections.Generic;
using NetworkPlugin.Network.Messages;

namespace NetworkPlugin.Network.Security
{
        public enum MessageSourcePermission
    {
                ServerInternal,

                HostOnly,

                MemberRequest,

                AnyAuthenticated,
    }

        public static class NetworkMessagePolicy
    {
        private static readonly Dictionary<string, MessageSourcePermission> MessagePermissions = new(StringComparer.Ordinal)
        {
            // === ServerInternal ===
            { NetworkMessageTypes.Welcome, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.PlayerJoined, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.PlayerLeft, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.PlayerListUpdate, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.HostChanged, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.GetSelf_RESPONSE, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.Reconnect_RESPONSE, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.RoomList, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.RoomCreated, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.RoomJoined, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.NatInfoResponse, MessageSourcePermission.ServerInternal },
            { NetworkMessageTypes.NatError, MessageSourcePermission.ServerInternal },

            // === HostOnly ===
            { NetworkMessageTypes.OnGameStart, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnGameEnd, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnGameRunResult, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnGameSave, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnGameLoad, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnBattleStart, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnBattleEnd, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnTurnStart, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnTurnEnd, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattleEnemyIntentChanged, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattleEnemyStateChanged, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattleEnemySpawned, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.EnemyStateUpdate, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.EnemySpawned, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnEnemyAttackPlayerVisual, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattlePlayerDamageBroadcast, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattlePlayerHealBroadcast, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattlePlayerStatusEffectsDeltaBroadcast, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattlePlayerStatusEffectsFullBroadcast, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattlePlayerUsUsedBroadcast, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.BattlePlayerCardUsedBroadcast, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.EndTurnStatus, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.EndTurnConfirm, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnTradeStateUpdate, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnResurrectFailed, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnPlayerResurrected, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnGapHealFailed, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnGapPlayerHealed, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnMapNodeVoteResult, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnEventVotingResult, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnDebutBonusRolled, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.RoomStateResponse, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.FullStateSyncResponse, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.MidGameJoinResponse, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.GameStateTransfer, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.StateSyncResponse, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.SaveSyncResponse, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.QuickSaveSync, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnRemoteCardResolved, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnLobbyResumeGame, MessageSourcePermission.HostOnly },
            { NetworkMessageTypes.OnLobbyResumeInfo, MessageSourcePermission.HostOnly },

            // === MemberRequest ===
            { NetworkMessageTypes.OnTradeStartRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnTradeOfferUpdateRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnTradeConfirmRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnTradeCancelRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnTradeSnapshotRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnTradePrepareResultRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnTradeCommitResultRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnTradeCompensationResultRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnResurrectRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnGapHealRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.EndTurnRequest, MessageSourcePermission.MemberRequest },
            { "EndTurnCancel", MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.BattlePlayerDamageReport, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.BattlePlayerHealReport, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.BattlePlayerStatusEffectsDeltaReport, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.BattlePlayerStatusEffectsFullReport, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.BattlePlayerUsUsedReport, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.BattlePlayerCardUsedReport, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnMapNodeVoteCast, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.OnEventVoteCast, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.MidGameJoinRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.StateSyncRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.FullStateSyncRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.RoomStateRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.RoomStateUpload, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.SaveSyncRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.HandSyncRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.DeckSyncRequest, MessageSourcePermission.MemberRequest },
            { NetworkMessageTypes.DiscardSyncRequest, MessageSourcePermission.MemberRequest },

            // === AnyAuthenticated ===
            { NetworkMessageTypes.Heartbeat, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.HeartbeatResponse, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.GetSelf_REQUEST, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.Reconnect_REQUEST, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.DirectMessage, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.UpdatePlayerLocation, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.PlayerReadyChanged, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.ChatMessage, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.CreateRoom, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.JoinRoom, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.LeaveRoom, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.RoomMessage, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.GetRoomList, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.KickPlayer, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.Error, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnError, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnCardPlayStart, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnCardPlayComplete, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnCardDraw, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnCardDiscard, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnCardExile, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnCardUpgrade, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnCardRemove, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnRemoteCardUse, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.CardStateChanged, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.DeckOperation, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.HandSyncResponse, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.DeckSyncResponse, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.DiscardSyncResponse, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.ManaConsumeStarted, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.ManaConsumeCompleted, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.ManaRegain, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.TurnManaCalculated, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.MaxManaChange, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnDamageDealt, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnDamageReceived, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnBlockGained, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnShieldGained, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnHealingReceived, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnStatusEffectApplied, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnStatusEffectRemoved, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnMoodEffectLoopStarted, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnMoodEffectLoopEnded, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnMoodEffectStateSync, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnPlayerStateUpdate, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnPlayerDeathStatusChanged, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnMapNodeEnter, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnMapNodeComplete, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnMapNodeMarkChanged, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnEventStart, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnEventSelection, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnEventResult, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnDialogText, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnDialogOptions, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnBossRewardSelection, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnShopEvent, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnShopEnter, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnShopExit, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnShopPurchase, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnTreasureEvent, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.GapStationEntered, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.DrinkTeaStarted, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.DrinkTeaCompleted, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.GapOptionsUpgradeSelected, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.GapOptionsRemoveCard, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnExhibitObtained, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnExhibitRemoved, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.ExhibitActivationChanged, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.ExhibitCounterChanged, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnToolCardUsed, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnToolCardObtained, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnToolCardRemoved, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnToolCardEffectApplied, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnConnectionEstablished, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnConnectionLost, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.OnReconnectionAttempt, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.RoomStateBroadcast, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.NatInfoReport, MessageSourcePermission.AnyAuthenticated },
            { NetworkMessageTypes.NatInfoRequest, MessageSourcePermission.AnyAuthenticated },
        };

                public static bool IsRegistered(string messageType)
        {
            return !string.IsNullOrWhiteSpace(messageType) && MessagePermissions.ContainsKey(messageType);
        }

                public static bool TryGetPermission(string messageType, out MessageSourcePermission permission)
        {
            if (string.IsNullOrWhiteSpace(messageType))
            {
                permission = default;
                return false;
            }

            return MessagePermissions.TryGetValue(messageType, out permission);
        }

                public static bool IsMessageAllowed(string messageType, in AuthenticatedSender sender, out string reason)
        {
            if (string.IsNullOrWhiteSpace(messageType))
            {
                reason = "消息类型为空";
                return false;
            }

            if (!MessagePermissions.TryGetValue(messageType, out var permission))
            {
                reason = $"未在白名单中注册的消息类型: {messageType}";
                return false;
            }

            switch (permission)
            {
                case MessageSourcePermission.ServerInternal:
                    reason = $"客户端禁止直接发送服务端内部控制消息: {messageType}";
                    return false;

                case MessageSourcePermission.HostOnly:
                    if (!sender.IsHost)
                    {
                        reason = $"非房主客户端无权发送 Host 权威消息: {messageType}";
                        return false;
                    }
                    break;

                case MessageSourcePermission.MemberRequest:
                case MessageSourcePermission.AnyAuthenticated:
                    break;
            }

            reason = string.Empty;
            return true;
        }

        private static readonly HashSet<string> TradeMessageTypes = new(StringComparer.Ordinal)
        {
            NetworkMessageTypes.OnTradeStartRequest,
            NetworkMessageTypes.OnTradeOfferUpdateRequest,
            NetworkMessageTypes.OnTradeConfirmRequest,
            NetworkMessageTypes.OnTradeCancelRequest,
            NetworkMessageTypes.OnTradeSnapshotRequest,
            NetworkMessageTypes.OnTradePrepareResultRequest,
            NetworkMessageTypes.OnTradeCommitResultRequest,
            NetworkMessageTypes.OnTradeCompensationResultRequest,
            NetworkMessageTypes.OnTradeStateUpdate,
        };

        public static bool IsTradeMessage(string messageType)
        {
            return !string.IsNullOrWhiteSpace(messageType) && TradeMessageTypes.Contains(messageType);
        }

        public static void GetTimestampContract(string messageType, out TimestampFormat format, out bool requireTimestamp)
        {
            if (IsTradeMessage(messageType))
            {
                format = TimestampFormat.UtcTicks;
                requireTimestamp = true;
            }
            else
            {
                format = TimestampFormat.UnixSeconds;
                requireTimestamp = false;
            }
        }
    }
}

