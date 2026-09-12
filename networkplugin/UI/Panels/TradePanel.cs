using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LBoL.Core;
using LBoL.Core.Cards;
using LBoL.Presentation;
using LBoL.Presentation.I10N;
using LBoL.Presentation.UI;
using LBoL.Presentation.UI.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using LBoL.Presentation.UI.ExtraWidgets;
using LBoL.Presentation.UI.Panels;
using LBoL.Presentation.UI.Widgets;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.UI.Factories;
using NetworkPlugin.UI.Payloads;
using NetworkPlugin.UI.Rules;
using NetworkPlugin.UI.Widgets;
using NetworkPlugin.Utils;
using NetworkPlugin.Core.Trade;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NetworkPlugin.UI.Panels;

public partial class TradePanel : UiPanel<TradePayload>, IInputActionHandler
{
    #region 常量

        private const int DefaultMaxTradeSlots = 3;

        private const float TradeCompleteWaitTime = 2f;

    private const int MaxMoneyOffer = 99999;

    private static readonly FieldInfo LocalizationTableField =
        typeof(Localization).GetField("LocalizationTable", BindingFlags.NonPublic | BindingFlags.Static);

    private GameRunController ActiveGameRun
        => GameRun ?? GameStateUtils.GetCurrentGameRun();

