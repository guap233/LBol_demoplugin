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
#region 展品预览栏

    private void EnsureExhibitPreviewContainers()
    {
        player1TradeArea ??= _localOfferPreviewPanel?.transform as RectTransform;
        player2TradeArea ??= _remoteOfferPreviewPanel?.transform as RectTransform;
        if (_localExhibitContainer != null) return;
        if (player1TradeArea == null || player2TradeArea == null) return;

        if (_exhibitIconTemplate == null)
        {
            try
            {
                var systemBoard = UiManager.GetPanel<SystemBoard>();
                if (systemBoard != null)
                {
                    var templateField = typeof(SystemBoard).GetField("exhibitTemplate",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (templateField != null)
                        _exhibitIconTemplate = templateField.GetValue(systemBoard) as ExhibitWidget;
                }
            }
            catch (Exception ex) { Plugin.Logger?.LogWarning($"[TradePanel] 获取 ExhibitTemplate 失败: {ex.Message}"); }
        }

        _localExhibitContainer = new GameObject("LocalExhibitPreviews");
        _localExhibitContainer.transform.SetParent(player1TradeArea, false);
        var lRt = _localExhibitContainer.AddComponent<RectTransform>();
        lRt.anchorMin = new Vector2(0f, 0f);
        lRt.anchorMax = new Vector2(1f, 0f);
        lRt.pivot = new Vector2(0.5f, 0f);
        lRt.sizeDelta = new Vector2(0f, 30f);
        lRt.anchoredPosition = new Vector2(0f, 6f);

        _remoteExhibitContainer = new GameObject("RemoteExhibitPreviews");
        _remoteExhibitContainer.transform.SetParent(player2TradeArea, false);
        var rRt = _remoteExhibitContainer.AddComponent<RectTransform>();
        rRt.anchorMin = new Vector2(0f, 0f);
        rRt.anchorMax = new Vector2(1f, 0f);
        rRt.pivot = new Vector2(0.5f, 0f);
        rRt.sizeDelta = new Vector2(0f, 30f);
        rRt.anchoredPosition = new Vector2(0f, 6f);
    }

    private void RebuildExhibitPreviews()
    {
        EnsureExhibitPreviewContainers();

        RebuildSideExhibitPreviews(_localExhibitContainer, true);
        RebuildSideExhibitPreviews(_remoteExhibitContainer, false);
    }

    private void RebuildSideExhibitPreviews(GameObject container, bool isLocal)
    {
        if (container == null) return;

        foreach (Transform child in container.transform)
            Destroy(child.gameObject);

        GameRunController run = ActiveGameRun;
        List<Exhibit> exhibits = new List<Exhibit>();

        if (isLocal)
        {
            if (run?.Player?.Exhibits != null)
            {
                exhibits = run.Player.Exhibits
                    .Where(e => e != null && _localExhibitOfferIds.Contains(e.Id))
                    .ToList();
            }
        }
        else
        {

            TradeSyncPatch.TradeSessionState state = TradeSyncPatch.GetLastKnown(_tradeId);
            if (state != null)
            {
                bool localIsA = IsPlayerA(state);
                var remoteExhibitRefs = localIsA ? state.ExhibitsB : state.ExhibitsA;
                if (remoteExhibitRefs != null)
                {
                    foreach (var exRef in remoteExhibitRefs)
                    {
                        if (exRef != null && !string.IsNullOrWhiteSpace(exRef.ExhibitId))
                        {
                            Exhibit ex = null;
                            if (run?.Player?.Exhibits != null)
                            {
                                ex = run.Player.Exhibits.FirstOrDefault(e => e != null && string.Equals(e.Id, exRef.ExhibitId, StringComparison.Ordinal));
                            }
                            ex ??= Library.TryCreateExhibit(exRef.ExhibitId);
                            if (ex != null)
                            {
                                exhibits.Add(ex);
                            }
                        }
                    }
                }
            }
        }

        if (exhibits.Count == 0) return;

        float iconSize = 26f;
        float spacing = 4f;
        float totalWidth = exhibits.Count * iconSize + (exhibits.Count - 1) * spacing;
        float startX = -totalWidth * 0.5f + iconSize * 0.5f;

        for (int i = 0; i < exhibits.Count; i++)
        {
            Exhibit exhibit = exhibits[i];
            ExhibitWidget widget = null;

            if (_exhibitIconTemplate != null)
            {
                widget = Instantiate(_exhibitIconTemplate, container.transform, false);
                widget.Exhibit = exhibit;
                widget.ShowBattleStatus = false;
                widget.ShowCounter = false;
            }
            else
            {
                var iconGo = new GameObject($"ExIcon_{exhibit.Id}");
                iconGo.transform.SetParent(container.transform, false);
                var img = iconGo.AddComponent<Image>();
                img.preserveAspect = true;
                img.raycastTarget = true;
                Sprite sprite = null;
                try { sprite = ResourcesHelper.TryGetSprite<Exhibit>(exhibit.Id); }
                catch (Exception ex) { Plugin.Logger?.LogWarning($"[TradePanel] 加载展品图标失败: ExhibitId={exhibit.Id}, {ex.Message}"); }
                if (sprite != null) img.sprite = sprite;

                var commonBtn = iconGo.AddComponent<CommonButtonWidget>();
                var btnField = typeof(CommonButtonWidget).GetField("button", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (btnField != null)
                {
                    var btn = iconGo.AddComponent<Button>();
                    btn.targetGraphic = img;
                    btnField.SetValue(commonBtn, btn);
                }

                widget = iconGo.AddComponent<ExhibitWidget>();
                var widgetExhibitField = typeof(ExhibitWidget).GetField("image", BindingFlags.Instance | BindingFlags.NonPublic);
                if (widgetExhibitField != null)
                    widgetExhibitField.SetValue(widget, img);
                widget.Exhibit = exhibit;
            }

            var rt = widget.transform as RectTransform;
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(iconSize, iconSize);
                rt.anchoredPosition = new Vector2(startX + i * (iconSize + spacing), 0f);
                rt.localScale = Vector3.one;
            }
        }
    }

    #endregion

    private void SetRect(RectTransform rt, float minX, float minY, float maxX, float maxY)
    {
        rt.anchorMin = new Vector2(minX, minY);
        rt.anchorMax = new Vector2(maxX, maxY);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private GameObject _exhibitPickerRoot;

    private void ShowExhibitPickerOverlay()
    {
        EnsureExhibitPickerOverlay();
        if (_exhibitPickerRoot is null)
        {
            TryShowTopMessage("展品选择界面不可用。");
            return;
        }

        RebuildExhibitPickerList();
        _exhibitPickerRoot.SetActive(true);
        ForceEnableRaycasts(_exhibitPickerRoot);
        SetTradeDetailsVisible(false);
        EnsurePopupTopmost();
    }

    private void HideExhibitPickerOverlay(bool apply)
    {
        _exhibitPickerRoot?.SetActive(false);
        SetTradeDetailsVisible(true);

        if (apply)
        {
            RefreshOfferEditorTexts();
            TrySendOfferUpdate();
            CheckTradeReady();
        }
    }

    private void EnsureExhibitPickerOverlay()
    {
        if (_exhibitPickerRoot is not null)
        {
            return;
        }

        try
        {
            RuntimeSelectionPanelFactory.SelectionPanelScaffold scaffold = RuntimeSelectionPanelFactory.CreateModal(
                transform,
                "TradeExhibitPicker",
                "选择要交易的展品",
                "确定",
                "取消",
                showConfirmButton: true);
            if (scaffold is null)
            {
                _exhibitPickerRoot = null;
                return;
            }

            _exhibitPickerRoot = scaffold.Root;

            if (scaffold.ScrollRect is not null)
            {
                RectTransform scrollRt = scaffold.ScrollRect.GetComponent<RectTransform>();
                if (scrollRt is not null)
                {
                    scrollRt.anchorMin = new Vector2(0.5f, scrollRt.anchorMin.y);
                    scrollRt.anchorMax = new Vector2(0.5f, scrollRt.anchorMax.y);
                    scrollRt.anchoredPosition = new Vector2(0f, scrollRt.anchoredPosition.y);
                    scrollRt.sizeDelta = new Vector2(1100f, scrollRt.sizeDelta.y);
                }
            }

            if (scaffold.ConfirmButton is not null)
            {
                scaffold.ConfirmButton.onClick = new Button.ButtonClickedEvent();
                scaffold.ConfirmButton.onClick.AddListener(() => HideExhibitPickerOverlay(true));
            }

            if (scaffold.CancelButton is not null)
            {
                scaffold.CancelButton.onClick = new Button.ButtonClickedEvent();
                scaffold.CancelButton.onClick.AddListener(() => HideExhibitPickerOverlay(false));
            }

            ExhibitPickerTag tag = _exhibitPickerRoot.AddComponent<ExhibitPickerTag>();
            tag.Content = scaffold.Content;
            tag.ScrollRect = scaffold.ScrollRect;
            tag.EmptyText = scaffold.EmptyText;
            tag.RowButtonTemplate = scaffold.RowButtonTemplate;
            tag.TextTemplate = scaffold.TextTemplate;
        }
        catch
        {
            _exhibitPickerRoot = null;
        }
    }

    private sealed class ExhibitPickerTag : MonoBehaviour
    {
        public RectTransform Content;
        public ScrollRect ScrollRect;
        public TextMeshProUGUI EmptyText;
        public CommonButtonWidget RowButtonTemplate;
        public TextMeshProUGUI TextTemplate;
    }

    private void RebuildExhibitPickerList()
    {
        if (_exhibitPickerRoot is null)
        {
            return;
        }

        ExhibitPickerTag tag = _exhibitPickerRoot.GetComponent<ExhibitPickerTag>();
        if (tag is null || tag.Content is null)
        {
            return;
        }

        foreach (Transform child in tag.Content)
        {
            Destroy(child.gameObject);
        }

        GameRunController run = ActiveGameRun;
        List<Exhibit> tradable = new List<Exhibit>();
        try
        {
            if (run?.Player?.Exhibits is not null)
            {
                tradable = run.Player.Exhibits
                    .Where(TradeExhibitRules.IsTradable)
                    .OrderBy(e => e.Name)
                    .ToList();
            }
        }
        catch
        {
            tradable = new List<Exhibit>();
        }

        if (tradable.Count == 0)
        {
            Plugin.Logger?.LogInfo($"[TradePanel] Exhibit picker empty: panelGameRun={(GameRun != null)}, activeGameRun={(run != null)}, exhibitCount={(run?.Player?.Exhibits?.Count ?? 0)}, selectedCount={_localExhibitOfferIds.Count}, tradeId={_tradeId ?? "<null>"}");
            tag.ScrollRect?.gameObject.SetActive(false);
            if (tag.EmptyText != null)
            {
                tag.EmptyText.gameObject.SetActive(true);
                tag.EmptyText.text = "没有可交易的展品。";
                Color c2 = tag.EmptyText.color;
                c2.a = 1f;
                tag.EmptyText.color = c2;
            }
            return;
        }

        if (tag.EmptyText != null)
        {
            tag.EmptyText.gameObject.SetActive(true);
            tag.EmptyText.text = string.Empty;
            Color c3 = tag.EmptyText.color;
            c3.a = 0f;
            tag.EmptyText.color = c3;
        }
        tag.ScrollRect?.gameObject.SetActive(true);

        foreach (var ex in tradable)
        {
            CreateExhibitRecordRow(tag, ex);
        }
    }

    private void CreateExhibitRecordRow(ExhibitPickerTag tag, Exhibit exhibit)
    {
        try
        {
            if (tag?.Content is null || exhibit is null)
            {
                return;
            }

            bool selected = _localExhibitOfferIds.Contains(exhibit.Id);
            Sprite sprite = null;
            try
            {
                sprite = ResourcesHelper.TryGetSprite<Exhibit>(exhibit.Id);
            }
            catch
            {
                sprite = null;
            }

            RuntimeSelectionPanelFactory.RuntimeSelectionRow row = RuntimeSelectionPanelFactory.CreateRow(
                tag.Content,
                tag.RowButtonTemplate,
                tag.TextTemplate,
                $"Ex_{exhibit.Id}",
                exhibit.Name,
                BuildExhibitSecondaryText(exhibit.Id, selected),
                sprite,
                selected,
                interactable: true);
            if (row is null)
            {
                return;
            }

            row.Button.onClick = new Button.ButtonClickedEvent();
            row.Button.onClick.AddListener(() =>
            {
                bool nowSelected = !_localExhibitOfferIds.Contains(exhibit.Id);
                if (nowSelected)
                    _localExhibitOfferIds.Add(exhibit.Id);
                else
                    _localExhibitOfferIds.Remove(exhibit.Id);

                row.SetSelected(nowSelected);
                row.SetSecondaryText(BuildExhibitSecondaryText(exhibit.Id, nowSelected));
                RefreshOfferEditorTexts();
            });
        }
        catch
        {

        }
    }

    private static string BuildExhibitSecondaryText(string exhibitId, bool selected)
    {
        return selected ? $"{exhibitId} · 已选择" : exhibitId;
    }

    private static void SetButtonText(CommonButtonWidget button, string label)
    {
        if (button is null)
            return;

        var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp is not null)
        {
            tmp.text = label;
            tmp.alignment = TextAlignmentOptions.Center;
        }
    }

    private readonly struct ApplyingStateScope : IDisposable
    {
        private readonly TradePanel _panel;
        private readonly bool _prev;

        public ApplyingStateScope(TradePanel panel)
        {
            _panel = panel;
            _prev = panel._isApplyingState;
            panel._isApplyingState = true;
        }

        public void Dispose()
        {
            if (_panel is not null) _panel._isApplyingState = _prev;
        }
    }

        public IEnumerator ShowTradeAsync(TradePayload payload)
    {
        Plugin.Logger?.LogInfo($"[TradePanel] ShowTradeAsync enter: payloadNull={(payload is null)}, isVisibleBefore={IsVisible}, gameObjectActiveSelf={gameObject.activeSelf}, activeInHierarchy={gameObject.activeInHierarchy}");

        Show(payload);
        Plugin.Logger?.LogInfo($"[TradePanel] ShowTradeAsync after Show: isVisibleAfter={IsVisible}, gameObjectActiveSelf={gameObject.activeSelf}, activeInHierarchy={gameObject.activeInHierarchy}");

        yield return new WaitWhile(() => IsVisible);
        Plugin.Logger?.LogInfo("[TradePanel] ShowTradeAsync exit: panel hidden.");
    }
}
