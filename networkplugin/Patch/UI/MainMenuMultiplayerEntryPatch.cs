using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using LBoL.Core;
using LBoL.Core.SaveData;
using LBoL.Core.Units;
using LBoL.Presentation;
using LBoL.Presentation.UI;
using LBoL.Presentation.UI.Dialogs;
using LBoL.Presentation.UI.Panels;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Configuration;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.Server;
using NetworkPlugin.Network.Messages;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Core;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NetworkPlugin.Patch.UI;

public static class MainMenuMultiplayerEntryPatch
{
    public static void ForceEnsureButtonInCurrentScene()
    {
        try
        {
            if (_multiplayerButton != null)
            {
                return;
            }

            MainMenuPanel panel = _lastMainMenuPanel;
            if (panel == null || !panel.gameObject.activeInHierarchy)
            {
                panel = UnityEngine.Object.FindObjectOfType<MainMenuPanel>(true);
            }

            if (panel != null && panel.gameObject.activeInHierarchy)
            {
                EnsureMultiplayerButton(panel);
            }
        }
        catch
        {

        }
    }

    #region 常量与字段

    private const string MultiplayerButtonName = "NetworkPlugin_MultiplayerButton";
    private const string LegacySubMenuMultiplayerButtonName = "NetworkPlugin_SubMenuMultiplayerButton";

    private const string OverlayRootName = "NetworkPlugin_MultiplayerOverlay";
    private const string StartGameMultiplayerButtonName = "NetworkPlugin_StartGameMultiplayerButton";

    private static Button _multiplayerButton;
    private static Button _subMenuMultiplayerButton;
    private static MainMenuPanel _lastMainMenuPanel;
    private static Button _startGameMultiplayerButton;
    private static StartGamePanel _lastStartGamePanel;
    private static bool _isSilentStarting;
    private static GameObject _overlayRoot;
    private static PanelAnimator _rootAnimator;
    private static TMP_FontAsset _defaultFont;

    private const string RoomListRootName = "NetworkPlugin_RoomPlayerListPanel";
    private static GameObject _roomListRoot;
    private static PanelAnimator _roomListAnimator;
    private static TMP_FontAsset _roomListFont;
    private static RectTransform _roomListFrameRect;
    private static ScrollRect _roomListScroll;
    private static Transform _roomListContainer;
    private static TextMeshProUGUI _roomListEmptyText;
    private static bool _roomListEventSubscribed;
    private static float _roomListPanelScale = 2.2f;
    private static INetworkClient _roomListSubscribedClient;
    private static readonly Action<string, object> _onRoomListGameEvent = OnRoomListGameEventReceived;
    private static readonly Action<bool> _onRoomListConnStateChanged = OnRoomListConnectionStateChanged;

        private static readonly Dictionary<string, bool> _playerReadyStates = new(StringComparer.Ordinal);

        private static Button _readyOrStartButton;
        private static TextMeshProUGUI _roomListTitleText;
        private static Button _roomListCloseBtn;
        private static bool _lobbyResumeMode;
        private static GameRunSaveData _resumingSave;
        private static bool _clientLobbyResumeMode;
        private static bool _isOldResumePlayer;
        private static ulong _hostResumeRootSeed;

    private const string ConnStatusRootName = "NetworkPlugin_ConnectionStatusPanel";
    private static GameObject _connStatusRoot;
    private static TextMeshProUGUI _connStatusText;
    private static PanelAnimator _connStatusAnimator;
    private static TMP_FontAsset _connStatusFont;

    private static NetworkServer _localServer;
    private static bool _localServerRunning;
    public static bool IsLocalServerRunning => _localServerRunning;
    private static Thread _localServerThread;
    private static CancellationTokenSource _localServerCts;

    private static long _sessionGeneration = 0;
    private static bool _hasEnteredGameRun = false;

    #endregion

    #region 依赖注入获取

        private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

        private static INetworkClient TryGetNetworkClient()
        => ServiceProvider?.GetService<INetworkClient>();

        private static ConfigManager TryGetConfig()
        => ServiceProvider?.GetService<ConfigManager>() ?? Plugin.ConfigManager;

    #endregion

    #region Harmony 嵌套补丁类

