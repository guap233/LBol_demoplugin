using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LBoL.Core;
using LBoL.Core.Cards;
using LBoL.Presentation;
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
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
namespace NetworkPlugin.UI.Panels;

public sealed partial class TradePanel
{
private void EnsureCardPickerOverlay()
    {
        if (_cardPickerRoot is not null)
        {
            Plugin.Logger?.LogInfo($"[TradePanel] EnsureCardPickerOverlay skipped: existing root name={_cardPickerRoot.name}, activeSelf={_cardPickerRoot.activeSelf}");
            return;
        }

        try
        {
            ShowCardsPanel sourcePanel = UiManager.GetPanel<ShowCardsPanel>();
            if (sourcePanel is null)
            {
                Plugin.Logger?.LogWarning("[TradePanel] EnsureCardPickerOverlay failed: ShowCardsPanel template unavailable.");
                _cardPickerRoot = null;
                return;
            }

            SelectCardPanel selectCardPanel = UiManager.GetPanel<SelectCardPanel>();
            Plugin.Logger?.LogInfo($"[TradePanel] EnsureCardPickerOverlay creating: sourcePanel={sourcePanel.name}, selectCardPanel={(selectCardPanel is not null ? selectCardPanel.name : "<null>")}");
            _cardPickerRoot = Instantiate(sourcePanel.gameObject, transform, false);
            _cardPickerRoot.name = "TradeCardPicker";
            _cardPickerRoot.SetActive(false);

            ShowCardsPanel clonedPanel = _cardPickerRoot.GetComponent<ShowCardsPanel>();
            if (clonedPanel is null)
            {
                Plugin.Logger?.LogWarning("[TradePanel] EnsureCardPickerOverlay failed: cloned ShowCardsPanel missing.");
                Destroy(_cardPickerRoot);
                _cardPickerRoot = null;
                return;
            }

            clonedPanel.enabled = false;

            CardPickerTag tag = _cardPickerRoot.AddComponent<CardPickerTag>();
            tag.DeckHolder = GetPrivateFieldValue<DeckHolder>(clonedPanel, "deckHolder");
            tag.ReturnButton = GetPrivateFieldValue<Button>(clonedPanel, "returnButton");
            tag.TopHideButton = GetPrivateFieldValue<Button>(clonedPanel, "topHideButton");
            tag.Portrait = GetPrivateFieldValue<Image>(clonedPanel, "portrait");
            tag.ActualOrderToggle = GetPrivateFieldValue<CommonToggleWidget>(clonedPanel, "actualOrderToggle");
            tag.IndexOrderToggle = GetPrivateFieldValue<CommonToggleWidget>(clonedPanel, "indexOrderToggle");
            tag.TypeOrderToggle = GetPrivateFieldValue<CommonToggleWidget>(clonedPanel, "typeOrderToggle");
            tag.RarityOrderToggle = GetPrivateFieldValue<CommonToggleWidget>(clonedPanel, "rarityOrderToggle");
            tag.FollowCardOnlyToggle = GetPrivateFieldValue<CommonToggleWidget>(clonedPanel, "followCardOnlyToggle");
            tag.DreamCardOnlyToggle = GetPrivateFieldValue<CommonToggleWidget>(clonedPanel, "dreamCardOnlyToggle");
            tag.MinimizedButton = GetPrivateFieldValue<GameObject>(clonedPanel, "minimizedButton");
            tag.SelectParticleTemplate = selectCardPanel is not null
                ? GetPrivateFieldValue<GameObject>(selectCardPanel, "selectParticle")
                : null;

            if (tag.DeckHolder is null)
            {
                Plugin.Logger?.LogWarning("[TradePanel] EnsureCardPickerOverlay failed: DeckHolder missing on cloned panel.");
                Destroy(_cardPickerRoot);
                _cardPickerRoot = null;
                return;
            }

            Plugin.Logger?.LogInfo($"[TradePanel] EnsureCardPickerOverlay ready: returnButton={(tag.ReturnButton is not null)}, topHideButton={(tag.TopHideButton is not null)}, portrait={(tag.Portrait is not null)}, deckHolder={(tag.DeckHolder is not null)}");

            if (tag.MinimizedButton is not null)
            {
                tag.MinimizedButton.SetActive(false);
            }

            if (tag.ReturnButton is not null)
            {
                tag.ReturnButton.onClick.RemoveAllListeners();
                tag.ReturnButton.onClick.AddListener(() => ApplyCardPickerSelection(tag));
                var label = tag.ReturnButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label is not null)
                {
                    var localized = label.GetComponent<LBoL.Presentation.I10N.LocalizedText>();
                    if (localized is not null) localized.enabled = false;
                    label.text = "确认选择";
                }
            }

            if (tag.TopHideButton != null)
            {
                tag.TopHideButton.onClick.RemoveAllListeners();
                tag.TopHideButton.onClick.AddListener(HideCardPickerOverlay);
            }

            BindCardPickerToggle(tag.ActualOrderToggle, on =>
            {
                if (!on)
                {
                    return;
                }

                tag.CurrentOrder = CardPickerOrderStatus.Actual;
                PopulateCardPickerWidgets(tag);
            });

            BindCardPickerToggle(tag.IndexOrderToggle, on =>
            {
                if (!on)
                {
                    return;
                }

                tag.CurrentOrder = CardPickerOrderStatus.Index;
                PopulateCardPickerWidgets(tag);
            });

            BindCardPickerToggle(tag.TypeOrderToggle, on =>
            {
                if (!on)
                {
                    return;
                }

                tag.CurrentOrder = CardPickerOrderStatus.Type;
                PopulateCardPickerWidgets(tag);
            });

            BindCardPickerToggle(tag.RarityOrderToggle, on =>
            {
                if (!on)
                {
                    return;
                }

                tag.CurrentOrder = CardPickerOrderStatus.Rarity;
                PopulateCardPickerWidgets(tag);
            });

            BindCardPickerToggle(tag.DreamCardOnlyToggle, on =>
            {
                if (on)
                {
                    tag.CurrentFilter = CardPickerFilterStatus.DreamCardsOnly;
                    tag.FollowCardOnlyToggle?.toggle?.SetIsOnWithoutNotify(false);
                }
                else
                {
                    tag.CurrentFilter = tag.FollowCardOnlyToggle?.toggle?.isOn == true
                        ? CardPickerFilterStatus.FollowCardsOnly
                        : CardPickerFilterStatus.AllCards;
                }

                PopulateCardPickerWidgets(tag);
            });

            BindCardPickerToggle(tag.FollowCardOnlyToggle, on =>
            {
                if (on)
                {
                    tag.CurrentFilter = CardPickerFilterStatus.FollowCardsOnly;
                    tag.DreamCardOnlyToggle?.toggle?.SetIsOnWithoutNotify(false);
                }
                else
                {
                    tag.CurrentFilter = tag.DreamCardOnlyToggle?.toggle?.isOn == true
                        ? CardPickerFilterStatus.DreamCardsOnly
                        : CardPickerFilterStatus.AllCards;
                }

                PopulateCardPickerWidgets(tag);
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[TradePanel] EnsureCardPickerOverlay exception: {ex.Message}");
            _cardPickerRoot = null;
        }
    }

    private enum CardPickerOrderStatus
    {
        Actual,
        Index,
        Type,
        Rarity,
    }

    private enum CardPickerFilterStatus
    {
        AllCards,
        DreamCardsOnly,
        FollowCardsOnly,
    }

    private sealed class CardPickerTag : MonoBehaviour
    {
        public DeckHolder DeckHolder;
        public Button ReturnButton;
        public Button TopHideButton;
        public Image Portrait;
        public CommonToggleWidget ActualOrderToggle;
        public CommonToggleWidget IndexOrderToggle;
        public CommonToggleWidget TypeOrderToggle;
        public CommonToggleWidget RarityOrderToggle;
        public CommonToggleWidget FollowCardOnlyToggle;
        public CommonToggleWidget DreamCardOnlyToggle;
        public GameObject MinimizedButton;
        public GameObject SelectParticleTemplate;
        public readonly List<Card> SourceCards = new();
        public readonly List<SelectCardWidget> SelectWidgets = new();
        public readonly List<Card> SelectedCards = new();
        public int TargetSelectCount;
        public CardPickerOrderStatus CurrentOrder = CardPickerOrderStatus.Actual;
        public CardPickerFilterStatus CurrentFilter = CardPickerFilterStatus.AllCards;
    }

    private void ShowCardPickerOverlay()
    {
        if (!CanEditOffer() || _cardSelectionCompleted)
        {
            TryShowTopMessage("本次交易已完成选牌；如需交易其他卡牌，请发起新的交易。");
            return;
        }
        GameRunController run = ActiveGameRun;
        Plugin.Logger?.LogInfo($"[TradePanel] ShowCardPickerOverlay enter: tradeId={_tradeId ?? "<null>"}, cardPickerExists={(_cardPickerRoot is not null)}, activeGameRun={(run is not null)}, deckCount={(run?.BaseDeck?.Count ?? 0)}, offeredLocal={_player1OfferedCards.Count}, maxSlots={_maxTradeSlots}, canEdit={CanEditOffer()}");
        EnsureCardPickerOverlay();
        if (_cardPickerRoot is null)
        {
            Plugin.Logger?.LogWarning($"[TradePanel] ShowCardPickerOverlay aborted: card picker root missing, tradeId={_tradeId ?? "<null>"}");
            TryShowTopMessage("卡牌选择界面不可用。");
            return;
        }

        CardPickerTag tag = _cardPickerRoot.GetComponent<CardPickerTag>();
        if (tag is not null)
        {
            tag.SelectedCards.Clear();
            tag.CurrentOrder = CardPickerOrderStatus.Actual;
            tag.CurrentFilter = CardPickerFilterStatus.AllCards;
        }

        _cardPickerRoot.SetActive(true);
        ForceEnableRaycasts(_cardPickerRoot);
        SetTradeDetailsVisible(false);
        EnsurePopupTopmost();
        RebuildCardPickerList();
        Plugin.Logger?.LogInfo($"[TradePanel] ShowCardPickerOverlay shown: tradeId={_tradeId ?? "<null>"}, pickerActive={_cardPickerRoot.activeSelf}");
    }

    private void HideCardPickerOverlay()
    {
        CardPickerTag tag = _cardPickerRoot is not null ? _cardPickerRoot.GetComponent<CardPickerTag>() : null;
        if (tag?.DeckHolder is not null)
        {
            tag.SelectWidgets.Clear();
            tag.SelectedCards.Clear();
            tag.DeckHolder.Clear();
        }

        _cardPickerRoot?.SetActive(false);
        SetTradeDetailsVisible(true);
    }

    private void EnsureOfferPreviewOverlay()
    {
        if (_offerPreviewRoot is not null)
        {
            _offerPreviewRoot.SetActive(true);
            _offerPreviewRoot.transform.SetAsLastSibling();
            return;
        }

        try
        {
            ShowCardsPanel sourcePanel = UiManager.GetPanel<ShowCardsPanel>();
            DeckHolder template = sourcePanel is not null ? GetPrivateFieldValue<DeckHolder>(sourcePanel, "deckHolder") : null;
            CardWidget cardTemplate = template is not null ? GetPrivateFieldValue<CardWidget>(template, "cardTemplate") : null;
            if (template is null || cardTemplate is null)
            {
                return;
            }

            _offerPreviewCardTemplate = cardTemplate;

            _offerPreviewRoot = new GameObject("TradeOfferPreview");
            _offerPreviewRoot.transform.SetParent(GetTradePanelContentParent(), false);

            RectTransform rootRect = _offerPreviewRoot.AddComponent<RectTransform>();
            SetRect(rootRect, 0.02f, 0.36f, 0.98f, 0.82f);

            _localOfferPreviewPanel = CreateOfferPreviewPanel(_offerPreviewRoot.transform, "LocalOfferPreview", 0.00f, 0.00f, 0.48f, 1.00f);
            _remoteOfferPreviewPanel = CreateOfferPreviewPanel(_offerPreviewRoot.transform, "RemoteOfferPreview", 0.52f, 0.00f, 1.00f, 1.00f);

            RefreshOfferPreview();
        }
        catch
        {
            _offerPreviewRoot = null;
            _localOfferPreviewPanel = null;
            _remoteOfferPreviewPanel = null;
            _offerPreviewCardTemplate = null;
        }
    }

    private static OfferPreviewPanelTag CreateOfferPreviewPanel(Transform parent, string name, float minX, float minY, float maxX, float maxY)
    {
        if (parent is null)
        {
            return null;
        }

        GameObject root = new GameObject(name);
        root.transform.SetParent(parent, false);

        RectTransform rootRect = root.AddComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(minX, minY);
        rootRect.anchorMax = new Vector2(maxX, maxY);
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        GameObject layout = new GameObject("CardLayout");
        layout.transform.SetParent(root.transform, false);
        RectTransform layoutRect = layout.AddComponent<RectTransform>();
        layoutRect.anchorMin = Vector2.zero;
        layoutRect.anchorMax = Vector2.one;
        layoutRect.offsetMin = new Vector2(8f, 8f);
        layoutRect.offsetMax = new Vector2(-8f, -8f);

        OfferPreviewPanelTag tag = root.AddComponent<OfferPreviewPanelTag>();
        tag.CardLayout = layoutRect;
        return tag;
    }

    private void RefreshOfferPreview()
    {
        EnsureOfferPreviewOverlay();

        RebuildOfferPreviewPanel(_localOfferPreviewPanel, _player1OfferedCards);
        RebuildOfferPreviewPanel(_remoteOfferPreviewPanel, _player2OfferedCards);
    }

    private void RebuildOfferPreviewPanel(OfferPreviewPanelTag panel, IEnumerable<Card> cards)
    {
        if (panel?.CardLayout is null || _offerPreviewCardTemplate is null)
        {
            return;
        }

        ClearOfferPreviewPanel(panel);

        List<Card> list = (cards ?? Enumerable.Empty<Card>())
            .Where(card => card is not null)
            .ToList();

        if (list.Count == 0)
        {
            return;
        }

        Canvas.ForceUpdateCanvases();
        RectTransform layout = panel.CardLayout;
        LayoutRebuilder.ForceRebuildLayoutImmediate(layout);

        float availableWidth = layout.rect.width > 1f ? layout.rect.width : 480f;
        float availableHeight = layout.rect.height > 1f ? layout.rect.height : 240f;
        RectTransform templateRect = _offerPreviewCardTemplate.transform as RectTransform;
        float cardWidth = templateRect is not null && templateRect.rect.width > 1f ? templateRect.rect.width : 360f;
        float cardHeight = templateRect is not null && templateRect.rect.height > 1f ? templateRect.rect.height : 540f;

        int count = list.Count;
        float spacing = Mathf.Clamp(availableWidth * 0.02f, 8f, 18f);
        float scaleByWidth = (availableWidth - Mathf.Max(0, count - 1) * spacing) / (count * cardWidth);
        float scaleByHeight = availableHeight / cardHeight;
        float scale = Mathf.Clamp(Mathf.Min(scaleByWidth, scaleByHeight, 0.42f), 0.22f, 0.42f);
        float displayedWidth = cardWidth * scale;
        float totalWidth = displayedWidth * count + Mathf.Max(0, count - 1) * spacing;
        float startX = -totalWidth * 0.5f + displayedWidth * 0.5f;

        for (int i = 0; i < list.Count; i++)
        {
            Card card = list[i];
            if (card is null)
            {
                continue;
            }

            CardWidget cardWidget = Instantiate(_offerPreviewCardTemplate, layout, false);
            cardWidget.name = $"PreviewCard_{card.InstanceId}_{i}";
            cardWidget.Card = card;

            ShowingCard showing = cardWidget.gameObject.GetComponent<ShowingCard>() ?? cardWidget.gameObject.AddComponent<ShowingCard>();
            showing.SetScale(scale, scale);

            RectTransform rect = cardWidget.transform as RectTransform;
            if (rect is not null)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = new Vector2(startX + i * (displayedWidth + spacing), 0f);
            }
        }
    }

