using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LBoL.Core;
using LBoL.Core.Cards;
using NetworkPlugin.Core.Trade;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace NetworkPlugin.UI.Panels;

public sealed partial class TradePanel
{
    private void TrySubscribeTradeEvents()
    {
        if (_subscribedToTrade) return;
        TradeSyncPatch.OnTradeStateUpdated += OnTradeStateUpdated;
        TradeSettlementCoordinator.EnsureInitialized();
        if (TradeSettlementCoordinator.Instance != null)
        {
            TradeSettlementCoordinator.Instance.OnSettlementStateChanged += OnSettlementStateChanged;
        }
        _subscribedToTrade = true;
    }

    private void TryUnsubscribeTradeEvents()
    {
        if (!_subscribedToTrade) return;
        TradeSyncPatch.OnTradeStateUpdated -= OnTradeStateUpdated;
        if (TradeSettlementCoordinator.Instance != null)
        {
            TradeSettlementCoordinator.Instance.OnSettlementStateChanged -= OnSettlementStateChanged;
        }
        _subscribedToTrade = false;
    }

    private void OnSettlementStateChanged(string tradeId, TradeCommitStatus status, TradeSettlementResult result)
    {
        if (!string.Equals(tradeId, _tradeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Plugin.RunOnMainThread(() =>
        {
            if (_lastTradeStatus == TradeSyncPatch.TradeStatus.Failed)
            {
                StopDelayedHide();
                var state = TradeSyncPatch.GetLastKnown(tradeId);
                if (state != null)
                {
                    UpdateUIStatus(FormatFailureReason(state, _selfPlayerId));
                }
            }
        });
    }

    private void OnTradeStateUpdated(TradeSyncPatch.TradeSessionState state)
    {
        try
        {
            if (state is null || !string.Equals(state.TradeId, _tradeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!state.IsParticipant(_selfPlayerId))
            {
                return;
            }

            ApplyStateToUi(state);

            _lastTradeStatus = state.Status;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[TradePanel] OnTradeStateUpdated failed: tradeId={state?.TradeId ?? "<null>"}, error={ex}");
        }
    }

    private void ApplyStateToUi(TradeSyncPatch.TradeSessionState state)
    {
        bool localIsA = IsPlayerA(state);

        using (new ApplyingStateScope(this))
        {
            _player1OfferedCards.Clear();
            _player2OfferedCards.Clear();
            player1Slots?.ToList().ForEach(s => s?.ClearSlot());
            player2Slots?.ToList().ForEach(s => s?.ClearSlot());
            ClearOfferPreviewPanel(_localOfferPreviewPanel);
            ClearOfferPreviewPanel(_remoteOfferPreviewPanel);

            _tradeId = state.TradeId;
            _selfPlayerId = NetworkIdentityTracker.GetSelfPlayerId();
            _playerAId = state.PlayerAId;
            _playerBId = state.PlayerBId;

            EnsureOfferEditorOverlay();
            EnsureCardPickerOverlay();
            EnsureOfferPreviewOverlay();
            EnsureExhibitPickerOverlay();
            SetTradeDetailsVisible(true);

            if (player1NameText is not null)
            {
                string selfName = localIsA ? state.PlayerAName : state.PlayerBName;
                player1NameText.text = ResolveLocalPlayerDisplayName(_payload);
                if (string.IsNullOrWhiteSpace(player1NameText.text))
                {
                    player1NameText.text = OtherPlayersOverlayPatch.ResolveDisplayName(_selfPlayerId, selfName, isLocal: true);
                }
            }
            if (player2NameText is not null)
            {
                string partnerId = localIsA ? state.PlayerBId : state.PlayerAId;
                string partnerName = localIsA ? state.PlayerBName : state.PlayerAName;
                player2NameText.text = OtherPlayersOverlayPatch.ResolveDisplayName(partnerId, partnerName, isLocal: false);
            }

            _localMoneyOffer = localIsA ? state.MoneyA : state.MoneyB;
            _localExhibitOfferIds.Clear();
            (localIsA ? state.ExhibitsA : state.ExhibitsB)?
                .Where(ex => ex is not null && !string.IsNullOrWhiteSpace(ex.ExhibitId))
                .Select(ex => ex.ExhibitId)
                .ToList()
                .ForEach(id => _localExhibitOfferIds.Add(id));

            (localIsA ? state.OfferA : state.OfferB)
                ?.Select(c => TryFindDeckCard(c))
                .Where(real => real is not null)
                .ToList()
                .ForEach(real => AddCardToTrade(real, true));

            (localIsA ? state.OfferB : state.OfferA)
                ?.Where(c => c != null && !string.IsNullOrWhiteSpace(c.CardId))
                .Select(c =>
                {
                    try { return Library.TryCreateCard(c.CardId, c.IsUpgraded, c.UpgradeCounter); }
                    catch (Exception ex) { Plugin.Logger?.LogWarning($"[TradePanel] 远端报价创建临时卡失败: CardId={c.CardId}, {ex.Message}"); return null; }
                })
                .Where(temp => temp != null)
                .ToList()
                .ForEach(temp => AddCardToTrade(temp, false));

            RebuildExhibitPreviews();

            RefreshOfferEditorTexts();
            RefreshOfferPreview();

            player2Slots?.ToList().ForEach(s => s?.SetLocked(true));
        }

        UpdateTradeStatusUi(state, localIsA);
    }

    private void UpdateTradeStatusUi(TradeSyncPatch.TradeSessionState state, bool localIsA)
    {
        if (state is null) return;

        bool localConfirmed = localIsA ? state.AConfirmed : state.BConfirmed;
        bool remoteConfirmed = localIsA ? state.BConfirmed : state.AConfirmed;
        bool hasAnyOffer = (state.OfferA?.Count ?? 0) > 0 || (state.OfferB?.Count ?? 0) > 0 || state.MoneyA > 0 || state.MoneyB > 0 || (state.ExhibitsA?.Count ?? 0) > 0 || (state.ExhibitsB?.Count ?? 0) > 0;

        // 检查版本不兼容
        if (state.ProtocolVersion != 0 && state.ProtocolVersion != TradeConstants.CurrentProtocolVersion)
        {
            UpdateUIStatus(TryLocalize("Trade.IncompatibleVersion", $"协议版本不兼容 (远程: v{state.ProtocolVersion}, 本地: v{TradeConstants.CurrentProtocolVersion})"));
            if (confirmButton?.button is not null)
            {
                confirmButton.button.interactable = false;
            }
            if (cancelButton?.button is not null)
            {
                cancelButton.button.interactable = true;
            }
            SetOfferEditingInteractable(false);
            return;
        }

        switch (state.Status)
        {
            case TradeSyncPatch.TradeStatus.Open:
                SetOfferEditingInteractable(!localConfirmed);
                if (confirmButton?.button is not null)
                {
                    confirmButton.button.interactable = !localConfirmed;
                }
                if (cancelButton?.button is not null)
                {
                    cancelButton.button.interactable = true;
                }

                if (!string.IsNullOrWhiteSpace(state.FailureCode) || !string.IsNullOrWhiteSpace(state.Reason))
                {
                    UpdateUIStatus(FormatFailureReason(state, _selfPlayerId));
                }
                else if (localConfirmed && remoteConfirmed)
                {
                    UpdateUIStatus(TryLocalize("Trade.BothConfirmed", "双方已确认，准备交换..."));
                }
                else if (localConfirmed)
                {
                    UpdateUIStatus(TryLocalize("Trade.WaitingForPartner", "已确认交易，等待对方确认..."));
                }
                else if (remoteConfirmed)
                {
                    UpdateUIStatus(TryLocalize("Trade.PartnerConfirmed", "对方已确认交易，请点击确定开始交换"));
                }
                else if (hasAnyOffer)
                {
                    UpdateUIStatus(TryLocalize("Trade.ReadyToConfirm", "可以确认交易"));
                }
                else
                {
                    UpdateUIStatus(TryLocalize("Trade.WaitingForItems", "等待放入物品..."));
                }
                break;

            case TradeSyncPatch.TradeStatus.Preparing:
                SetOfferEditingInteractable(false);
                if (confirmButton?.button is not null)
                {
                    confirmButton.button.interactable = false;
                }
                if (cancelButton?.button is not null)
                {
                    cancelButton.button.interactable = false;
                }
                UpdateUIStatus(TryLocalize("Trade.Preparing", "正在检查交易条件..."));
                break;

            case TradeSyncPatch.TradeStatus.Committing:
                SetOfferEditingInteractable(false);
                if (confirmButton?.button is not null)
                {
                    confirmButton.button.interactable = false;
                }
                if (cancelButton?.button is not null)
                {
                    cancelButton.button.interactable = false;
                }
                UpdateUIStatus(TryLocalize("Trade.Committing", "正在交换产品..."));
                break;

            case TradeSyncPatch.TradeStatus.Completed:
                SetOfferEditingInteractable(false);
                if (confirmButton?.button is not null)
                {
                    confirmButton.button.interactable = false;
                }
                if (cancelButton?.button is not null)
                {
                    cancelButton.button.interactable = false;
                }
                UpdateUIStatus(TryLocalize("Trade.Completed", "交易完成"));
                if (_lastTradeStatus != TradeSyncPatch.TradeStatus.Completed)
                {
                    StartDelayedHide(TradeCompleteWaitTime);
                }
                break;

            case TradeSyncPatch.TradeStatus.Failed:
                StopDelayedHide();
                SetOfferEditingInteractable(false);
                if (confirmButton?.button is not null)
                {
                    confirmButton.button.interactable = false;
                }
                if (cancelButton?.button is not null)
                {
                    cancelButton.button.interactable = true;
                }
                UpdateUIStatus(FormatFailureReason(state, _selfPlayerId));
                break;

            case TradeSyncPatch.TradeStatus.Canceled:
                SetOfferEditingInteractable(false);
                if (confirmButton?.button is not null)
                {
                    confirmButton.button.interactable = false;
                }
                if (cancelButton?.button is not null)
                {
                    cancelButton.button.interactable = false;
                }
                UpdateUIStatus(TryLocalize("Trade.Canceled", "交易已取消"));
                if (_lastTradeStatus != TradeSyncPatch.TradeStatus.Canceled)
                {
                    StartDelayedHide(1f);
                }
                break;
        }
    }

    private void SetOfferEditingInteractable(bool interactable)
    {
        if (_offerEditorRoot is not null)
        {
            var buttons = _offerEditorRoot.GetComponentsInChildren<Button>(true);
            foreach (var b in buttons)
            {
                b.interactable = interactable;
            }
        }

        if (_offerActionsRoot is not null)
        {
            var buttons = _offerActionsRoot.GetComponentsInChildren<Button>(true);
            foreach (var b in buttons)
            {
                b.interactable = interactable;
            }
        }
    }

    internal static Func<string, string> ExhibitDisplayNameResolver { get; set; }

    internal static string ResolveExhibitDisplayName(string exhibitId)
    {
        if (string.IsNullOrWhiteSpace(exhibitId))
        {
            return string.Empty;
        }

        if (ExhibitDisplayNameResolver != null)
        {
            try
            {
                string custom = ExhibitDisplayNameResolver(exhibitId);
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    return custom;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[TradePanel] ExhibitDisplayNameResolver exception for {exhibitId}: {ex.Message}");
            }
        }

        try
        {
            var run = GameStateUtils.GetCurrentGameRun();
            var owned = run?.Player?.Exhibits?.FirstOrDefault(e => e != null && string.Equals(e.Id, exhibitId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(owned?.Name))
            {
                return owned.Name;
            }

            var ex = Library.TryCreateExhibit(exhibitId);
            if (!string.IsNullOrWhiteSpace(ex?.Name))
            {
                return ex.Name;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[TradePanel] ResolveExhibitDisplayName exception for {exhibitId}: {ex.Message}");
        }

        return exhibitId;
    }

    internal static string FormatDuplicateExhibitReason(TradeSyncPatch.TradeSessionState state, string localPlayerId = null)
    {
        string conflictExhibitId = state?.ConflictExhibitId;
        if (string.IsNullOrWhiteSpace(conflictExhibitId) && !string.IsNullOrWhiteSpace(state?.Reason))
        {
            int colonIdx = state.Reason.LastIndexOf(':');
            if (colonIdx >= 0 && colonIdx < state.Reason.Length - 1)
            {
                string candidate = state.Reason.Substring(colonIdx + 1).Trim();
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !string.Equals(candidate, TradeFailureCodes.DuplicateExhibit, StringComparison.OrdinalIgnoreCase))
                {
                    conflictExhibitId = candidate;
                }
            }
        }

        string selfId = localPlayerId ?? NetworkIdentityTracker.GetSelfPlayerId();
        bool? isReceiver = null;

        if (string.IsNullOrWhiteSpace(conflictExhibitId) && state != null)
        {
            try
            {
                var run = GameStateUtils.GetCurrentGameRun();
                var localExhibits = run?.Player?.Exhibits;
                if (localExhibits != null && localExhibits.Count > 0)
                {
                    bool localIsA = !string.IsNullOrWhiteSpace(selfId) && string.Equals(selfId, state.PlayerAId, StringComparison.OrdinalIgnoreCase);
                    var partnerExhibits = localIsA ? state.ExhibitsB : state.ExhibitsA;
                    var conflict = partnerExhibits?.FirstOrDefault(e => e != null && localExhibits.Any(owned => owned != null && string.Equals(owned.Id, e.ExhibitId, StringComparison.Ordinal)));
                    if (conflict != null)
                    {
                        conflictExhibitId = conflict.ExhibitId;
                        isReceiver = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"[TradePanel] 推断冲突展品异常: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(conflictExhibitId))
        {
            return TryLocalize("Trade.DuplicateExhibitFallback", "接收方已拥有报价中的同类展品");
        }

        bool isParticipant = state != null && !string.IsNullOrWhiteSpace(selfId) &&
            (string.Equals(selfId, state.PlayerAId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(selfId, state.PlayerBId, StringComparison.OrdinalIgnoreCase));

        if (isReceiver == null && isParticipant)
        {
            if (!string.IsNullOrWhiteSpace(state.ReceiverPlayerId))
            {
                isReceiver = string.Equals(selfId, state.ReceiverPlayerId, StringComparison.OrdinalIgnoreCase);
            }
            else if (!string.IsNullOrWhiteSpace(state.FailurePlayerId))
            {
                isReceiver = string.Equals(selfId, state.FailurePlayerId, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                bool aOffers = state.ExhibitsA?.Any(e => string.Equals(e?.ExhibitId, conflictExhibitId, StringComparison.OrdinalIgnoreCase)) == true;
                bool bOffers = state.ExhibitsB?.Any(e => string.Equals(e?.ExhibitId, conflictExhibitId, StringComparison.OrdinalIgnoreCase)) == true;
                if (aOffers && !bOffers)
                {
                    isReceiver = string.Equals(selfId, state.PlayerBId, StringComparison.OrdinalIgnoreCase);
                }
                else if (bOffers && !aOffers)
                {
                    isReceiver = string.Equals(selfId, state.PlayerAId, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        string exhibitName = ResolveExhibitDisplayName(conflictExhibitId);
        if (string.IsNullOrWhiteSpace(exhibitName))
        {
            exhibitName = conflictExhibitId;
        }

        if (isReceiver == true)
        {
            string template = TryLocalize("Trade.DuplicateExhibitReceiver", "你已拥有【{0}】，请让对方移除此展品后重新确认");
            return string.Format(template, exhibitName);
        }
        else if (isReceiver == false)
        {
            string template = TryLocalize("Trade.DuplicateExhibitSender", "对方已拥有【{0}】，请移除此展品后重新确认");
            return string.Format(template, exhibitName);
        }
        else
        {
            return TryLocalize("Trade.DuplicateExhibitFallback", "接收方已拥有报价中的同类展品");
        }
    }

    internal static string FormatFailureReason(TradeSyncPatch.TradeSessionState state, string localPlayerId = null)
    {
        if (state is null)
        {
            return TryLocalize("Trade.Failed", "交易失败");
        }

        if (MatchesCode(state, TradeFailureCodes.DuplicateExhibit))
        {
            return FormatDuplicateExhibitReason(state, localPlayerId);
        }

        bool isLocalCompensationFailed = false;
        TradeCommitStatus localCommitStatus = TradeCommitStatus.None;
        TradeSettlementResult localResult = null;
        if (TradeSettlementCoordinator.Instance != null &&
            TradeSettlementCoordinator.Instance.TryGetSessionStatus(state.TradeId, out localCommitStatus, out localResult))
        {
            if (localCommitStatus == TradeCommitStatus.Failed &&
                string.Equals(localResult?.FailureCode, TradeFailureCodes.CompensationFailed, StringComparison.OrdinalIgnoreCase))
            {
                isLocalCompensationFailed = true;
            }
        }

        if (isLocalCompensationFailed || MatchesCode(state, TradeFailureCodes.CompensationFailed))
        {
            return TryLocalize("Trade.CompensationFailed", "致命错误：交易回滚失败，资产可能不一致，请手动对账！");
        }

        if (MatchesCode(state, TradeFailureCodes.ProtocolIncompatible))
        {
            return TryLocalize("Trade.IncompatibleVersion", $"交易失败：版本不兼容（对方或协议版本={state.ProtocolVersion}）");
        }

        if (MatchesCode(state, TradeFailureCodes.CommitTimeout))
        {
            if (localCommitStatus == TradeCommitStatus.Compensated)
            {
                return TryLocalize("Trade.CommitTimeoutRolledBack", "交易失败：提交超时，本地资产已安全回滚");
            }
            if (localCommitStatus == TradeCommitStatus.Compensating)
            {
                return TryLocalize("Trade.CommitTimeoutCompensating", "交易失败：提交超时，正在回滚本地资产...");
            }
            return TryLocalize("Trade.CommitTimeout", "交易失败：提交超时，交易已终止");
        }

        if (MatchesCode(state, TradeFailureCodes.CommitAborted))
        {
            return TryLocalize("Trade.CommitAborted", "交易失败：提交已中止");
        }

        if (MatchesCode(state, TradeFailureCodes.MissingCard))
        {
            return TryLocalize("Trade.MissingCard", "交易失败：牌库缺少报价卡牌");
        }

        if (MatchesCode(state, TradeFailureCodes.InsufficientMoney))
        {
            return TryLocalize("Trade.InsufficientMoney", "交易失败：金币不足");
        }

        if (MatchesCode(state, TradeFailureCodes.MissingExhibit))
        {
            return TryLocalize("Trade.MissingExhibit", "交易失败：缺少报价展品");
        }

        if (MatchesCode(state, TradeFailureCodes.ExhibitNotTradable))
        {
            return TryLocalize("Trade.ExhibitNotTradable", "交易失败：展品不可交易");
        }

        if (MatchesCode(state, TradeFailureCodes.EmptyTrade))
        {
            return TryLocalize("Trade.EmptyTrade", "交易失败：报价为空");
        }

        if (!string.IsNullOrWhiteSpace(state.FailureCode))
        {
            return !string.IsNullOrWhiteSpace(state.Reason)
                ? $"交易失败: {state.FailureCode} ({state.Reason})"
                : $"交易失败: {state.FailureCode}";
        }

        if (!string.IsNullOrWhiteSpace(state.Reason))
        {
            return $"交易失败: {state.Reason}";
        }

        return TryLocalize("Trade.Failed", "交易失败");
    }

    private static bool MatchesCode(TradeSyncPatch.TradeSessionState state, string code)
    {
        if (state == null || string.IsNullOrWhiteSpace(code)) return false;
        if (string.Equals(state.FailureCode, code, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.Reason, code, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(state.FailureCode) &&
            state.FailureCode.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(state.Reason) &&
            state.Reason.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private Coroutine _delayedHideCoroutine;

    private void StartDelayedHide(float delay)
    {
        StopDelayedHide();
        _delayedHideCoroutine = StartCoroutine(DelayedHidePanel(delay));
    }

    private void StopDelayedHide()
    {
        if (_delayedHideCoroutine != null)
        {
            StopCoroutine(_delayedHideCoroutine);
            _delayedHideCoroutine = null;
        }
    }

    private IEnumerator DelayedHidePanel(float delay)
    {
        yield return new WaitForSeconds(delay);
        _delayedHideCoroutine = null;
        Hide();
    }

    private Card TryFindDeckCard(TradeSyncPatch.CardRef cardRef)
    {
        try
        {
            if (cardRef is null || cardRef.InstanceId < 0)
            {
                return null;
            }

            return ActiveGameRun?.GetDeckCardByInstanceId(cardRef.InstanceId);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[TradePanel] TryFindDeckCard exception: {ex.Message}");
            return null;
        }
    }

    private void TrySendOfferUpdate()
    {
        if (!TryIsNetworkTrade(out _))
        {
            return;
        }

        TradeSyncPatch.TradeSessionState state = TradeSyncPatch.GetLastKnown(_tradeId);
        if (state is not null && state.Status != TradeSyncPatch.TradeStatus.Open)
        {
            Plugin.Logger?.LogWarning($"[TradePanel] TrySendOfferUpdate ignored: status={state.Status}, tradeId={_tradeId}");
            return;
        }

        List<Card> offered = _player1OfferedCards;
        List<TradeSyncPatch.CardRef> refs = offered
            .Where(c => c is not null)
            .Select(c => new TradeSyncPatch.CardRef
            {
                CardId = c.Id,
                InstanceId = c.InstanceId,
                IsUpgraded = c.IsUpgraded,
                UpgradeCounter = c.UpgradeCounter ?? 0,
                DeckCounter = c.DeckCounter,
                CardName = c.Name,
                CardType = c.CardType.ToString(),
            })
            .ToList();

        TradeSyncPatch.RequestOfferUpdate(_tradeId, _selfPlayerId, refs, _localMoneyOffer, _localExhibitOfferIds.ToList());
    }
}