    [HarmonyPatch(typeof(MainMenuPanel), "Awake")]
    public static class MainMenuPanel_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MainMenuPanel __instance)
        {
            try
            {
                _lastMainMenuPanel = __instance;
                EnsureMultiplayerButton(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 添加按钮失败: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), "OnShowing")]
    public static class MainMenuPanel_OnShowing_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MainMenuPanel __instance)
        {
            try
            {
                _lastMainMenuPanel = __instance;
                EnsureMultiplayerButton(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] OnShowing 失败: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), "RefreshProfile")]
    public static class MainMenuPanel_RefreshProfile_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MainMenuPanel __instance)
        {
            try
            {
                _lastMainMenuPanel = __instance;
                EnsureMultiplayerButton(__instance);
                _multiplayerButton?.gameObject.SetActive(true);
            }
            catch
            {

            }
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), "OnShown")]
    public static class MainMenuPanel_OnShown_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MainMenuPanel __instance)
        {
            try
            {
                _lastMainMenuPanel = __instance;
                EnsureMultiplayerButton(__instance);
                _multiplayerButton?.gameObject.SetActive(true);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] OnShown 失败: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), "StartMenuButtonAnim")]
    public static class MainMenuPanel_StartMenuButtonAnim_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MainMenuPanel __instance, float delay, int menuType)
        {
            try
            {
                _lastMainMenuPanel = __instance;
                EnsureMultiplayerButton(__instance);
                _multiplayerButton?.gameObject.SetActive(true);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] StartMenuButtonAnim 失败: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), "OnLocaleChanged")]
    public static class MainMenuPanel_OnLocaleChanged_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MainMenuPanel __instance)
        {
            try
            {
                _lastMainMenuPanel = __instance;
                EnsureMultiplayerButton(__instance);
                _multiplayerButton?.gameObject.SetActive(true);
            }
            catch
            {

            }
        }
    }

    [HarmonyPatch(typeof(StartGamePanel), "Awake")]
    public static class StartGamePanel_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(StartGamePanel __instance)
        {
            try
            {
                _lastStartGamePanel = __instance;
                EnsureStartGameMultiplayerButton(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] StartGamePanel_Awake_Postfix 失败: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(StartGamePanel), "OnShowing")]
    public static class StartGamePanel_OnShowing_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(StartGamePanel __instance, StartGameData data)
        {
            try
            {
                _lastStartGamePanel = __instance;
                EnsureStartGameMultiplayerButton(__instance);
                _startGameMultiplayerButton?.gameObject.SetActive(true);
            }
            catch
            {

            }
        }
    }

    [HarmonyPatch(typeof(StartGamePanel), "OnHiding")]
    public static class StartGamePanel_OnHiding_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (_isSilentStarting)
                {
                    Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 正在静默启动中，忽略 OnHiding 断开连接逻辑。");
                    return;
                }

                if (GameMaster.Status != GameMaster.GameMasterStatus.MainMenu)
                {
                    return;
                }

                INetworkClient client = TryGetNetworkClient();
                if (client?.IsConnected != true)
                {
                    return;
                }

                Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 选角面板关闭且仍在主菜单，自动断开联机连接。");
                HideRoomListOverlay();
                Disconnect(client);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] StartGamePanel_OnHiding_Postfix 失败: {ex.Message}");
            }
        }
    }

    #endregion

    #region 按钮构建与文案

        private static void EnsureMultiplayerButton(MainMenuPanel panel)
    {

        if (panel == null)
        {
            return;
        }

        CleanupLegacySubMenuMultiplayerButton(panel);

        if (_multiplayerButton != null)
        {
            if (!_multiplayerButton.gameObject.activeSelf)
            {
                _multiplayerButton.gameObject.SetActive(true);
            }
            TrySetButtonText(_multiplayerButton, "多人游戏");
            return;
        }

        Transform parent = TryGetMainMenuButtonGroup(panel);
        if (parent == null)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 未能定位 mainMenuButtonGroup，无法创建多人入口按钮。");
            return;
        }

        Button template = TryFindButtonByPersistentMethodName(parent, "UI_Settings");
        if (template == null)
        {
            if (!TryFindTemplateButton(panel, out template, out string whyTemplateFailed))
            {
                Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 未能定位主菜单模板按钮，无法创建入口：{whyTemplateFailed}");
                return;
            }
        }

        _defaultFont ??= FindDefaultFont(parent);

        _multiplayerButton = CreateButtonFromTemplate(template, parent, MultiplayerButtonName, "多人游戏");
        _multiplayerButton.name = MultiplayerButtonName;
        _multiplayerButton.interactable = true;

        _multiplayerButton.onClick = new Button.ButtonClickedEvent();
        _multiplayerButton.onClick.AddListener(OpenMultiplayerEntry);

        TryStripLocalizationComponents(_multiplayerButton.gameObject);

        TrySetButtonText(_multiplayerButton, "多人游戏");

        Button museumButton = TryFindButtonByPersistentMethodName(parent, "UI_ShowMuseum");
        Button settingsButton = TryFindButtonByPersistentMethodName(parent, "UI_Settings");
        if (museumButton != null && settingsButton != null && museumButton.transform.parent == parent && settingsButton.transform.parent == parent)
        {
            TryInsertButtonByShiftingAbsoluteLayout(parent, settingsButton, museumButton, _multiplayerButton);
        }
        else
        {
            int sibling = template.transform.GetSiblingIndex();
            _multiplayerButton.transform.SetSiblingIndex(Mathf.Min(sibling + 1, parent.childCount - 1));
            var tRt = template.GetComponent<RectTransform>();
            var mRt = _multiplayerButton.GetComponent<RectTransform>();
            if (tRt != null && mRt != null)
            {
                mRt.anchoredPosition = new Vector2(tRt.anchoredPosition.x, tRt.anchoredPosition.y - 70f);
            }
        }

        _multiplayerButton.gameObject.SetActive(true);
    }

    private static bool TryInsertButtonByShiftingAbsoluteLayout(Transform parent, Button settingsButton, Button museumButton, Button newButton)
    {
        try
        {
            if (parent == null || settingsButton == null || newButton == null)
            {
                return false;
            }

            bool hasLayout = parent.GetComponent<HorizontalLayoutGroup>() != null || parent.GetComponent<VerticalLayoutGroup>() != null;
            if (hasLayout)
            {
                newButton.transform.SetSiblingIndex(settingsButton.transform.GetSiblingIndex());
                return true;
            }

            var settingsRect = settingsButton.GetComponent<RectTransform>();
            var newRect = newButton.GetComponent<RectTransform>();
            if (settingsRect == null || newRect == null)
            {
                return false;
            }

            float stepY = -120f;
            if (museumButton != null)
            {
                var museumRect = museumButton.GetComponent<RectTransform>();
                if (museumRect != null)
                {
                    float diff = -Mathf.Abs(museumRect.anchoredPosition.y - settingsRect.anchoredPosition.y);
                    if (Mathf.Abs(diff) >= 30f)
                    {
                        stepY = diff;
                    }
                }
            }
            Vector2 step = new Vector2(0f, stepY);

            Vector2 insertPos = settingsRect.anchoredPosition;
            int insertIndex = settingsButton.transform.GetSiblingIndex();

            newButton.transform.SetSiblingIndex(insertIndex);
            newRect.anchoredPosition = insertPos;

            for (int i = insertIndex + 1; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null || child == newButton.transform)
                {
                    continue;
                }

                var rt = child.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchoredPosition += step;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryStripLocalizationComponents(GameObject root)
    {
        try
        {
            if (root == null)
            {
                return;
            }

            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var b in behaviours)
            {
                if (b == null)
                {
                    continue;
                }

                string n = b.GetType().Name;
                if (string.IsNullOrWhiteSpace(n))
                {
                    continue;
                }

                if (n.IndexOf("localiz", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("locale", StringComparison.OrdinalIgnoreCase) >= 0)
                {

                    if (n.Equals("CommonButtonWidget", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("MainMenuButtonWidget", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    UnityEngine.Object.Destroy(b);
                }
            }
        }
        catch
        {

        }
    }

    private static void CleanupLegacySubMenuMultiplayerButton(MainMenuPanel panel)
    {
        try
        {

            if (_subMenuMultiplayerButton == null)
            {

                Transform subGroup = TryGetSubMenuButtonGroup(panel);
                if (subGroup == null)
                {
                    return;
                }

                Transform legacy = subGroup.Find(LegacySubMenuMultiplayerButtonName);
                if (legacy != null)
                {
                    UnityEngine.Object.Destroy(legacy.gameObject);
                }
                return;
            }

            if (_subMenuMultiplayerButton.name == LegacySubMenuMultiplayerButtonName)
            {
                UnityEngine.Object.Destroy(_subMenuMultiplayerButton.gameObject);
            }

            _subMenuMultiplayerButton = null;
        }
        catch
        {

        }
    }

    private static Button TryFindButtonByPersistentMethodName(Transform root, string methodName)
    {
        try
        {
            if (root == null || string.IsNullOrWhiteSpace(methodName))
            {
                return null;
            }

            var buttons = root.GetComponentsInChildren<Button>(true);
            foreach (var b in buttons)
            {
                if (b == null)
                {
                    continue;
                }

                try
                {
                    int n = b.onClick?.GetPersistentEventCount() ?? 0;
                    for (int i = 0; i < n; i++)
                    {
                        string m = b.onClick.GetPersistentMethodName(i);
                        if (string.Equals(m, methodName, StringComparison.Ordinal))
                        {
                            return b;
                        }
                    }
                }
                catch
                {

                }
            }
        }
        catch
        {

        }

        return null;
    }

        private static void EnsureStartGameMultiplayerButton(StartGamePanel panel)
    {
        if (panel == null)
        {
            return;
        }

        if (_startGameMultiplayerButton != null)
        {
            _startGameMultiplayerButton.gameObject.SetActive(true);

            TryStripLocalizationComponents(_startGameMultiplayerButton.gameObject);
            TrySetButtonText(_startGameMultiplayerButton, "多人游戏");
            return;
        }

        Button template;
        try
        {
            template = Traverse.Create(panel).Field("returnButton").GetValue<Button>();
        }
        catch
        {
            template = null;
        }

        if (template == null)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 未能定位 StartGamePanel.returnButton，无法创建多人入口按钮。");
            return;
        }

        Transform parent = template.transform.parent;
        if (parent == null)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] StartGamePanel 模板按钮父节点为空，无法挂载多人入口按钮。");
            return;
        }

        _defaultFont ??= FindDefaultFont(parent);

        _startGameMultiplayerButton = CreateButtonFromTemplate(template, parent, StartGameMultiplayerButtonName, "多人游戏");
        _startGameMultiplayerButton.name = StartGameMultiplayerButtonName;
        _startGameMultiplayerButton.onClick.RemoveAllListeners();
        _startGameMultiplayerButton.onClick.AddListener(OpenMultiplayerEntryFromStartGame);

        TryStripLocalizationComponents(_startGameMultiplayerButton.gameObject);
        TrySetButtonText(_startGameMultiplayerButton, "多人游戏");
        _startGameMultiplayerButton.interactable = true;
        _startGameMultiplayerButton.gameObject.SetActive(true);

        try
        {
            int sibling = template.transform.GetSiblingIndex();
            _startGameMultiplayerButton.transform.SetSiblingIndex(Mathf.Min(sibling + 1, parent.childCount - 1));

            bool hasLayout = parent.GetComponent<HorizontalLayoutGroup>() != null || parent.GetComponent<VerticalLayoutGroup>() != null;
            if (!hasLayout)
            {
                var srcRect = template.GetComponent<RectTransform>();
                var dstRect = _startGameMultiplayerButton.GetComponent<RectTransform>();
                if (srcRect != null && dstRect != null)
                {

                    var canvasRect = TryGetRootCanvasRectTransform(parent);
                    float scale = 1f;
                    try
                    {
                        var canvas = parent.GetComponentInParent<Canvas>();
                        if (canvas != null)
                        {
                            scale = Mathf.Max(0.0001f, canvas.scaleFactor);
                        }
                    }
                    catch
                    {

                    }

                    float desiredSpacingPx = Mathf.Max(30f, Screen.width / 20f);
                    float spacing = desiredSpacingPx / scale;
                    float dx = srcRect.rect.width + spacing;

                    Vector2 rightPos = srcRect.anchoredPosition + new Vector2(dx, 0f);
                    Vector2 leftPos = srcRect.anchoredPosition + new Vector2(-dx, 0f);

                    dstRect.anchoredPosition = rightPos;
                    if (canvasRect != null && !IsFullyInside(dstRect, canvasRect, paddingWorld: 0f))
                    {
                        dstRect.anchoredPosition = leftPos;
                    }

                    if (canvasRect != null)
                    {
                        ClampToContainer(dstRect, canvasRect, paddingWorld: 0f);
                    }
                }
            }
        }
        catch
        {

        }
    }

    private static RectTransform TryGetRootCanvasRectTransform(Transform any)
    {
        try
        {
            var canvas = any?.GetComponentInParent<Canvas>();
            return canvas?.GetComponent<RectTransform>();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFullyInside(RectTransform target, RectTransform container, float paddingWorld)
    {
        if (target == null || container == null)
        {
            return true;
        }

        Vector3[] t = new Vector3[4];
        Vector3[] c = new Vector3[4];
        target.GetWorldCorners(t);
        container.GetWorldCorners(c);

        float minX = c[0].x + paddingWorld;
        float maxX = c[2].x - paddingWorld;
        float minY = c[0].y + paddingWorld;
        float maxY = c[2].y - paddingWorld;

        float tMinX = t[0].x;
        float tMaxX = t[2].x;
        float tMinY = t[0].y;
        float tMaxY = t[2].y;

        return tMinX >= minX && tMaxX <= maxX && tMinY >= minY && tMaxY <= maxY;
    }

    private static void ClampToContainer(RectTransform target, RectTransform container, float paddingWorld)
    {
        if (target == null || container == null)
        {
            return;
        }

        Vector3[] t = new Vector3[4];
        Vector3[] c = new Vector3[4];
        target.GetWorldCorners(t);
        container.GetWorldCorners(c);

        float minX = c[0].x + paddingWorld;
        float maxX = c[2].x - paddingWorld;
        float minY = c[0].y + paddingWorld;
        float maxY = c[2].y - paddingWorld;

        float tMinX = t[0].x;
        float tMaxX = t[2].x;
        float tMinY = t[0].y;
        float tMaxY = t[2].y;

        var delta = Vector3.zero;
        if (tMinX < minX)
        {
            delta.x += (minX - tMinX);
        }
        else if (tMaxX > maxX)
        {
            delta.x -= (tMaxX - maxX);
        }

        if (tMinY < minY)
        {
            delta.y += (minY - tMinY);
        }
        else if (tMaxY > maxY)
        {
            delta.y -= (tMaxY - maxY);
        }

        if (delta != Vector3.zero)
        {

            target.position += delta;
        }
    }

    private static Transform TryGetMainMenuButtonGroup(MainMenuPanel panel)
    {
        try
        {

            return Traverse.Create(panel).Field("mainMenuButtonGroup").GetValue<Transform>();
        }
        catch
        {
            return null;
        }
    }

    private static Transform TryGetSubMenuButtonGroup(MainMenuPanel panel)
    {
        try
        {

            return Traverse.Create(panel).Field("subMenuButtonGroup").GetValue<Transform>();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryFindTemplateButton(MainMenuPanel panel, out Button template, out string why)
    {
        template = null;
        why = null;

        try
        {
            template = Traverse.Create(panel).Field("newGameButton").GetValue<Button>();
            if (template != null)
            {
                return true;
            }
        }
        catch
        {

        }

        try
        {
            List<FieldInfo> fields = AccessTools.GetDeclaredFields(panel.GetType())
                .Where(f => typeof(Button).IsAssignableFrom(f.FieldType))
                .ToList();

            foreach (var f in fields)
            {
                Button b = f.GetValue(panel) as Button;
                if (b != null)
                {
                    template = b;
                    return true;
                }
            }
        }
        catch
        {

        }

        try
        {
            var buttons = panel.GetComponentsInChildren<Button>(true);
            if (buttons != null && buttons.Length > 0)
            {

                List<Button> candidates = buttons
                    .Where(b => b != null && b.name != MultiplayerButtonName)
                    .Where(b => b.GetComponentInChildren<TextMeshProUGUI>(true) != null)
                    .ToList();

                if (candidates.Count > 0)
                {
                    template = candidates[0];
                    return true;
                }

                why = $"panel children has {buttons.Length} Button(s), but no candidate with TextMeshProUGUI";
                return false;
            }

            why = "panel children contains no Button";
            return false;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

        private static void TrySetButtonText(Button button, string text)
    {
        try
        {
            if (button == null)
            {
                return;
            }

            var labels = button.GetComponentsInChildren<TMP_Text>(true);
            if (labels != null)
            {
                foreach (var label in labels)
                {
                    if (label == null)
                    {
                        continue;
                    }

                    label.text = text;
                    if (_defaultFont != null)
                    {
                        label.font = _defaultFont;
                    }
                }
            }

            var legacyTexts = button.GetComponentsInChildren<Text>(true);
            if (legacyTexts != null)
            {
                foreach (var t in legacyTexts)
                {
                    if (t == null)
                    {
                        continue;
                    }

                    t.text = text;
                }
            }
        }
        catch
        {

        }
    }

    private static Button CreateButtonFromTemplate(Button template, Transform parent, string name, string labelText)
    {
        if (template != null)
        {
            try
            {
                GameObject cloned = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
                cloned.name = name;
                cloned.transform.localScale = Vector3.one;

                TryStripLocalizationComponents(cloned);

                Button btn = cloned.GetComponent<Button>();
                if (btn == null)
                {
                    btn = cloned.AddComponent<Button>();
                }

                btn.onClick = new Button.ButtonClickedEvent();
                TrySetButtonText(btn, labelText);
                return btn;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] Instantiate 克隆模板失败，退回手动建构: {ex.Message}");
            }
        }

        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one;

        Image img = go.AddComponent<Image>();
        if (template != null && template.targetGraphic is Image templateImg)
        {
            img.sprite = templateImg.sprite;
            img.type = templateImg.type;
            img.color = templateImg.color;
        }
        else
        {
            img.color = new Color(0.15f, 0.15f, 0.15f, 0.8f);
        }

        Button fallbackBtn = go.AddComponent<Button>();
        fallbackBtn.targetGraphic = img;
        if (template != null)
        {
            fallbackBtn.transition = template.transition;
            fallbackBtn.colors = template.colors;
        }

        GameObject textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localScale = Vector3.one;
        var rt = textGo.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.text = labelText;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        if (_defaultFont != null)
        {
            tmp.font = _defaultFont;
        }

        return fallbackBtn;
    }

    #endregion

    #region 入口面板（非弹窗）

        private static void OpenMultiplayerEntry()
    {
        if (!UiManager.IsInitialized)
        {
            return;
        }

        Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 打开多人入口面板（MainMenu）。");
        Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 打开多人入口面板（MainMenu -> StartGame）。");

        INetworkClient client = TryGetNetworkClient();
        if (client?.IsConnected == true)
        {
            ShowConnectedDialog(client);
            ShowRoomPlayerListOverlay();
            return;
        }

        ShowMultiplayerEntryOverlayFromMainMenu();

        try
        {
            StartGameData defaultMode = Traverse.Create(typeof(MainMenuPanel)).Property<StartGameData>("DefaultMode").Value;
            if (defaultMode != null)
            {
                UiManager.GetPanel<StartGamePanel>().Show(defaultMode);
            }
            else
            {
                UiManager.GetPanel<StartGamePanel>().Show(new StartGameData());
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 打开 StartGamePanel 异常: {ex.Message}");
        }

        OpenMultiplayerEntryFromStartGame();
    }

    private static void OpenMultiplayerEntryFromStartGame()
    {
        if (!UiManager.IsInitialized)
        {
            return;
        }

        Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 打开多人入口面板（StartGame）。");

        INetworkClient client = TryGetNetworkClient();
        if (client?.IsConnected == true)
        {
            ShowConnectedDialog(client);
            ShowRoomPlayerListOverlay();
            return;
        }

        ShowMultiplayerEntryOverlayFromStartGame();
    }

    private static void ShowMultiplayerEntryOverlayFromMainMenu()
    {
        MainMenuPanel panel = _lastMainMenuPanel ?? UiManager.GetPanel<MainMenuPanel>();
        if (panel == null)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 无法获取 MainMenuPanel，无法显示多人入口面板。");
            return;
        }

        Button template = _multiplayerButton;
        if (template == null && !TryFindTemplateButton(panel, out template, out string whyTemplateFailed))
        {
            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 无法创建多人入口面板（模板按钮不可用）：{whyTemplateFailed}");
            return;
        }

        ShowMultiplayerEntryOverlay(panel.transform, template);
    }

    private static void ShowMultiplayerEntryOverlayFromStartGame()
    {
        StartGamePanel panel = _lastStartGamePanel ?? UiManager.GetPanel<StartGamePanel>();
        if (panel == null)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 无法获取 StartGamePanel，无法显示多人入口面板。");
            return;
        }

        Button template = _startGameMultiplayerButton;
        if (template == null)
        {
            try
            {
                template = Traverse.Create(panel).Field("returnButton").GetValue<Button>();
            }
            catch
            {
                template = null;
            }
        }

        if (template == null)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] StartGamePanel 模板按钮 returnButton 不可用，无法创建多人入口面板。");
            return;
        }

        ShowMultiplayerEntryOverlay(panel.transform, template);
    }

    private static void ShowMultiplayerEntryOverlay(Transform panelTransform, Button template)
    {
        try
        {
            if (panelTransform == null || template == null)
            {
                return;
            }

            if (_overlayRoot != null)
            {
                UnityEngine.Object.Destroy(_overlayRoot);
                _overlayRoot = null;
                _rootAnimator = null;
            }

            _defaultFont ??= FindDefaultFont(panelTransform);

            GameObject dialogPrefab = Resources.Load<GameObject>("UI/Dialogs/MessageDialog");
            if (dialogPrefab == null)
            {
                Plugin.Logger?.LogError("[MainMenuMultiplayerEntry] 无法加载原生 MessageDialog 预制体！");
                return;
            }

            Transform rootParent = TryGetRootCanvasRectTransform(UiManager.Instance?.transform)
                ?? UiManager.Instance?.transform
                ?? panelTransform;

            GameObject root = new GameObject(OverlayRootName);
            root.transform.SetParent(rootParent, false);
            root.transform.SetAsLastSibling();
            _overlayRoot = root;

            var rootRect = root.AddComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0f, 0f);
            rootRect.anchorMax = new Vector2(1f, 1f);
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            var bg = root.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.62f);
            bg.raycastTarget = true;

            var rootGroup = root.AddComponent<CanvasGroup>();
            rootGroup.alpha = 0f;
            rootGroup.interactable = false;
            rootGroup.blocksRaycasts = true;

            float panelScale = 2.2f;
            try
            {
                const float baseW = 520f;
                const float baseH = 380f;
                const float widthFactor = 1.00f;
                const float heightFactor = 0.95f;

                var parentRect = panelTransform.GetComponent<RectTransform>();
                if (parentRect != null)
                {
                    float maxW = Mathf.Max(1f, parentRect.rect.width * 0.92f);
                    float maxH = Mathf.Max(1f, parentRect.rect.height * 0.92f);
                    float fitScale = Mathf.Min(maxW / (baseW * widthFactor), maxH / (baseH * heightFactor));
                    panelScale = Mathf.Clamp(fitScale, 1f, 2.2f);
                }
            }
            catch
            {
                panelScale = 2.2f;
            }

            GameObject frame = UnityEngine.Object.Instantiate(dialogPrefab, root.transform, false);
            frame.name = OverlayRootName + "_Frame";
            frame.SetActive(true);

            RectTransform frameRect = frame.GetComponent<RectTransform>();
            if (frameRect != null)
            {
                frameRect.anchorMin = new Vector2(0.5f, 0.5f);
                frameRect.anchorMax = new Vector2(0.5f, 0.5f);
                frameRect.pivot = new Vector2(0.5f, 0.5f);
                frameRect.sizeDelta = new Vector2(520f * panelScale, 380f * panelScale);
                frameRect.anchoredPosition = Vector2.zero;
            }

            MessageDialog dialog = frame.GetComponentInChildren<MessageDialog>(true);
            if (dialog != null)
            {
                dialog.enabled = false;
            }

            TextMeshProUGUI mainText = GetDialogField<TextMeshProUGUI>(dialog, "mainText");
            TextMeshProUGUI subText = GetDialogField<TextMeshProUGUI>(dialog, "subText");
            Button singleConfirm = GetDialogField<Button>(dialog, "singleConfirmButton");
            Button confirm = GetDialogField<Button>(dialog, "confirmButton");
            Button cancel = GetDialogField<Button>(dialog, "cancelButton");

            RectTransform panelRect = mainText?.rectTransform.parent as RectTransform ?? frameRect;

            if (frame != null)
            {
                var rootFitters = frame.GetComponents<ContentSizeFitter>();
                foreach (var fitter in rootFitters) UnityEngine.Object.Destroy(fitter);

                var rootLayouts = frame.GetComponents<LayoutGroup>();
                foreach (var l in rootLayouts) UnityEngine.Object.Destroy(l);
            }

            if (panelRect != null)
            {

                Transform current = panelRect;
                while (current != null && current != frame.transform)
                {
                    var rt = current.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        rt.anchorMin = Vector2.zero;
                        rt.anchorMax = Vector2.one;
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;
                        rt.pivot = new Vector2(0.5f, 0.5f);
                    }

                    var fitters = current.GetComponents<ContentSizeFitter>();
                    foreach (var f in fitters) UnityEngine.Object.Destroy(f);

                    var layouts = current.GetComponents<LayoutGroup>();
                    foreach (var l in layouts) UnityEngine.Object.Destroy(l);

                    current = current.parent;
                }

                panelRect.anchorMin = Vector2.zero;
                panelRect.anchorMax = Vector2.one;
                panelRect.offsetMin = Vector2.zero;
                panelRect.offsetMax = Vector2.zero;
                panelRect.pivot = new Vector2(0.5f, 0.5f);
            }

            if (frame != null)
            {
                var nativeImages = frame.GetComponentsInChildren<Image>(true);
                foreach (var img in nativeImages)
                {
                    if (img == null) continue;
                    if (singleConfirm != null && (img.transform == singleConfirm.transform || IsDescendantOf(img.transform, singleConfirm.transform))) continue;
                    if (confirm != null && (img.transform == confirm.transform || IsDescendantOf(img.transform, confirm.transform))) continue;
                    if (cancel != null && (img.transform == cancel.transform || IsDescendantOf(img.transform, cancel.transform))) continue;

                    img.enabled = false;
                }
            }

            if (subText != null) subText.gameObject.SetActive(false);
            if (singleConfirm != null) singleConfirm.gameObject.SetActive(false);
            if (confirm != null) confirm.gameObject.SetActive(false);
            if (cancel != null) cancel.gameObject.SetActive(false);

            Button buttonTemplate = confirm ?? singleConfirm ?? cancel ?? template;

            try
            {
                _rootAnimator = root.AddComponent<PanelAnimator>();
                _rootAnimator.Init(rootGroup, frameRect);
                _rootAnimator.PlayOpen();
            }
            catch
            {

            }

            GameObject mainArea = new GameObject("MainArea");
            mainArea.transform.SetParent(panelRect, false);
            var mainAreaRt = mainArea.AddComponent<RectTransform>();
            mainAreaRt.anchorMin = Vector2.zero;
            mainAreaRt.anchorMax = Vector2.one;
            mainAreaRt.offsetMin = Vector2.zero;
            mainAreaRt.offsetMax = Vector2.zero;
            mainAreaRt.pivot = new Vector2(0.5f, 0.5f);
            var mainGroup = mainArea.AddComponent<CanvasGroup>();
            var mainAnim = mainArea.AddComponent<PanelAnimator>();
            mainAnim.Init(mainGroup, mainAreaRt);

            if (mainText != null)
            {
                mainText.text = "多人游戏";
                mainText.alignment = TextAlignmentOptions.Center;
                mainText.fontSize = Mathf.Clamp(24f * panelScale, 24f, 80f);

                var mainTextRt = mainText.rectTransform;
                if (mainTextRt != null)
                {
                    mainTextRt.anchorMin = new Vector2(0f, 1f);
                    mainTextRt.anchorMax = new Vector2(1f, 1f);
                    mainTextRt.pivot = new Vector2(0.5f, 1f);
                    mainTextRt.anchoredPosition = new Vector2(0f, -20f * panelScale);
                    mainTextRt.sizeDelta = new Vector2(-40f * panelScale, 45f * panelScale);
                }
                mainText.transform.SetAsLastSibling();
                mainText.gameObject.SetActive(true);
            }

            GameObject descGo = new GameObject("Description");
            descGo.transform.SetParent(mainArea.transform, false);
            var descRect = descGo.AddComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0f, 1f);
            descRect.anchorMax = new Vector2(1f, 1f);
            descRect.pivot = new Vector2(0.5f, 1f);
            descRect.anchoredPosition = new Vector2(0f, -74f * panelScale);
            descRect.sizeDelta = new Vector2(-40f * panelScale, 70f * panelScale);

            var desc = descGo.AddComponent<TextMeshProUGUI>();
            desc.text = "请选择联机方式：\n房主：启动本机服务器并连接\n加入：连接到配置的服务器";
            desc.fontSize = 18f * panelScale;
            desc.alignment = TextAlignmentOptions.TopLeft;
            desc.color = Color.white;
            desc.raycastTarget = false;
            if (_defaultFont != null)
            {
                desc.font = _defaultFont;
            }

            GameObject buttonsGo = new GameObject("Buttons");
            buttonsGo.transform.SetParent(mainArea.transform, false);
            var buttonsRect = buttonsGo.AddComponent<RectTransform>();
            buttonsRect.anchorMin = new Vector2(0.5f, 0f);
            buttonsRect.anchorMax = new Vector2(0.5f, 0f);
            buttonsRect.pivot = new Vector2(0.5f, 0f);
            float buttonsWidth = 380f * panelScale;
            try
            {
                buttonsWidth = Mathf.Min(buttonsWidth, frameRect.sizeDelta.x * 0.60f);
            }
            catch
            {

            }
            buttonsRect.sizeDelta = new Vector2(buttonsWidth, 196f * panelScale);
            buttonsRect.anchoredPosition = new Vector2(0f, 10f * panelScale);

            var layout = buttonsGo.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 12f * panelScale;
            layout.padding = new RectOffset(0, 0, 0, 0);

            GameObject hostArea = new GameObject("HostArea");
            PanelAnimator hostAnim = null;
            var hostBtn = CreateDialogButton(buttonTemplate, buttonsGo.transform, "NetworkPlugin_HostButton", "做房主", panelScale);
            hostBtn.onClick.AddListener(() =>
            {
                if (mainAnim != null && hostAnim != null)
                {
                    mainAnim.PlayClose(() =>
                    {
                        hostAnim.PlayOpen();
                        if (mainText != null) mainText.text = "做房主";
                    });
                }
                else
                {
                    mainArea.SetActive(false);
                    hostArea.SetActive(true);
                    if (mainText != null) mainText.text = "做房主";
                }
            });

            GameObject joinArea = new GameObject("JoinArea");
            PanelAnimator joinAnim = null;
            var joinBtn = CreateDialogButton(buttonTemplate, buttonsGo.transform, "NetworkPlugin_JoinButton", "加入房主", panelScale);
            joinBtn.onClick.AddListener(() =>
            {
                if (mainAnim != null && joinAnim != null)
                {
                    mainAnim.PlayClose(() =>
                    {
                        joinAnim.PlayOpen();
                        if (mainText != null) mainText.text = "加入房主";
                    });
                }
                else
                {
                    mainArea.SetActive(false);
                    joinArea.SetActive(true);
                    if (mainText != null) mainText.text = "加入房主";
                }
            });

            var backBtn = CreateDialogButton(buttonTemplate, buttonsGo.transform, "NetworkPlugin_BackButton", "返回", panelScale);
            backBtn.onClick.AddListener(HideOverlay);

            foreach (var b in new[] { hostBtn, joinBtn, backBtn })
            {
                if (b == null) continue;
                var r = b.GetComponent<RectTransform>();
                if (r != null) r.sizeDelta = new Vector2(r.sizeDelta.x, 58f * panelScale);
            }

            joinArea.transform.SetParent(panelRect, false);
            var joinAreaRt = joinArea.AddComponent<RectTransform>();
            joinAreaRt.anchorMin = Vector2.zero;
            joinAreaRt.anchorMax = Vector2.one;
            joinAreaRt.offsetMin = Vector2.zero;
            joinAreaRt.offsetMax = Vector2.zero;
            joinAreaRt.pivot = new Vector2(0.5f, 0.5f);
            var joinGroup = joinArea.AddComponent<CanvasGroup>();
            joinAnim = joinArea.AddComponent<PanelAnimator>();
            joinAnim.Init(joinGroup, joinAreaRt);
            joinArea.SetActive(false);

            GameObject formGo = new GameObject("Form");
            formGo.transform.SetParent(joinArea.transform, false);
            var formRt = formGo.AddComponent<RectTransform>();
            formRt.anchorMin = new Vector2(0.5f, 1f);
            formRt.anchorMax = new Vector2(0.5f, 1f);
            formRt.pivot = new Vector2(0.5f, 1f);
            formRt.sizeDelta = new Vector2(400f * panelScale, 200f * panelScale);
            formRt.anchoredPosition = new Vector2(0f, -54f * panelScale);

            var formLayout = formGo.AddComponent<VerticalLayoutGroup>();
            formLayout.childAlignment = TextAnchor.UpperCenter;
            formLayout.childControlWidth = true;
            formLayout.childControlHeight = true;
            formLayout.childForceExpandWidth = true;
            formLayout.childForceExpandHeight = false;
            formLayout.spacing = 6f * panelScale;

            ConfigManager config = TryGetConfig();
            string curIp = config?.ServerIP?.Value ?? "127.0.0.1";
            string curPort = config?.ServerPort?.Value.ToString() ?? "7777";
            string curKey = config?.HostConnectionKey?.Value ?? NetworkPlugin.Network.Security.SecurityUtils.GenerateSecureKey();
            string curName = config?.PlayerNameOverride?.Value;
            if (string.IsNullOrWhiteSpace(curName))
            {
                try
                {
                    curName = Singleton<GameMaster>.Instance?.CurrentProfile?.Name ?? "joiner";
                }
                catch
                {
                    curName = "joiner";
                }
            }

            TMP_InputField ipInput;
            TMP_InputField portInput;
            TMP_InputField keyInput;
            TMP_InputField nameInput;

            CreateInputRow(formGo.transform, buttonTemplate, "服务器 IP:", "请输入 IP 地址...", curIp, panelScale, out ipInput);
            CreateInputRow(formGo.transform, buttonTemplate, "端口:", "请输入端口号...", curPort, panelScale, out portInput);
            CreateInputRow(formGo.transform, buttonTemplate, "连接密钥:", "请输入连接密钥...", curKey, panelScale, out keyInput);
            CreateInputRow(formGo.transform, buttonTemplate, "玩家昵称:", "请输入昵称...", curName, panelScale, out nameInput);

            GameObject joinButtonsGo = new GameObject("JoinButtons");
            joinButtonsGo.transform.SetParent(joinArea.transform, false);
            var joinButtonsRt = joinButtonsGo.AddComponent<RectTransform>();
            joinButtonsRt.anchorMin = new Vector2(0.5f, 0f);
            joinButtonsRt.anchorMax = new Vector2(0.5f, 0f);
            joinButtonsRt.pivot = new Vector2(0.5f, 0f);
            joinButtonsRt.sizeDelta = new Vector2(380f * panelScale, 60f * panelScale);
            joinButtonsRt.anchoredPosition = new Vector2(0f, 15f * panelScale);

            var joinButtonsLayout = joinButtonsGo.AddComponent<HorizontalLayoutGroup>();
            joinButtonsLayout.childAlignment = TextAnchor.MiddleCenter;
            joinButtonsLayout.childControlWidth = true;
            joinButtonsLayout.childControlHeight = true;
            joinButtonsLayout.childForceExpandWidth = true;
            joinButtonsLayout.childForceExpandHeight = false;
            joinButtonsLayout.spacing = 20f * panelScale;

            var connectBtn = CreateDialogButton(buttonTemplate, joinButtonsGo.transform, "NetworkPlugin_ConnectBtn", "开始连接", panelScale);
            connectBtn.onClick.AddListener(() =>
            {
                string ip = ipInput.text.Trim();
                string portStr = portInput.text.Trim();
                string key = keyInput.text.Trim();
                string name = nameInput.text.Trim();

                if (string.IsNullOrWhiteSpace(ip))
                {
                    ShowWarningDialog("IP 地址不能为空。");
                    return;
                }
                if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
                {
                    ShowWarningDialog("请输入有效的端口号（1-65535）。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(key))
                {
                    ShowWarningDialog("连接密钥不能为空。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    ShowWarningDialog("昵称不能为空。");
                    return;
                }

                ConfigManager cfg = TryGetConfig();
                if (cfg != null)
                {
                    cfg.ServerIP.Value = ip;
                    cfg.ServerPort.Value = port;
                    cfg.HostConnectionKey.Value = key;
                    cfg.PlayerNameOverride.Value = name;
                    try
                    {
                        cfg.ServerIP.ConfigFile.Save();
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 保存配置文件失败: {ex.Message}");
                    }
                }

                HideOverlay();
                TryConnectToServerAndShowRoomList(ip, port);
            });

            var cancelBtn = CreateDialogButton(buttonTemplate, joinButtonsGo.transform, "NetworkPlugin_CancelBtn", "返回", panelScale);
            cancelBtn.onClick.AddListener(() =>
            {
                if (joinAnim != null && mainAnim != null)
                {
                    joinAnim.PlayClose(() =>
                    {
                        mainAnim.PlayOpen();
                        if (mainText != null) mainText.text = "多人游戏";
                    });
                }
                else
                {
                    joinArea.SetActive(false);
                    mainArea.SetActive(true);
                    if (mainText != null) mainText.text = "多人游戏";
                }
            });

            foreach (var b in new[] { connectBtn, cancelBtn })
            {
                if (b == null) continue;
                var r = b.GetComponent<RectTransform>();
                if (r != null) r.sizeDelta = new Vector2(r.sizeDelta.x, 58f * panelScale);
            }

            hostArea.transform.SetParent(panelRect, false);
            var hostAreaRt = hostArea.AddComponent<RectTransform>();
            hostAreaRt.anchorMin = Vector2.zero;
            hostAreaRt.anchorMax = Vector2.one;
            hostAreaRt.offsetMin = Vector2.zero;
            hostAreaRt.offsetMax = Vector2.zero;
            hostAreaRt.pivot = new Vector2(0.5f, 0.5f);
            var hostGroup = hostArea.AddComponent<CanvasGroup>();
            hostAnim = hostArea.AddComponent<PanelAnimator>();
            hostAnim.Init(hostGroup, hostAreaRt);
            hostArea.SetActive(false);

            GameObject hostFormGo = new GameObject("HostForm");
            hostFormGo.transform.SetParent(hostArea.transform, false);
            var hostFormRt = hostFormGo.AddComponent<RectTransform>();
            hostFormRt.anchorMin = new Vector2(0.5f, 1f);
            hostFormRt.anchorMax = new Vector2(0.5f, 1f);
            hostFormRt.pivot = new Vector2(0.5f, 1f);
            hostFormRt.sizeDelta = new Vector2(400f * panelScale, 200f * panelScale);
            hostFormRt.anchoredPosition = new Vector2(0f, -54f * panelScale);

            var hostFormLayout = hostFormGo.AddComponent<VerticalLayoutGroup>();
            hostFormLayout.childAlignment = TextAnchor.UpperCenter;
            hostFormLayout.childControlWidth = true;
            hostFormLayout.childControlHeight = true;
            hostFormLayout.childForceExpandWidth = true;
            hostFormLayout.childForceExpandHeight = false;
            hostFormLayout.spacing = 6f * panelScale;

            string hostPort = config?.HostServerPort?.Value.ToString() ?? "7777";
            string hostMaxConn = config?.HostMaxConnections?.Value.ToString() ?? "4";
            string hostKey = config?.HostConnectionKey?.Value ?? NetworkPlugin.Network.Security.SecurityUtils.GenerateSecureKey();
            string hostName = config?.HostPlayerNameOverride?.Value;
            if (string.IsNullOrWhiteSpace(hostName))
            {
                try
                {
                    hostName = Singleton<GameMaster>.Instance?.CurrentProfile?.Name ?? "host";
                }
                catch
                {
                    hostName = "host";
                }
            }

            TMP_InputField hostPortInput;
            TMP_InputField hostMaxConnInput;
            TMP_InputField hostKeyInput;
            TMP_InputField hostNameInput;

            CreateInputRow(hostFormGo.transform, buttonTemplate, "监听端口:", "请输入端口号...", hostPort, panelScale, out hostPortInput);
            CreateInputRow(hostFormGo.transform, buttonTemplate, "最大玩家数:", "请输入最大玩家数...", hostMaxConn, panelScale, out hostMaxConnInput);
            CreateInputRow(hostFormGo.transform, buttonTemplate, "连接密钥:", "请输入连接密钥...", hostKey, panelScale, out hostKeyInput);
            CreateInputRow(hostFormGo.transform, buttonTemplate, "玩家昵称:", "请输入昵称...", hostName, panelScale, out hostNameInput);

            GameObject hostButtonsGo = new GameObject("HostButtons");
            hostButtonsGo.transform.SetParent(hostArea.transform, false);
            var hostButtonsRt = hostButtonsGo.AddComponent<RectTransform>();
            hostButtonsRt.anchorMin = new Vector2(0.5f, 0f);
            hostButtonsRt.anchorMax = new Vector2(0.5f, 0f);
            hostButtonsRt.pivot = new Vector2(0.5f, 0f);
            hostButtonsRt.sizeDelta = new Vector2(380f * panelScale, 60f * panelScale);
            hostButtonsRt.anchoredPosition = new Vector2(0f, 15f * panelScale);

            var hostButtonsLayout = hostButtonsGo.AddComponent<HorizontalLayoutGroup>();
            hostButtonsLayout.childAlignment = TextAnchor.MiddleCenter;
            hostButtonsLayout.childControlWidth = true;
            hostButtonsLayout.childControlHeight = true;
            hostButtonsLayout.childForceExpandWidth = true;
            hostButtonsLayout.childForceExpandHeight = false;
            hostButtonsLayout.spacing = 20f * panelScale;

            var startHostBtn = CreateDialogButton(buttonTemplate, hostButtonsGo.transform, "NetworkPlugin_StartHostBtn", "开始做房主", panelScale);
            startHostBtn.onClick.AddListener(() =>
            {
                string portStr = hostPortInput.text.Trim();
                string maxConnStr = hostMaxConnInput.text.Trim();
                string key = hostKeyInput.text.Trim();
                string name = hostNameInput.text.Trim();

                if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
                {
                    ShowWarningDialog("请输入有效的端口号（1-65535）。");
                    return;
                }
                if (!int.TryParse(maxConnStr, out int maxConn) || maxConn <= 0 || maxConn > 1000)
                {
                    ShowWarningDialog("请输入有效的最大玩家数（1-1000）。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(key))
                {
                    ShowWarningDialog("连接密钥不能为空。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    ShowWarningDialog("昵称不能为空。");
                    return;
                }

                ConfigManager cfg = TryGetConfig();
                if (cfg != null)
                {
                    cfg.HostServerPort.Value = port;
                    cfg.HostMaxConnections.Value = maxConn;
                    cfg.HostConnectionKey.Value = key;
                    cfg.HostPlayerNameOverride.Value = name;
                    try
                    {
                        cfg.HostServerPort.ConfigFile.Save();
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 保存房主配置失败: {ex.Message}");
                    }
                }

                HideOverlay();

                GameRunSaveData save = null;
                try
                {
                    save = MultiplayerSaveManager.LoadMultiplayerSave();
                }
                catch
                {
                    save = null;
                }

                if (save != null && Singleton<GameMaster>.Instance?.CurrentGameRun == null)
                {
                    UiManager.GetDialog<MessageDialog>().Show(
                        new MessageContent
                        {
                            Text = "检测到可继续的多人存档。\n\n确认：作为房主继续存档并进入联机房间\n取消：作为房主开新游戏（仍保持联机）",
                            Icon = MessageIcon.Warning,
                            Buttons = DialogButtons.ConfirmCancel,
                            OnConfirm = () =>
                            {
                                Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 房主选择继续存档，启动服务器并进入大厅。");
                                _lobbyResumeMode = true;
                                _resumingSave = save;
                                TryHostLocalServerAndConnectAndShowRoomList();
                            },
                            OnCancel = () =>
                            {
                                UiManager.GetDialog<MessageDialog>().Show(
                                    new MessageContent
                                    {
                                        Text = "开新游戏将覆盖现有未完成的多人存档，是否继续？",
                                        Icon = MessageIcon.Warning,
                                        Buttons = DialogButtons.ConfirmCancel,
                                        OnConfirm = () =>
                                        {
                                            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 房主确认开新游戏，清空旧多人存档。");
                                            MultiplayerSaveManager.DeleteMultiplayerSave();
                                            _lobbyResumeMode = false;
                                            _resumingSave = null;
                                            TryHostLocalServerAndConnectAndShowRoomList();
                                        },
                                        OnCancel = () =>
                                        {
                                            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 房主取消开新游戏，保留旧多人存档。");
                                        }
                                    }
                                );
                            },
                        }
                    );
                    return;
                }

                _lobbyResumeMode = false;
                _resumingSave = null;
                TryHostLocalServerAndConnectAndShowRoomList();
            });

            var hostCancelBtn = CreateDialogButton(buttonTemplate, hostButtonsGo.transform, "NetworkPlugin_HostCancelBtn", "返回", panelScale);
            hostCancelBtn.onClick.AddListener(() =>
            {
                if (hostAnim != null && mainAnim != null)
                {
                    hostAnim.PlayClose(() =>
                    {
                        mainAnim.PlayOpen();
                        if (mainText != null) mainText.text = "多人游戏";
                    });
                }
                else
                {
                    hostArea.SetActive(false);
                    mainArea.SetActive(true);
                    if (mainText != null) mainText.text = "多人游戏";
                }
            });

            foreach (var b in new[] { startHostBtn, hostCancelBtn })
            {
                if (b == null) continue;
                var r = b.GetComponent<RectTransform>();
                if (r != null) r.sizeDelta = new Vector2(r.sizeDelta.x, 58f * panelScale);
            }

            try
            {
                var canvasRect = TryGetRootCanvasRectTransform(rootParent);
                if (canvasRect != null)
                {
                    ClampToContainer(frameRect, canvasRect, paddingWorld: 0f);
                }
            }
            catch
            {

            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 创建多人入口面板失败: {ex}");
        }
    }

    private sealed class PanelAnimator : MonoBehaviour
    {
        private CanvasGroup _group;
        private RectTransform _container;
        private Coroutine _currentAnim;
        private bool _isOpen;

        public void Init(CanvasGroup group, RectTransform container)
        {
            _group = group;
            _container = container;
            if (_group != null)
            {
                _isOpen = gameObject.activeSelf;
                _group.alpha = _isOpen ? 1f : 0f;
                if (_container != null)
                {
                    _container.localScale = _isOpen ? Vector3.one : new Vector3(0.92f, 0.92f, 1f);
                }
            }
        }

        public void PlayOpen(Action onComplete = null)
        {
            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }
            if (_group != null)
            {
                _group.interactable = false;
                _group.blocksRaycasts = true;
            }
            if (_currentAnim != null) StopCoroutine(_currentAnim);
            _isOpen = true;
            _currentAnim = StartCoroutine(Animate(true, onComplete));
        }

        public void PlayClose(Action onComplete = null)
        {
            if (!gameObject.activeInHierarchy)
            {
                onComplete?.Invoke();
                return;
            }
            if (_group != null)
            {
                _group.interactable = false;
                _group.blocksRaycasts = true;
            }
            if (_currentAnim != null) StopCoroutine(_currentAnim);
            _isOpen = false;
            _currentAnim = StartCoroutine(Animate(false, onComplete));
        }

        private IEnumerator Animate(bool isOpen, Action onComplete)
        {
            if (_group == null)
            {
                onComplete?.Invoke();
                yield break;
            }

            const float duration = 0.12f;
            float startAlpha = _group.alpha;
            float targetAlpha = isOpen ? 1f : 0f;
            float startScale = _container != null ? _container.localScale.x : 1f;
            float targetScale = isOpen ? 1f : 0.92f;

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                float e = isOpen ? (1f - Mathf.Pow(1f - p, 3f)) : Mathf.Pow(p, 3f);

                _group.alpha = Mathf.Lerp(startAlpha, targetAlpha, p);
                if (_container != null)
                {
                    float s = Mathf.Lerp(startScale, targetScale, e);
                    _container.localScale = new Vector3(s, s, 1f);
                }

                yield return null;
            }

            _group.alpha = targetAlpha;
            if (_container != null) _container.localScale = new Vector3(targetScale, targetScale, 1f);

            _group.interactable = isOpen;
            _group.blocksRaycasts = isOpen;

            if (!isOpen)
            {
                gameObject.SetActive(false);
            }

            onComplete?.Invoke();
        }
    }

    private static void CreateHorizontalRule(Transform parent, string name, float anchorY, float y, float height, Color color)
    {
        if (parent == null)
        {
            return;
        }

        GameObject rule = new GameObject(name);
        rule.transform.SetParent(parent, false);

        var rect = rule.AddComponent<RectTransform>();

        rect.anchorMin = new Vector2(0f, anchorY);
        rect.anchorMax = new Vector2(1f, anchorY);
        rect.pivot = new Vector2(0.5f, anchorY);
        rect.anchoredPosition = new Vector2(0f, y);

        rect.sizeDelta = new Vector2(0f, height);

        var img = rule.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;

        try
        {
            var shadow = rule.AddComponent<Shadow>();
            var glow = color;
            glow.a = 0.35f;
            shadow.effectColor = glow;
            shadow.effectDistance = new Vector2(0f, -1.5f);
        }
        catch
        {

        }
    }

    private static void TryScaleButtonText(Button button, float scale)
    {
        try
        {
            if (button == null)
            {
                return;
            }

            var labels = button.GetComponentsInChildren<TMP_Text>(true);
            if (labels != null)
            {
                foreach (var label in labels)
                {
                    if (label == null)
                    {
                        continue;
                    }

                    if (label.fontSize > 0f && label.fontSize < 40f)
                    {
                        label.fontSize = Mathf.Clamp(label.fontSize * scale, 14f, 72f);
                    }

                    label.enableWordWrapping = false;
                    label.enableAutoSizing = true;
                    label.fontSizeMin = 10f * scale;
                    label.fontSizeMax = Mathf.Clamp(18f * scale, 14f, 60f);
                }
            }

            var legacyTexts = button.GetComponentsInChildren<Text>(true);
            if (legacyTexts != null)
            {
                foreach (var t in legacyTexts)
                {
                    if (t == null)
                    {
                        continue;
                    }

                    if (t.fontSize > 0 && t.fontSize < 40)
                    {
                        t.fontSize = Mathf.Clamp((int)(t.fontSize * scale), 14, 72);
                    }
                }
            }
        }
        catch
        {

        }
    }

    private static void HideOverlay()
    {
        if (_rootAnimator != null && _overlayRoot != null && _overlayRoot.activeInHierarchy)
        {
            _rootAnimator.PlayClose(() =>
            {
                _overlayRoot.SetActive(false);
            });
        }
        else
        {
            _overlayRoot?.SetActive(false);
        }
    }

    private static T GetDialogField<T>(MessageDialog dialog, string fieldName) where T : class
    {
        if (dialog == null || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        FieldInfo field = typeof(MessageDialog).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(dialog) as T;
    }

    private static Button CreateDialogButton(Button template, Transform parent, string name, string labelText, float panelScale)
    {
        if (template == null)
        {
            return null;
        }

        GameObject go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
        go.name = name;
        Button btn = go.GetComponent<Button>();
        btn.onClick.RemoveAllListeners();

        TryStripLocalizationComponents(go);

        TrySetButtonText(btn, labelText);
        TryScaleButtonText(btn, panelScale);

        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.preferredHeight = 58f * panelScale;
        le.preferredWidth = -1f;

        btn.interactable = true;
        go.SetActive(true);

        return btn;
    }

    private static void ShowWarningDialog(string message)
    {
        UiManager.GetDialog<MessageDialog>().Show(
            new MessageContent
            {
                Text = message,
                Icon = MessageIcon.Warning,
                Buttons = DialogButtons.Confirm,
                OnConfirm = null,
                OnCancel = null,
            }
        );
    }

    private static GameObject CreateInputRow(Transform parent, Button templateButton, string labelText, string placeholderText, string defaultVal, float panelScale, out TMP_InputField inputField)
    {
        GameObject row = new GameObject("Row_" + labelText);
        row.transform.SetParent(parent, false);
        var rowRt = row.AddComponent<RectTransform>();
        rowRt.sizeDelta = new Vector2(400f * panelScale, 42f * panelScale);

        var le = row.AddComponent<LayoutElement>();
        le.preferredHeight = 42f * panelScale;
        le.preferredWidth = 400f * panelScale;

        var layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.spacing = 15f * panelScale;

        GameObject labelGo = new GameObject("Label");
        labelGo.transform.SetParent(row.transform, false);
        var labelRt = labelGo.AddComponent<RectTransform>();
        labelRt.sizeDelta = new Vector2(130f * panelScale, 40f * panelScale);

        var labelTextComp = labelGo.AddComponent<TextMeshProUGUI>();
        labelTextComp.text = labelText;
        labelTextComp.alignment = TextAlignmentOptions.MidlineRight;
        labelTextComp.color = new Color(0.86f, 0.73f, 0.34f, 1f);
        labelTextComp.fontSize = 18f * panelScale;
        labelTextComp.raycastTarget = false;
        if (_defaultFont != null)
        {
            labelTextComp.font = _defaultFont;
        }

        inputField = CreateInputField(row.transform, templateButton, "InputField", placeholderText, defaultVal, 240f * panelScale, 40f * panelScale, panelScale);

        return row;
    }

    private static TMP_InputField CreateInputField(Transform parent, Button templateButton, string name, string placeholderText, string defaultText, float width, float height, float panelScale)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(width, height);

        var img = go.AddComponent<Image>();
        if (templateButton != null && templateButton.targetGraphic is Image templateImg)
        {
            img.sprite = templateImg.sprite;
            img.type = templateImg.type;
            img.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
        }
        else
        {
            img.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
        }

        GameObject textArea = new GameObject("TextArea");
        textArea.transform.SetParent(go.transform, false);
        var textAreaRt = textArea.AddComponent<RectTransform>();
        textAreaRt.anchorMin = Vector2.zero;
        textAreaRt.anchorMax = Vector2.one;

        textAreaRt.offsetMin = new Vector2(14f * panelScale, 2f * panelScale);
        textAreaRt.offsetMax = new Vector2(-14f * panelScale, -2f * panelScale);
        textArea.AddComponent<RectMask2D>();

        GameObject placeholderGo = new GameObject("Placeholder");
        placeholderGo.transform.SetParent(textArea.transform, false);
        var placeholderRt = placeholderGo.AddComponent<RectTransform>();
        placeholderRt.anchorMin = Vector2.zero;
        placeholderRt.anchorMax = Vector2.one;
        placeholderRt.offsetMin = Vector2.zero;
        placeholderRt.offsetMax = Vector2.zero;

        var placeholderTmp = placeholderGo.AddComponent<TextMeshProUGUI>();
        placeholderTmp.text = placeholderText;
        placeholderTmp.alignment = TextAlignmentOptions.MidlineLeft;
        placeholderTmp.color = new Color(0.6f, 0.6f, 0.6f, 0.6f);
        placeholderTmp.fontSize = 18f * panelScale;
        placeholderTmp.raycastTarget = false;
        if (_defaultFont != null)
        {
            placeholderTmp.font = _defaultFont;
        }

        GameObject textGo = new GameObject("Text");
        textGo.transform.SetParent(textArea.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        var textTmp = textGo.AddComponent<TextMeshProUGUI>();
        textTmp.text = defaultText;
        textTmp.alignment = TextAlignmentOptions.MidlineLeft;
        textTmp.color = Color.white;
        textTmp.fontSize = 18f * panelScale;
        textTmp.raycastTarget = false;
        if (_defaultFont != null)
        {
            textTmp.font = _defaultFont;
        }

        var inputField = go.AddComponent<TMP_InputField>();
        inputField.textViewport = textAreaRt;
        inputField.textComponent = textTmp;
        inputField.placeholder = placeholderTmp;
        inputField.text = defaultText;
        if (_defaultFont != null)
        {
            inputField.fontAsset = _defaultFont;
        }

        return inputField;
    }

    private static TMP_FontAsset FindDefaultFont(Transform any)
    {
        try
        {
            if (any == null)
            {
                return null;
            }

            var label = any.GetComponentInChildren<TextMeshProUGUI>(true);
            return label?.font;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDescendantOf(Transform child, Transform parent)
    {
        if (child == null || parent == null)
        {
            return false;
        }
        Transform current = child.parent;
        while (current != null)
        {
            if (current == parent)
            {
                return true;
            }
            current = current.parent;
        }
        return false;
    }

        private static void ShowConnectedDialog(INetworkClient client)
    {
        ShowRoomPlayerListOverlay();
    }

        private static void ShowJoinConfirmDialog()
    {
        ConfigManager config = TryGetConfig();
        string ip = config?.ServerIP?.Value ?? "127.0.0.1";
        int port = config?.ServerPort?.Value ?? 7777;
        if (port <= 0)
        {
            port = 7777;
        }

        UiManager.GetDialog<MessageDialog>().Show(
            new MessageContent
            {
                Text = $"将作为客户端加入服务器：{ip}:{port}\n\n确认：开始连接\n取消：关闭",
                Icon = MessageIcon.Warning,
                Buttons = DialogButtons.ConfirmCancel,
                OnConfirm = () => TryConnectToServerAndShowRoomList(ip, port),
                OnCancel = null,
            }
        );
    }

    #endregion

    #region 房间玩家列表面板（独立界面，非 MessageDialog 弹窗）

        private static void ShowRoomPlayerListOverlay()
    {
        if (!UiManager.IsInitialized)
        {
            return;
        }

        try
        {
            Transform parent = TryGetRootCanvasRectTransform(UiManager.Instance?.transform) ?? UiManager.Instance?.transform;
            if (parent == null)
            {
                Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 无法获取根画布，房间玩家列表未显示。");
                return;
            }

            Button template = TryGetButtonTemplate();
            EnsureRoomListOverlay(parent, template);
            if (_roomListRoot == null)
            {
                Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 房间玩家列表面板构建失败。");
                return;
            }

            SubscribeRoomListEvents();
            RefreshRoomList();
            _roomListRoot.SetActive(true);
            _roomListRoot.transform.SetAsLastSibling();
            try
            {
                _roomListAnimator?.PlayOpen();
            }
            catch
            {

            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 显示房间玩家列表面板失败: {ex}");
        }
    }

        private static Button TryGetButtonTemplate()
    {
        try
        {
            if (_multiplayerButton != null)
            {
                return _multiplayerButton;
            }

            MainMenuPanel panel = _lastMainMenuPanel ?? UiManager.GetPanel<MainMenuPanel>();
            if (panel != null)
            {
                var buttons = panel.GetComponentsInChildren<Button>(true);
                if (buttons != null)
                {
                    foreach (var b in buttons)
                    {
                        if (b == null) continue;
                        if (b.name == MultiplayerButtonName || b.name == RoomListRootName) continue;
                        return b;
                    }
                }
            }
        }
        catch
        {

        }
        return null;
    }

        private static void EnsureRoomListOverlay(Transform parent, Button template)
    {
        if (_roomListRoot != null)
        {
            if (_roomListRoot.transform.parent != parent)
            {
                _roomListRoot.transform.SetParent(parent, false);
            }
            return;
        }

        try
        {
            _roomListFont = _defaultFont ?? FindDefaultFont(parent);

            GameObject root = new GameObject(RoomListRootName);
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling();
            _roomListRoot = root;

            var rootRect = root.AddComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            var bg = root.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.62f);
            bg.raycastTarget = true;

            var rootGroup = root.AddComponent<CanvasGroup>();
            rootGroup.alpha = 0f;
            rootGroup.interactable = false;
            rootGroup.blocksRaycasts = true;

            float panelScale = 2.2f;
            try
            {
                const float baseW = 800f;
                const float baseH = 580f;
                var parentRect = parent.GetComponent<RectTransform>();
                if (parentRect != null)
                {
                    float maxW = Mathf.Max(1f, parentRect.rect.width * 0.92f);
                    float maxH = Mathf.Max(1f, parentRect.rect.height * 0.92f);
                    panelScale = Mathf.Clamp(Mathf.Min(maxW / baseW, maxH / baseH), 1f, 3.0f);
                }
            }
            catch
            {
                panelScale = 2.2f;
            }
            _roomListPanelScale = panelScale;

            GameObject frame = new GameObject(RoomListRootName + "_Frame");
            frame.transform.SetParent(root.transform, false);
            var frameRect = frame.AddComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0.5f, 0.5f);
            frameRect.anchorMax = new Vector2(0.5f, 0.5f);
            frameRect.pivot = new Vector2(0.5f, 0.5f);
            frameRect.sizeDelta = new Vector2(800f * panelScale, 580f * panelScale);
            frameRect.anchoredPosition = Vector2.zero;
            _roomListFrameRect = frameRect;

            var frameBg = frame.AddComponent<Image>();
            frameBg.color = new Color(0.10f, 0.09f, 0.14f, 0.96f);
            frameBg.raycastTarget = true;

            try
            {
                var outline = frame.AddComponent<Outline>();
                outline.effectColor = new Color(0.78f, 0.63f, 0.25f, 0.9f);
                outline.effectDistance = new Vector2(3f * panelScale, 3f * panelScale);
            }
            catch
            {

            }

            CreateHorizontalRule(frame.transform, RoomListRootName + "_TopRule", 1f, -88f * panelScale, 2f * panelScale, new Color(0.78f, 0.63f, 0.25f, 0.9f));

            CreateHorizontalRule(frame.transform, RoomListRootName + "_BottomRule", 0f, 70f * panelScale, 2f * panelScale, new Color(0.78f, 0.63f, 0.25f, 0.6f));

            GameObject titleGo = new GameObject("Title");
            titleGo.transform.SetParent(frame.transform, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -12f * panelScale);
            titleRt.sizeDelta = new Vector2(-40f * panelScale, 36f * panelScale);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text = "房间玩家列表";
            title.alignment = TextAlignmentOptions.Center;
            title.fontSize = Mathf.Clamp(22f * panelScale, 20f, 72f);
            title.color = new Color(1f, 0.92f, 0.6f, 1f);
            title.raycastTarget = false;
            if (_roomListFont != null) title.font = _roomListFont;
            _roomListTitleText = title;

            GameObject subGo = new GameObject("Subtitle");
            subGo.transform.SetParent(frame.transform, false);
            var subRt = subGo.AddComponent<RectTransform>();
            subRt.anchorMin = new Vector2(0f, 1f);
            subRt.anchorMax = new Vector2(1f, 1f);
            subRt.pivot = new Vector2(0.5f, 1f);
            subRt.anchoredPosition = new Vector2(0f, -50f * panelScale);
            subRt.sizeDelta = new Vector2(-40f * panelScale, 26f * panelScale);
            _roomListEmptyText = subGo.AddComponent<TextMeshProUGUI>();
            _roomListEmptyText.alignment = TextAlignmentOptions.Center;
            _roomListEmptyText.fontSize = Mathf.Clamp(15f * panelScale, 14f, 48f);
            _roomListEmptyText.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            _roomListEmptyText.raycastTarget = false;
            if (_roomListFont != null) _roomListEmptyText.font = _roomListFont;

            GameObject scrollGo = new GameObject("PlayerScroll");
            scrollGo.transform.SetParent(frame.transform, false);
            var scrollRt = scrollGo.AddComponent<RectTransform>();
            scrollRt.anchorMin = new Vector2(0.04f, 0f);
            scrollRt.anchorMax = new Vector2(0.96f, 1f);
            scrollRt.offsetMin = new Vector2(0f, 74f * panelScale);
            scrollRt.offsetMax = new Vector2(0f, -92f * panelScale);

            var scrollImg = scrollGo.AddComponent<Image>();
            scrollImg.color = new Color(0f, 0f, 0f, 0.35f);
            scrollImg.raycastTarget = true;

            var scrollRect = scrollGo.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _roomListScroll = scrollRect;

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
            vlg.spacing = 8f * panelScale;
            int padVal = Mathf.RoundToInt(8f * panelScale);
            vlg.padding = new RectOffset(padVal, padVal, padVal, padVal);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var csf = contentGo.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewportRt;
            scrollRect.content = contentRt;
            _roomListContainer = contentRt;

            GameObject buttonsGo = new GameObject("Buttons");
            buttonsGo.transform.SetParent(frame.transform, false);
            var buttonsRt = buttonsGo.AddComponent<RectTransform>();
            buttonsRt.anchorMin = new Vector2(0.04f, 0f);
            buttonsRt.anchorMax = new Vector2(0.96f, 0f);
            buttonsRt.pivot = new Vector2(0.5f, 0f);
            buttonsRt.sizeDelta = new Vector2(0f, 50f * panelScale);
            buttonsRt.anchoredPosition = new Vector2(0f, 15f * panelScale);

            var hlg = buttonsGo.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.spacing = 16f * panelScale;

            var refreshBtn = CreateTextActionButton(buttonsGo.transform, RoomListRootName + "_RefreshBtn", "刷新", panelScale, RefreshRoomList);

            var closeBtn = CreateTextActionButton(buttonsGo.transform, RoomListRootName + "_CloseBtn", "更换角色", panelScale, HideRoomListOverlay);
            _roomListCloseBtn = closeBtn;

            var disconnectBtn = CreateTextActionButton(buttonsGo.transform, RoomListRootName + "_DisconnectBtn", "断开联机", panelScale, () =>
            {
                INetworkClient c = TryGetNetworkClient();
                Disconnect(c);
                HideRoomListOverlay();
            });

            _readyOrStartButton = CreateTextActionButton(buttonsGo.transform, RoomListRootName + "_ReadyStartBtn", "准备就绪", panelScale, OnActionBtnClicked);

            try
            {
                _roomListAnimator = root.AddComponent<PanelAnimator>();
                _roomListAnimator.Init(rootGroup, frameRect);
            }
            catch
            {

            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 构建房间玩家列表面板失败: {ex}");
            if (_roomListRoot != null) UnityEngine.Object.Destroy(_roomListRoot);
            _roomListRoot = null;
        }
    }

        private static Button CreateTextActionButton(Transform parent, string name, string labelText, float panelScale, Action onClick)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(150f * panelScale, 46f * panelScale);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 46f * panelScale;
        le.preferredWidth = 140f * panelScale;
        le.flexibleWidth = 1f;
        le.flexibleHeight = 1f;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = labelText;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = Mathf.Clamp(18f * panelScale, 15f, 44f);
        tmp.color = new Color(0.96f, 0.90f, 0.74f, 1f);
        tmp.raycastTarget = true;
        tmp.enableWordWrapping = false;
        if (_roomListFont != null)
        {
            tmp.font = _roomListFont;
        }
        else if (_defaultFont != null)
        {
            tmp.font = _defaultFont;
        }

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = tmp;
        btn.transition = Selectable.Transition.ColorTint;

        var colors = btn.colors;
        colors.normalColor = new Color(0.96f, 0.90f, 0.74f, 1f);
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.85f, 0.78f, 0.55f, 1f);
        colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        colors.fadeDuration = 0.08f;
        btn.colors = colors;

        var nav = btn.navigation;
        nav.mode = Navigation.Mode.None;
        btn.navigation = nav;

        go.AddComponent<TextButtonHover>();

        if (onClick != null)
        {
            btn.onClick.AddListener(() => onClick());
        }

        return btn;
    }

    private sealed class TextButtonHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public float HoverScale = 1.10f;
        public float PressedScale = 1.18f;
        public float AnimationSpeed = 12f;

        private RectTransform _rt;
        private Button _btn;
        private bool _hovering;
        private bool _pressed;
        private float _targetScale = 1f;

        private void Awake()
        {
            _rt = transform as RectTransform;
            _btn = GetComponent<Button>();
            ResetScale();
        }

        private void OnEnable()
        {
            ResetScale();
        }

        private void OnDisable()
        {
            _hovering = false;
            _pressed = false;
            ResetScale();
        }

        private void ResetScale()
        {
            _targetScale = 1f;
            if (_rt != null) _rt.localScale = Vector3.one;
        }

        private void Update()
        {
            if (_rt == null) return;
            float cur = _rt.localScale.x;
            if (Mathf.Approximately(cur, _targetScale))
                return;
            float next = Mathf.Lerp(cur, _targetScale, Time.unscaledDeltaTime * AnimationSpeed);
            _rt.localScale = new Vector3(next, next, 1f);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_btn != null && !_btn.interactable) return;
            _hovering = true;
            ApplyScale();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
            _pressed = false;
            ApplyScale();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_btn != null && !_btn.interactable) return;
            _pressed = true;
            ApplyScale();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _pressed = false;
            ApplyScale();
        }

        private void ApplyScale()
        {
            if (_rt == null) return;
            if (_btn != null && !_btn.interactable)
            {
                _targetScale = 1f;
                return;
            }
            _targetScale = _pressed ? PressedScale : _hovering ? HoverScale : 1f;
        }
    }

    public static void OnLobbyGameStartedReceived()
    {
        try
        {
            if (_hasEnteredGameRun)
            {
                Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] OnLobbyGameStartedReceived: 已进入游戏，忽略重复广播");
                return;
            }
            HideRoomListOverlay(immediate: true);

            Singleton<GameMaster>.Instance.StartCoroutine(DelayedStartGame());
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] OnLobbyGameStartedReceived 失败: {ex}");
        }
    }

    private static IEnumerator DelayedStartGame()
    {
        if (_hasEnteredGameRun)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] DelayedStartGame: 已进入游戏，忽略重复调用");
            yield break;
        }
        _hasEnteredGameRun = true;
        HideRoomListOverlay(immediate: true);

        yield return null;
        _isSilentStarting = true;
        GameMasterSaveHookPatch.IsMultiplayerRunActive = true;
        try
        {
            StartGamePanel panel = null;
            try
            {
                panel = _lastStartGamePanel ?? UiManager.GetPanel<StartGamePanel>();
            }
            catch
            {
                panel = null;
            }

            bool isHost = NetworkIdentityTracker.GetSelfIsHost();
            if (isHost)
            {
                if (panel == null)
                {
                    Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 房主未打开 StartGamePanel，无法自动进入游戏");
                    yield break;
                }

                Button confirmBtn = Traverse.Create(panel).Field("characterConfirmButton").GetValue<Button>();
                if (confirmBtn != null)
                {
                    confirmBtn.onClick?.Invoke();
                    Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 房主已自动确认角色，进入难度选择");
                }
            }
            else
            {

                Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 客机直接静默开局中...");

                PlayerUnit playerUnit = null;
                LBoL.Core.Units.PlayerType playerType = LBoL.Core.Units.PlayerType.TypeA;
                LBoL.Core.Exhibit exhibit = null;
                IEnumerable<LBoL.Core.Cards.Card> deck = null;
                LBoL.Core.Units.UltimateSkill us = null;
                IEnumerable<LBoL.Core.Stage> stages = null;
                Type debutAdventure = null;
                bool gameModeIsOn = false;
                bool showRandomResult = false;

                var traverse = panel != null ? Traverse.Create(panel) : null;
                var typeCandidate = traverse?.Field("_typeCandidate").GetValue();

                if (panel != null && typeCandidate != null)
                {
                    var player = traverse.Field("_player").GetValue<PlayerUnit>();
                    var selectedType = traverse.Field("_selectedType").GetValue<int>();

                    exhibit = Traverse.Create(typeCandidate).Field("Exhibit").GetValue<LBoL.Core.Exhibit>();
                    deck = Traverse.Create(typeCandidate).Field("Deck").GetValue<IEnumerable<LBoL.Core.Cards.Card>>();
                    us = Traverse.Create(typeCandidate).Field("Us").GetValue<LBoL.Core.Units.UltimateSkill>();

                    stages = traverse.Field("_stages").GetValue<IEnumerable<LBoL.Core.Stage>>();
                    debutAdventure = traverse.Field("_debutAdventure").GetValue<Type>();
                    var gameModeSwitch = traverse.Field("gameModeSwitch").GetValue();
                    var randomResultSwitch = traverse.Field("randomResultSwitch").GetValue();

                    gameModeIsOn = gameModeSwitch != null && Traverse.Create(gameModeSwitch).Property<bool>("IsOn").Value;
                    showRandomResult = randomResultSwitch != null && Traverse.Create(randomResultSwitch).Property<bool>("IsOn").Value;

                    playerUnit = LBoL.Core.Library.CreatePlayerUnit(player.GetType());
                    playerType = selectedType == 1 ? LBoL.Core.Units.PlayerType.TypeB : LBoL.Core.Units.PlayerType.TypeA;
                }
                else
                {
                    Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 未找到 StartGamePanel 或角色尚未就绪，使用网络同步的角色及默认参数启动...");

                    string selfId = NetworkIdentityTracker.GetSelfPlayerId();
                    string charaId = "Reimu";
                    INetworkManager netManager = ServiceProvider?.GetService<INetworkManager>();
                    if (netManager != null && !string.IsNullOrEmpty(selfId))
                    {
                        var selfPlayer = netManager.GetPlayer(selfId);
                        if (selfPlayer != null && !string.IsNullOrEmpty(selfPlayer.chara))
                        {
                            charaId = selfPlayer.chara;
                        }
                    }

                    Type charaType = typeof(LBoL.EntityLib.PlayerUnits.Reimu).Assembly.GetType($"LBoL.EntityLib.PlayerUnits.{charaId}")
                                  ?? Type.GetType($"LBoL.EntityLib.PlayerUnits.{charaId}, LBoL.EntityLib")
                                  ?? Type.GetType($"LBoL.Core.Units.{charaId}")
                                  ?? typeof(LBoL.EntityLib.PlayerUnits.Reimu);
                    playerUnit = LBoL.Core.Library.CreatePlayerUnit(charaType);

                    var config = playerUnit.Config;
                    if (config != null)
                    {
                        us = LBoL.Core.Library.CreateUs(config.UltimateSkillA);
                        exhibit = LBoL.Core.Library.CreateExhibit(config.ExhibitA);
                        deck = config.DeckA.Select(cardId => LBoL.Core.Library.CreateCard(cardId)).ToArray();
                    }

                    stages = new LBoL.Core.Stage[]
                    {
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.BambooForest>(),
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.XuanwuRavine>(),
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.WindGodLake>().AsNormalFinal(),
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.FinalStage>().AsTrueEndFinal()
                    };
                    debutAdventure = typeof(LBoL.EntityLib.Adventures.Debut);
                }

                if (playerUnit != null)
                {
                    var config = playerUnit.Config;
                    if (config != null)
                    {
                        if (us == null)
                        {
                            us = LBoL.Core.Library.CreateUs(config.UltimateSkillA);
                            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] us 为 null，已从 Config.UltimateSkillA 补全");
                        }
                        if (exhibit == null)
                        {
                            exhibit = LBoL.Core.Library.CreateExhibit(config.ExhibitA);
                            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] exhibit 为 null，已从 Config.ExhibitA 补全");
                        }
                        if (deck == null)
                        {
                            deck = config.DeckA.Select(cardId => LBoL.Core.Library.CreateCard(cardId)).ToArray();
                            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] deck 为 null，已从 Config.DeckA 补全");
                        }
                    }
                }
                if (stages == null)
                {
                    stages = new LBoL.Core.Stage[]
                    {
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.BambooForest>(),
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.XuanwuRavine>(),
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.WindGodLake>().AsNormalFinal(),
                        LBoL.Core.Library.CreateStage<LBoL.EntityLib.Stages.NormalStages.FinalStage>().AsTrueEndFinal()
                    };
                    Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] stages 为 null，已使用默认关卡列表补全");
                }
                if (debutAdventure == null)
                {
                    debutAdventure = typeof(LBoL.EntityLib.Adventures.Debut);
                    Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] debutAdventure 为 null，已使用默认 Debut 补全");
                }

                if (playerUnit != null && us != null)
                {
                    playerUnit.SetUs(us);
                }

                LBoL.Core.GameDifficulty difficulty = LBoL.Core.GameDifficulty.Normal;
                LBoL.Core.PuzzleFlag puzzles = LBoL.Core.PuzzleFlag.None;
                LBoL.Core.GameMode finalGameMode = gameModeIsOn ? LBoL.Core.GameMode.StoryMode : LBoL.Core.GameMode.FreeMode;
                bool finalShowRandom = showRandomResult;
                IEnumerable<LBoL.Core.JadeBox> jadeBoxes = Array.Empty<LBoL.Core.JadeBox>();

                ulong hostSeed = 0;
                List<string> hostStageNames = null;
                string hostDebutName = null;
                List<string> hostJadeBoxIds = null;

                if (NetworkPlugin.Patch.Network.GameSeedSyncPatch.TryGetCachedHostConfig(
                    out hostSeed,
                    out var hostDiff,
                    out var hostPuzzles,
                    out var hostMode,
                    out var hostShowRandom,
                    out hostStageNames,
                    out hostDebutName,
                    out hostJadeBoxIds))
                {
                    difficulty = hostDiff;
                    puzzles = hostPuzzles;
                    finalGameMode = hostMode;
                    finalShowRandom = hostShowRandom;

                    if (hostJadeBoxIds != null && hostJadeBoxIds.Count > 0)
                    {
                        var jbList = new List<LBoL.Core.JadeBox>();
                        foreach (var id in hostJadeBoxIds)
                        {
                            try
                            {
                                var jb = LBoL.Core.Library.CreateJadeBox(id);
                                if (jb != null)
                                {
                                    jbList.Add(jb);
                                }
                            }
                            catch (Exception ex)
                            {
                                Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 实例化房主玉匣失败: id={id}, err={ex.Message}");
                            }
                        }
                        jadeBoxes = jbList;
                    }

                    if (hostStageNames != null && hostStageNames.Count > 0)
                    {
                        var stageList = new List<LBoL.Core.Stage>();
                        foreach (var name in hostStageNames)
                        {
                            try
                            {
                                Type stageType = Type.GetType($"LBoL.EntityLib.Stages.NormalStages.{name}")
                                              ?? Type.GetType($"LBoL.Core.Stages.{name}");
                                if (stageType != null)
                                {
                                    var method = typeof(LBoL.Core.Library).GetMethod("CreateStage", BindingFlags.Public | BindingFlags.Static);
                                    if (method != null)
                                    {
                                        var generic = method.MakeGenericMethod(stageType);
                                        var stage = (LBoL.Core.Stage)generic.Invoke(null, null);
                                        if (stage != null)
                                        {
                                            stageList.Add(stage);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 反射创建同步 Stage 失败: {name}, {ex.Message}");
                            }
                        }
                        if (stageList.Count > 0)
                        {
                            if (stageList.Count >= 2)
                            {
                                stageList[stageList.Count - 2] = stageList[stageList.Count - 2].AsNormalFinal();
                                stageList[stageList.Count - 1] = stageList[stageList.Count - 1].AsTrueEndFinal();
                            }
                            stages = stageList;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(hostDebutName))
                    {
                        try
                        {
                            Type debutType = Type.GetType($"LBoL.EntityLib.Adventures.{hostDebutName}")
                                          ?? Type.GetType($"LBoL.Core.Adventures.{hostDebutName}");
                            if (debutType != null)
                            {
                                debutAdventure = debutType;
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 反射同步首发事件失败: {hostDebutName}, {ex.Message}");
                        }
                    }

                    Plugin.Logger?.LogInfo($"[MainMenuMultiplayerEntry] 已成功同步房主开局配置: Difficulty={difficulty}, Puzzles={puzzles}, Mode={finalGameMode}, JadeBoxCount={hostJadeBoxIds?.Count}");
                }

                GameMaster.StartGame(
                    difficulty,
                    puzzles,
                    playerUnit,
                    playerType,
                    exhibit,
                    default(int?),
                    deck,
                    stages,
                    debutAdventure,
                    jadeBoxes,
                    finalGameMode,
                    finalShowRandom
                );

                if (panel != null)
                {
                    panel.Hide();
                }
                Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 客机已成功静默启动游戏");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 延迟启动游戏失败: {ex}");
        }
        finally
        {
            _isSilentStarting = false;
        }
    }

        internal static void RefreshRoomList()
    {
        if (_roomListRoot == null || _roomListContainer == null)
        {
            return;
        }

        try
        {
            foreach (Transform child in _roomListContainer)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            INetworkManager manager = ServiceProvider?.GetService<INetworkManager>();
            string selfId = NetworkIdentityTracker.GetSelfPlayerId();
            bool selfIsHost = NetworkIdentityTracker.GetSelfIsHost();

            var entries = new List<(string Id, string Name, bool IsHost, string CharaId, bool IsReady)>();
            if (manager != null)
            {
                foreach (INetworkPlayer p in manager.GetAllPlayers() ?? Enumerable.Empty<INetworkPlayer>())
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.playerId)) continue;

                    bool isConnected = OtherPlayersOverlayPatch.IsPlayerConnected(p.playerId);

                    if (!isConnected && !string.Equals(p.playerId, selfId, StringComparison.Ordinal))
                    {

                        continue;
                    }

                    string name = string.IsNullOrWhiteSpace(p.userName) ? p.playerId : p.userName;
                    string charaId = p.chara;
                    bool isReady = isConnected && _playerReadyStates.TryGetValue(p.playerId, out var rdy) && rdy;
                    entries.Add((p.playerId, name, p.IsLobbyOwner(), charaId, isReady));
                }
            }

            if (entries.Count == 0 && !string.IsNullOrWhiteSpace(selfId))
            {
                entries.Add((selfId, ResolveSelfDisplayName(), selfIsHost, null, false));
            }

            entries.Sort((a, b) =>
            {
                int ha = a.IsHost ? 0 : 1;
                int hb = b.IsHost ? 0 : 1;
                if (ha != hb) return ha.CompareTo(hb);
                bool aSelf = !string.IsNullOrWhiteSpace(selfId) && string.Equals(a.Id, selfId, StringComparison.Ordinal);
                bool bSelf = !string.IsNullOrWhiteSpace(selfId) && string.Equals(b.Id, selfId, StringComparison.Ordinal);
                if (aSelf != bSelf) return aSelf ? -1 : 1;
                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });

            bool isResume = _lobbyResumeMode || _clientLobbyResumeMode;
            bool hostInGrace = !selfIsHost && NetworkIdentityTracker.IsHostInGracePeriod() && !NetworkIdentityTracker.HasOnlineHost();

            if (_roomListTitleText != null)
            {
                if (hostInGrace)
                {
                    _roomListTitleText.text = "联机房间【房主已断线】";
                }
                else
                {
                    _roomListTitleText.text = isResume ? "联机房间【继续存档】" : "房间玩家列表";
                }
            }

            if (_roomListEmptyText != null)
            {
                if (hostInGrace)
                {
                    _roomListEmptyText.text = "【房主已断线】等待房主恢复连接中…";
                }
                else if (isResume)
                {
                    if (selfIsHost)
                    {
                        string charaRaw = _resumingSave?.Player?.Name ?? "未知角色";
                        string chara = ResolveCharacterDisplayName(charaRaw);
                        string diff = _resumingSave != null ? _resumingSave.Difficulty.ToString() : "Normal";
                        _roomListEmptyText.text = $"存档进度: 角色 {chara} - 难度 {diff} (共 {entries.Count} 名玩家)";
                    }
                    else
                    {
                        _roomListEmptyText.text = _isOldResumePlayer
                            ? $"【老玩家继承】角色已锁定，发车将恢复旧进度 (共 {entries.Count} 名玩家)"
                            : $"【新玩家加入】可自由选择角色，发车将追赶进度 (共 {entries.Count} 名玩家)";
                    }
                }
                else
                {
                    _roomListEmptyText.text = entries.Count == 0
                        ? "暂无玩家（等待连接或玩家加入…）"
                        : $"共 {entries.Count} 名玩家";
                }
            }

            if (_roomListCloseBtn != null)
            {
                if (selfIsHost && _lobbyResumeMode)
                {
                    _roomListCloseBtn.interactable = false;
                    var btnTxt = _roomListCloseBtn.GetComponentInChildren<TextMeshProUGUI>();
                    if (btnTxt != null) btnTxt.text = "角色已锁定";
                }
                else if (!selfIsHost && _clientLobbyResumeMode && _isOldResumePlayer)
                {
                    _roomListCloseBtn.interactable = false;
                    var btnTxt = _roomListCloseBtn.GetComponentInChildren<TextMeshProUGUI>();
                    if (btnTxt != null) btnTxt.text = "角色已锁定";
                }
                else
                {
                    _roomListCloseBtn.interactable = true;
                    var btnTxt = _roomListCloseBtn.GetComponentInChildren<TextMeshProUGUI>();
                    if (btnTxt != null) btnTxt.text = "更换角色";
                }
            }

            if (selfIsHost && _lobbyResumeMode && _resumingSave != null)
            {
                string hostChara = _resumingSave.Player?.Name ?? "Reimu";
                var netManager = ServiceProvider?.GetService<NetworkManager>();
                if (netManager != null)
                {
                    var self = netManager.GetSelf();
                    if (self != null && self.chara != hostChara)
                    {
                        self.chara = hostChara;
                        INetworkClient client = TryGetNetworkClient();
                        client?.SendRequest(NetworkMessageTypes.UpdatePlayerLocation, JsonCompat.Serialize(new { CharacterId = hostChara }));
                    }
                }

                INetworkClient netClient = TryGetNetworkClient();
                if (netClient != null && netClient.IsConnected)
                {
                    var roster = MultiplayerSaveManager.LoadRoster(_resumingSave.RootSeed);
                    netClient.SendGameEventData(NetworkMessageTypes.OnLobbyResumeInfo, new
                    {
                        RootSeed = _resumingSave.RootSeed,
                        Difficulty = (int)_resumingSave.Difficulty,
                        GameMode = (int)_resumingSave.Mode,
                        HostChara = hostChara,
                        HostPlayerId = selfId,
                        Roster = roster
                    });
                }
            }

            if (entries.Count == 0)
            {
                _roomListScroll?.gameObject.SetActive(false);
            }
            else
            {
                _roomListScroll?.gameObject.SetActive(true);
                foreach (var e in entries)
                {
                    bool isSelf = !string.IsNullOrWhiteSpace(selfId)
                        && string.Equals(e.Id, selfId, StringComparison.Ordinal);
                    CreateRoomListRow(_roomListContainer, e.Id, e.Name, e.IsHost, isSelf, e.CharaId, e.IsReady, _roomListPanelScale);
                }
            }

            UpdateActionBtn(selfIsHost, selfId);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 刷新房间玩家列表失败: {ex}");
        }
    }

        private static void UpdateActionBtn(bool selfIsHost, string selfId)
    {
        if (_readyOrStartButton == null) return;

        try
        {
            if (selfIsHost)
            {
                bool allReady = AreAllClientsReady(selfId);
                _readyOrStartButton.interactable = allReady;
                var label = _readyOrStartButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = allReady ? "开始游戏" : "等待玩家准备…";
            }
            else
            {
                bool hasOnlineHost = NetworkIdentityTracker.HasOnlineHost();
                bool hostInGrace = NetworkIdentityTracker.IsHostInGracePeriod();

                if (!hasOnlineHost && hostInGrace)
                {
                    _readyOrStartButton.interactable = false;
                    var label = _readyOrStartButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (label != null) label.text = "【房主已断线】等待恢复…";
                }
                else
                {
                    bool isReady = _playerReadyStates.TryGetValue(selfId ?? "", out var rdy) && rdy;
                    _readyOrStartButton.interactable = true;
                    var label = _readyOrStartButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (label != null) label.text = isReady ? "取消准备" : "准备就绪";
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[MainMenuMultiplayerEntry] UpdateRoomListActionButtons 异常: {ex.Message}");
        }
    }

        private static bool AreAllClientsReady(string selfId)
    {
        INetworkManager manager = ServiceProvider?.GetService<INetworkManager>();
        if (manager == null) return false;

        foreach (INetworkPlayer p in manager.GetAllPlayers() ?? Enumerable.Empty<INetworkPlayer>())
        {
            if (p == null || string.IsNullOrWhiteSpace(p.playerId)) continue;
            if (p.IsLobbyOwner()) continue;
            if (!_playerReadyStates.TryGetValue(p.playerId, out var rdy) || !rdy)
                return false;
        }
        return true;
    }

        private static void OnActionBtnClicked()
    {
        try
        {
            string selfId = NetworkIdentityTracker.GetSelfPlayerId();
            bool selfIsHost = NetworkIdentityTracker.GetSelfIsHost();
            INetworkClient client = TryGetNetworkClient();

            if (selfIsHost)
            {
                if (AreAllClientsReady(selfId))
                {
                    if (_hasEnteredGameRun)
                    {
                        Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 房主已在开局流程中，忽略重复点击");
                        return;
                    }

                    if (_lobbyResumeMode && _resumingSave != null)
                    {
                        _hasEnteredGameRun = true;
                        Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 房主点击开始游戏 (继续存档模式)");

                        // 1. 广播恢复发车通知
                        if (client != null)
                        {
                            client.SendGameEventData(
                                NetworkMessageTypes.OnLobbyResumeGame,
                                new
                                {
                                    RootSeed = _resumingSave.RootSeed,
                                    Difficulty = (int)_resumingSave.Difficulty,
                                    GameMode = (int)_resumingSave.Mode,
                                    Puzzles = (int)_resumingSave.Puzzles,
                                    ShowRandomResult = _resumingSave.ShowRandomResult,
                                    StageIndex = _resumingSave.StageIndex ?? 0
                                }
                            );
                        }

                        // 2. 房主持久化 Roster
                        string hostChara = _resumingSave.Player?.Name ?? "Reimu";
                        string hostId = NetworkIdentityTracker.GetSelfPlayerId();
                        string hostName = ResolveSelfDisplayName();
                        INetworkManager netManager = ServiceProvider?.GetService<INetworkManager>();
                        MultiplayerSaveManager.RecordRoomPlayers(_resumingSave.RootSeed, hostId, hostName, hostChara, netManager?.GetAllPlayers());

                        // 3. 标记联机运行活跃并恢复游戏
                        GameMasterSaveHookPatch.IsMultiplayerRunActive = true;
                        HideRoomListOverlay(immediate: true);
                        Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 房主跳过角色/难度确认，直接恢复存档进入游戏...");
                        GameMaster.RestoreGameRun(_resumingSave);

                        _lobbyResumeMode = false;
                        _resumingSave = null;
                        return;
                    }

                    _hasEnteredGameRun = true;
                    Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 房主开始游戏");
                    GameMasterSaveHookPatch.IsMultiplayerRunActive = true;
                    StartGamePanel startGamePanel = UiManager.GetPanel<StartGamePanel>();
                    Button confirmBtn = startGamePanel != null
                        ? Traverse.Create(startGamePanel).Field("characterConfirmButton").GetValue<Button>()
                        : null;
                    confirmBtn?.onClick?.Invoke();
                    HideRoomListOverlay(immediate: true);
                }
            }
            else
            {

                bool isReady = _playerReadyStates.TryGetValue(selfId ?? "", out var rdy) && rdy;
                bool newReady = !isReady;
                _playerReadyStates[selfId ?? ""] = newReady;

                if (client != null)
                {
                    client.SendGameEventData(NetworkMessageTypes.PlayerReadyChanged, new { IsReady = newReady });
                    Plugin.Logger?.LogInfo($"[MainMenuMultiplayerEntry] 发送准备状态: {newReady}");
                }

                RefreshRoomList();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] OnActionBtnClicked 失败: {ex}");
        }
    }

    private static void CreateRoomListRow(Transform container, string playerId, string playerName, bool isHost, bool isSelf, string charaId, bool isReady, float panelScale)
    {
        GameObject go = new GameObject("RoomPlayer");
        go.transform.SetParent(container, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.12f, 0.11f, 0.15f, 0.85f);
        img.raycastTarget = false;

        try
        {
            var borderOutline = go.AddComponent<Outline>();
            borderOutline.effectColor = isHost
                ? new Color(0.95f, 0.78f, 0.25f, 0.5f)
                : (isSelf ? new Color(0.25f, 0.65f, 0.95f, 0.5f) : new Color(0.6f, 0.6f, 0.6f, 0.25f));
            borderOutline.effectDistance = new Vector2(1.5f * panelScale, 1.5f * panelScale);
        }
        catch
        {

        }

        GameObject leftStripe = new GameObject("LeftStripe");
        leftStripe.transform.SetParent(go.transform, false);
        var stripeRt = leftStripe.AddComponent<RectTransform>();
        stripeRt.anchorMin = new Vector2(0f, 0f);
        stripeRt.anchorMax = new Vector2(0f, 1f);
        stripeRt.pivot = new Vector2(0f, 0.5f);
        stripeRt.sizeDelta = new Vector2(6f * panelScale, 0f);
        stripeRt.anchoredPosition = Vector2.zero;
        var stripeImg = leftStripe.AddComponent<Image>();
        stripeImg.color = isHost
            ? new Color(0.95f, 0.78f, 0.25f, 1f)
            : (isSelf ? new Color(0.25f, 0.65f, 0.95f, 1f) : new Color(0.75f, 0.75f, 0.75f, 1f));
        stripeImg.raycastTarget = false;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 50f * panelScale;
        le.flexibleWidth = 1f;

        Sprite avatar = null;
        if (!string.IsNullOrWhiteSpace(charaId))
        {
            avatar = OtherPlayersOverlayPatch.TryGetAvatarSprite(charaId);
        }

        if (avatar == null)
        {
            avatar = OtherPlayersOverlayPatch.TryGetAvatarSprite("Koishi");
        }

        if (avatar != null)
        {

            GameObject avatarRoot = new GameObject("AvatarRoot");
            avatarRoot.transform.SetParent(go.transform, false);
            var avatarRootRt = avatarRoot.AddComponent<RectTransform>();
            avatarRootRt.anchorMin = new Vector2(0f, 0.5f);
            avatarRootRt.anchorMax = new Vector2(0f, 0.5f);
            avatarRootRt.pivot = new Vector2(0.5f, 0.5f);
            avatarRootRt.sizeDelta = new Vector2(38f * panelScale, 38f * panelScale);
            avatarRootRt.anchoredPosition = new Vector2(35f * panelScale, 0f);

            GameObject maskGo = new GameObject("AvatarMask");
            maskGo.transform.SetParent(avatarRoot.transform, false);
            var maskRt = maskGo.AddComponent<RectTransform>();
            maskRt.anchorMin = new Vector2(0.5f, 0.5f);
            maskRt.anchorMax = new Vector2(0.5f, 0.5f);
            maskRt.pivot = new Vector2(0.5f, 0.5f);
            maskRt.sizeDelta = new Vector2(34f * panelScale, 34f * panelScale);
            maskRt.anchoredPosition = Vector2.zero;

            var maskImg = maskGo.AddComponent<Image>();
            maskImg.sprite = OtherPlayersOverlayPatch.GetCircleMaskSprite();
            maskImg.color = Color.white;
            maskImg.raycastTarget = false;
            maskImg.preserveAspect = true;

            var mask = maskGo.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            GameObject avatarGo = new GameObject("AvatarImage");
            avatarGo.transform.SetParent(maskGo.transform, false);
            var avatarRt = avatarGo.AddComponent<RectTransform>();
            avatarRt.anchorMin = new Vector2(0.5f, 0.5f);
            avatarRt.anchorMax = new Vector2(0.5f, 0.5f);
            avatarRt.pivot = new Vector2(0.5f, 0.5f);
            avatarRt.sizeDelta = new Vector2(34f * panelScale, 34f * panelScale);
            avatarRt.anchoredPosition = Vector2.zero;

            var avatarImg = avatarGo.AddComponent<Image>();
            avatarImg.sprite = avatar;
            avatarImg.raycastTarget = false;
            avatarImg.preserveAspect = true;

            GameObject borderGo = new GameObject("Border");
            borderGo.transform.SetParent(avatarRoot.transform, false);
            var borderRt = borderGo.AddComponent<RectTransform>();
            borderRt.anchorMin = new Vector2(0.5f, 0.5f);
            borderRt.anchorMax = new Vector2(0.5f, 0.5f);
            borderRt.pivot = new Vector2(0.5f, 0.5f);
            borderRt.sizeDelta = new Vector2(38f * panelScale, 38f * panelScale);
            borderRt.anchoredPosition = Vector2.zero;

            var borderImg = borderGo.AddComponent<Image>();
            borderImg.sprite = OtherPlayersOverlayPatch.GetCircleBorderSprite();
            borderImg.color = isHost
                ? new Color(0.95f, 0.78f, 0.25f, 1f)
                : (isSelf ? new Color(0.25f, 0.65f, 0.95f, 1f) : Color.white);
            borderImg.raycastTarget = false;
            borderImg.preserveAspect = true;
        }

        GameObject textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var rt = textGo.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;

        rt.offsetMin = new Vector2((avatar != null ? 65f : 20f) * panelScale, 0f);

        rt.offsetMax = new Vector2(-140f * panelScale, 0f);

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        string charaName = ResolveCharacterDisplayName(charaId);
        string label = playerName;
        if (isSelf)
        {
            label += " <color=#4CAF50>(你)</color>";
        }
        if (!string.IsNullOrWhiteSpace(charaName))
        {
            label += $" <color=#B0BEC5><size=80%>[{charaName}]</size></color>";
        }

        tmp.text = label;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.raycastTarget = false;
        tmp.fontSize = Mathf.Clamp(19f * panelScale, 15f, 60f);
        tmp.color = Color.white;
        tmp.richText = true;
        if (_roomListFont != null) tmp.font = _roomListFont;

        GameObject statusGo = new GameObject("StatusLabel");
        statusGo.transform.SetParent(go.transform, false);
        var statusRt = statusGo.AddComponent<RectTransform>();
        statusRt.anchorMin = new Vector2(1f, 0.5f);
        statusRt.anchorMax = new Vector2(1f, 0.5f);
        statusRt.pivot = new Vector2(1f, 0.5f);
        statusRt.sizeDelta = new Vector2(120f * panelScale, 50f * panelScale);
        statusRt.anchoredPosition = new Vector2(-15f * panelScale, 0f);

        var statusTmp = statusGo.AddComponent<TextMeshProUGUI>();
        string statusLabel = "";
        if (isHost)
        {
            statusLabel = "<color=#FFD700>★ 房主</color>";
        }
        else
        {
            statusLabel = isReady ? "<color=#4CAF50>● 已就绪</color>" : "<color=#B0BEC5>● 准备中</color>";
        }
        statusTmp.text = statusLabel;
        statusTmp.alignment = TextAlignmentOptions.MidlineRight;
        statusTmp.raycastTarget = false;
        statusTmp.fontSize = Mathf.Clamp(18f * panelScale, 14f, 54f);
        statusTmp.color = Color.white;
        statusTmp.richText = true;
        if (_roomListFont != null) statusTmp.font = _roomListFont;
    }

        private static string ResolveCharacterDisplayName(string charaId)
    {
        if (string.IsNullOrWhiteSpace(charaId)) return null;
        try
        {
            PlayerUnit unit = Library.TryCreatePlayerUnit(charaId);
            if (unit != null && !string.IsNullOrWhiteSpace(unit.ModelName))
                return unit.ModelName;
        }
        catch
        {

        }
        return null;
    }

    private static string ResolveSelfDisplayName()
    {
        try
        {
            ConfigManager config = TryGetConfig();
            string name = config?.PlayerNameOverride?.Value;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            name = config?.HostPlayerNameOverride?.Value;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            name = Singleton<GameMaster>.Instance?.CurrentProfile?.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {

        }
        return NetworkIdentityTracker.GetSelfPlayerId() ?? "我";
    }

    internal static void HideRoomListOverlay()
    {
        HideRoomListOverlay(false);
    }

    internal static void HideRoomListOverlay(bool immediate)
    {
        if (_roomListRoot == null) return;
        if (immediate)
        {
            _roomListRoot.SetActive(false);
        }
        else if (_roomListAnimator != null && _roomListRoot.activeInHierarchy)
        {
            _roomListAnimator.PlayClose(() => _roomListRoot.SetActive(false));
        }
        else
        {
            _roomListRoot.SetActive(false);
        }
        UnsubscribeRoomListEvents();
    }

    private static void SubscribeRoomListEvents()
    {
        if (_roomListEventSubscribed) return;
        INetworkClient client = TryGetNetworkClient();
        if (client == null) return;
        try
        {
            client.OnGameEventReceived += _onRoomListGameEvent;
            client.OnConnectionStateChanged += _onRoomListConnStateChanged;
            _roomListSubscribedClient = client;
            _roomListEventSubscribed = true;
        }
        catch
        {

        }
    }

    private static void UnsubscribeRoomListEvents()
    {
        if (!_roomListEventSubscribed) return;
        try
        {
            if (_roomListSubscribedClient != null)
            {
                _roomListSubscribedClient.OnGameEventReceived -= _onRoomListGameEvent;
                _roomListSubscribedClient.OnConnectionStateChanged -= _onRoomListConnStateChanged;
            }
        }
        catch
        {

        }
        _roomListSubscribedClient = null;
        _roomListEventSubscribed = false;
    }

    private static void OnRoomListGameEventReceived(string eventType, object payload)
    {
        if (eventType == NetworkMessageTypes.OnLobbyResumeInfo)
        {
            HandleLobbyResumeInfoReceived(payload);
            return;
        }

        if (eventType == NetworkMessageTypes.OnLobbyResumeGame)
        {
            HandleLobbyResumeGameReceived(payload);
            return;
        }

        if (eventType == NetworkMessageTypes.Welcome
            || eventType == NetworkMessageTypes.PlayerListUpdate
            || eventType == NetworkMessageTypes.PlayerJoined
            || eventType == NetworkMessageTypes.PlayerLeft
            || eventType == NetworkMessageTypes.HostChanged)
        {
            if (eventType == NetworkMessageTypes.Welcome || eventType == NetworkMessageTypes.PlayerListUpdate)
            {
                ParseReadyStatesFromPayload(payload);
            }
            Plugin.RunOnMainThread(() => RefreshRoomList());
        }
    }

    private static void HandleLobbyResumeInfoReceived(object payload)
    {
        try
        {
            if (NetworkIdentityTracker.GetSelfIsHost()) return;
            if (!NetworkEventHelper.TryGetJsonElement(payload, out JsonElement root)) return;

            ulong? rootSeed = null;
            if (root.TryGetProperty("RootSeed", out var sElem) && sElem.TryGetUInt64(out var sVal))
                rootSeed = sVal;

            if (rootSeed.HasValue && rootSeed.Value > 0)
            {
                _clientLobbyResumeMode = true;
                _hostResumeRootSeed = rootSeed.Value;

                // 解析房主下发的对局历史名单 Roster
                Dictionary<string, string> hostRoster = null;
                if (root.TryGetProperty("Roster", out var rElem) && rElem.ValueKind == JsonValueKind.Object)
                {
                    hostRoster = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in rElem.EnumerateObject())
                    {
                        string chara = prop.Value.GetString() ?? "";
                        hostRoster[prop.Name] = chara;
                        MultiplayerSaveManager.RecordPlayerInRoster(_hostResumeRootSeed, prop.Name, chara);
                    }
                }

                string selfId = NetworkIdentityTracker.GetSelfPlayerId();
                string selfName = ResolveSelfDisplayName();
                bool isOld = MultiplayerSaveManager.IsOldPlayer(_hostResumeRootSeed, selfId, hostRoster, out var inheritedChara)
                          || (!string.IsNullOrWhiteSpace(selfName) && MultiplayerSaveManager.IsOldPlayer(_hostResumeRootSeed, selfName, hostRoster, out inheritedChara));
                _isOldResumePlayer = isOld;

                Plugin.Logger?.LogInfo($"[MainMenuMultiplayerEntry] 客机收到房间继续存档信息: RootSeed={_hostResumeRootSeed}, 是老玩家={isOld}, 角色={inheritedChara}");

                if (isOld && !string.IsNullOrWhiteSpace(inheritedChara))
                {
                    INetworkClient client = TryGetNetworkClient();
                    var netManager = ServiceProvider?.GetService<NetworkManager>();
                    if (netManager != null)
                    {
                        var self = netManager.GetSelf();
                        if (self != null) self.chara = inheritedChara;
                    }
                    client?.SendRequest(NetworkMessageTypes.UpdatePlayerLocation, JsonCompat.Serialize(new { CharacterId = inheritedChara }));
                }

                Plugin.RunOnMainThread(() => RefreshRoomList());
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] HandleLobbyResumeInfoReceived 失败: {ex}");
        }
    }

    private static void HandleLobbyResumeGameReceived(object payload)
    {
        try
        {
            if (NetworkIdentityTracker.GetSelfIsHost()) return;
            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 客机收到房主恢复发车广播 (OnLobbyResumeGame)");

            Plugin.RunOnMainThread(() =>
            {
                if (_hasEnteredGameRun)
                {
                    Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] HandleLobbyResumeGameReceived: 已在游戏对局中，忽略广播");
                    return;
                }
                HideRoomListOverlay(immediate: true);
                GameMasterSaveHookPatch.IsMultiplayerRunActive = true;

                bool wasOld = _isOldResumePlayer;
                ulong targetSeed = _hostResumeRootSeed;

                // 显式复位大厅继续状态，防止在关卡内或后续对局中残留
                _clientLobbyResumeMode = false;
                _isOldResumePlayer = false;
                _hostResumeRootSeed = 0;

                if (wasOld)
                {
                    Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 老玩家客机：正在加载本地多人存档...");
                    var localSave = MultiplayerSaveManager.LoadMultiplayerSave();
                    if (localSave != null && localSave.RootSeed == targetSeed)
                    {
                        Plugin.Logger?.LogInfo($"[MainMenuMultiplayerEntry] 老玩家客机：本地存档校验匹配成功 (Seed={localSave.RootSeed})，直接恢复进入关卡！");
                        _hasEnteredGameRun = true;
                        GameMaster.RestoreGameRun(localSave);
                        return;
                    }
                    else
                    {
                        Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 老玩家客机本地多人存档缺失或种子不匹配，转入新玩家追赶模式开局。");
                    }
                }

                // 新玩家（或老玩家本地存档缺失的回退）：
                if (NetworkEventHelper.TryGetJsonElement(payload, out JsonElement root))
                {
                    GameSeedSyncPatch.SetCachedResumeConfig(root);
                }

                Singleton<GameMaster>.Instance.StartCoroutine(DelayedStartGame());
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] HandleLobbyResumeGameReceived 失败: {ex}");
        }
    }

        private static void ParseReadyStatesFromPayload(object payload)
    {
        try
        {

            string json = payload as string;
            if (string.IsNullOrWhiteSpace(json)) return;

            JsonElement root = JsonSerializer.Deserialize<JsonElement>(json);
            if (root.ValueKind != JsonValueKind.Object) return;

            JsonElement playersElem = default;
            if (root.TryGetProperty("PlayerList", out var pl) && pl.ValueKind == JsonValueKind.Array)
                playersElem = pl;
            else if (root.TryGetProperty("Players", out var ps) && ps.ValueKind == JsonValueKind.Array)
                playersElem = ps;

            if (playersElem.ValueKind != JsonValueKind.Array) return;

            foreach (JsonElement player in playersElem.EnumerateArray())
            {
                if (player.ValueKind != JsonValueKind.Object) continue;
                if (!player.TryGetProperty("PlayerId", out var idElem) || idElem.ValueKind != JsonValueKind.String) continue;
                string pid = idElem.GetString();
                if (string.IsNullOrWhiteSpace(pid)) continue;

                bool ready = player.TryGetProperty("Ready", out var rdyElem) && rdyElem.ValueKind == JsonValueKind.True;
                _playerReadyStates[pid] = ready;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogDebug($"[MainMenuMultiplayerEntry] ParseReadyStatesFromPayload 异常: {ex.Message}");
        }
    }

    private static void OnRoomListConnectionStateChanged(bool connected)
    {
        if (!connected)
        {
            _lobbyResumeMode = false;
            _resumingSave = null;
            _clientLobbyResumeMode = false;
            _isOldResumePlayer = false;
            _hostResumeRootSeed = 0;
        }
        RefreshRoomList();
    }

    #endregion

    #region 连接状态浮层

        private static void ShowConnectionStatusOverlay(string initialText)
    {
        try
        {
            if (_connStatusRoot != null)
            {
                if (_connStatusText != null) _connStatusText.text = initialText;
                _connStatusRoot.SetActive(true);
                _connStatusRoot.transform.SetAsLastSibling();
                _connStatusAnimator?.PlayOpen();
                return;
            }

            Transform parent = TryGetRootCanvasRectTransform(UiManager.Instance?.transform) ?? UiManager.Instance?.transform;
            if (parent == null) return;

            _connStatusFont = _defaultFont ?? FindDefaultFont(parent);

            GameObject root = new GameObject(ConnStatusRootName);
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling();
            _connStatusRoot = root;

            var rootRect = root.AddComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            var bg = root.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.45f);
            bg.raycastTarget = true;

            var rootGroup = root.AddComponent<CanvasGroup>();
            rootGroup.alpha = 0f;
            rootGroup.interactable = false;
            rootGroup.blocksRaycasts = true;

            GameObject frame = new GameObject(ConnStatusRootName + "_Frame");
            frame.transform.SetParent(root.transform, false);
            var frameRect = frame.AddComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0.5f, 0.5f);
            frameRect.anchorMax = new Vector2(0.5f, 0.5f);
            frameRect.pivot = new Vector2(0.5f, 0.5f);
            frameRect.sizeDelta = new Vector2(420f, 120f);
            frameRect.anchoredPosition = Vector2.zero;

            var frameBg = frame.AddComponent<Image>();
            frameBg.color = new Color(0.10f, 0.09f, 0.14f, 0.96f);
            frameBg.raycastTarget = true;

            try
            {
                var outline = frame.AddComponent<Outline>();
                outline.effectColor = new Color(0.78f, 0.63f, 0.25f, 0.8f);
                outline.effectDistance = new Vector2(2f, 2f);
            }
            catch
            {

            }

            GameObject textGo = new GameObject("StatusText");
            textGo.transform.SetParent(frame.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(20f, 15f);
            textRt.offsetMax = new Vector2(-20f, -15f);

            _connStatusText = textGo.AddComponent<TextMeshProUGUI>();
            _connStatusText.text = initialText;
            _connStatusText.alignment = TextAlignmentOptions.Center;
            _connStatusText.fontSize = 22f;
            _connStatusText.color = new Color(0.9f, 0.88f, 0.7f, 1f);
            _connStatusText.raycastTarget = false;
            if (_connStatusFont != null) _connStatusText.font = _connStatusFont;

            try
            {
                _connStatusAnimator = root.AddComponent<PanelAnimator>();
                _connStatusAnimator.Init(rootGroup, frameRect);
                _connStatusAnimator.PlayOpen();
            }
            catch
            {

            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 显示连接状态浮层失败: {ex.Message}");
        }
    }

        private static void UpdateConnectionStatusText(string text)
    {
        if (_connStatusText != null)
        {
            _connStatusText.text = text;
        }
    }

        private static void HideConnectionStatusOverlay()
    {
        if (_connStatusRoot != null)
        {
            try { _connStatusAnimator?.PlayClose(); }
            catch (Exception ex) { Plugin.Logger?.LogDebug($"[MainMenuMultiplayerEntryPatch] PlayClose error: {ex.Message}"); }
            UnityEngine.Object.Destroy(_connStatusRoot);
        }
        _connStatusRoot = null;
        _connStatusText = null;
        _connStatusAnimator = null;
    }

    #endregion

    #region 连接等待与房间列表弹出

    private static IEnumerator CoWaitForConnectedThenShowRoomList(float timeoutSeconds = 8f, bool isGuest = false, long expectedGen = 0)
    {
        INetworkClient client = TryGetNetworkClient();
        float start = Time.realtimeSinceStartup;

        while (Time.realtimeSinceStartup - start < timeoutSeconds)
        {
            if (expectedGen != 0 && expectedGen != _sessionGeneration)
            {
                yield break;
            }

            bool connected = false;
            try
            {
                connected = client != null && client.IsConnected;
            }
            catch
            {
                connected = false;
            }
            if (connected) break;
            yield return null;
        }

        if (expectedGen != 0 && expectedGen != _sessionGeneration)
        {
            yield break;
        }

        bool ok = false;
        try
        {
            ok = client != null && client.IsConnected;
        }
        catch
        {
            ok = false;
        }

        HideConnectionStatusOverlay();

        if (!ok)
        {
            string reason = client?.LastDisconnectReason;
            bool isRejected = string.Equals(reason, "ConnectionRejected", StringComparison.OrdinalIgnoreCase);

            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] 等待联机连接未成功(Reason: {reason})，房间玩家列表未弹出。");

            string errorText = isRejected
                ? "连接被服务器拒绝！\n\n请检查联机密钥（Secret Key）是否与房主/服务器一致，或 MOD 版本是否统一。"
                : "连接服务器超时。\n\n请检查网络连接、服务器地址和端口是否正确。";

            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = errorText,
                    Icon = MessageIcon.Error,
                    Buttons = DialogButtons.Confirm,
                }
            );
            yield break;
        }

        if (isGuest)
        {
            float hostWaitStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - hostWaitStart < 3.0f)
            {
                if (expectedGen != 0 && expectedGen != _sessionGeneration)
                {
                    yield break;
                }

                if (NetworkIdentityTracker.HasOnlineHost() || NetworkIdentityTracker.IsHostInGracePeriod())
                {
                    break;
                }
                yield return null;
            }

            if (expectedGen != 0 && expectedGen != _sessionGeneration)
            {
                yield break;
            }

            if (!NetworkIdentityTracker.HasOnlineHost() && !NetworkIdentityTracker.IsHostInGracePeriod())
            {
                Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 目标房间暂无在线房主，无法进入房间。");
                ShutdownMultiplayerSession();
                UiManager.GetDialog<MessageDialog>().Show(
                    new MessageContent
                    {
                        Text = "目标房间暂无在线房主，无法开始游戏。\n\n请确认房主已创建房间并处于在线状态。",
                        Icon = MessageIcon.Warning,
                        Buttons = DialogButtons.Confirm,
                    }
                );
                yield break;
            }
        }

        ShowRoomPlayerListOverlay();
    }

    internal static void TryConnectToServerAndShowRoomList(string host, int port)
    {
        long gen = Interlocked.Increment(ref _sessionGeneration);
        _hasEnteredGameRun = false;
        ShowConnectionStatusOverlay($"正在连接到 {host}:{port}…");
        bool started = TryConnectToServer(host, port);
        if (!started)
        {
            HideConnectionStatusOverlay();
            return;
        }
        UpdateConnectionStatusText($"已发起连接请求到 {host}:{port}，等待服务器响应…");
        try
        {
            SubscribeRoomListEvents();
            Singleton<GameMaster>.Instance.StartCoroutine(CoWaitForConnectedThenShowRoomList(8f, isGuest: true, gen));
        }
        catch
        {
            HideConnectionStatusOverlay();
        }
    }

    internal static void TryHostLocalServerAndConnectAndShowRoomList()
    {
        long gen = Interlocked.Increment(ref _sessionGeneration);
        _hasEnteredGameRun = false;
        ShowConnectionStatusOverlay("正在启动本机服务器…");
        TryHostLocalServerAndConnect();
        UpdateConnectionStatusText("正在连接到本机服务器…");
        try
        {
            SubscribeRoomListEvents();
            Singleton<GameMaster>.Instance.StartCoroutine(CoWaitForConnectedThenShowRoomList(8f, isGuest: false, gen));
        }
        catch
        {
            HideConnectionStatusOverlay();
        }
    }

    #endregion

    #region 连接与断开

        internal static void TryHostLocalServerAndConnect()
    {
        ConfigManager config = TryGetConfig();
        int port = config?.HostServerPort?.Value ?? 7777;
        if (port <= 0)
        {
            port = 7777;
        }
        int maxConn = config?.HostMaxConnections?.Value ?? 4;
        string key = config?.HostConnectionKey?.Value ?? NetworkPlugin.Network.Security.SecurityUtils.GenerateSecureKey();
        string listenAddr = config?.HostListenAddress?.Value ?? "0.0.0.0";

        try
        {
            if (!_localServerRunning)
            {
                _localServer = new NetworkServer(port, maxConn, key, Plugin.Logger, listenAddr);
                _localServer.Start();
                _localServerRunning = true;
                StartLocalServerLoop();

                Plugin.Logger?.LogInfo($"[MainMenuMultiplayerEntry] Host server started: {listenAddr}:{port}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 启动本机服务器失败: {ex.Message}");
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = $"启动本机服务器失败：\n{ex.Message}",
                    Icon = MessageIcon.Error,
                    Buttons = DialogButtons.Confirm,
                }
            );
            return;
        }

        TryConnectToServer("127.0.0.1", port);
    }

    internal static void TryHostLocalServerAndConnectAndRestore(GameRunSaveData save)
    {
        if (save == null)
        {
            return;
        }

        long gen = Interlocked.Increment(ref _sessionGeneration);
        _hasEnteredGameRun = false;

        try
        {
            TryHostLocalServerAndConnect();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 启动房主并连接失败: {ex.Message}");
            return;
        }

        try
        {
            Singleton<GameMaster>.Instance.StartCoroutine(CoWaitForConnectedThenRestore(save, gen));
        }
        catch
        {

        }
    }

    private static IEnumerator CoWaitForConnectedThenRestore(GameRunSaveData save, long expectedGen = 0)
    {
        INetworkClient client = TryGetNetworkClient();

        float start = Time.realtimeSinceStartup;
        const float timeoutSeconds = 8f;

        while (Time.realtimeSinceStartup - start < timeoutSeconds)
        {
            if (expectedGen != 0 && expectedGen != _sessionGeneration)
            {
                yield break;
            }

            try
            {
                if (client != null && client.IsConnected)
                {
                    break;
                }
            }
            catch
            {

            }

            yield return null;
        }

        if (expectedGen != 0 && expectedGen != _sessionGeneration)
        {
            yield break;
        }

        bool connected = false;
        try
        {
            connected = client != null && client.IsConnected;
        }
        catch
        {
            connected = false;
        }

        if (!connected)
        {
            Plugin.Logger?.LogWarning("[MainMenuMultiplayerEntry] 等待联机连接超时，已取消继续存档（联机）。");
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = "联机连接超时，无法作为房主继续存档。\n\n请检查端口占用或网络模块状态。",
                    Icon = MessageIcon.Warning,
                    Buttons = DialogButtons.Confirm,
                }
            );
            yield break;
        }

        try
        {
            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 联机已连接，开始恢复存档进入游戏。");
            GameMaster.RestoreGameRun(save);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 恢复存档失败: {ex.Message}");
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = "恢复存档失败，请检查日志。",
                    Icon = MessageIcon.Warning,
                    Buttons = DialogButtons.Confirm,
                }
            );
        }
    }

        internal static bool TryConnectToServer(string host, int port)
    {
        INetworkClient client = TryGetNetworkClient();
        if (client == null)
        {
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = "网络客户端未初始化（INetworkClient 解析失败）。\n请先确认依赖注入与网络模块已就绪。",
                    Icon = MessageIcon.Error,
                    Buttons = DialogButtons.Confirm,
                }
            );
            return false;
        }

        try
        {
            client.Start();
        }
        catch
        {

        }

        try
        {
            client.ConnectToServer(host, port);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerPlayer] 连接失败: {ex.Message}");
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = $"连接服务器失败：\n{ex.Message}",
                    Icon = MessageIcon.Error,
                    Buttons = DialogButtons.Confirm,
                }
            );
            return false;
        }
    }

    private static void TryConnectToServerAndRestoreAndCatchUp(string host, int port, GameRunSaveData save)
    {
        if (save == null)
        {
            TryConnectToServer(host, port);
            return;
        }

        long gen = Interlocked.Increment(ref _sessionGeneration);
        _hasEnteredGameRun = false;

        TryConnectToServer(host, port);

        try
        {
            Singleton<GameMaster>.Instance.StartCoroutine(CoWaitForConnectedThenRestoreThenCatchUp(save, gen));
        }
        catch
        {

        }
    }

    private static IEnumerator CoWaitForConnectedThenRestoreThenCatchUp(GameRunSaveData save, long expectedGen = 0)
    {
        INetworkClient client = TryGetNetworkClient();

        float start = Time.realtimeSinceStartup;
        const float timeoutSeconds = 10f;

        while (Time.realtimeSinceStartup - start < timeoutSeconds)
        {
            if (expectedGen != 0 && expectedGen != _sessionGeneration)
            {
                yield break;
            }

            bool connected = false;
            try
            {
                connected = client != null && client.IsConnected;
            }
            catch
            {
                connected = false;
            }

            if (connected)
            {
                break;
            }

            yield return null;
        }

        if (expectedGen != 0 && expectedGen != _sessionGeneration)
        {
            yield break;
        }

        bool ok = false;
        try
        {
            ok = client != null && client.IsConnected;
        }
        catch
        {
            ok = false;
        }

        if (!ok)
        {
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = "联机连接超时，无法重连继续存档。\n\n请检查网络模块状态。",
                    Icon = MessageIcon.Warning,
                    Buttons = DialogButtons.Confirm,
                }
            );
            yield break;
        }

        NetworkPlugin.Network.MidGameJoin.MidGameJoinManager mgrHandshake = null;
        try
        {
            mgrHandshake = ServiceProvider?.GetService<NetworkPlugin.Network.MidGameJoin.MidGameJoinManager>();
        }
        catch
        {
            mgrHandshake = null;
        }

        float hsStart = Time.realtimeSinceStartup;
        const float hsTimeoutSeconds = 6f;
        while (Time.realtimeSinceStartup - hsStart < hsTimeoutSeconds)
        {
            string selfId = string.Empty;
            string hostId = string.Empty;
            try
            {
                selfId = NetworkPlugin.Utils.NetworkIdentityTracker.GetSelfPlayerId() ?? string.Empty;
                hostId = mgrHandshake?.GetLastKnownHostPlayerId() ?? string.Empty;
            }
            catch
            {

            }

            if (!string.IsNullOrWhiteSpace(selfId) && !string.IsNullOrWhiteSpace(hostId))
            {
                break;
            }

            yield return null;
        }

        try
        {
            Plugin.Logger?.LogInfo("[MainMenuMultiplayerEntry] 联机已连接，开始本地恢复存档。");
            GameMaster.RestoreGameRun(save);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[MainMenuMultiplayerEntry] 本地恢复存档失败: {ex.Message}");
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = "本地恢复存档失败，请检查日志。",
                    Icon = MessageIcon.Warning,
                    Buttons = DialogButtons.Confirm,
                }
            );
            yield break;
        }

        float runStart = Time.realtimeSinceStartup;
        const float runTimeoutSeconds = 8f;
        while (Time.realtimeSinceStartup - runStart < runTimeoutSeconds)
        {
            bool hasRun = false;
            try
            {
                hasRun = NetworkPlugin.Utils.GameStateUtils.GetCurrentGameRun() != null;
            }
            catch
            {
                hasRun = false;
            }

            if (hasRun)
            {
                break;
            }

            yield return null;
        }

        yield return null;
        yield return null;

        var mgr2 = ServiceProvider?.GetService<NetworkPlugin.Network.MidGameJoin.MidGameJoinManager>();
        if (mgr2 == null)
        {
            yield break;
        }

        float joinGateStart = Time.realtimeSinceStartup;
        const float joinGateTimeoutSeconds = 10f;
        while (Time.realtimeSinceStartup - joinGateStart < joinGateTimeoutSeconds)
        {
            string selfId = string.Empty;
            string hostId = string.Empty;
            try
            {
                selfId = NetworkPlugin.Utils.NetworkIdentityTracker.GetSelfPlayerId() ?? string.Empty;
                hostId = mgr2.GetLastKnownHostPlayerId() ?? string.Empty;
            }
            catch
            {

            }

            if (!string.IsNullOrWhiteSpace(selfId) && !string.IsNullOrWhiteSpace(hostId))
            {
                break;
            }

            yield return null;
        }

        try
        {

            string playerName = "joiner";
            try
            {
                playerName = Singleton<GameMaster>.Instance?.CurrentProfile?.Name ?? playerName;
            }
            catch
            {

            }

            const string roomId = "default";

            mgr2.BeginReconnectAndCatchUp(
                roomId,
                playerName,
                onStatus: s => Plugin.Logger?.LogInfo("[Reconnect] " + s),
                onCompleted: r =>
                {
                    try
                    {
                        if (r?.IsSuccess == true)
                        {
                            Plugin.Logger?.LogInfo("[Reconnect] Catch-up completed.");
                            return;
                        }

                        string err = r?.ErrorMessage ?? "Unknown error";
                        Plugin.Logger?.LogWarning("[Reconnect] Catch-up failed: " + err);
                        UiManager.GetDialog<MessageDialog>().Show(new MessageContent
                        {
                            Text = "追赶同步失败：\n" + err,
                            Icon = MessageIcon.Warning,
                            Buttons = DialogButtons.Confirm,
                        });
                    }
                    catch
                    {

                    }
                },
                timeoutSeconds: 20);
        }
        catch
        {

        }
    }

    public static void ShutdownMultiplayerSession(bool clearCredentials = true)
    {
        Interlocked.Increment(ref _sessionGeneration);
        _hasEnteredGameRun = false;

        INetworkClient client = TryGetNetworkClient();
        try
        {
            if (clearCredentials)
            {
                client?.ClearSessionCredentials();
            }
            client?.Stop();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] Stop client error: {ex.Message}");
        }

        StopLocalServerLoop();

        try
        {
            if (_localServerRunning)
            {
                _localServer?.Stop();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] Stop local server error: {ex.Message}");
        }
        finally
        {
            _localServer = null;
            _localServerRunning = false;
        }

        try
        {
            INetworkManager manager = ServiceProvider?.GetService<INetworkManager>();
            manager?.ClearAllPlayers();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] ClearAllPlayers error: {ex.Message}");
        }

        try
        {
            _playerReadyStates.Clear();
            _lobbyResumeMode = false;
            _resumingSave = null;
            _clientLobbyResumeMode = false;
            _isOldResumePlayer = false;
            _hostResumeRootSeed = 0;

            HideRoomListOverlay(immediate: true);
            HideConnectionStatusOverlay();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] UI cleanup error: {ex.Message}");
        }
    }

    private static void Disconnect(INetworkClient client)
    {
        try
        {
            if (client != null && client.IsConnected)
            {
                string selfId = NetworkIdentityTracker.GetSelfPlayerId();
                client.SendRequest(NetworkMessageTypes.LeaveRoom, new { PlayerId = selfId });
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[MainMenuMultiplayerEntry] Send LeaveRoom error: {ex.Message}");
        }

        ShutdownMultiplayerSession(clearCredentials: false);
    }

    #endregion

    #region 本机服务器轮询线程

        private static void StartLocalServerLoop()
    {
        try
        {

            StopLocalServerLoop();

            _localServerCts = new CancellationTokenSource();
            CancellationToken token = _localServerCts.Token;

            _localServerThread = new Thread(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        _localServer?.PollEvents();
                    }
                    catch
                    {

                    }

                    Thread.Sleep(15);
                }
            })
            {
                IsBackground = true,
                Name = "NetworkPlugin.LocalServerPoll",
            };

            _localServerThread.Start();
        }
        catch
        {

        }
    }

        private static void StopLocalServerLoop()
    {
        try
        {
            _localServerCts?.Cancel();
        }
        catch
        {

        }

        try
        {
            if (_localServerThread != null && _localServerThread.IsAlive)
            {
                _localServerThread.Join(200);
            }
        }
        catch
        {

        }
        finally
        {
            _localServerThread = null;
            _localServerCts?.Dispose();
            _localServerCts = null;
        }
    }

    #endregion
}