    private static void ClearOfferPreviewPanel(OfferPreviewPanelTag panel)
    {
        if (panel?.CardLayout is null)
        {
            return;
        }

        panel.CardLayout.Cast<Transform>()
            .Where(c => c is not null)
            .ToList()
            .ForEach(c => Destroy(c.gameObject));
    }

    private sealed class OfferPreviewPanelTag : MonoBehaviour
    {
        public RectTransform CardLayout;
    }

    private void RebuildCardPickerList()
    {
        if (_cardPickerRoot is null)
        {
            Plugin.Logger?.LogWarning($"[TradePanel] RebuildCardPickerList aborted: card picker root missing, tradeId={_tradeId ?? "<null>"}");
            return;
        }

        CardPickerTag tag = _cardPickerRoot.GetComponent<CardPickerTag>();
        if (tag is null || tag.DeckHolder is null)
        {
            Plugin.Logger?.LogWarning($"[TradePanel] RebuildCardPickerList aborted: picker tag/holder missing, tradeId={_tradeId ?? "<null>"}, tag={(tag is not null)}, holder={(tag?.DeckHolder is not null)}");
            return;
        }

        GameRunController run = ActiveGameRun;
        Plugin.Logger?.LogInfo($"[TradePanel] RebuildCardPickerList enter: tradeId={_tradeId ?? "<null>"}, activeGameRun={(run is not null)}, deckCount={(run?.BaseDeck?.Count ?? 0)}, offeredLocal={_player1OfferedCards.Count}, maxSlots={_maxTradeSlots}, pickerActive={_cardPickerRoot.activeSelf}, canEdit={CanEditOffer()}");

        if (!CanEditOffer())
        {
            Plugin.Logger?.LogInfo($"[TradePanel] RebuildCardPickerList blocked: cannot edit offer, tradeId={_tradeId ?? "<null>"}");
            tag.SourceCards.Clear();
            tag.TargetSelectCount = 0;
            tag.DeckHolder.Clear();
            tag.DeckHolder.SetTitle("Game.Deck".Localize(true), "当前状态无法编辑报价。");
            return;
        }

        List<Card> deck = new List<Card>();
        try
        {
            if (run?.BaseDeck is not null)
            {
                deck = run.BaseDeck
                    .Where(c => c is not null)
                    .ToList();
            }
        }
        catch
        {
            deck = new List<Card>();
        }

        HashSet<int> offered;
        try
        {
            offered = _player1OfferedCards.Where(c => c is not null).Select(c => c.InstanceId).ToHashSet();
        }
        catch
        {
            offered = new HashSet<int>();
        }

        List<Card> candidates = deck.Where(c => c is not null && !offered.Contains(c.InstanceId)).ToList();
        if (candidates.Count == 0)
        {
            Plugin.Logger?.LogInfo($"[TradePanel] Card picker empty: panelGameRun={(GameRun is not null)}, activeGameRun={(run is not null)}, deckCount={deck.Count}, offeredCount={offered.Count}, tradeId={_tradeId ?? "<null>"}");
            tag.SourceCards.Clear();
            tag.TargetSelectCount = 0;
            tag.DeckHolder.Clear();
            tag.DeckHolder.SetTitle("Game.Deck".Localize(true), "没有可交易的卡牌。");
            return;
        }

        int remaining = Math.Max(0, _maxTradeSlots - (_player1OfferedCards?.Count ?? 0));
        if (remaining <= 0)
        {
            Plugin.Logger?.LogInfo($"[TradePanel] RebuildCardPickerList blocked: no remaining slots, tradeId={_tradeId ?? "<null>"}, maxSlots={_maxTradeSlots}, offeredLocal={_player1OfferedCards.Count}");
            tag.SourceCards.Clear();
            tag.TargetSelectCount = 0;
            tag.DeckHolder.Clear();
            tag.DeckHolder.SetTitle("Game.Deck".Localize(true), "卡槽已满。");
            return;
        }

        tag.SourceCards.Clear();
        tag.SourceCards.AddRange(candidates);
        tag.TargetSelectCount = Math.Max(1, Math.Min(remaining, candidates.Count));
        Plugin.Logger?.LogInfo($"[TradePanel] RebuildCardPickerList prepared: tradeId={_tradeId ?? "<null>"}, candidateCount={candidates.Count}, remaining={remaining}, targetSelectCount={tag.TargetSelectCount}");

        if (tag.Portrait is not null)
        {
            ShowCardsPanel sourcePanel = UiManager.GetPanel<ShowCardsPanel>();
            Image sourcePortrait = sourcePanel is not null ? GetPrivateFieldValue<Image>(sourcePanel, "portrait") : null;
            if (sourcePortrait is not null && sourcePortrait.sprite is not null)
            {
                tag.Portrait.sprite = sourcePortrait.sprite;
            }
        }

        ApplyCardPickerToggleStates(tag);
        PopulateCardPickerWidgets(tag);
    }