    private bool IsPlayerA(TradeSyncPatch.TradeSessionState state)
    {
        if (state == null) return false;
        string self = _selfPlayerId ?? NetworkIdentityTracker.GetSelfPlayerId();
        return string.Equals(state.PlayerAId, self, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region 序列化字段（UI 组件）

    [SerializeField]
    private RectTransform player1TradeArea;

    [SerializeField]
    private RectTransform player2TradeArea;

    [SerializeField]
    private TradeSlotWidget[] player1Slots;

    [SerializeField]
    private TradeSlotWidget[] player2Slots;

    [SerializeField]
    private CommonButtonWidget confirmButton;

    [SerializeField]
    private CommonButtonWidget cancelButton;

    [SerializeField]
    private TextMeshProUGUI statusText;

    [SerializeField]
    private TextMeshProUGUI player1NameText;

    [SerializeField]
    private TextMeshProUGUI player2NameText;

    #endregion

    #region 内部状态字段

    private readonly List<Card> _player1OfferedCards = new List<Card>();
    private readonly List<Card> _player2OfferedCards = new List<Card>();

    private string _tradeId;
    private string _selfPlayerId;
    private string _playerAId;
    private string _playerBId;
    private bool _isApplyingState;
    private bool _subscribedToTrade;

    private TradePayload _payload;
    private int _maxTradeSlots = DefaultMaxTradeSlots;
    private CanvasGroup _canvasGroup;
    private bool _canCancel = true;

    private int _localMoneyOffer;
    private readonly HashSet<string> _localExhibitOfferIds = new HashSet<string>(StringComparer.Ordinal);

    private GameObject _localExhibitContainer;
    private GameObject _remoteExhibitContainer;
    private ExhibitWidget _exhibitIconTemplate;

    private TradeSyncPatch.TradeStatus? _lastTradeStatus;
    private long _lastPreparingHandledTimestamp;

    private GameObject _partnerPickerRoot;
    private bool _partnerPickerActive;
    private float _partnerPickerInputReadyTime;
    private string _partnerPickerBuildError;
    private Button _partnerPickerCancelButton;

    private Coroutine _partnerPickerAutoRefreshCo;

    private bool _actionHandlerPushed;

    private bool _blockingCenterMessageActive;

    private bool _localDebugTradeMode;

    private GameObject _offerEditorRoot;
    private GameObject _offerActionsRoot;
    private GameObject _moneyTripletRoot;
    private float _moneyValueBaseFontSize;
    private TextMeshProUGUI _ownedMoneyText;
    private TextMeshProUGUI _moneyValueText;
    private TextMeshProUGUI _exhibitValueText;

    private GameObject _cardPickerRoot;
    private bool _cardPickerApplyingSelection;
    private bool _cardSelectionCompleted;
    private TextMeshProUGUI _cardCountText;

    private GameObject _offerPreviewRoot;
    private OfferPreviewPanelTag _localOfferPreviewPanel;
    private OfferPreviewPanelTag _remoteOfferPreviewPanel;
    private CardWidget _offerPreviewCardTemplate;
    private RectTransform _runtimeContentRoot;

    internal void BindRuntimeUi(
        RectTransform runtimeContentRoot,
        CommonButtonWidget runtimeConfirmButton,
        CommonButtonWidget runtimeCancelButton,
        TextMeshProUGUI runtimeStatusText,
        TextMeshProUGUI runtimePlayer1NameText,
        TextMeshProUGUI runtimePlayer2NameText)
    {

        _runtimeContentRoot = runtimeContentRoot;
        confirmButton = runtimeConfirmButton;
        cancelButton = runtimeCancelButton;
        statusText = runtimeStatusText;
        player1NameText = runtimePlayer1NameText;
        player2NameText = runtimePlayer2NameText;

        if (confirmButton?.button is not null)
        {
            confirmButton.button.onClick.RemoveAllListeners();
            confirmButton.button.onClick.AddListener(OnConfirmTrade);

            try
            {
                Traverse traverse = HarmonyLib.Traverse.Create(confirmButton);
                traverse.Field("buttonBehavior").SetValue(0);
                traverse.Field("buttonWeight").SetValue(0);
            }
            catch
            {

            }
        }

        cancelButton?.button?.onClick.RemoveAllListeners();
        cancelButton?.button?.onClick.AddListener(OnCancelTrade);
    }

    private Transform GetTradePanelContentParent()
        => _runtimeContentRoot is not null ? _runtimeContentRoot.transform : transform;

    #endregion

    #region UiPanel 属性

    public override PanelLayer Layer => PanelLayer.Top;

    #endregion

    #region Unity 生命周期

    public void Awake()
    {

        _canvasGroup = GetComponent<CanvasGroup>();
        _canvasGroup ??= gameObject.AddComponent<CanvasGroup>();

        confirmButton?.button?.onClick.AddListener(OnConfirmTrade);
        cancelButton?.button?.onClick.AddListener(OnCancelTrade);
    }

    #endregion

    #region 多语言

    public override void OnLocaleChanged()
    {

        if (_payload is not null)
        {
            UpdateUIStrings();
        }
    }

    #endregion

    #region 面板生命周期

    protected override void OnShowing(TradePayload payload)
    {
        Plugin.Logger?.LogInfo($"[TradePanel] OnShowing enter: payloadNull={(payload is null)}, activeGameRun={(ActiveGameRun is not null)}");
        EnsurePopupTopmost();

        bool networkConnected = TryEnsureNetworkConnected();
        _localDebugTradeMode = !networkConnected && IsLocalDebugTradeAllowed();
        if (!networkConnected && !_localDebugTradeMode)
        {
            Plugin.Logger?.LogWarning("[TradePanel] OnShowing aborted: network unavailable.");
            Hide();
            return;
        }

        UiManager.PushActionHandler(this);
        _actionHandlerPushed = true;

        _canvasGroup.interactable = true;
        _canvasGroup.blocksRaycasts = true;

        _payload = payload;

        _maxTradeSlots = payload?.MaxTradeSlots ?? DefaultMaxTradeSlots;

        _canCancel = payload?.CanCancel ?? true;

        ResetTradeData();

        SetupTradeSession(payload);

        if (_partnerPickerActive || _blockingCenterMessageActive)
        {
            return;
        }

        EnsureOfferEditorOverlay();
        EnsureCardPickerOverlay();
        EnsureOfferPreviewOverlay();

        SetTradeDetailsVisible(true);

        if (player1NameText is not null) player1NameText.text = ResolveLocalPlayerDisplayName(payload);
        if (player2NameText is not null) player2NameText.text = ResolvePartnerDisplayName(payload);

        cancelButton?.gameObject.SetActive(_canCancel);

        UpdateUIStrings();

        RefreshOfferEditorTexts();
        RefreshOfferPreview();
    }

    protected override void OnShown()
    {

        EnsurePopupTopmost();
    }

    protected override void OnHiding()
    {

        _canvasGroup.interactable = false;

        if (_actionHandlerPushed)
        {
            try
            {
                UiManager.PopActionHandler(this);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"[TradePanel] PopActionHandler 异常: {ex.Message}");
            }
            finally
            {
                _actionHandlerPushed = false;
            }
        }

        TryUnsubscribeTradeEvents();
    }

    protected override void OnHided()
    {

        ResetTradeData();
        _payload = null;
    }

    #endregion

    #region UI 文本与状态

    private void UpdateUIStrings()
    {

        UpdateUIStatus(TryLocalize("Trade.WaitingForItems", "等待放入物品..."));

        SetButtonText(confirmButton, "确认交易");
        SetButtonText(cancelButton, "取消");
    }

    private static string TryLocalize(string key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }

        if (TryGetLocalizedStringQuiet(key, out string localized))
        {
            return localized;
        }

        return fallback;
    }

