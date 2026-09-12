using System;
using System.Collections.Generic;
using HarmonyLib;
using LBoL.Core;
using LBoL.Core.Cards;
using LBoL.Core.Units;
using Microsoft.Extensions.DependencyInjection;
using NetworkPlugin.Network.Services;
using NetworkPlugin.Network.Client;
using NetworkPlugin.Network.MidGameJoin;
using NetworkPlugin.Network.Snapshot;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Patch.UI;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Patch.MidGameJoin;

[HarmonyPatch]
public static class JoinerStartGameLockPatch
{
    private static IServiceProvider ServiceProvider => ModService.ServiceProvider;

    private static INetworkClient TryGetClient()
        => ServiceProvider?.GetService<INetworkClient>();

    private static MapCatchUpOrchestrator TryGetCatchUp()
        => ServiceProvider?.GetService<MapCatchUpOrchestrator>();

        [HarmonyPatch(typeof(LBoL.Presentation.GameMaster), nameof(LBoL.Presentation.GameMaster.StartGame),
        new[]
        {
            typeof(ulong?),
            typeof(GameDifficulty),
            typeof(PuzzleFlag),
            typeof(PlayerUnit),
            typeof(PlayerType),
            typeof(Exhibit),
            typeof(int?),
            typeof(IEnumerable<Card>),
            typeof(IEnumerable<Stage>),
            typeof(Type),
            typeof(IEnumerable<JadeBox>),
            typeof(GameMode),
            typeof(bool),
        })]
    [HarmonyPrefix]
    public static void GameMaster_StartGame_Prefix(
        ref ulong? seed,
        ref GameDifficulty difficulty,
        ref PuzzleFlag puzzles,
        PlayerUnit player,
        PlayerType playerType,
        Exhibit initExhibit,
        int? initMoneyOverride,
        IEnumerable<Card> deck,
        ref IEnumerable<Stage> stages,
        ref Type debutAdventureType,
        ref IEnumerable<JadeBox> jadeBoxes,
        ref GameMode gameMode,
        ref bool showRandomResult)
    {
        try
        {
            MainMenuMultiplayerEntryPatch.HideRoomListOverlay(immediate: true);

            INetworkClient client = TryGetClient();
            if (client == null || !client.IsConnected)
            {
                return;
            }

            NetworkIdentityTracker.EnsureSubscribed(client);

            if (NetworkIdentityTracker.GetSelfIsHost())
            {
                return;
            }

            MapCatchUpOrchestrator catchUp = TryGetCatchUp();
            if (catchUp == null)
            {

                TryApplyCachedHostSeed(ref seed, ref difficulty, ref puzzles, ref stages, ref debutAdventureType, ref jadeBoxes, ref gameMode, ref showRandomResult);
                return;
            }

            if (!catchUp.TryGetPendingFullSnapshot(out FullStateSnapshot snapshot))
            {

                TryApplyCachedHostSeed(ref seed, ref difficulty, ref puzzles, ref stages, ref debutAdventureType, ref jadeBoxes, ref gameMode, ref showRandomResult);
                return;
            }

            if (snapshot?.GameState == null)
            {
                TryApplyCachedHostSeed(ref seed, ref difficulty, ref puzzles, ref stages, ref debutAdventureType, ref jadeBoxes, ref gameMode, ref showRandomResult);
                return;
            }

            if (snapshot.GameState.RootSeed == null ||
                snapshot.GameState.Difficulty == null ||
                snapshot.GameState.Puzzles == null ||
                snapshot.GameState.GameMode == null ||
                snapshot.GameState.StageTypeNames == null ||
                snapshot.GameState.StageTypeNames.Count == 0)
            {

                TryApplyCachedHostSeed(ref seed, ref difficulty, ref puzzles, ref stages, ref debutAdventureType, ref jadeBoxes, ref gameMode, ref showRandomResult);
                return;
            }

            seed = snapshot.GameState.RootSeed;
            difficulty = (GameDifficulty)snapshot.GameState.Difficulty.Value;
            puzzles = (PuzzleFlag)snapshot.GameState.Puzzles.Value;
            gameMode = (GameMode)snapshot.GameState.GameMode.Value;

            if (snapshot.GameState.ShowRandomResult != null)
            {
                showRandomResult = snapshot.GameState.ShowRandomResult.Value;
            }

            stages = BuildStages(snapshot.GameState.StageTypeNames, stages);

            if (!string.IsNullOrWhiteSpace(snapshot.GameState.DebutAdventureTypeName))
            {
                Type resolved = TryResolveDebutAdventureType(snapshot.GameState.DebutAdventureTypeName);
                if (resolved != null)
                {
                    debutAdventureType = resolved;
                }
            }
        }
        catch
        {

        }
    }