    private static void BindCardPickerToggle(CommonToggleWidget widget, UnityEngine.Events.UnityAction<bool> handler)
    {
        if (widget?.toggle is null || handler is null)
        {
            return;
        }

        widget.toggle.onValueChanged.AddListener(handler);
    }

    private static void ApplyCardPickerToggleStates(CardPickerTag tag)
    {
        if (tag is null)
        {
            return;
        }

        if (tag.ActualOrderToggle?.toggle is not null)
        {
            tag.ActualOrderToggle.toggle.SetIsOnWithoutNotify(tag.CurrentOrder == CardPickerOrderStatus.Actual);
            tag.ActualOrderToggle.toggle.interactable = true;
            tag.ActualOrderToggle.SetLock(true);
        }

        if (tag.IndexOrderToggle?.toggle is not null)
        {
            tag.IndexOrderToggle.toggle.SetIsOnWithoutNotify(tag.CurrentOrder == CardPickerOrderStatus.Index);
        }

        if (tag.TypeOrderToggle?.toggle is not null)
        {
            tag.TypeOrderToggle.toggle.SetIsOnWithoutNotify(tag.CurrentOrder == CardPickerOrderStatus.Type);
        }

        if (tag.RarityOrderToggle?.toggle is not null)
        {
            tag.RarityOrderToggle.toggle.SetIsOnWithoutNotify(tag.CurrentOrder == CardPickerOrderStatus.Rarity);
        }

        if (tag.DreamCardOnlyToggle?.toggle is not null)
        {
            tag.DreamCardOnlyToggle.toggle.SetIsOnWithoutNotify(tag.CurrentFilter == CardPickerFilterStatus.DreamCardsOnly);
        }

        if (tag.FollowCardOnlyToggle?.toggle is not null)
        {
            tag.FollowCardOnlyToggle.toggle.SetIsOnWithoutNotify(tag.CurrentFilter == CardPickerFilterStatus.FollowCardsOnly);
        }
    }