    private static bool TryGetLocalizedStringQuiet(string key, out string localized)
    {
        localized = null;

        try
        {
            if (LocalizationTableField?.GetValue(null) is not IDictionary table)
            {
                return false;
            }

            if (!table.Contains(key))
            {
                return false;
            }

            if (table[key] is not string raw || string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            try
            {
                localized = StringDecorator.Decorate(raw);
            }
            catch
            {
                localized = raw;
            }

            return !string.IsNullOrWhiteSpace(localized);
        }
        catch
        {
            return false;
        }
    }

    private void ResetTradeData()
    {

        _player1OfferedCards.Clear();
        _player2OfferedCards.Clear();

        _tradeId = null;
        _playerAId = null;
        _playerBId = null;
        _selfPlayerId = null;
        _isApplyingState = false;

        _localDebugTradeMode = false;

        _localMoneyOffer = 0;
        _localExhibitOfferIds.Clear();
        _lastTradeStatus = null;
        _lastPreparingHandledTimestamp = 0;

        if (player1NameText is not null) player1NameText.text = string.Empty;
        if (player2NameText is not null) player2NameText.text = string.Empty;

        _partnerPickerActive = false;
        _blockingCenterMessageActive = false;
        _partnerPickerRoot?.SetActive(false);

        if (_partnerPickerAutoRefreshCo is not null)
        {
            StopCoroutine(_partnerPickerAutoRefreshCo);
            _partnerPickerAutoRefreshCo = null;
        }
        _cardPickerRoot?.SetActive(false);
        _cardSelectionCompleted = false;
        _exhibitPickerRoot?.SetActive(false);
        _offerEditorRoot?.SetActive(false);
        _offerActionsRoot?.SetActive(false);
        _offerPreviewRoot?.SetActive(false);

        player1Slots?.ToList().ForEach(s => s?.ClearSlot());

        player2Slots?.ToList().ForEach(s => s?.ClearSlot());

        ClearOfferPreviewPanel(_localOfferPreviewPanel);
        ClearOfferPreviewPanel(_remoteOfferPreviewPanel);

        if (confirmButton?.button is not null)
        {
            confirmButton.button.interactable = false;
        }

        RefreshOfferEditorTexts();
    }

    private void UpdateUIStatus(string message)
    {
        if (statusText is not null) statusText.text = message;
    }

    #endregion

    #region 交易卡牌操作

        public void AddCardToTrade(Card card, bool isPlayer1)
    {
        if (card is null)
        {
            return;
        }

        if (!_isApplyingState && TryIsNetworkTrade(out _) && !isPlayer1)
        {
            return;
        }

        List<Card> offeredCards = isPlayer1 ? _player1OfferedCards : _player2OfferedCards;

        if (offeredCards.Count < _maxTradeSlots)
        {
            offeredCards.Add(card);
            UpdateTradeSlot(card, isPlayer1 ? player1Slots : player2Slots, offeredCards.Count - 1);
            RefreshOfferPreview();
            CheckTradeReady();

            if (!_isApplyingState)
            {
                TrySendOfferUpdate();
            }
        }
    }

        public void RemoveCardFromTrade(Card card, bool isPlayer1)
    {
        if (card is null)
        {
            return;
        }

        if (!_isApplyingState && TryIsNetworkTrade(out _) && !isPlayer1)
        {
            return;
        }

        List<Card> offeredCards = isPlayer1 ? _player1OfferedCards : _player2OfferedCards;
        TradeSlotWidget[] slots = isPlayer1 ? player1Slots : player2Slots;

        int index = offeredCards.IndexOf(card);
        if (index >= 0)
        {
            offeredCards.RemoveAt(index);

            for (int i = index; i < offeredCards.Count; i++)
            {
                UpdateTradeSlot(offeredCards[i], slots, i);
            }

            if (slots is not null && offeredCards.Count < slots.Length)
            {
                slots[offeredCards.Count]?.ClearSlot();
            }

            RefreshOfferPreview();
            CheckTradeReady();

            if (!_isApplyingState)
            {
                TrySendOfferUpdate();
            }
        }
    }

    private void UpdateTradeSlot(Card card, TradeSlotWidget[] slots, int index)
    {
        if (slots is not null && index >= 0 && index < slots.Length && slots[index] is not null)
        {
            bool isPlayer1 = slots == player1Slots;
            slots[index].SetCard(card, (c) => RemoveCardFromTrade(c, isPlayer1));
        }
    }

    private void CheckTradeReady()
    {

        bool localHasOffer = (_player1OfferedCards?.Count ?? 0) > 0 || _localMoneyOffer > 0 || _localExhibitOfferIds.Count > 0;
        bool remoteHasOffer = (_player2OfferedCards?.Count ?? 0) > 0;
        bool hasAnyOffer = localHasOffer || remoteHasOffer;

        if (confirmButton?.button is not null)
        {
            confirmButton.button.interactable = true;
        }

        var state = TradeSyncPatch.GetLastKnown(_tradeId);
        if (state != null && state.Status == TradeSyncPatch.TradeStatus.Open &&
            (!string.IsNullOrWhiteSpace(state.FailureCode) || !string.IsNullOrWhiteSpace(state.Reason)))
        {
            if (IsDuplicateExhibitResolvedLocally(state))
            {
                // 本地已主动移除冲突展品，解除陈旧错误显示
            }
            else
            {
                UpdateUIStatus(FormatFailureReason(state, _selfPlayerId));
                return;
            }
        }

        if (hasAnyOffer)
        {
            UpdateUIStatus(TryLocalize("Trade.ReadyToConfirm", "可以确认交易"));
        }
        else
        {
            UpdateUIStatus(TryLocalize("Trade.WaitingForItems", "等待放入物品..."));
        }
    }

    private bool IsDuplicateExhibitResolvedLocally(TradeSyncPatch.TradeSessionState state)
        => IsDuplicateExhibitResolved(state, _selfPlayerId, _localExhibitOfferIds);

    internal static bool IsDuplicateExhibitResolved(
        TradeSyncPatch.TradeSessionState state,
        string localPlayerId,
        ICollection<string> currentLocalExhibitOfferIds)
    {
        if (state == null || !MatchesCode(state, TradeFailureCodes.DuplicateExhibit))
        {
            return false;
        }

        string conflictExhibitId = state.ConflictExhibitId;
        if (string.IsNullOrWhiteSpace(conflictExhibitId) && !string.IsNullOrWhiteSpace(state.Reason))
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

        if (string.IsNullOrWhiteSpace(conflictExhibitId))
        {
            return false;
        }

        string selfId = localPlayerId ?? NetworkIdentityTracker.GetSelfPlayerId();
        if (string.IsNullOrWhiteSpace(selfId))
        {
            return false;
        }

        bool localIsA = string.Equals(selfId, state.PlayerAId, StringComparison.OrdinalIgnoreCase);
        bool localOfferedPreviously = (localIsA ? state.ExhibitsA : state.ExhibitsB)?
            .Any(e => string.Equals(e?.ExhibitId, conflictExhibitId, StringComparison.OrdinalIgnoreCase)) == true;

        if (localOfferedPreviously && (currentLocalExhibitOfferIds == null || !currentLocalExhibitOfferIds.Contains(conflictExhibitId)))
        {
            return true;
        }

        return false;
    }

    private static bool HasOffer(TradeSyncPatch.TradeSessionState state, bool isOfferA)
        => (isOfferA ? state.OfferA : state.OfferB)?.Count > 0
        || (isOfferA ? state.MoneyA : state.MoneyB) > 0
        || (isOfferA ? state.ExhibitsA : state.ExhibitsB)?.Count > 0;

    #endregion

    #region 按钮与输入处理

    private void OnConfirmTrade()
    {
        if (TryIsNetworkTrade(out _))
        {

            TradeSyncPatch.TradeSessionState state = TradeSyncPatch.GetLastKnown(_tradeId);
            if (state is null)
            {
                return;
            }

            bool localIsA = IsPlayerA(state);
            bool localHasOffer = HasOffer(state, localIsA);
            bool remoteHasOffer = HasOffer(state, !localIsA);
            bool localConfirmed = localIsA ? state.AConfirmed : state.BConfirmed;
            bool remoteConfirmed = localIsA ? state.BConfirmed : state.AConfirmed;

            if (!localHasOffer && !remoteHasOffer)
            {
                UpdateUIStatus(TryLocalize("Trade.WaitingForItems", "等待放入物品..."));
                return;
            }

            if (localConfirmed)
            {

                UpdateUIStatus(remoteConfirmed
                    ? TryLocalize("Trade.BothConfirmed", "双方已确认，准备交换...")
                    : TryLocalize("Trade.WaitingForPartner", "已确认交易，等待对方确认..."));
                return;
            }

            if (!string.IsNullOrWhiteSpace(state.FailureCode) || !string.IsNullOrWhiteSpace(state.Reason))
            {
                if (!IsDuplicateExhibitResolvedLocally(state))
                {
                    UpdateUIStatus(FormatFailureReason(state, _selfPlayerId));
                }
                else
                {
                    UpdateUIStatus(remoteConfirmed
                        ? TryLocalize("Trade.BothConfirmed", "双方已确认，准备交换...")
                        : TryLocalize("Trade.WaitingForPartner", "已确认交易，等待对方确认..."));
                }
            }
            else
            {
                UpdateUIStatus(remoteConfirmed
                    ? TryLocalize("Trade.BothConfirmed", "双方已确认，准备交换...")
                    : TryLocalize("Trade.WaitingForPartner", "已确认交易，等待对方确认..."));
            }

            TradeSyncPatch.RequestConfirm(_tradeId, _selfPlayerId);
            return;
        }

        if (_player1OfferedCards.Count <= 0 || _player2OfferedCards.Count <= 0)
        {
            UpdateUIStatus(TryLocalize("Trade.WaitingForItems", "等待放入物品..."));
            return;
        }

        UpdateUIStatus("Trade.Confirmed".Localize());
        StartCoroutine(ExecuteTrade());
    }

    private void OnCancelTrade()
    {
        if (TryIsNetworkTrade(out _))
        {
            TradeSyncPatch.RequestCancel(_tradeId, _selfPlayerId);
        }

        Hide();
    }

    public void OnCancel()
    {
        if (_exhibitPickerRoot is not null && _exhibitPickerRoot.activeSelf)
        {
            HideExhibitPickerOverlay(false);
            return;
        }

        if (_cardPickerRoot is not null && _cardPickerRoot.activeSelf)
        {
            HideCardPickerOverlay();
            return;
        }

        if (_canCancel)
        {
            OnCancelTrade();
        }
    }

    #endregion

    #region 协程与网络同步

    private IEnumerator ExecuteTrade()
    {
        if (TryIsNetworkTrade(out _))
        {
            yield break;
        }

        ITradeInventory inventory = ModService.ServiceProvider?.GetService<ITradeInventory>()
            ?? new GameRunTradeInventory();

        if (confirmButton?.button is not null)
        {
            confirmButton.button.interactable = false;
        }
        if (cancelButton?.button is not null)
        {
            cancelButton.button.interactable = false;
        }

        var plan = new TradeSettlementPlan
        {
            TradeId = "local_mock",
            CommitId = Guid.NewGuid().ToString("N"),
            LocalPlayerId = _selfPlayerId ?? "local",
            SpentCards = _player1OfferedCards.Where(c => c != null).Select(c => new TradeCardItem
            {
                CardId = c.Id,
                InstanceId = c.InstanceId,
                IsUpgraded = c.IsUpgraded,
                UpgradeCounter = c.UpgradeCounter ?? 0,
                CardName = c.Name
            }).ToList(),
            GainedCards = _player2OfferedCards.Where(c => c != null).Select(c => new TradeCardItem
            {
                CardId = c.Id,
                InstanceId = -1,
                IsUpgraded = c.IsUpgraded,
                UpgradeCounter = c.UpgradeCounter ?? 0,
                CardName = c.Name
            }).ToList()
        };

        var result = inventory.ApplyPlan(plan);
        if (result == null || !result.Success)
        {
            UpdateUIStatus("Trade.Failed".Localize());
            yield break;
        }

        UpdateUIStatus("Trade.Completed".Localize());

        yield return new WaitForSeconds(TradeCompleteWaitTime);
        Hide();
    }

    private void SetupTradeSession(TradePayload payload)
    {
        try
        {
            INetworkClient client = ModService.ServiceProvider.GetService<INetworkClient>();
            bool connected = client is not null && client.IsConnected;
            Plugin.Logger?.LogInfo($"[TradePanel] SetupTradeSession enter: connected={connected}, tradeId={payload?.TradeId ?? "<null>"}");
            if (connected)
            {
                NetworkIdentityTracker.EnsureSubscribed(client);
                TradeSyncPatch.EnsureSubscribed(client);

                _selfPlayerId = NetworkIdentityTracker.GetSelfPlayerId();
                if (string.IsNullOrWhiteSpace(_selfPlayerId))
                {
                    var netMgr = ModService.ServiceProvider?.GetService<INetworkManager>();
                    _selfPlayerId = netMgr?.GetSelf()?.playerId;
                }
                if (string.IsNullOrWhiteSpace(_selfPlayerId))
                {
                    _selfPlayerId = "self";
                }
            }
            else
            {
                _selfPlayerId = "local";
            }

            _tradeId = payload?.TradeId;
            _tradeId ??= Guid.NewGuid().ToString("N");
            _playerAId = payload?.Player1Id ?? _selfPlayerId;
            _playerBId = payload?.Player2Id;

            if (string.IsNullOrWhiteSpace(_playerBId) || string.Equals(_playerBId, _selfPlayerId, StringComparison.Ordinal))
            {
                Plugin.Logger?.LogInfo($"[TradePanel] SetupTradeSession: partner unresolved, showing picker overlay. self={_selfPlayerId ?? "<null>"}, playerB={_playerBId ?? "<null>"}");
                ShowPartnerPickerOverlay();
                return;
            }

            transform.Find("NetworkPlugin_TradePanel_Frame")?.gameObject.SetActive(true);
            if (connected)
            {
                Plugin.Logger?.LogInfo($"[TradePanel] SetupTradeSession: start network trade session, tradeId={_tradeId}");
                TrySubscribeTradeEvents();
                EnsureOfferEditorOverlay();
                EnsureCardPickerOverlay();
                EnsureExhibitPickerOverlay();
                EnsureOfferPreviewOverlay();
                SetTradeDetailsVisible(true);
                cancelButton?.gameObject.SetActive(_canCancel);

                if (string.IsNullOrWhiteSpace(payload?.TradeId))
                {
                    TradeSyncPatch.RequestStartTrade(_tradeId, _playerAId, _playerBId, _maxTradeSlots);
                }
                TradeSyncPatch.RequestSnapshot(_tradeId, _selfPlayerId);
            }
            else
            {
                EnsureOfferEditorOverlay();
                EnsureCardPickerOverlay();
                EnsureExhibitPickerOverlay();
                EnsureOfferPreviewOverlay();
                SetTradeDetailsVisible(true);
                cancelButton?.gameObject.SetActive(_canCancel);
                UpdateUIStatus("本地调试交易：未连接服务器");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[TradePanel] SetupTradeSession 失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static bool IsLocalDebugTradeAllowed()
    {
        var cfg = ModService.ServiceProvider?.GetService<Configuration.ConfigManager>();
        return cfg?.DebugVirtualPlayerAiDefault?.Value == true || cfg?.DebugFakePlayersForTrade?.Value == true;
    }

    private bool TryEnsureNetworkConnected()
    {
        INetworkClient client = ModService.ServiceProvider?.GetService<INetworkClient>();
        if (client is null || !client.IsConnected)
        {
            Plugin.Logger?.LogWarning("[TradePanel] TryEnsureNetworkConnected failed.");
            TryShowTopMessage("交易仅在联机模式下可用。");
            return false;
        }

        return true;
    }

    private void TryShowTopMessage(string message)
    {
        if (UiManager.IsInitialized)
            UiManager.GetPanel<TopMessagePanel>().ShowMessage(message);
    }

    #region 展品预览栏

    #endregion

    #endregion
}
