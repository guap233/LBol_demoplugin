using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using LBoL.Core;
using LBoL.Presentation.UI;
using LBoL.Presentation.UI.Panels;
using LBoL.Presentation.UI.Widgets;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Configuration;
using LBoL.Presentation.Units;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.NetworkPlayer;
using NetworkPlugin.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NetworkPlugin.Patch.UI;

[HarmonyPatch]
public static partial class OtherPlayersOverlayPatch
{
    #region 常量和字段

    private const float AvatarEntryBaseWidth = 260f;
    private const float AvatarEntryBaseHeight = 350f;
    private const float AvatarVisualSize = 230f;
    private const float AvatarMaskDiameterScale = 1f;
    private const float AvatarImageScale = 1.6f;
    private const float AvatarEntrySpacing = 2f;
    private const float OverlayRootWidth = 280f;
    private const float OverlayRootTopPadding = 8f;
    private const float OverlayRootBottomPadding = 12f;
    private const float AvatarPanelScale = 0.7f;
    private const float AvatarPanelOffsetY = -30f;
    private static readonly Vector3 HealthBarLocalPosition;
    private static readonly Vector3 HealthBarLocalScale;
    private static readonly Vector3 PlayerNameLocalPosition;
    private static readonly Vector3 PlayerNameLocalScale;
    private static readonly Vector2 PlayerNameSize;
    private const float PlayerNameFontSize = 48f;
    private const float RuntimeLayoutEpsilon = 0.01f;
    private const float OverlayDebugLogInterval = 0.5f;
    private static readonly Vector3 OverlayRootLocalPosition;

        private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

        private static readonly object _syncLock;

        private static readonly Dictionary<string, PlayerSummary> _players;

        private static OverlayUi _ui;

        private static TMP_FontAsset _defaultFont;

        private static bool _missingFontWarningLogged;

        private static INetworkClient _subscribedClient;

        private static bool _subscribed;

        private static string _selfPlayerId;

        private static Transform _remoteCharactersRoot;

        private static readonly Dictionary<string, RemoteCharacterView> _remoteCharacters;

        private static RectTransform _mapIconsRoot;

        private static readonly Dictionary<string, MapIconUi> _mapIcons;

        private static readonly Dictionary<MapNodeWidget, RectTransform> _mapNodeIconsRoots;

        private static readonly Dictionary<string, Sprite> _avatarCache;

        private static Sprite _circleMaskSprite;

        private static Texture2D _circleMaskTexture;

    private static readonly Action<string, object> _onGameEventReceived;
    private static readonly Action<bool> _onConnectionStateChanged;

