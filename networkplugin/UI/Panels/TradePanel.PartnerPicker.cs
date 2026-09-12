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
    private void ShowPartnerPickerOverlay()
    {
        if (_partnerPickerActive)
        {
            Plugin.Logger?.LogInfo("[TradePanel] ShowPartnerPickerOverlay skipped: picker already active.");
            return;
        }

        Plugin.Logger?.LogInfo($"[TradePanel] ShowPartnerPickerOverlay enter: tradeId={_tradeId ?? "<null>"}, self={_selfPlayerId ?? "<null>"}, currentPartner={_playerBId ?? "<null>"}");

        _canvasGroup.interactable = true;
        _canvasGroup.blocksRaycasts = true;

        EnsurePartnerPickerOverlay();
        if (_partnerPickerRoot is null)
        {
            Plugin.Logger?.LogWarning($"[TradePanel] ShowPartnerPickerOverlay failed: picker root missing, buildError={_partnerPickerBuildError ?? "<null>"}");
            ShowTradeTargetUnavailableDialog(_partnerPickerBuildError);
            return;
        }

        _partnerPickerActive = true;
    _partnerPickerInputReadyTime = Time.unscaledTime + 0.15f;
        _partnerPickerRoot.SetActive(true);
        EnsurePopupTopmost();

        transform.Find("NetworkPlugin_TradePanel_Frame")?.gameObject.SetActive(false);
    Plugin.Logger?.LogInfo($"[TradePanel] ShowPartnerPickerOverlay armed click guard until={_partnerPickerInputReadyTime:F3}");

        ForceEnableRaycasts(_partnerPickerRoot);

        SetTradeDetailsVisible(false);

        cancelButton?.gameObject.SetActive(false);

        UpdateUIStatus(string.Empty);
        RebuildPartnerPickerList();

        if (!OtherPlayersOverlayPatch.TryGetSelfLocation(out _, out _, out _, out _))
        {
            Plugin.Logger?.LogInfo("[TradePanel] ShowPartnerPickerOverlay: self location unavailable, waiting for auto refresh.");
            TryShowPartnerPickerWaiting();
            if (_partnerPickerAutoRefreshCo is not null)
            {
                StopCoroutine(_partnerPickerAutoRefreshCo);
                _partnerPickerAutoRefreshCo = null;
            }
            _partnerPickerAutoRefreshCo = StartCoroutine(CoPartnerPickerAutoRefreshOnce());
        }
    }

    private void TryShowPartnerPickerWaiting()
    {
        if (_partnerPickerRoot is null)
        {
            return;
        }

        PartnerPickerTag tag = _partnerPickerRoot.GetComponentInChildren<PartnerPickerTag>(true);
        if (tag is null)
        {
            return;
        }

        tag.ScrollRect?.gameObject.SetActive(false);

        if (tag.EmptyText is not null)
        {
            tag.EmptyText.gameObject.SetActive(true);
            tag.EmptyText.text = "正在同步位置信息...";
            tag.EmptyText.alignment = TextAlignmentOptions.Center;
            var c = tag.EmptyText.color;
            c.a = 1f;
            tag.EmptyText.color = c;
        }
    }

    private IEnumerator CoPartnerPickerAutoRefreshOnce()
    {

        float t = 0f;
        while (t < 1.0f)
        {
            if (!_partnerPickerActive || _partnerPickerRoot is null)
            {
                _partnerPickerAutoRefreshCo = null;
                yield break;
            }

            if (OtherPlayersOverlayPatch.TryGetSelfLocation(out _, out _, out _, out _))
            {
                break;
            }

            t += Time.unscaledDeltaTime;
            yield return null;
        }

        if (_partnerPickerActive && _partnerPickerRoot is not null)
        {
            RebuildPartnerPickerList();
        }

        _partnerPickerAutoRefreshCo = null;
    }

    private static void ForceEnableRaycasts(GameObject root)
    {
        if (root is null)
        {
            return;
        }

        root.GetComponentsInChildren<CanvasGroup>(true).ToList().ForEach(cg =>
        {
            cg.interactable = true;
            cg.blocksRaycasts = true;
            cg.ignoreParentGroups = true;
        });
    }

    private void ShowTradeTargetUnavailableDialog(string detail)
    {

        if (!UiManager.IsInitialized)
        {
            return;
        }

        _blockingCenterMessageActive = true;
        SetTradeDetailsVisible(false);

        confirmButton?.gameObject.SetActive(false);
        cancelButton?.gameObject.SetActive(false);
        UpdateUIStatus(string.Empty);
        SetCanvasInteractable(false);

        UiManager.GetDialog<MessageDialog>().Show(new MessageContent
        {
            Text = "交易对象不可用",
            SubText = string.IsNullOrWhiteSpace(detail) ? "交易对象选择界面不可用。" : ("交易对象选择界面不可用。\n" + detail),
            Icon = MessageIcon.Error,
            Buttons = DialogButtons.Confirm,
            OnConfirm = () =>
            {
                _blockingCenterMessageActive = false;
                Hide();
            },
            OnCancel = () =>
            {
                _blockingCenterMessageActive = false;
                Hide();
            }
        });
    }

    private void HidePartnerPickerOverlay()
    {
        if (_partnerPickerAutoRefreshCo is not null)
        {
            StopCoroutine(_partnerPickerAutoRefreshCo);
            _partnerPickerAutoRefreshCo = null;
        }

        _partnerPickerActive = false;
    _partnerPickerInputReadyTime = 0f;
        _partnerPickerRoot?.SetActive(false);

        transform.Find("NetworkPlugin_TradePanel_Frame")?.gameObject.SetActive(true);

        SetTradeDetailsVisible(true);

        _canvasGroup.interactable = true;
        _canvasGroup.blocksRaycasts = true;

        cancelButton?.gameObject.SetActive(_canCancel);
    }

    private void EnsurePopupTopmost()
    {
        transform.SetAsLastSibling();
        _partnerPickerRoot?.transform.SetAsLastSibling();
        _cardPickerRoot?.transform.SetAsLastSibling();
        _exhibitPickerRoot?.transform.SetAsLastSibling();
        _offerPreviewRoot?.transform.SetAsLastSibling();
        _offerEditorRoot?.transform.SetAsLastSibling();
        _offerActionsRoot?.transform.SetAsLastSibling();
    }

    private void SetTradeDetailsVisible(bool visible)
    {
        statusText?.gameObject.SetActive(visible);

        player1NameText?.gameObject.SetActive(visible);
        player2NameText?.gameObject.SetActive(visible);

        confirmButton?.gameObject.SetActive(visible);
        cancelButton?.gameObject.SetActive(visible || _canCancel);

        _offerPreviewRoot?.SetActive(visible);
        _offerEditorRoot?.SetActive(visible);
        _offerActionsRoot?.SetActive(visible);
    }

    private void SetCanvasInteractable(bool interactable)
    {
        if (_canvasGroup is null) return;
        _canvasGroup.interactable = interactable;
        _canvasGroup.blocksRaycasts = interactable;
    }

    private void EnsurePartnerPickerOverlay()
    {
        if (_partnerPickerRoot is not null)
        {
            return;
        }

        _partnerPickerBuildError = null;
        try
        {
            Transform parent = transform;

            GameObject prefab = Resources.Load<GameObject>("UI/Dialogs/MessageDialog");
            if (prefab is null)
            {
                _partnerPickerBuildError = "无法加载 UI/Dialogs/MessageDialog";
                _partnerPickerRoot = null;
                return;
            }

            _partnerPickerRoot = Instantiate(prefab, parent, false);
            _partnerPickerRoot.name = "TradePartnerPicker";
            _partnerPickerRoot.SetActive(false);

            RectTransform rootRect = _partnerPickerRoot.GetComponent<RectTransform>();
            if (rootRect is not null)
            {
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = Vector2.zero;
                rootRect.offsetMax = Vector2.zero;
            }

            MessageDialog dialog = _partnerPickerRoot.GetComponentInChildren<MessageDialog>(true);
            if (dialog is null)
            {
                _partnerPickerBuildError = "MessageDialog 组件缺失";
                Destroy(_partnerPickerRoot);
                _partnerPickerRoot = null;
                return;
            }

            TextMeshProUGUI mainText = GetDialogField<TextMeshProUGUI>(dialog, "mainText");
            TextMeshProUGUI subText = GetDialogField<TextMeshProUGUI>(dialog, "subText");
            Button singleConfirm = GetDialogField<Button>(dialog, "singleConfirmButton");
            Button confirm = GetDialogField<Button>(dialog, "confirmButton");
            Button cancel = GetDialogField<Button>(dialog, "cancelButton");

            RectTransform subTextRect = subText?.rectTransform;

            if (mainText is not null)
            {
                mainText.text = "请选择交易对象";
                mainText.alignment = TextAlignmentOptions.Center;
                mainText.raycastTarget = false;
            }

            if (subText is not null)
            {

                subText.text = string.Empty;
                subText.raycastTarget = true;
                subText.alignment = TextAlignmentOptions.Center;
                var c = subText.color;
                c.a = 0f;
                subText.color = c;
                subText.gameObject.SetActive(true);

                var subTextBtn = subText.gameObject.GetComponent<Button>() ?? subText.gameObject.AddComponent<Button>();
                subTextBtn.targetGraphic = subText;
                subTextBtn.onClick.RemoveAllListeners();
                subTextBtn.onClick.AddListener(() => OnPartnerPickerRefreshClicked());
            }

            if (singleConfirm is not null)
            {
                singleConfirm.onClick.RemoveAllListeners();
                Destroy(singleConfirm.gameObject);
            }
            if (confirm is not null)
            {
                confirm.onClick.RemoveAllListeners();
                confirm.gameObject.SetActive(true);

                var refreshLabel = confirm.GetComponentInChildren<TextMeshProUGUI>(true);
                if (refreshLabel is not null)
                {
                    refreshLabel.text = "刷新";
                    refreshLabel.alignment = TextAlignmentOptions.Center;
                }

                confirm.onClick.AddListener(OnPartnerPickerRefreshClicked);
            }

            if (cancel is not null)
            {
                cancel.onClick.RemoveAllListeners();
                cancel.gameObject.SetActive(true);

                _partnerPickerCancelButton = cancel;

                var cancelLabel = cancel.GetComponentInChildren<TextMeshProUGUI>(true);
                if (cancelLabel is not null)
                {
                    cancelLabel.text = "取消";
                    cancelLabel.alignment = TextAlignmentOptions.Center;
                }

                cancel.onClick.AddListener(() =>
                {
                    HidePartnerPickerOverlay();
                    Hide();
                });
            }

            var catcher = _partnerPickerRoot.GetComponent<PartnerPickerClickCatcher>();
            if (catcher is null)
            {
                catcher = _partnerPickerRoot.AddComponent<PartnerPickerClickCatcher>();
            }
            catcher.Panel = this;

            RectTransform panelRect = TryFindCommonAncestorRect(mainText?.rectTransform,
                cancel?.GetComponent<RectTransform>());
            if (panelRect is null)
            {
                panelRect = mainText is not null ? mainText.rectTransform.parent as RectTransform : null;
            }
            if (panelRect is null)
            {
                panelRect = rootRect;
            }
            if (panelRect is null)
            {
                Destroy(_partnerPickerRoot);
                _partnerPickerRoot = null;
                _partnerPickerBuildError = "找不到可挂载列表的面板 RectTransform";
                return;
            }

            TextMeshProUGUI pickerTextTemplate = mainText ?? subText;
            if (pickerTextTemplate is null)
            {
                Destroy(_partnerPickerRoot);
                _partnerPickerRoot = null;
                _partnerPickerBuildError = "无法获取文字模板";
                return;
            }

            {
                GameObject scrollGo = new GameObject("PartnerScroll");
                scrollGo.transform.SetParent(panelRect, false);
                scrollGo.transform.SetAsLastSibling();

                var scrollRt = scrollGo.AddComponent<RectTransform>();
                scrollRt.anchorMin = new Vector2(0.15f, 0.42f);
                scrollRt.anchorMax = new Vector2(0.85f, 0.58f);
                scrollRt.offsetMin = Vector2.zero;
                scrollRt.offsetMax = Vector2.zero;

                var scrollImg = scrollGo.AddComponent<Image>();
                scrollImg.color = new Color(0f, 0f, 0f, 0f);
                scrollImg.raycastTarget = false;

                var scrollRect = scrollGo.AddComponent<ScrollRect>();
                scrollRect.horizontal = false;
                scrollRect.vertical = true;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;

                GameObject viewport = new GameObject("Viewport");
                viewport.transform.SetParent(scrollGo.transform, false);
                var viewportRt = viewport.AddComponent<RectTransform>();
                viewportRt.anchorMin = Vector2.zero;
                viewportRt.anchorMax = Vector2.one;
                viewportRt.offsetMin = Vector2.zero;
                viewportRt.offsetMax = Vector2.zero;
                viewport.AddComponent<RectMask2D>();

                GameObject contentGo = new GameObject("Content");
                contentGo.transform.SetParent(viewport.transform, false);
                var contentRt = contentGo.AddComponent<RectTransform>();
                contentRt.anchorMin = new Vector2(0f, 1f);
                contentRt.anchorMax = new Vector2(1f, 1f);
                contentRt.pivot = new Vector2(0.5f, 1f);
                contentRt.sizeDelta = new Vector2(0f, 0f);

                var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
                vlg.childAlignment = TextAnchor.UpperCenter;
                vlg.spacing = 140f;
                vlg.padding = new RectOffset(10, 10, 4, 4);
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;

                var csf = contentGo.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                scrollRect.viewport = viewportRt;
                scrollRect.content = contentRt;

                var tag = contentGo.AddComponent<PartnerPickerTag>();
                tag.TextTemplate = pickerTextTemplate;
                tag.ListContainer = contentRt;
                tag.ScrollRect = scrollRect;
                tag.EmptyText = subText;
            }

            if (cancel is not null)
            {
                cancel.transform.SetAsLastSibling();
            }

            PartnerPickerTag tagCheck = _partnerPickerRoot.GetComponentInChildren<PartnerPickerTag>(true);
            if (tagCheck is null || tagCheck.TextTemplate is null)
            {
                Destroy(_partnerPickerRoot);
                _partnerPickerRoot = null;
                _partnerPickerBuildError = "列表模板创建失败";
                return;
            }

            dialog.enabled = false;
        }
        catch
        {
            if (string.IsNullOrWhiteSpace(_partnerPickerBuildError))
            {
                _partnerPickerBuildError = "构建交易对象选择界面时发生异常";
            }
            if (_partnerPickerRoot is not null)
            {
                Destroy(_partnerPickerRoot);
            }
            _partnerPickerRoot = null;
        }
    }

    private void OnPartnerPickerRefreshClicked()
    {
        if (!_partnerPickerActive || _partnerPickerRoot is null)
        {
            return;
        }

        RebuildPartnerPickerList();

        if (!OtherPlayersOverlayPatch.TryGetSelfLocation(out _, out _, out _, out _))
        {
            TryShowPartnerPickerWaiting();

            if (_partnerPickerAutoRefreshCo is not null)
            {
                StopCoroutine(_partnerPickerAutoRefreshCo);
                _partnerPickerAutoRefreshCo = null;
            }

            _partnerPickerAutoRefreshCo = StartCoroutine(CoPartnerPickerAutoRefreshOnce());
        }
    }

    private static void CopyRectTransform(RectTransform dst, RectTransform src)
    {
        if (dst is null || src is null)
        {
            return;
        }

        dst.anchorMin = src.anchorMin;
        dst.anchorMax = src.anchorMax;
        dst.pivot = src.pivot;
        dst.anchoredPosition = src.anchoredPosition;
        dst.sizeDelta = src.sizeDelta;
        dst.offsetMin = src.offsetMin;
        dst.offsetMax = src.offsetMax;
        dst.localScale = src.localScale;
    }

    private static T GetDialogField<T>(MessageDialog dialog, string fieldName) where T : class
    {
        if (dialog is null || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        FieldInfo fi = typeof(MessageDialog).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (fi is null)
        {
            return null;
        }

        return fi.GetValue(dialog) as T;
    }

    private static T GetPrivateFieldValue<T>(object target, string fieldName) where T : class
    {
        if (target is null || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        Type t = target.GetType();
        while (t is not null)
        {
            FieldInfo fi = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi is not null)
            {
                return fi.GetValue(target) as T;
            }

            t = t.BaseType;
        }

        return null;
    }

    private static RectTransform TryFindCommonAncestorRect(RectTransform a, RectTransform b)
    {
        if (a is null || b is null)
        {
            return null;
        }

        HashSet<Transform> ancestors = new HashSet<Transform>();
        Transform t = a;
        while (t is not null)
        {
            ancestors.Add(t);
            t = t.parent;
        }

        Transform u = b;
        while (u is not null)
        {
            if (ancestors.Contains(u))
            {
                return u as RectTransform;
            }
            u = u.parent;
        }

        return null;
    }

    private void RebuildPartnerPickerList()
    {
        if (_partnerPickerRoot is null)
        {
            return;
        }

        PartnerPickerTag tag = _partnerPickerRoot.GetComponentInChildren<PartnerPickerTag>(true);
        if (tag is null || tag.TextTemplate is null)
        {
            return;
        }

        Transform container = tag.ListContainer is not null ? tag.ListContainer : tag.transform;
        foreach (Transform child in container)
        {
            Destroy(child.gameObject);
        }

        string selfId = _selfPlayerId ?? NetworkIdentityTracker.GetSelfPlayerId();

        var players = OtherPlayersOverlayPatch.SnapshotPlayersDetailed();

        bool hasSelfLoc = OtherPlayersOverlayPatch.TryGetSelfLocation(out int selfStage, out int selfX, out int selfY, out string selfLocName);

        if (hasSelfLoc && IsLocalDebugTradeAllowed())
        {
            if (players.All(p => !string.Equals(p.PlayerId, "aidefault", StringComparison.OrdinalIgnoreCase)))
            {
                string loc = IsShopLikeLocation(selfLocName) ? selfLocName : "Trade";
                players.Add(("aidefault", "AI Default", true, false, selfStage, selfX, selfY, loc, null));
            }

            if (players.All(p => !string.Equals(p.PlayerId, "aidefault2", StringComparison.OrdinalIgnoreCase)))
            {
                string loc = IsShopLikeLocation(selfLocName) ? selfLocName : "Trade";
                players.Add(("aidefault2", "AI Default 2", true, false, selfStage, selfX, selfY, loc, null));
            }

            if (players.All(p => !string.Equals(p.PlayerId, "aidefault3", StringComparison.OrdinalIgnoreCase)))
            {
                string loc = IsShopLikeLocation(selfLocName) ? selfLocName : "Trade";
                players.Add(("aidefault3", "AI Default 3", true, false, selfStage, selfX, selfY, loc, null));
            }
        }

            List<(string PlayerId, string PlayerName, bool IsConnected, bool IsHost, int Stage, int LocationX, int LocationY, string LocationName, string CharacterId)> connectedOthers = players
                .Where(p => !string.IsNullOrWhiteSpace(p.PlayerId))
                .Where(p => !string.Equals(p.PlayerId, selfId, StringComparison.OrdinalIgnoreCase))
                .Where(p => p.IsConnected)
                .ToList();

            List<(string PlayerId, string PlayerName, bool IsConnected, bool IsHost, int Stage, int LocationX, int LocationY, string LocationName, string CharacterId)> candidates = connectedOthers
                .Where(p => IsShopLikeLocation(p.LocationName))
                .ToList();

            if (!hasSelfLoc)
            {
                candidates.Clear();
            }
            else
            {
                candidates = candidates
                    .Where(p => p.Stage >= 0 && selfStage >= 0 && p.Stage == selfStage
                                && p.LocationX >= 0 && selfX >= 0 && p.LocationX == selfX
                                && p.LocationY >= 0 && selfY >= 0 && p.LocationY == selfY)
                    .ToList();
            }

            if (candidates.Count == 0)
            {

                tag.ScrollRect?.gameObject.SetActive(false);

                if (tag.EmptyText is not null)
                {
                    tag.EmptyText.gameObject.SetActive(true);
                    tag.EmptyText.raycastTarget = true;
                    tag.EmptyText.text = hasSelfLoc
                        ? "暂无同节点玩家"
                        : "无法获取自身位置，暂不显示可交易玩家";
                    tag.EmptyText.alignment = TextAlignmentOptions.Center;
                    var c = tag.EmptyText.color;
                    c.a = 1f;
                    tag.EmptyText.color = c;
                }
                return;
            }

            if (tag.EmptyText is not null)
            {
                tag.EmptyText.text = string.Empty;
                tag.EmptyText.raycastTarget = false;
                tag.EmptyText.gameObject.SetActive(false);
            }
            tag.ScrollRect?.gameObject.SetActive(true);

            foreach (var p in candidates)
            {
                string displayName = string.IsNullOrWhiteSpace(p.PlayerName) ? p.PlayerId : p.PlayerName;

                bool sameNode = hasSelfLoc
                    && (p.Stage < 0 || selfStage < 0 || p.Stage == selfStage)
                    && (p.LocationX < 0 || selfX < 0 || p.LocationX == selfX)
                    && (p.LocationY < 0 || selfY < 0 || p.LocationY == selfY);

                string loc2 = string.IsNullOrWhiteSpace(p.LocationName) ? "?" : p.LocationName;
                string coord = (p.Stage >= 0 || p.LocationX >= 0 || p.LocationY >= 0) ? $"Act {p.Stage}, ({p.LocationX},{p.LocationY})" : "位置未知";

                string label = string.IsNullOrWhiteSpace(coord) ? displayName : $"{displayName}  {loc2} {coord}";
                if (sameNode) label += " - 同节点";
                if (p.IsHost) label += " [Host]";

                Button btn = CreateTextButton(tag.TextTemplate, container, $"Player_{p.PlayerId}", label, tag.TextTemplate.fontSize * 0.5f);

                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 36f;
                le.flexibleWidth = 1f;

                var cand = btn.gameObject.AddComponent<PartnerCandidateTag>();
                cand.PlayerId = p.PlayerId;
                cand.PlayerName = displayName;

                string pid = p.PlayerId;
                string pname = displayName;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() =>
                {
                    Plugin.Logger?.LogInfo($"[TradePanel] Candidate button clicked: pid={pid}, pname={pname}");
                    OnPartnerSelected(pid, pname);
                });
            }
    }

    private void OnPartnerSelected(string partnerPlayerId, string partnerPlayerName)
    {
        string realSelfId = NetworkIdentityTracker.GetSelfPlayerId();
        if (!string.IsNullOrWhiteSpace(realSelfId))
        {
            _selfPlayerId = realSelfId;
        }

        Plugin.Logger?.LogInfo($"[TradePanel] OnPartnerSelected: tradeId={_tradeId ?? "<null>"}, self={_selfPlayerId ?? "<null>"}, partner={partnerPlayerId ?? "<null>"}, partnerName={partnerPlayerName ?? "<null>"}");
        _playerAId = _selfPlayerId;
        _playerBId = partnerPlayerId;

        if (string.IsNullOrWhiteSpace(_tradeId))
        {
            _tradeId = Guid.NewGuid().ToString("N");
        }

        if (player1NameText is not null) player1NameText.text = ResolveLocalPlayerDisplayName(_payload);
        if (player2NameText is not null) player2NameText.text = OtherPlayersOverlayPatch.ResolveDisplayName(partnerPlayerId, partnerPlayerName, isLocal: false);

        HidePartnerPickerOverlay();
        transform.Find("NetworkPlugin_TradePanel_Frame")?.gameObject.SetActive(true);

        if (IsLocalDebugTradeAllowed() && (!string.IsNullOrWhiteSpace(partnerPlayerId) && partnerPlayerId.StartsWith("aidefault", StringComparison.OrdinalIgnoreCase)))
        {

            _localDebugTradeMode = true;
            Plugin.Logger?.LogInfo($"[TradePanel] OnPartnerSelected: local debug shortcut for {partnerPlayerId}");
            PopulateLocalDebugRemoteOffer();
            EnsureOfferEditorOverlay();
            EnsureCardPickerOverlay();
            EnsureOfferPreviewOverlay();
            EnsureExhibitPickerOverlay();
            SetTradeDetailsVisible(true);
            cancelButton?.gameObject.SetActive(_canCancel);
            UpdateUIStatus($"本地调试交易：{partnerPlayerName}（不走服务器）");
            return;
        }

        EnsureOfferEditorOverlay();
        EnsureCardPickerOverlay();
        EnsureOfferPreviewOverlay();
        EnsureExhibitPickerOverlay();
        SetTradeDetailsVisible(true);
        cancelButton?.gameObject.SetActive(_canCancel);

        if (TryIsNetworkTrade(out _))
        {
            TrySubscribeTradeEvents();
            TradeSyncPatch.RequestStartTrade(_tradeId, _playerAId, _playerBId, _maxTradeSlots);
            TradeSyncPatch.RequestSnapshot(_tradeId, _selfPlayerId);
        }
        else
        {
            UpdateUIStatus("本地调试交易：未连接服务器");
        }
    }

    private void PopulateLocalDebugRemoteOffer()
    {
        GameRunController run = ActiveGameRun;

        using (new ApplyingStateScope(this))
        {

            var deck = run?.BaseDeck?.Where(c => c is not null).ToList() ?? new List<Card>();
            deck.Take(2)
                .Where(src => src is not null)
                .Select(src => Library.TryCreateCard(src.Id, src.IsUpgraded, src.UpgradeCounter ?? 0))
                .Where(temp => temp is not null)
                .ToList()
                .ForEach(temp => AddCardToTrade(temp, false));

            _localMoneyOffer = Math.Min(10, run?.Money ?? 10);
            RefreshOfferEditorTexts();
            CheckTradeReady();
        }
    }

    private string ResolveLocalPlayerDisplayName(TradePayload payload)
    {
        string id = _selfPlayerId;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = payload?.Player1Id;
        }

        return OtherPlayersOverlayPatch.ResolveDisplayName(id, payload?.Player1Name, isLocal: true);
    }

    private string ResolvePartnerDisplayName(TradePayload payload)
    {

        string id = _playerBId;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = payload?.Player2Id;
        }

        return OtherPlayersOverlayPatch.ResolveDisplayName(id, payload?.Player2Name, isLocal: false);
    }

    private static bool IsShopLikeLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
        {
            return false;
        }

        return locationName.IndexOf("shop", StringComparison.OrdinalIgnoreCase) >= 0
            || locationName.IndexOf("trade", StringComparison.OrdinalIgnoreCase) >= 0
            || locationName.IndexOf("gap", StringComparison.OrdinalIgnoreCase) >= 0
            || locationName.IndexOf("商店", StringComparison.OrdinalIgnoreCase) >= 0
            || locationName.IndexOf("交易", StringComparison.OrdinalIgnoreCase) >= 0
            || locationName.IndexOf("间隙", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private sealed class PartnerPickerTag : MonoBehaviour
    {
        public RecordRow RecordRowTemplate;
        public TextMeshProUGUI EmptyText;
        public ScrollRect ScrollRect;
        public Button ButtonTemplate;
        public RectTransform ButtonContainer;
        public TextMeshProUGUI TextTemplate;
        public RectTransform ListContainer;
    }

    private sealed class PartnerCandidateTag : MonoBehaviour
    {
        public string PlayerId;
        public string PlayerName;
    }

    private sealed class PartnerPickerClickCatcher : MonoBehaviour, IPointerClickHandler
    {
        public TradePanel Panel;

        private static bool Contains(RectTransform rt, Vector2 screenPoint)
        {
            if (rt is null)
            {
                return false;
            }

            try
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, CameraController.UiCamera))
                {
                    return true;
                }
            }
            catch
            {

            }

            return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, null);
        }

        private static bool TryGetLeftClickThisFrame(out Vector2 screenPos)
        {
            screenPos = default;

            try
            {
                var mouse = Mouse.current;
                if (mouse is not null && mouse.leftButton is not null && mouse.leftButton.wasPressedThisFrame)
                {
                    screenPos = mouse.position.ReadValue();
                    return true;
                }
            }
            catch
            {

            }

            try
            {
                if (Input.GetMouseButtonDown(0))
                {
                    screenPos = Input.mousePosition;
                    return true;
                }
            }
            catch
            {

            }

            return false;
        }

        private void Update()
        {
            try
            {
                if (Panel is null || !Panel._partnerPickerActive || Panel._partnerPickerRoot is null)
                {
                    return;
                }

                if (Time.unscaledTime < Panel._partnerPickerInputReadyTime)
                {
                    return;
                }

                if (!TryGetLeftClickThisFrame(out Vector2 pos))
                {
                    return;
                }

                if (Panel._partnerPickerCancelButton is not null)
                {
                    RectTransform cancelRt = Panel._partnerPickerCancelButton.transform as RectTransform;
                    if (Contains(cancelRt, pos))
                    {
                        return;
                    }
                }

                PartnerPickerTag pickerTag = Panel._partnerPickerRoot.GetComponentInChildren<PartnerPickerTag>(true);
                if (pickerTag is null)
                {
                    return;
                }

                RectTransform listRegion = null;
                if (pickerTag.ListContainer is not null)
                {
                    listRegion = pickerTag.ListContainer;
                }
                else if (pickerTag.ScrollRect is not null)
                {
                    listRegion = pickerTag.ScrollRect.GetComponent<RectTransform>();
                }
                else
                {
                    listRegion = pickerTag.transform as RectTransform;
                }

                if (listRegion is not null && !Contains(listRegion, pos))
                {
                    return;
                }

                Transform container = pickerTag.ListContainer is not null ? pickerTag.ListContainer : pickerTag.transform;

                if (container is null)
                {
                    return;
                }

                var candidates = container.GetComponentsInChildren<PartnerCandidateTag>(true);
                for (int i = candidates.Length - 1; i >= 0; i--)
                {
                    var cand = candidates[i];
                    if (cand is null || string.IsNullOrWhiteSpace(cand.PlayerId))
                    {
                        continue;
                    }

                    RectTransform rt = cand.transform as RectTransform;
                    if (Contains(rt, pos))
                    {
                        Panel.OnPartnerSelected(cand.PlayerId, cand.PlayerName);
                        return;
                    }

                    foreach (var g in cand.GetComponentsInChildren<Graphic>(true))
                    {
                        if (g is null)
                        {
                            continue;
                        }

                        if (Contains(g.rectTransform, pos))
                        {
                            Panel.OnPartnerSelected(cand.PlayerId, cand.PlayerName);
                            return;
                        }
                    }
                }
            }
            catch
            {

            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            try
            {
                if (eventData is null || eventData.button != PointerEventData.InputButton.Left)
                {
                    return;
                }

                if (Panel is null || !Panel._partnerPickerActive || Panel._partnerPickerRoot is null)
                {
                    return;
                }

                if (Time.unscaledTime < Panel._partnerPickerInputReadyTime)
                {
                    return;
                }

                try
                {
                    if (Panel._partnerPickerCancelButton is not null)
                    {
                        var press = eventData.pointerPress;
                        if (press is not null && press.transform is not null && press.transform.IsChildOf(Panel._partnerPickerCancelButton.transform))
                        {
                            return;
                        }

                        var hit = eventData.pointerCurrentRaycast.gameObject;
                        if (hit is not null && hit.transform is not null && hit.transform.IsChildOf(Panel._partnerPickerCancelButton.transform))
                        {
                            return;
                        }
                    }
                }
                catch
                {

                }

                PartnerPickerTag pickerTag = Panel._partnerPickerRoot.GetComponentInChildren<PartnerPickerTag>(true);
                if (pickerTag is null)
                {
                    return;
                }

                Transform container = pickerTag.ListContainer is not null ? pickerTag.ListContainer : pickerTag.transform;

                if (container is null)
                {
                    return;
                }

                var candidates = container.GetComponentsInChildren<PartnerCandidateTag>(true);
                foreach (var cand in candidates)
                {
                    if (cand is null || string.IsNullOrWhiteSpace(cand.PlayerId))
                    {
                        continue;
                    }

                    RectTransform rt = cand.transform as RectTransform;
                    if (rt is null || !rt.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Camera cam;
                    try
                    {
                        cam = eventData.pressEventCamera ?? CameraController.UiCamera;
                    }
                    catch
                    {
                        cam = null;
                    }

                    if (RectTransformUtility.RectangleContainsScreenPoint(rt, eventData.position, cam)
                        || RectTransformUtility.RectangleContainsScreenPoint(rt, eventData.position, null))
                    {
                        Plugin.Logger?.LogInfo($"[TradePanel] ClickCatcher hit candidate: pid={cand.PlayerId}, pname={cand.PlayerName}");
                        Panel.OnPartnerSelected(cand.PlayerId, cand.PlayerName);
                        return;
                    }

                    foreach (var g in cand.GetComponentsInChildren<Graphic>(true))
                    {
                        if (g is null)
                        {
                            continue;
                        }

                        var grt = g.rectTransform;
                        if (grt is null)
                        {
                            continue;
                        }

                        if (RectTransformUtility.RectangleContainsScreenPoint(grt, eventData.position, cam)
                            || RectTransformUtility.RectangleContainsScreenPoint(grt, eventData.position, null))
                        {
                            Plugin.Logger?.LogInfo($"[TradePanel] ClickCatcher hit candidate graphic: pid={cand.PlayerId}, pname={cand.PlayerName}");
                            Panel.OnPartnerSelected(cand.PlayerId, cand.PlayerName);
                            return;
                        }
                    }
                }
            }
            catch
            {

            }
        }
    }
}
