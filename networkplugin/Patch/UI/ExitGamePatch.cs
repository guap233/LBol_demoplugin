using System;
using System.Reflection;
using HarmonyLib;
using LBoL.Core;
using LBoL.Presentation;
using LBoL.Presentation.UI;
using LBoL.Presentation.UI.Dialogs;
using LBoL.Presentation.UI.Panels;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using UnityEngine.UI;

namespace NetworkPlugin.Patch.UI;

[HarmonyPatch]
public static class ExitGamePatch
{
    private static int _quitGameInterceptCount;

    #region 依赖注入服务访问

        private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

        private static INetworkClient TryGetNetworkClient()
        => ServiceProvider?.GetService<INetworkClient>();

        private static INetworkManager TryGetNetworkManager()
        => ServiceProvider?.GetService<INetworkManager>();

    #endregion

    #region 联机状态辅助方法

        private static bool IsMultiplayerConnected()
        => TryGetNetworkClient()?.IsConnected == true;

        private static bool TryIsHost()
    {
        try
        {
            INetworkClient client = TryGetNetworkClient();
            if (client == null)
                return false;

            Type clientType = client.GetType();

            PropertyInfo prop = clientType.GetProperty(
                "IsHost",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            if (prop != null && prop.PropertyType == typeof(bool))
            {
                return (bool)prop.GetValue(client);
            }

            FieldInfo field = clientType.GetField(
                "IsHost",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            if (field != null && field.FieldType == typeof(bool))
            {
                return (bool)field.GetValue(client);
            }
        }
        catch
        {

        }

        return false;
    }

    #endregion

    #region 断开联机与返回主菜单流程

    private static void DisconnectMultiplayer()
    {
        try
        {
            MainMenuMultiplayerEntryPatch.ShutdownMultiplayerSession();
            Plugin.Logger?.LogInfo("[退出/返回主菜单] 已断开联机连接并重置联机状态");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[退出/返回主菜单] 统一关闭联机失败: {ex.Message}");
        }
    }

        private static void DisconnectAndReturnToMainMenu()
    {

        DisconnectMultiplayer();

        try
        {

            if (Singleton<GameMaster>.Instance?.CurrentGameRun != null)
            {
                Plugin.Logger?.LogInfo("[退出/返回主菜单] 当前在游戏局内，开始退出并返回主菜单");
                GameMaster.LeaveGameRun();
            }
            else
            {

                Plugin.Logger?.LogInfo("[退出/返回主菜单] 当前不在游戏局内，刷新主菜单档案信息");
                UiManager.GetPanel<MainMenuPanel>()?.RefreshProfile();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[退出/返回主菜单] 返回主菜单失败: {ex.Message}");
        }
    }

    #endregion

    #region 弹窗辅助方法

        private static void ShowConnectedQuitDialog(string textKey)
    {
        try
        {
            bool isHost = TryIsHost();

            string subText = isHost
                ? "联机模式：你是房主，退出将导致房间关闭并影响所有已连接玩家。\n是否继续？"
                : "联机模式：退出将断开联机并返回主菜单。\n之后可以重新连接继续游戏。\n是否继续？";

            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    TextKey = textKey,
                    SubText = subText,
                    Buttons = DialogButtons.ConfirmCancel,
                    Icon = MessageIcon.Warning,

                    OnConfirm = DisconnectAndReturnToMainMenu
                }
            );
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[退出/返回主菜单] 显示退出确认弹窗失败: {ex.Message}");
        }
    }

        private static void ShowConnectedAbandonBlockedDialog()
    {
        try
        {
            UiManager.GetDialog<MessageDialog>().Show(
                new MessageContent
                {
                    Text = "联机模式下已禁用“放弃存档/弃档”。\n请先断开联机后再进行该操作。",
                    Buttons = DialogButtons.Confirm,
                    Icon = MessageIcon.Warning
                }
            );
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[退出/返回主菜单] 显示弃档拦截弹窗失败: {ex.Message}");
        }
    }

    #endregion

    #region 主菜单面板补丁

        [HarmonyPatch(typeof(MainMenuPanel), "RefreshProfile")]
    [HarmonyPostfix]
    public static void MainMenuPanel_RefreshProfile_Postfix(MainMenuPanel __instance)
    {
        try
        {

            if (!IsMultiplayerConnected())
                return;

            Button abandonButton = Traverse.Create(__instance)
                .Field("abandonGameButton")
                .GetValue<Button>();
            abandonButton?.gameObject.SetActive(false);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[退出/返回主菜单] RefreshProfile 补丁失败: {ex.Message}");
        }
    }

        [HarmonyPatch(typeof(MainMenuPanel), "UI_AbandonGameClicked")]
    [HarmonyPrefix]
    public static bool MainMenuPanel_UI_AbandonGameClicked_Prefix()
    {

        if (!IsMultiplayerConnected())
            return true;

        ShowConnectedAbandonBlockedDialog();
        return false;
    }

        [HarmonyPatch(typeof(MainMenuPanel), "UI_QuitGame")]
    [HarmonyPrefix]
    public static bool MainMenuPanel_UI_QuitGame_Prefix()
    {
        if (!IsMultiplayerConnected())
            return true;

        ShowConnectedQuitDialog("QuitGame");
        return false;
    }

    #endregion

    #region 设置面板补丁

        [HarmonyPatch(typeof(SettingPanel), "UI_LeaveGameRun")]
    [HarmonyPrefix]
    public static bool SettingPanel_UI_LeaveGameRun_Prefix()
    {
        if (!IsMultiplayerConnected())
            return true;

        ShowConnectedQuitDialog("ReturnToMainMenu");
        return false;
    }

        [HarmonyPatch(typeof(SettingPanel), "UI_Quit")]
    [HarmonyPrefix]
    public static bool SettingPanel_UI_Quit_Prefix()
    {
        if (!IsMultiplayerConnected())
            return true;

        ShowConnectedQuitDialog("QuitGame");
        return false;
    }

    #endregion

    #region 游戏主控 GameMaster 补丁

        [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.OnWantsToQuit))]
    [HarmonyPrefix]
    public static bool GameMaster_OnWantsToQuit_Prefix(ref bool __result)
    {
        if (!IsMultiplayerConnected())
            return true;

        __result = false;
        ShowConnectedQuitDialog("QuitGame");
        return false;
    }

        [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.QuitGame))]
    [HarmonyPrefix]
    public static bool GameMaster_QuitGame_Prefix()
    {
        if (!IsMultiplayerConnected())
            return true;

        int count = ++_quitGameInterceptCount;
        Plugin.Logger?.LogWarning(
            $"[退出/返回主菜单] 拦截到 GameMaster.QuitGame 调用（第{count}次）。为避免误断线/误退回主菜单，本次已忽略。"
        );

        if (count <= 3)
        {
            try
            {
                Plugin.Logger?.LogWarning($"[退出/返回主菜单] QuitGame 调用栈:\n{Environment.StackTrace}");
            }
            catch
            {

            }
        }

        return false;
    }

    #endregion
}