    private void PopulateCardPickerWidgets(CardPickerTag tag)
    {
        if (tag?.DeckHolder is null)
        {
            return;
        }

        tag.DeckHolder.Clear();
        tag.SelectWidgets.Clear();

        int dreamCount = tag.SourceCards.Count(c => c is not null && c.IsDreamCard);
        int followCount = tag.SourceCards.Count(c => c is not null && c.IsFollowCard);
        if (tag.DreamCardOnlyToggle is not null)
        {
            tag.DreamCardOnlyToggle.gameObject.SetActive(dreamCount > 0);
        }

        if (tag.FollowCardOnlyToggle is not null)
        {
            tag.FollowCardOnlyToggle.gameObject.SetActive(followCount > 0);
        }

        List<Card> displayCards = GetCardPickerDisplayCards(tag);
        string description = $"可选择0～{tag.TargetSelectCount}张牌交易，选好后点击确认选择";

        if (displayCards.Count == 0)
        {
            string emptyReason = tag.SourceCards.Count == 0
                ? "没有可交易的卡牌。"
                : "当前过滤条件下没有可交易的卡牌。";
            tag.DeckHolder.SetTitle("Game.Deck".Localize(true), emptyReason);
            return;
        }

        tag.DeckHolder.SetTitle("Game.Deck".Localize(true), description);

        foreach (Card card in displayCards)
        {
            CreateCardPickerCardWidget(tag, card);
        }
    }

