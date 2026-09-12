using System;
using HarmonyLib;
using LBoL.Presentation.UI;
using UnityEngine;

namespace NetworkPlugin.Patch.UI;

[HarmonyPatch(typeof(UiManager), "HideDialogCheck")]
public static class HideDialogCheckPatch
{
    [HarmonyPrefix]
    public static bool Prefix(UiDialogBase dialog)
    {
        try
        {
            var instance = UiManager.Instance;
            if (instance == null)
            {
                return false;
            }

            var currentDialogField = Traverse.Create(instance).Field<UiDialogBase>("_currentDialog");
            var currentDialog = currentDialogField.Value;

            if (currentDialog != dialog)
            {
                string currentName = currentDialog != null ? currentDialog.GetType().Name : "<null>";
                string dialogName = dialog != null ? dialog.GetType().Name : "<null>";
                Plugin.Logger?.LogWarning($"[HideDialogCheckPatch] Hiding dialog '{dialogName}' while current dialog == '{currentName}'");
            }

            currentDialogField.Value = null;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"[HideDialogCheckPatch] Error during HideDialogCheck safe handle: {ex}");
        }

        return false;
    }
}