    private static IEnumerable<Stage> BuildStages(List<string> stageTypeNames, IEnumerable<Stage> fallback)
    {
        try
        {
            if (stageTypeNames == null || stageTypeNames.Count == 0)
            {
                return fallback;
            }

            List<Stage> stages = new();
            foreach (string name in stageTypeNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                Stage s = Library.CreateStage(name);
                if (s != null)
                {
                    stages.Add(s);
                }
            }

            return stages.Count > 0 ? stages : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Type TryResolveDebutAdventureType(string typeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            try
            {
                var adv = Library.TryCreateAdventure(typeName);
                if (adv != null)
                {
                    return adv.GetType();
                }
            }
            catch
            {

            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in asm.GetTypes())
                    {
                        if (t != null && string.Equals(t.Name, typeName, StringComparison.Ordinal))
                        {
                            return t;
                        }
                    }
                }
                catch
                {

                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

        private static void TryApplyCachedHostSeed(
        ref ulong? seed,
        ref GameDifficulty difficulty,
        ref PuzzleFlag puzzles,
        ref IEnumerable<Stage> stages,
        ref Type debutAdventureType,
        ref IEnumerable<JadeBox> jadeBoxes,
        ref GameMode gameMode,
        ref bool showRandomResult)
    {
        try
        {
            if (GameSeedSyncPatch.TryGetCachedHostConfig(
                out ulong rootSeed,
                out GameDifficulty cachedDifficulty,
                out PuzzleFlag cachedPuzzles,
                out GameMode cachedGameMode,
                out bool cachedShowRandomResult,
                out List<string> stageTypeNames,
                out string? debutAdventureTypeName,
                out List<string> jadeBoxIds))
            {
                seed = rootSeed;
                difficulty = cachedDifficulty;
                puzzles = cachedPuzzles;
                gameMode = cachedGameMode;
                showRandomResult = cachedShowRandomResult;

                stages = BuildStages(stageTypeNames, stages);

                if (!string.IsNullOrWhiteSpace(debutAdventureTypeName))
                {
                    Type resolved = TryResolveDebutAdventureType(debutAdventureTypeName);
                    if (resolved != null)
                    {
                        debutAdventureType = resolved;
                    }
                }

                jadeBoxes = BuildJadeBoxes(jadeBoxIds, jadeBoxes);

                Plugin.Logger?.LogInfo($"[JoinerStartGameLock] 使用缓存房主配置: RootSeed={rootSeed}, Difficulty={cachedDifficulty}, Mode={cachedGameMode}, JadeBoxes={jadeBoxIds.Count}");
            }
            else if (GameSeedSyncPatch.TryGetCachedHostSeed(out ulong cachedSeed))
            {
                seed = cachedSeed;
                Plugin.Logger?.LogInfo($"[JoinerStartGameLock] 使用缓存房主种子(仅seed): RootSeed={cachedSeed}");
            }
        }
        catch
        {

        }
    }

    private static IEnumerable<JadeBox> BuildJadeBoxes(List<string> jadeBoxIds, IEnumerable<JadeBox> fallback)
    {
        try
        {
            if (jadeBoxIds == null || jadeBoxIds.Count == 0)
            {
                return fallback;
            }

            List<JadeBox> result = new();
            foreach (string id in jadeBoxIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                JadeBox jb = Library.TryCreateJadeBox(id);
                if (jb != null)
                {
                    result.Add(jb);
                }
            }

            return result.Count > 0 ? result : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