    private static List<Card> GetCardPickerDisplayCards(CardPickerTag tag)
    {
        IEnumerable<Card> ordered = tag.SourceCards.Where(card => card is not null);
        switch (tag.CurrentOrder)
        {
            case CardPickerOrderStatus.Index:
                ordered = ordered.OrderBy(card => card.Config.Index);
                break;
            case CardPickerOrderStatus.Type:
                ordered = ordered.OrderBy(card => card.CardType).ThenBy(card => card.Config.Index);
                break;
            case CardPickerOrderStatus.Rarity:
                ordered = ordered.OrderBy(card => card.Config.Rarity).ThenBy(card => card.Config.Index);
                break;
            case CardPickerOrderStatus.Actual:
            default:
                break;
        }

        switch (tag.CurrentFilter)
        {
            case CardPickerFilterStatus.DreamCardsOnly:
                ordered = ordered.Where(card => card.IsDreamCard);
                break;
            case CardPickerFilterStatus.FollowCardsOnly:
                ordered = ordered.Where(card => card.IsFollowCard);
                break;
            case CardPickerFilterStatus.AllCards:
            default:
                break;
        }

        return ordered.ToList();
    }

    private void CreateCardPickerCardWidget(CardPickerTag tag, Card card)
    {
        try
        {
            if (tag?.DeckHolder is null || card is null)
            {
                return;
            }

            CardWidget cardWidget = tag.DeckHolder.AddCardWidget(card, true);
            if (cardWidget is null)
            {
                return;
            }

            SelectCardWidget selectWidget = cardWidget.gameObject.AddComponent<SelectCardWidget>();
            selectWidget.SelectParticle = CreateCardPickerSelectionMarker(selectWidget.transform, tag.SelectParticleTemplate);
            selectWidget.SelectParticle.SetActive(false);
            selectWidget.SelectedChanged += (_, _) => OnCardPickerSelectionChanged(tag, selectWidget);
            selectWidget.SetSelected(tag.SelectedCards.Contains(card), false);
            tag.SelectWidgets.Add(selectWidget);
        }
        catch
        {

        }
    }