    static OtherPlayersOverlayPatch()
    {
        try
        {
            _syncLock = new object();
            _players = new Dictionary<string, PlayerSummary>(StringComparer.Ordinal);
            _remoteCharacters = new Dictionary<string, RemoteCharacterView>(StringComparer.Ordinal);
            _mapIcons = new Dictionary<string, MapIconUi>(StringComparer.Ordinal);
            _avatarCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            _virtualAiBattleStates = new Dictionary<string, VirtualAiBattleStateEntry>(StringComparer.Ordinal);
            VirtualAiDebugPlayers = new (string, string)[]
            {
                ("aidefault", "AI Default"),
                ("aidefault2", "AI Default 2"),
                ("aidefault3", "AI Default 3"),
            };

            try
            {
                _mapNodeIconsRoots = new Dictionary<MapNodeWidget, RectTransform>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cctor _mapNodeIconsRoots EXCEPTION] {ex}");
            }

            HealthBarLocalPosition = new Vector3(350f, -500f, 0f);
            HealthBarLocalScale = new Vector3(0.7f, 0.7f, 1f);
            PlayerNameLocalPosition = new Vector3(180f, -410f, 0f);
            PlayerNameLocalScale = new Vector3(1f, 1f, 1f);
            PlayerNameSize = new Vector2(400f, 65f);
            OverlayRootLocalPosition = new Vector3(1360f, 900f, 0f);
            _onGameEventReceived = OnGameEventReceived;
            _onConnectionStateChanged = OnConnectionStateChanged;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OtherPlayersOverlayPatch cctor EXCEPTION] {ex}");
        }
    }

        private static float _nextOverlayDebugLogTime;

        private static string _lastOverlayDebugSummary;

        private static int _uiDirtyCounter;
    private const int UiRefreshFrameInterval = 30;
    private static bool _wasOverlayVisible;
    private static bool _wasRemoteCharactersVisible;
    private static bool _uiDirty;

    #endregion

    #region Harmony 补丁方法

        [HarmonyPatch(typeof(GameDirector), "Update")]
    [HarmonyPostfix]
    public static void GameDirector_Update_Postfix()
    {
        try
        {

            _selfPlayerId = NetworkIdentityTracker.GetSelfPlayerId();

            INetworkClient client = TryGetNetworkClient();

            if (client != null)
            {
                EnsureSubscribed(client);
                try { client.PollEvents(); }
                catch (Exception ex) { Plugin.Logger?.LogWarning($"[OtherPlayersOverlay] PollEvents 失败: {ex.Message}"); }
            }

            _uiDirtyCounter++;
            if (!_uiDirty && _uiDirtyCounter < UiRefreshFrameInterval)
                return;
            _uiDirtyCounter = 0;

            bool rosterDirty = _uiDirty;
            _uiDirty = false;

            EnsureSceneBoundBindingsCurrent();
            bool isMapPanelVisible = IsMapPanelVisible();

            bool showOverlay;
            bool showRemoteChars;

            if (client == null)
            {
                EnsureVirtualAiDefaultPlayer_NoThrow();
                bool virt = IsVirtualAiDefaultEnabled();
                showOverlay = false;
                showRemoteChars = virt && !isMapPanelVisible;
            }
            else if (!client.IsConnected)
            {
                EnsureVirtualAiDefaultPlayer_NoThrow();
                bool virt = IsVirtualAiDefaultEnabled();
                showOverlay = false;
                showRemoteChars = virt && !isMapPanelVisible;
            }
            else if (UiManager.Instance == null || UiManager.IsShowingLoading || UiManager.IsBlockingInput)
            {
                showOverlay = false;
                showRemoteChars = false;
            }
            else
            {
                showOverlay = IsBattleOverlayActive();
                showRemoteChars = ShouldRenderRemoteCharacters(isMapPanelVisible);
            }

            if (showOverlay != _wasOverlayVisible)
            {
                _wasOverlayVisible = showOverlay;
                if (showOverlay) { EnsureUi(); RefreshUi(); }
                else { HideUi(); }
            }
            else if (showOverlay)
            {
                RefreshUi();
            }

            if (showRemoteChars != _wasRemoteCharactersVisible)
            {
                _wasRemoteCharactersVisible = showRemoteChars;
                if (showRemoteChars) { EnsureRemoteCharacters(); UpdateRemoteCharactersLayout(); }
                else { HideRemoteCharacters(); }
            }
            else if (showRemoteChars)
            {

                if (rosterDirty) { EnsureRemoteCharacters(); }
                UpdateRemoteCharactersLayout();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[OtherPlayersOverlayPatch] GameDirector_Update_Postfix 异常: {ex}");
        }
    }

        [HarmonyPatch(typeof(GameDirector), "MasterTick")]
    [HarmonyPostfix]
    private static void GameDirector_MasterTick_Postfix()
    {
        try
        {
            TickRemoteCharacters();
        }
        catch
        {

        }
    }

        [HarmonyPatch(typeof(MapPanel), "UpdateMapNodesStatus")]
    [HarmonyPostfix]
    public static void MapPanel_UpdateMapNodesStatus_Postfix(MapPanel __instance)
    {
        try
        {
            UpdateMapIcons(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[NetworkPlugin] UpdateMapIcons exception: {ex}");
        }
    }

    #endregion

    #region UI 创建和更新

    private static void EnsureUi()
    {
        if (_ui != null && _ui.Root != null)
        {
            return;
        }

        Transform parent = TryGetUiLayerTransform("topLayer") ?? TryGetUiLayerTransform("topmostLayer") ?? UiManager.Instance?.transform;
        if (parent == null)
        {
            return;
        }

        EnsureDefaultFont(parent);

        GameObject root = new("NetworkPlugin_OtherPlayersOverlay");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = OverlayRootLocalPosition;

        RectTransform rootRect = root.AddComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(OverlayRootWidth, AvatarEntryBaseHeight + OverlayRootTopPadding + OverlayRootBottomPadding);

        GameObject entriesRootGo = new("EntriesRoot");
        entriesRootGo.transform.SetParent(root.transform, false);
        RectTransform entriesRect = entriesRootGo.AddComponent<RectTransform>();

        _ui = new OverlayUi
        {
            Root = root,
            RootRect = rootRect,
            EntriesRoot = entriesRect,
            RootLayoutState = new RectLayoutState(),
            EntriesRootLayoutState = new RectLayoutState(),
            Entries = new Dictionary<string, AvatarEntryUi>(StringComparer.Ordinal)
        };

        LogOverlayDebug($"EnsureUi 创建 Root: path={GetTransformPath(root.transform)}, rootRect={DescribeRect(rootRect)}, entriesRect={DescribeRect(entriesRect)}, defaultFont={_defaultFont?.name ?? "<null>"}", force: true);

        DestroyChildIfExists(entriesRect, "RemotePlayerAvatarTemplate");
        DestroyChildIfExists(entriesRect, "RemotePlayerHealthBarTemplate");

        ApplyRuntimeEditableRectLayout(rootRect, _ui.RootLayoutState, rect =>
        {
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.localPosition = OverlayRootLocalPosition;
            rect.sizeDelta = new Vector2(OverlayRootWidth, AvatarEntryBaseHeight + OverlayRootTopPadding + OverlayRootBottomPadding);
        });

        ApplyRuntimeEditableRectLayout(entriesRect, _ui.EntriesRootLayoutState, rect =>
        {
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.localPosition = new Vector3(0f, -OverlayRootTopPadding, 0f);
            rect.sizeDelta = new Vector2(OverlayRootWidth, AvatarEntryBaseHeight);
        });

        root.SetActive(false);
    }

    private static bool HasOngoingBattleData(PlayerSummary p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.PlayerId)) return false;
        return TryGetLatestRemoteBattleSnapshot(p.PlayerId) != null;
    }

    private static void RefreshUi()
    {
        EnsureVirtualAiDefaultPlayer_NoThrow();

        List<PlayerSummary> list;
        lock (_syncLock)
        {
            list = _players.Values
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.PlayerId))
                .Where(p => string.IsNullOrWhiteSpace(_selfPlayerId) || !string.Equals(p.PlayerId, _selfPlayerId, StringComparison.Ordinal))
                .Where(p => p.IsConnected || HasOngoingBattleData(p))
                .OrderByDescending(p => p.IsHost)
                .ThenByDescending(p => p.IsConnected)
                .ThenBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        LogOverlayPlayersSnapshot("RefreshUi-AfterFilter", list);

        if (list.Count == 0)
        {
            LogOverlayDebug($"RefreshUi 列表为空，隐藏 Overlay: self={_selfPlayerId ?? "<null>"}", force: true);
            HideUi();
            return;
        }

        EnsureUi();
        if (_ui?.Root == null)
        {
            LogOverlayDebug("RefreshUi 中止：_ui.Root 为空", force: true);
            return;
        }

        if (!TryAttachUiToTopRight(list.Count))
        {
            LogOverlayDebug($"RefreshUi 中止：TryAttachUiToTopRight 失败，entryCount={list.Count}", force: true);
            HideUi();
            return;
        }

        EnsureAvatarTemplate();
        if (_ui.AvatarTemplate == null)
        {
            LogOverlayDebug("RefreshUi 中止：AvatarTemplate 为空", force: true);
            HideUi();
            return;
        }

        EnsureHealthBarTemplate();

        _ui.Root.SetActive(true);

        HashSet<string> alive = new(list.Select(p => p.PlayerId), StringComparer.Ordinal);
        foreach (var kv in _ui.Entries.ToList())
        {
            if (alive.Contains(kv.Key))
            {
                continue;
            }

            if (kv.Value?.Root != null)
            {
                UnityEngine.Object.Destroy(kv.Value.Root);
            }

            _ui.Entries.Remove(kv.Key);
        }

        List<AvatarEntryUi> orderedEntries = new List<AvatarEntryUi>(list.Count);
        foreach (PlayerSummary player in list)
        {
            AvatarEntryUi entry = EnsureAvatarEntry(player.PlayerId);
            if (entry == null)
            {
                continue;
            }

            ApplyAvatarEntry(entry, player);
            orderedEntries.Add(entry);
        }

        if (orderedEntries.Count == 0)
        {
            LogOverlayDebug("RefreshUi 中止：orderedEntries 为空", force: true);
            HideUi();
            return;
        }

        if (!TryAttachUiToTopRight(orderedEntries.Count))
        {
            LogOverlayDebug($"RefreshUi 中止：第二次 TryAttachUiToTopRight 失败，entryCount={orderedEntries.Count}", force: true);
            HideUi();
            return;
        }

        LayoutAvatarEntries(orderedEntries);
    }

    private static void HideUi()
    {
        if (_ui?.Root != null)
        {
            _ui.Root.SetActive(false);
        }
    }

    private static void MarkOverlayUiDirty()
    {
        _uiDirty = true;
        RefreshVisibleMapPanelIcons_NoThrow();
    }

    #endregion

    private static void EnsureAvatarTemplate()
    {
        if (_ui == null || _ui.EntriesRoot == null || _ui.AvatarTemplate != null)
        {
            return;
        }

        GameObject source = null;
        try
        {
            UltimateSkillPanel panel = UiManager.GetPanel<UltimateSkillPanel>();
            if (panel != null)
            {
                source = panel.gameObject;
            }
        }
        catch
        {

        }

        if (source == null)
        {
            try
            {
                source = Resources.Load<GameObject>("UI/Panels/UltimateSkillPanel");
            }
            catch
            {

            }
        }

        if (source == null)
        {
            return;
        }

        GameObject template = UnityEngine.Object.Instantiate(source);
        template.name = "RemotePlayerAvatarTemplate";
        template.hideFlags = HideFlags.None;
        PrepareAvatarTemplate(template);
        template.SetActive(false);
        _ui.AvatarTemplate = template;
    }

    private static void PrepareAvatarTemplate(GameObject template)
    {
        if (template == null)
        {
            return;
        }

        foreach (Graphic graphic in template.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic != null) graphic.raycastTarget = false;
        }

        foreach (ParticleSystem particle in template.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (particle == null)
            {
                continue;
            }

            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particle.gameObject.SetActive(false);
        }

        UltimateSkillPanel panel = template.GetComponent<UltimateSkillPanel>();
        if (panel != null)
        {
            panel.enabled = false;
            SetUltimateVisualFieldActive(panel, "powerText", true);
            SetUltimateVisualFieldActive(panel, "gauge1", true);
            SetUltimateVisualFieldActive(panel, "gauge2", true);
            SetUltimateVisualFieldActive(panel, "gauge3", true);
            SetUltimateVisualFieldActive(panel, "fireParticle1", false);
            SetUltimateVisualFieldActive(panel, "fireParticle2", false);
            SetUltimateVisualFieldActive(panel, "fireParticle3", false);
            SetUltimateVisualFieldActive(panel, "lightParticle", false);
        }

        CanvasGroup group = GetOrAddCanvasGroup(template);
        if (group != null)
        {
            group.alpha = 1f;
            group.blocksRaycasts = false;
            group.interactable = false;
        }
    }

    private static void SetUltimateVisualFieldActive(UltimateSkillPanel panel, string fieldName, bool active)
    {
        if (panel == null || string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        try
        {
            Component component = Traverse.Create(panel).Field(fieldName).GetValue<Component>();
            component?.gameObject.SetActive(active);
        }
        catch
        {

        }
    }

    private static void EnsureHealthBarTemplate()
    {
        if (_ui == null || _ui.EntriesRoot == null || _ui.HealthBarTemplate != null)
        {
            return;
        }

        UnitStatusWidget sourceWidget = null;
        bool destroySource = false;

        try
        {
            UnitStatusHud hud = UiManager.GetPanel<UnitStatusHud>();
            if (hud != null)
            {
                sourceWidget = Traverse.Create(hud).Field("statusTemplate").GetValue<UnitStatusWidget>();
                if (sourceWidget == null && GameDirector.Player != null && GameDirector.Player.Unit != null)
                {
                    sourceWidget = hud.CreateStatusWidget(GameDirector.Player.Unit);
                    destroySource = sourceWidget != null;
                }
            }
        }
        catch
        {

        }

        if (sourceWidget == null)
        {
            return;
        }

        GameObject template = UnityEngine.Object.Instantiate(sourceWidget.gameObject);
        template.name = "RemotePlayerHealthBarTemplate";
        template.hideFlags = HideFlags.None;
        PrepareHealthBarTemplate(template);
        template.SetActive(false);
        _ui.HealthBarTemplate = template;

        if (destroySource)
        {
            UnityEngine.Object.Destroy(sourceWidget.gameObject);
        }
    }

    private static void PrepareHealthBarTemplate(GameObject template)
    {
        if (template == null)
        {
            return;
        }

        foreach (Graphic graphic in template.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic != null)
            {
                graphic.raycastTarget = false;
            }
        }

        CanvasGroup group = GetOrAddCanvasGroup(template);
        if (group != null)
        {
            group.alpha = 1f;
            group.blocksRaycasts = false;
            group.interactable = false;
        }

        RectTransform rect = template.GetComponent<RectTransform>();
        ConfigureHealthBarRect(rect);

        UnitStatusWidget widget = template.GetComponent<UnitStatusWidget>();
        if (widget == null)
        {
            return;
        }

        widget.enabled = false;
        widget.Unit = null;

        Transform statusEffectParent = TryGetUnitStatusTransform(widget, "statusEffectParent");
        if (statusEffectParent != null)
        {
            statusEffectParent.gameObject.SetActive(false);
        }

        HealthBar hpBar = TryGetUnitStatusField<HealthBar>(widget, "hpBar");
        if (hpBar != null)
        {
            widget.SetPlayerHpBarLength(60);
            hpBar.TweenHp(60, 60, 0, 0, true);
        }
    }

    private static void ConfigureHealthBarRect(RectTransform rect)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.localPosition = HealthBarLocalPosition;
        rect.localScale = HealthBarLocalScale;
        rect.localEulerAngles = Vector3.zero;
    }

    private static void ConfigurePlayerNameRect(RectTransform rect)
    {
        if (rect == null)
        {
            return;
        }

        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.localPosition = PlayerNameLocalPosition;
        rect.sizeDelta = PlayerNameSize;
        rect.localScale = PlayerNameLocalScale;
        rect.localEulerAngles = Vector3.zero;
    }

    private static AvatarEntryUi EnsureAvatarEntry(string playerId)
    {
        if (_ui == null || _ui.EntriesRoot == null || _ui.AvatarTemplate == null || string.IsNullOrWhiteSpace(playerId))
        {
            return null;
        }

        if (_ui.Entries.TryGetValue(playerId, out AvatarEntryUi existing) && existing?.Root != null)
        {
            EnsureStatusRemoved(existing);
            EnsureCircularAvatarApplied(existing);
            LogOverlayDebug($"EnsureAvatarEntry 复用现有条目: playerId={playerId}, root={DescribeGameObject(existing.Root)}, name={DescribeGameObject(existing.PlayerNameLabel?.gameObject)}, health={DescribeGameObject(existing.HealthRoot)}");
            return existing;
        }

        GameObject root = new($"AvatarEntry_{playerId}");
        root.transform.SetParent(_ui.EntriesRoot, false);

        RectTransform rootRect = root.AddComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(1f, 1f);
        rootRect.anchorMax = new Vector2(1f, 1f);
        rootRect.pivot = new Vector2(1f, 1f);
        rootRect.sizeDelta = new Vector2(AvatarEntryBaseWidth, AvatarEntryBaseHeight);

        GameObject visual = UnityEngine.Object.Instantiate(_ui.AvatarTemplate, root.transform, false);
        visual.name = "Visual";
        visual.SetActive(true);

        RectTransform visualRect = visual.GetComponent<RectTransform>();
        if (visualRect == null)
        {
            visualRect = visual.AddComponent<RectTransform>();
        }

        visualRect.anchorMin = new Vector2(0.5f, 1f);
        visualRect.anchorMax = new Vector2(0.5f, 1f);
        visualRect.pivot = new Vector2(0.5f, 1f);
        visualRect.anchoredPosition = new Vector2(0f, AvatarPanelOffsetY);
        visualRect.localScale = new Vector3(AvatarPanelScale, AvatarPanelScale, 1f);

        Image templateAvatar = TryGetAvatarImageFromTemplate(visual);
        Image avatar = CreateCircularAvatarVisual(visual.transform, templateAvatar);
        avatar.raycastTarget = false;
        avatar.preserveAspect = true;

        UltimateSkillPanel panel = visual.GetComponent<UltimateSkillPanel>();
        TextMeshProUGUI powerText = TryGetUltimateField<TextMeshProUGUI>(panel, "powerText");

        GameObject healthRoot = null;
        RectTransform healthRootRect = null;
        UnitStatusWidget healthWidget = null;
        HealthBar healthBar = null;
        CanvasGroup healthGroup = null;

        if (_ui.HealthBarTemplate != null)
        {
            healthRoot = UnityEngine.Object.Instantiate(_ui.HealthBarTemplate, root.transform, false);
            healthRoot.name = "RemotePlayerHealthBar";
            healthRoot.SetActive(true);
            healthRootRect = healthRoot.GetComponent<RectTransform>();
            ConfigureHealthBarRect(healthRootRect);
            healthWidget = healthRoot.GetComponent<UnitStatusWidget>();
            healthBar = TryGetUnitStatusField<HealthBar>(healthWidget, "hpBar");
            healthGroup = GetOrAddCanvasGroup(healthRoot);
        }

        TextMeshProUGUI status = null;
        RectTransform statusRect = null;
        Transform playerNameParent = root.transform;
        TextMeshProUGUI playerNameLabel = CreateTmpText(playerNameParent, "PlayerNameLabel", ResolveDisplayName(playerId), PlayerNameFontSize);
        playerNameLabel.alignment = TextAlignmentOptions.Left;
        playerNameLabel.fontStyle = FontStyles.Bold;
        playerNameLabel.enableWordWrapping = false;
        playerNameLabel.overflowMode = TextOverflowModes.Ellipsis;
        RectTransform playerNameRect = playerNameLabel.rectTransform;
        ConfigurePlayerNameRect(playerNameRect);

        AvatarEntryUi entry = new AvatarEntryUi
        {
            PlayerId = playerId,
            Root = root,
            RootRect = rootRect,
            RootLayoutState = CaptureRectLayoutState(rootRect),
            VisualRect = visualRect,
            VisualLayoutState = CaptureRectLayoutState(visualRect),
            Avatar = avatar,
            Status = status,
            StatusRect = statusRect,
            StatusLayoutState = CaptureRectLayoutState(statusRect),
            PlayerNameLabel = playerNameLabel,
            PlayerNameRect = playerNameRect,
            PlayerNameLayoutState = CaptureRectLayoutState(playerNameRect),
            VisualGroup = GetOrAddCanvasGroup(visual),
            PowerText = powerText,
            Gauge1 = TryGetUltimateField<Image>(panel, "gauge1"),
            Gauge2 = TryGetUltimateField<Image>(panel, "gauge2"),
            Gauge3 = TryGetUltimateField<Image>(panel, "gauge3"),
            Gauge1FontColor = TryGetUltimateColorField(panel, "gauge1FontColor", Color.white),
            Gauge2FontColor = TryGetUltimateColorField(panel, "gauge2FontColor", Color.white),
            Gauge3FontColor = TryGetUltimateColorField(panel, "gauge3FontColor", Color.white),
            HealthRoot = healthRoot,
            HealthRootRect = healthRootRect,
            HealthRootLayoutState = CaptureRectLayoutState(healthRootRect),
            HealthWidget = healthWidget,
            HealthBar = healthBar,
            HealthGroup = healthGroup,
            LastDebugSnapshot = null,
        };

        EnsureStatusRemoved(entry);
        ApplyRuntimeEditableAvatarEntryStaticLayout(entry);
        EnsureCircularAvatarApplied(entry);
        _ui.Entries[playerId] = entry;
        LogOverlayDebug($"EnsureAvatarEntry 创建条目: playerId={playerId}, root={DescribeGameObject(root)}, health={DescribeGameObject(healthRoot)}, nameParent={GetTransformPath(playerNameParent)}, name={DescribeGameObject(playerNameLabel.gameObject)}, nameRect={DescribeRect(playerNameRect)}", force: true);
        return entry;
    }

    private static void EnsureStatusRemoved(AvatarEntryUi entry)
    {
        if (entry?.Root == null)
        {
            return;
        }

        if (entry.Status != null)
        {
            UnityEngine.Object.Destroy(entry.Status.gameObject);
            entry.Status = null;
            entry.StatusRect = null;
            entry.StatusLayoutState = new RectLayoutState();
        }
        else
        {
            DestroyChildIfExists(entry.Root.transform, "Status");
        }

        DestroyChildIfExists(entry.Root.transform, "Name");
    }

    private static void EnsureCircularAvatarApplied(AvatarEntryUi entry)
    {
        if (entry?.VisualRect == null)
        {
            return;
        }

        Image templateAvatar = TryGetAvatarImageFromTemplate(entry.VisualRect.gameObject);
        Image avatar = CreateCircularAvatarVisual(entry.VisualRect.transform, templateAvatar);
        if (avatar == null)
        {
            return;
        }

        avatar.raycastTarget = false;
        avatar.preserveAspect = true;
        entry.Avatar = avatar;
    }

    private static Image TryGetAvatarImageFromTemplate(GameObject visual)
    {
        if (visual == null)
        {
            return null;
        }

        try
        {
            UltimateSkillPanel panel = visual.GetComponent<UltimateSkillPanel>();
            if (panel != null)
            {
                Image skillImage = Traverse.Create(panel).Field("skillImage").GetValue<Image>();
                if (skillImage != null)
                {
                    return skillImage;
                }
            }
        }
        catch
        {

        }

        try
        {
            return visual.GetComponentsInChildren<Image>(true).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static Image CreateCircularAvatarVisual(Transform parent, Image templateAvatar)
    {
        if (parent == null)
        {
            return null;
        }

        RectTransform sourceRect = templateAvatar != null ? templateAvatar.rectTransform : null;
        Transform maskParent = sourceRect?.parent ?? parent;
        Vector2 sourceSize = ResolveAvatarVisualSize(templateAvatar, sourceRect);
        float maskDiameter = Mathf.Min(sourceSize.x, sourceSize.y) * AvatarMaskDiameterScale;

        Transform existingMaskTransform = maskParent.Find("AvatarMask");
        GameObject maskGo = existingMaskTransform != null ? existingMaskTransform.gameObject : new GameObject("AvatarMask");
        if (existingMaskTransform == null)
        {
            maskGo.transform.SetParent(maskParent, false);
        }

        RectTransform maskRect = maskGo.GetComponent<RectTransform>();
        if (maskRect == null)
        {
            maskRect = maskGo.AddComponent<RectTransform>();
        }

        maskRect.anchorMin = new Vector2(0.5f, 0.5f);
        maskRect.anchorMax = new Vector2(0.5f, 0.5f);
        maskRect.pivot = new Vector2(0.5f, 0.5f);
        maskRect.localPosition = sourceRect != null ? sourceRect.localPosition : Vector3.zero;
        maskRect.localScale = Vector3.one;
        maskRect.localEulerAngles = sourceRect != null ? sourceRect.localEulerAngles : Vector3.zero;

        maskRect.sizeDelta = new Vector2(maskDiameter, maskDiameter);

        Image maskImage = maskGo.GetComponent<Image>();
        if (maskImage == null)
        {
            maskImage = maskGo.AddComponent<Image>();
        }

        maskImage.sprite = GetCircleMaskSprite();
        maskImage.color = Color.white;
        maskImage.raycastTarget = false;
        maskImage.preserveAspect = true;

        Mask mask = maskGo.GetComponent<Mask>();
        if (mask == null)
        {
            mask = maskGo.AddComponent<Mask>();
        }

        mask.showMaskGraphic = false;

        Transform existingAvatarTransform = maskGo.transform.Find("AvatarImage");
        GameObject avatarGo = existingAvatarTransform != null ? existingAvatarTransform.gameObject : new GameObject("AvatarImage");
        if (existingAvatarTransform == null)
        {
            avatarGo.transform.SetParent(maskGo.transform, false);
        }

        RectTransform avatarRect = avatarGo.GetComponent<RectTransform>();
        if (avatarRect == null)
        {
            avatarRect = avatarGo.AddComponent<RectTransform>();
        }

        avatarRect.anchorMin = new Vector2(0.5f, 0.5f);
        avatarRect.anchorMax = new Vector2(0.5f, 0.5f);
        avatarRect.pivot = new Vector2(0.5f, 0.5f);
        avatarRect.anchoredPosition = Vector2.zero;
        avatarRect.sizeDelta = sourceSize * AvatarImageScale;
        avatarRect.localScale = Vector3.one;
        avatarRect.localEulerAngles = Vector3.zero;

        Image avatarImage = avatarGo.GetComponent<Image>();
        if (avatarImage == null)
        {
            avatarImage = avatarGo.AddComponent<Image>();
        }

        avatarImage.sprite = templateAvatar != null ? templateAvatar.sprite : GetWhiteSprite();
        avatarImage.color = templateAvatar != null ? templateAvatar.color : Color.white;
        avatarImage.raycastTarget = false;
        avatarImage.preserveAspect = true;

        if (templateAvatar != null)
        {
            templateAvatar.enabled = false;
        }

        return avatarImage;
    }

    private static Vector2 ResolveAvatarVisualSize(Image templateAvatar, RectTransform sourceRect)
    {
        Sprite sprite = templateAvatar != null ? templateAvatar.sprite : null;
        if (sprite != null)
        {
            float baseSize = AvatarVisualSize * 0.85f;
            Rect spriteRect = sprite.rect;
            if (spriteRect.width > RuntimeLayoutEpsilon && spriteRect.height > RuntimeLayoutEpsilon)
            {
                float aspect = spriteRect.width / spriteRect.height;
                if (aspect >= 1f)
                {
                    return new Vector2(baseSize, baseSize / aspect);
                }

                return new Vector2(baseSize * aspect, baseSize);
            }
        }

        if (sourceRect != null &&
            Approximately(sourceRect.anchorMin, sourceRect.anchorMax))
        {
            Vector2 rectSize = sourceRect.rect.size;
            float width = rectSize.x > RuntimeLayoutEpsilon ? rectSize.x : Mathf.Abs(sourceRect.sizeDelta.x);
            float height = rectSize.y > RuntimeLayoutEpsilon ? rectSize.y : Mathf.Abs(sourceRect.sizeDelta.y);
            if (width > RuntimeLayoutEpsilon && height > RuntimeLayoutEpsilon)
            {
                return new Vector2(width, height);
            }
        }

        float fallbackSize = AvatarVisualSize * 0.85f;
        return new Vector2(fallbackSize, fallbackSize);
    }

    private static void ApplyAvatarEntry(AvatarEntryUi entry, PlayerSummary player)
    {
        if (entry == null || player == null || entry.Root == null)
        {
            return;
        }

        ApplyRuntimeEditableAvatarEntryStaticLayout(entry);
        entry.Root.SetActive(true);

        bool isConnected = player.IsConnected;

        if (entry.Status != null)
        {
            entry.Status.text = BuildStatusText(player.PlayerId, isConnected);
            entry.Status.color = isConnected ? new Color(0.72f, 1f, 0.72f, 1f) : new Color(1f, 0.66f, 0.66f, 1f);
        }

        if (entry.Avatar != null)
        {
            Sprite resolvedAvatar = TryGetAvatarSpriteForPlayer(player);
            if (resolvedAvatar != null)
            {
                entry.Avatar.sprite = resolvedAvatar;
            }
            else if (entry.Avatar.sprite == null)
            {
                entry.Avatar.sprite = GetWhiteSprite();
            }

            entry.Avatar.color = isConnected ? Color.white : new Color(0.55f, 0.55f, 0.55f, 0.95f);
        }

        if (entry.PlayerNameLabel != null)
        {
            if (entry.PlayerNameLabel.font == null)
            {
                TMP_FontAsset fallbackFont = EnsureDefaultFont(entry.Root?.transform);
                if (fallbackFont != null)
                {
                    entry.PlayerNameLabel.font = fallbackFont;
                }
            }

            entry.PlayerNameLabel.text = ResolveDisplayName(player.PlayerId, player.PlayerName);
            entry.PlayerNameLabel.color = isConnected ? Color.white : new Color(0.72f, 0.72f, 0.72f, 0.96f);
            entry.PlayerNameRect.sizeDelta = PlayerNameSize;
        }

        LogAvatarEntryDebug(entry, player, "ApplyAvatarEntry");

        ApplyBattleState(entry, player, isConnected);
    }

    private static Sprite TryGetAvatarSpriteForPlayer(PlayerSummary player)
    {
        if (player == null)
        {
            return null;
        }

        List<string> candidates = new List<string>(4);
        AddPlayerAvatarCandidate(candidates, player.CharacterId);

        try
        {
            INetworkPlayer networkPlayer = TryGetNetworkManager()?.GetPlayer(player.PlayerId);
            AddPlayerAvatarCandidate(candidates, networkPlayer?.chara);
        }
        catch
        {

        }

        if (!string.IsNullOrWhiteSpace(player.PlayerId) &&
            _remoteCharacters.TryGetValue(player.PlayerId, out RemoteCharacterView remoteView))
        {
            AddPlayerAvatarCandidate(candidates, remoteView?.CharacterId);
        }

        AddPlayerAvatarCandidate(candidates, GetFallbackCharacterId());

        foreach (string candidate in candidates)
        {
            Sprite sprite = TryGetAvatarSprite(candidate);
            if (sprite != null)
            {
                return sprite;
            }
        }

        return null;
    }

    private static void AddPlayerAvatarCandidate(List<string> candidates, string value)
    {
        if (candidates == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string normalized = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (candidates.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static void ApplyBattleState(AvatarEntryUi entry, PlayerSummary player, bool isConnected)
    {
        bool hasBattleState = TryGetRemoteBattleState(player, out RemoteBattleState battleState);

        if (entry.VisualGroup != null)
        {
            entry.VisualGroup.alpha = 1f;
        }

        if (entry.HealthGroup != null)
        {
            entry.HealthGroup.alpha = 1f;
        }

        if (hasBattleState && battleState.MaxHealth > 0)
        {
            ApplyPowerCharge(entry, battleState, isConnected);
            ApplyHealthBar(entry, battleState);
            return;
        }

        if (entry.HealthRoot != null)
        {
            entry.HealthRoot.SetActive(false);
        }

        ClearPowerCharge(entry);
    }

    private static void ApplyPowerCharge(AvatarEntryUi entry, RemoteBattleState battleState, bool isConnected)
    {
        if (entry == null)
        {
            return;
        }

        int powerPerLevel = Mathf.Max(1, battleState.PowerPerLevel <= 0 ? 1 : battleState.PowerPerLevel);
        int maxPowerLevel = Mathf.Max(1, battleState.MaxPowerLevel <= 0 ? 3 : battleState.MaxPowerLevel);
        int maxPower = powerPerLevel * maxPowerLevel;
        int currentPower = Mathf.Clamp(battleState.CurrentPower, 0, maxPower);

        if (entry.LastCurrentPower == currentPower &&
            entry.LastPowerPerLevel == powerPerLevel &&
            entry.LastMaxPowerLevel == maxPowerLevel)
        {
            return;
        }

        int currentLevel = Mathf.Clamp(currentPower / powerPerLevel, 0, 3);
        int residue = currentPower % powerPerLevel;

        if (entry.Gauge1 != null)
        {
            entry.Gauge1.fillAmount = currentLevel == 0 ? (float)residue / powerPerLevel : 1f;
        }

        if (entry.Gauge2 != null)
        {
            entry.Gauge2.fillAmount = currentLevel switch
            {
                0 => 0f,
                1 => (float)residue / powerPerLevel,
                _ => 1f,
            };
        }

        if (entry.Gauge3 != null)
        {
            entry.Gauge3.fillAmount = currentLevel switch
            {
                0 => 0f,
                1 => 0f,
                2 => (float)residue / powerPerLevel,
                _ => 1f,
            };
        }

        if (entry.PowerText != null)
        {
            Color textColor = currentLevel switch
            {
                1 => entry.Gauge1FontColor,
                2 => entry.Gauge2FontColor,
                3 => entry.Gauge3FontColor,
                _ => Color.white,
            };

            if (!isConnected)
            {
                textColor = Color.Lerp(textColor, new Color(0.74f, 0.74f, 0.74f, 1f), 0.5f);
            }

            entry.PowerText.text = battleState.PowerPerLevel > 0
                ? $"<color=#{ColorUtility.ToHtmlStringRGB(textColor)}>{currentPower} </color>/ {powerPerLevel}"
                : "-- / --";
        }

        entry.LastCurrentPower = currentPower;
        entry.LastPowerPerLevel = powerPerLevel;
        entry.LastMaxPowerLevel = maxPowerLevel;
    }

    private static void ClearPowerCharge(AvatarEntryUi entry)
    {
        if (entry == null)
        {
            return;
        }

        if (entry.Gauge1 != null)
        {
            entry.Gauge1.fillAmount = 0f;
        }

        if (entry.Gauge2 != null)
        {
            entry.Gauge2.fillAmount = 0f;
        }

        if (entry.Gauge3 != null)
        {
            entry.Gauge3.fillAmount = 0f;
        }

        if (entry.PowerText != null)
        {
            entry.PowerText.text = "-- / --";
        }

        entry.LastCurrentPower = int.MinValue;
        entry.LastPowerPerLevel = int.MinValue;
        entry.LastMaxPowerLevel = int.MinValue;
    }

    private static void ApplyHealthBar(AvatarEntryUi entry, RemoteBattleState battleState)
    {
        if (entry?.HealthBar == null || entry.HealthWidget == null)
        {
            return;
        }

        if (entry.HealthRoot != null && !entry.HealthRoot.activeSelf)
        {
            entry.HealthRoot.SetActive(true);
        }

        int maxHealth = Mathf.Max(1, battleState.MaxHealth);
        int health = Mathf.Clamp(battleState.Health, 0, maxHealth);
        int shield = Mathf.Max(0, battleState.Shield);
        int block = Mathf.Max(0, battleState.Block);

        if (entry.LastHealth == health && entry.LastMaxHealth == maxHealth && entry.LastShield == shield && entry.LastBlock == block)
        {
            return;
        }

        entry.HealthWidget.SetPlayerHpBarLength(Mathf.Min(maxHealth, 60));
        entry.HealthBar.TweenHp(health, maxHealth, shield, block, !entry.HasInitializedHealthBar);

        entry.LastHealth = health;
        entry.LastMaxHealth = maxHealth;
        entry.LastShield = shield;
        entry.LastBlock = block;
        entry.HasInitializedHealthBar = true;
    }

    private static void LayoutAvatarEntries(List<AvatarEntryUi> entries)
    {
        if (_ui?.EntriesRoot == null || entries == null || entries.Count == 0)
        {
            return;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            AvatarEntryUi entry = entries[i];
            if (entry?.RootRect == null)
            {
                continue;
            }

            ApplyRuntimeEditableRectLayout(entry.RootRect, entry.RootLayoutState, rect =>
            {
                rect.anchorMin = new Vector2(1f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(1f, 1f);
                rect.sizeDelta = new Vector2(AvatarEntryBaseWidth, AvatarEntryBaseHeight);
                rect.localScale = Vector3.one;
                rect.anchoredPosition = new Vector2(0f, -i * (AvatarEntryBaseHeight + AvatarEntrySpacing));
            });

            ApplyRuntimeEditableAvatarEntryStaticLayout(entry);
        }
    }

    private static void ApplyRuntimeEditableAvatarEntryStaticLayout(AvatarEntryUi entry)
    {
        if (entry == null)
        {
            return;
        }

        ApplyRuntimeEditableRectLayout(entry.VisualRect, entry.VisualLayoutState, rect =>
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, AvatarPanelOffsetY);
            rect.localScale = new Vector3(AvatarPanelScale, AvatarPanelScale, 1f);
        });

        ApplyRuntimeEditableRectLayout(entry.StatusRect, entry.StatusLayoutState, rect =>
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 10f);
            rect.sizeDelta = new Vector2(-8f, 18f);
        });

        ApplyRuntimeEditableRectLayout(entry.PlayerNameRect, entry.PlayerNameLayoutState, rect =>
        {
            if (entry.Root != null && rect.parent != entry.Root.transform)
            {
                rect.SetParent(entry.Root.transform, false);
            }

            ConfigurePlayerNameRect(rect);
        });

        ApplyRuntimeEditableRectLayout(entry.HealthRootRect, entry.HealthRootLayoutState, rect =>
        {
            ConfigureHealthBarRect(rect);
        });
    }

        private static TextMeshProUGUI CreateTmpText(Transform parent, string name, string text, float fontSize)
    {

        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        tmp.raycastTarget = false;

        TMP_FontAsset font = EnsureDefaultFont(parent);
        if (font != null)
        {
            tmp.font = font;
        }
        else if (!_missingFontWarningLogged)
        {
            _missingFontWarningLogged = true;
            Plugin.Logger?.LogWarning("[OtherPlayersOverlayDebug] PlayerNameLabel 未找到可用 TMP 字体，文本可能不可见。");
        }
        return tmp;
    }

    private static void LogOverlayPlayersSnapshot(string stage, List<PlayerSummary> filteredPlayers)
    {
        try
        {
            List<string> rawPlayers;
            lock (_syncLock)
            {
                rawPlayers = _players.Values
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.PlayerId))
                    .OrderBy(p => p.PlayerId, StringComparer.Ordinal)
                    .Select(FormatPlayerSummary)
                    .ToList();
            }

            List<string> filtered = filteredPlayers?
                .Where(p => p != null)
                .Select(FormatPlayerSummary)
                .ToList() ?? new List<string>();

            string summary = $"{stage}; self={_selfPlayerId ?? "<null>"}; battleActive={IsBattleOverlayActive()}; uiRoot={DescribeGameObject(_ui?.Root)}; raw=[{string.Join(" | ", rawPlayers)}]; filtered=[{string.Join(" | ", filtered)}]";
            LogOverlayDebug(summary);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[OtherPlayersOverlayDebug] 记录玩家快照失败: {ex.Message}");
        }
    }

    private static void LogAvatarEntryDebug(AvatarEntryUi entry, PlayerSummary player, string stage)
    {
        if (entry == null)
        {
            return;
        }

        string snapshot = $"{stage}; playerId={player?.PlayerId ?? entry.PlayerId}; playerName={player?.PlayerName ?? "<null>"}; root={DescribeGameObject(entry.Root)}; health={DescribeGameObject(entry.HealthRoot)}; name={DescribeGameObject(entry.PlayerNameLabel?.gameObject)}; nameParent={GetTransformPath(entry.PlayerNameRect?.parent)}; nameRect={DescribeRect(entry.PlayerNameRect)}; nameFont={entry.PlayerNameLabel?.font?.name ?? "<null>"}; nameText={entry.PlayerNameLabel?.text ?? "<null>"}";
        if (string.Equals(entry.LastDebugSnapshot, snapshot, StringComparison.Ordinal))
        {
            return;
        }

        entry.LastDebugSnapshot = snapshot;
        LogOverlayDebug(snapshot, force: true);
    }

    private static bool IsOverlayDebugLoggingEnabled()
    {
        try
        {
            return Plugin.ConfigManager?.DebugOtherPlayersOverlay?.Value == true;
        }
        catch
        {
            return false;
        }
    }

    private static void LogOverlayDebug(string message, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(message) || Plugin.Logger == null || !IsOverlayDebugLoggingEnabled())
        {
            return;
        }

        try
        {
            float now = Time.unscaledTime;
            if (!force && string.Equals(message, _lastOverlayDebugSummary, StringComparison.Ordinal) && now < _nextOverlayDebugLogTime)
            {
                return;
            }

            _lastOverlayDebugSummary = message;
            _nextOverlayDebugLogTime = now + OverlayDebugLogInterval;
            Plugin.Logger.LogInfo($"[OtherPlayersOverlayDebug] {message}");
        }
        catch
        {
        }
    }

    private static string FormatPlayerSummary(PlayerSummary player)
    {
        if (player == null)
        {
            return "<null-player>";
        }

        string displayName = ResolveDisplayName(player.PlayerId, player.PlayerName);
        return $"{player.PlayerId}/{displayName}/conn={player.IsConnected}/host={player.IsHost}/char={player.CharacterId ?? "<null>"}/loc=({player.Stage},{player.LocationX},{player.LocationY},{player.LocationName ?? "<null>"})";
    }

    private static string DescribeGameObject(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return "<null-go>";
        }

        return $"{GetTransformPath(gameObject.transform)} activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy}";
    }

    private static string DescribeRect(RectTransform rect)
    {
        if (rect == null)
        {
            return "<null-rect>";
        }

        return $"anchorMin={rect.anchorMin} anchorMax={rect.anchorMax} pivot={rect.pivot} local={rect.localPosition} anchored={rect.anchoredPosition} size={rect.sizeDelta} scale={rect.localScale}";
    }

    private static string GetTransformPath(Transform transform)
    {
        if (transform == null)
        {
            return "<null-transform>";
        }

        List<string> parts = new List<string>();
        Transform current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    #region 工具方法

    private static bool IsBattleOverlayActive()
    {
        try
        {
            GameDirector director = Singleton<GameDirector>.Instance;
            return director != null && director.PlayerUnitView != null && !IsMapPanelVisible();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMapPanelVisible()
    {
        try
        {
            MapPanel mapPanel = UiManager.GetPanel<MapPanel>();
            return mapPanel != null && mapPanel.IsVisible;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldRenderRemoteCharacters(bool isMapPanelVisible)
    {
        if (isMapPanelVisible)
        {
            return false;
        }

        try
        {
            UnitView playerUnitView = Singleton<GameDirector>.Instance?.PlayerUnitView;
            return playerUnitView != null && !playerUnitView.IsHidden;
        }
        catch
        {
            return false;
        }
    }

    private static void RefreshVisibleMapPanelIcons_NoThrow()
    {
        try
        {
            MapPanel mapPanel = UiManager.GetPanel<MapPanel>();
            if (mapPanel != null && mapPanel.IsVisible)
            {
                UpdateMapIcons(mapPanel);
            }
        }
        catch
        {

        }
    }

    private static bool TryAttachUiToTopRight(int entryCount)
    {
        if (_ui?.RootRect == null)
        {
            return false;
        }

        Transform fallback = TryGetUiLayerTransform("topLayer") ?? TryGetUiLayerTransform("topmostLayer") ?? UiManager.Instance?.transform;
        RectTransform parentRect = fallback as RectTransform;
        if (parentRect == null)
        {
            return false;
        }

        if (_ui.RootRect.parent != parentRect)
        {
            _ui.RootRect.SetParent(parentRect, false);
        }

        float height = entryCount * AvatarEntryBaseHeight + Mathf.Max(0, entryCount - 1) * AvatarEntrySpacing + OverlayRootTopPadding + OverlayRootBottomPadding;

        ApplyRuntimeEditableRectLayout(_ui.RootRect, _ui.RootLayoutState, rect =>
        {
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.localPosition = OverlayRootLocalPosition;
            rect.sizeDelta = new Vector2(OverlayRootWidth, height);
        });

        ApplyRuntimeEditableRectLayout(_ui.EntriesRoot, _ui.EntriesRootLayoutState, rect =>
        {
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.localPosition = new Vector3(0f, -OverlayRootTopPadding, 0f);
            rect.sizeDelta = new Vector2(OverlayRootWidth, height - OverlayRootTopPadding - OverlayRootBottomPadding);
        });

        return true;
    }

    private static string BuildStatusText(string playerId, bool isConnected)
    {
        if (!isConnected)
        {
            return "离线";
        }

        PlayerSummary summary = null;
        lock (_syncLock)
        {
            _players.TryGetValue(playerId, out summary);
        }

        if (TryGetRemoteBattleState(summary, out RemoteBattleState battleState))
        {
            var parts = new List<string> { "在线", $"{Math.Max(0, battleState.Health)}/{Math.Max(1, battleState.MaxHealth)}" };
            int defense = Math.Max(0, battleState.Block) + Math.Max(0, battleState.Shield);
            if (defense > 0)
            {
                parts.Add($"盾{defense}");
            }

            if (battleState.PowerPerLevel > 0)
            {
                parts.Add($"符{Math.Max(0, battleState.CurrentPower)}");
            }

            return string.Join(" · ", parts);
        }

        INetworkPlayer player = TryGetNetworkManager()?.GetPlayer(playerId);
        if (player == null)
        {
            return "同步中";
        }

        try
        {
            var parts = new List<string> { "在线" };

            if (player.maxHP > 0)
            {
                parts.Add($"{Math.Max(0, player.HP)}/{player.maxHP}");
            }

            int defense = Math.Max(0, player.block) + Math.Max(0, player.shield);
            if (defense > 0)
            {
                parts.Add($"盾{defense}");
            }

            if (player.endturn)
            {
                parts.Add("已结束");
            }

            if (parts.Count == 1)
            {
                return "同步中";
            }

            return string.Join(" · ", parts);
        }
        catch
        {
            return "同步中";
        }
    }

    private static INetworkManager TryGetNetworkManager()
    {
        try
        {
            return ServiceProvider?.GetService<INetworkManager>();
        }
        catch
        {
            return null;
        }
    }

        private static TMP_FontAsset EnsureDefaultFont(Transform searchRoot)
    {
        if (_defaultFont != null)
        {
            return _defaultFont;
        }

        try
        {
            if (UiManager.Instance != null)
            {
                var tmps = UiManager.Instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (tmps != null && tmps.Length > 0)
                {
                    _defaultFont = tmps
                        .Select(tmp => tmp?.font)
                        .FirstOrDefault(font => font != null && !string.IsNullOrEmpty(font.name) &&
                            (font.name.IndexOf("Han", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             font.name.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             font.name.IndexOf("Noto", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             font.name.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             font.name.IndexOf("SDF", StringComparison.OrdinalIgnoreCase) >= 0));

                    if (_defaultFont == null)
                    {
                        _defaultFont = tmps.Select(tmp => tmp?.font).FirstOrDefault(font => font != null);
                    }
                }
            }
        }
        catch
        {

        }

        if (_defaultFont == null && searchRoot != null)
        {
            _defaultFont = FindDefaultFont(searchRoot);
        }

        if (_defaultFont == null)
        {
            try
            {
                _defaultFont = TMP_Settings.defaultFontAsset;
            }
            catch
            {

            }
        }

        return _defaultFont;
    }

        private static TMP_FontAsset FindDefaultFont(Transform root)
    {
        try
        {

            TextMeshProUGUI tmp = root.GetComponentInChildren<TextMeshProUGUI>(true);
            return tmp?.font;
        }
        catch
        {
            return null;
        }
    }

        private static Transform TryGetUiLayerTransform(string fieldName)
    {
        try
        {

            RectTransform rect = Traverse.Create(UiManager.Instance).Field(fieldName).GetValue<RectTransform>();
            return rect?.transform;
        }
        catch
        {
            return null;
        }
    }

    private static CanvasGroup GetOrAddCanvasGroup(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return null;
        }

        CanvasGroup group = gameObject.GetComponent<CanvasGroup>();
        return group ?? gameObject.AddComponent<CanvasGroup>();
    }

    private static void DestroyChildIfExists(Transform parent, string childName)
    {
        if (parent == null || string.IsNullOrWhiteSpace(childName))
        {
            return;
        }

        Transform child = parent.Find(childName);
        if (child != null)
        {
            UnityEngine.Object.Destroy(child.gameObject);
        }
    }

    private static T TryGetUltimateField<T>(UltimateSkillPanel panel, string fieldName) where T : class
    {
        if (panel == null || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        try
        {
            return Traverse.Create(panel).Field(fieldName).GetValue<T>();
        }
        catch
        {
            return null;
        }
    }

    private static Color TryGetUltimateColorField(UltimateSkillPanel panel, string fieldName, Color fallback)
    {
        if (panel == null || string.IsNullOrWhiteSpace(fieldName))
        {
            return fallback;
        }

        try
        {
            return Traverse.Create(panel).Field(fieldName).GetValue<Color>();
        }
        catch
        {
            return fallback;
        }
    }

    private static T TryGetUnitStatusField<T>(UnitStatusWidget widget, string fieldName) where T : class
    {
        if (widget == null || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        try
        {
            return Traverse.Create(widget).Field(fieldName).GetValue<T>();
        }
        catch
        {
            return null;
        }
    }

    private static Transform TryGetUnitStatusTransform(UnitStatusWidget widget, string fieldName)
    {
        return TryGetUnitStatusField<Transform>(widget, fieldName);
    }

    private static RectLayoutState CaptureRectLayoutState(RectTransform rect)
    {
        if (rect == null)
        {
            return new RectLayoutState();
        }

        return new RectLayoutState
        {
            HasApplied = true,
            AnchorMin = rect.anchorMin,
            AnchorMax = rect.anchorMax,
            Pivot = rect.pivot,
            LocalPosition = rect.localPosition,
            AnchoredPosition = rect.anchoredPosition,
            SizeDelta = rect.sizeDelta,
            LocalScale = rect.localScale,
            LocalEulerAngles = rect.localEulerAngles,
        };
    }

    private static void ApplyRuntimeEditableRectLayout(RectTransform rect, RectLayoutState state, Action<RectTransform> apply)
    {
        if (rect == null || state == null || apply == null)
        {
            return;
        }

        if (state.ManualOverride)
        {
            return;
        }

        if (state.HasApplied && HasRectLayoutDrifted(rect, state))
        {
            state.ManualOverride = true;
            return;
        }

        apply(rect);
        UpdateRectLayoutState(rect, state);
    }

    private static void UpdateRectLayoutState(RectTransform rect, RectLayoutState state)
    {
        if (rect == null || state == null)
        {
            return;
        }

        state.HasApplied = true;
        state.AnchorMin = rect.anchorMin;
        state.AnchorMax = rect.anchorMax;
        state.Pivot = rect.pivot;
        state.LocalPosition = rect.localPosition;
        state.AnchoredPosition = rect.anchoredPosition;
        state.SizeDelta = rect.sizeDelta;
        state.LocalScale = rect.localScale;
        state.LocalEulerAngles = rect.localEulerAngles;
    }

    private static bool HasRectLayoutDrifted(RectTransform rect, RectLayoutState state)
    {
        if (rect == null || state == null || !state.HasApplied)
        {
            return false;
        }

        return !Approximately(rect.anchorMin, state.AnchorMin) ||
               !Approximately(rect.anchorMax, state.AnchorMax) ||
               !Approximately(rect.pivot, state.Pivot) ||
             !Approximately(rect.localPosition, state.LocalPosition) ||
               !Approximately(rect.anchoredPosition, state.AnchoredPosition) ||
               !Approximately(rect.sizeDelta, state.SizeDelta) ||
               !Approximately(rect.localScale, state.LocalScale) ||
               !Approximately(rect.localEulerAngles, state.LocalEulerAngles);
    }

    private static bool Approximately(Vector2 a, Vector2 b)
    {
        return Mathf.Abs(a.x - b.x) <= RuntimeLayoutEpsilon && Mathf.Abs(a.y - b.y) <= RuntimeLayoutEpsilon;
    }

    private static bool Approximately(Vector3 a, Vector3 b)
    {
        return Mathf.Abs(a.x - b.x) <= RuntimeLayoutEpsilon &&
               Mathf.Abs(a.y - b.y) <= RuntimeLayoutEpsilon &&
               Mathf.Abs(a.z - b.z) <= RuntimeLayoutEpsilon;
    }

    #endregion

    #region 资源缓存

        private static Sprite _whiteSprite;

        private static Texture2D _whiteTexture;

        private static Sprite _circleBorderSprite;

        private static Texture2D _circleBorderTexture;

        private static Sprite GetWhiteSprite()
    {

        if (_whiteSprite != null)
        {
            return _whiteSprite;
        }

        _whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _whiteTexture.SetPixel(0, 0, Color.white);

        _whiteTexture.Apply(false, true);

        _whiteSprite = Sprite.Create(_whiteTexture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        return _whiteSprite;
    }

    internal static Sprite GetCircleBorderSprite()
    {
        if (_circleBorderSprite != null)
        {
            return _circleBorderSprite;
        }

        const int size = 128;
        const float radius = (size - 1) * 0.5f;
        const float center = radius;
        const float innerRadius = radius - 4f;

        _circleBorderTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distSq = dx * dx + dy * dy;
                bool inside = distSq <= radius * radius && distSq >= innerRadius * innerRadius;
                _circleBorderTexture.SetPixel(x, y, inside ? Color.white : Color.clear);
            }
        }

        _circleBorderTexture.Apply(false, true);
        _circleBorderSprite = Sprite.Create(_circleBorderTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return _circleBorderSprite;
    }

    internal static Sprite GetCircleMaskSprite()
    {
        if (_circleMaskSprite != null)
        {
            return _circleMaskSprite;
        }

        const int size = 128;
        const float radius = (size - 1) * 0.5f;
        const float center = radius;

        _circleMaskTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                bool inside = dx * dx + dy * dy <= radius * radius;
                _circleMaskTexture.SetPixel(x, y, inside ? Color.white : Color.clear);
            }
        }

        _circleMaskTexture.Apply(false, true);
        _circleMaskSprite = Sprite.Create(_circleMaskTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return _circleMaskSprite;
    }

    #endregion

    #region 数据模型

    internal static bool TryGetRemoteBattleStateForTest(PlayerSummary playerSummary, out RemoteBattleState state)
    {
        if (playerSummary != null)
        {
            lock (_syncLock)
            {
                _players[playerSummary.PlayerId] = playerSummary;
            }
        }
        return TryGetRemoteBattleState(playerSummary, out state);
    }

    internal static string BuildStatusTextForTest(string playerId, bool isConnected)
    {
        return BuildStatusText(playerId, isConnected);
    }

    internal sealed class PlayerSummary
    {
                public string PlayerId { get; set; }

                public string PlayerName { get; set; }

                public bool IsHost { get; set; }

                public bool IsConnected { get; set; }

                public string CharacterId { get; set; }

                public int LocationX { get; set; }

                public int LocationY { get; set; }

                public int Stage { get; set; }

                public string LocationName { get; set; }

                public int Hp { get; set; } = -1;

                public int MaxHp { get; set; } = -1;

                public float LastUpdateTime { get; set; }
    }

        private sealed class OverlayUi
    {
        public GameObject Root { get; set; }
        public RectTransform RootRect { get; set; }
        public RectTransform EntriesRoot { get; set; }
        public RectLayoutState RootLayoutState { get; set; }
        public RectLayoutState EntriesRootLayoutState { get; set; }
        public GameObject AvatarTemplate { get; set; }
        public GameObject HealthBarTemplate { get; set; }
        public Dictionary<string, AvatarEntryUi> Entries { get; set; }
    }

    private sealed class RectLayoutState
    {
        public bool HasApplied { get; set; }
        public bool ManualOverride { get; set; }
        public Vector2 AnchorMin { get; set; }
        public Vector2 AnchorMax { get; set; }
        public Vector2 Pivot { get; set; }
        public Vector3 LocalPosition { get; set; }
        public Vector2 AnchoredPosition { get; set; }
        public Vector2 SizeDelta { get; set; }
        public Vector3 LocalScale { get; set; }
        public Vector3 LocalEulerAngles { get; set; }
    }

    private sealed class AvatarEntryUi
    {
        public string PlayerId { get; set; }
        public GameObject Root { get; set; }
        public RectTransform RootRect { get; set; }
        public RectLayoutState RootLayoutState { get; set; }
        public RectTransform VisualRect { get; set; }
        public RectLayoutState VisualLayoutState { get; set; }
        public Image Avatar { get; set; }
        public TextMeshProUGUI Status { get; set; }
        public RectTransform StatusRect { get; set; }
        public RectLayoutState StatusLayoutState { get; set; }
        public TextMeshProUGUI PlayerNameLabel { get; set; }
        public RectTransform PlayerNameRect { get; set; }
        public RectLayoutState PlayerNameLayoutState { get; set; }
        public CanvasGroup VisualGroup { get; set; }
        public TextMeshProUGUI PowerText { get; set; }
        public Image Gauge1 { get; set; }
        public Image Gauge2 { get; set; }
        public Image Gauge3 { get; set; }
        public Color Gauge1FontColor { get; set; }
        public Color Gauge2FontColor { get; set; }
        public Color Gauge3FontColor { get; set; }
        public GameObject HealthRoot { get; set; }
        public RectTransform HealthRootRect { get; set; }
        public RectLayoutState HealthRootLayoutState { get; set; }
        public UnitStatusWidget HealthWidget { get; set; }
        public HealthBar HealthBar { get; set; }
        public CanvasGroup HealthGroup { get; set; }
        public int LastCurrentPower { get; set; } = int.MinValue;
        public int LastPowerPerLevel { get; set; } = int.MinValue;
        public int LastMaxPowerLevel { get; set; } = int.MinValue;
        public int LastHealth { get; set; } = int.MinValue;
        public int LastMaxHealth { get; set; } = int.MinValue;
        public int LastShield { get; set; } = int.MinValue;
        public int LastBlock { get; set; } = int.MinValue;
        public bool HasInitializedHealthBar { get; set; }
        public string LastDebugSnapshot { get; set; }
    }

    [HarmonyPatch(typeof(GameDirector), nameof(GameDirector.GetUnit))]
    private static class GameDirector_GetUnit_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(LBoL.Core.Units.Unit unit, ref UnitView __result)
        {
            if (__result != null || unit == null)
            {
                return;
            }

            lock (_syncLock)
            {
                if (_remoteCharacters == null || _remoteCharacters.Count == 0)
                {
                    return;
                }

                foreach (var rc in _remoteCharacters.Values)
                {
                    if (rc?.View != null && rc.View.Unit == unit)
                    {
                        __result = rc.View;
                        return;
                    }
                }
            }
        }
    }
}
    #endregion