    private static GameObject CreateCardPickerSelectionMarker(Transform parent, GameObject template)
    {
        if (template is not null)
        {
            GameObject marker = Instantiate(template, parent, false);
            marker.name = "TradeSelectParticle";
            return marker;
        }

        GameObject fallback = new GameObject("TradeSelectParticle", typeof(RectTransform), typeof(Image));
        fallback.transform.SetParent(parent, false);
        RectTransform rect = fallback.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        Image image = fallback.GetComponent<Image>();
        image.color = new Color(0.35f, 0.85f, 1f, 0.22f);
        image.raycastTarget = false;
        return fallback;
    }

    private void OnCardPickerSelectionChanged(CardPickerTag tag, SelectCardWidget widget)
    {
        if (tag is null || widget is null || tag.TargetSelectCount <= 0)
        {
            return;
        }

        if (!tag.SelectWidgets.Contains(widget))
        {
            return;
        }

        if (widget.IsSelected)
        {
            if (!tag.SelectedCards.Contains(widget.Card))
            {
                tag.SelectedCards.Add(widget.Card);
            }
        }
        else
        {
            tag.SelectedCards.Remove(widget.Card);
        }

        if (tag.SelectedCards.Count > tag.TargetSelectCount)
        {
            tag.SelectedCards.Remove(widget.Card);
            widget.SetSelected(false, false);
        }

    }

    private void ApplyCardPickerSelection(CardPickerTag tag)
    {
        if (tag is null || _cardPickerApplyingSelection || _cardSelectionCompleted || !CanEditOffer())
        {
            return;
        }

        _cardPickerApplyingSelection = true;
        try
        {
            using (new ApplyingStateScope(this))
            {
                foreach (Card card in tag.SelectedCards.ToList())
                {
                    AddCardToTrade(card, true);
                }
            }

            _cardSelectionCompleted = true;
            TrySendOfferUpdate();
            RefreshOfferEditorTexts();
            CheckTradeReady();
            HideCardPickerOverlay();
        }
        finally
        {
            _cardPickerApplyingSelection = false;
        }
    }

    private bool TryIsNetworkTrade(out bool localIsA)
    {
        localIsA = false;

        if (_localDebugTradeMode)
        {
            return false;
        }

        INetworkClient client = ModService.ServiceProvider.GetService<INetworkClient>();
        if (client is null || !client.IsConnected)
        {
            return false;
        }

        localIsA = string.Equals(_selfPlayerId, _playerAId, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(_tradeId) && !string.IsNullOrWhiteSpace(_selfPlayerId);
    }
}
