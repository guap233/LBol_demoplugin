using System;
using System.Collections.Generic;
using System.Linq;
using LBoL.Base;
using LBoL.Core;
using LBoL.Core.Cards;
using LBoL.Presentation.UI;
using LBoL.Presentation.UI.Panels;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.UI.Rules;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Core.Trade;

public class GameRunTradeInventory : ITradeInventory
{
    private readonly Func<GameRunController> _gameRunProvider;
    private readonly Func<string, bool, int, Card> _cardFactory;
    private readonly Func<string, Exhibit> _exhibitFactory;

    public const int MaxHistoryCapacity = 32;
    public static Action<Exhibit> OnExhibitAddedToUiHook;
    public static Action<Exhibit> OnExhibitRemovedFromUiHook;

    private static void TryNotifyExhibitAddedToUi(Exhibit exhibit)
    {
        if (exhibit == null) return;
        try
        {
            OnExhibitAddedToUiHook?.Invoke(exhibit);
            if (UiManager.IsInitialized)
            {
                var systemBoard = UiManager.GetPanel<SystemBoard>();
                if (systemBoard != null)
                {
                    systemBoard.OnExhibitAdded(exhibit, 0f);
                    Plugin.Logger?.LogInfo($"[GameRunTradeInventory] 成功刷新 SystemBoard 顶栏展品: {exhibit.Id}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[GameRunTradeInventory] 刷新 SystemBoard 展品异常 (非阻塞): {ex.Message}");
        }
    }

    private static void TryNotifyExhibitRemovedFromUi(Exhibit exhibit)
    {
        if (exhibit == null) return;
        try
        {
            OnExhibitRemovedFromUiHook?.Invoke(exhibit);
            if (UiManager.IsInitialized)
            {
                var systemBoard = UiManager.GetPanel<SystemBoard>();
                if (systemBoard != null)
                {
                    systemBoard.OnExhibitRemoved(exhibit);
                    Plugin.Logger?.LogInfo($"[GameRunTradeInventory] 成功从 SystemBoard 顶栏移除展品: {exhibit.Id}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[GameRunTradeInventory] 移除 SystemBoard 展品异常 (非阻塞): {ex.Message}");
        }
    }

    private readonly object _lock = new();
    private readonly List<string> _historyOrder = new();
    private readonly Dictionary<string, TradeCommitStatus> _commitStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TradeInventorySnapshot> _snapshots = new(StringComparer.Ordinal);

    public static TradeInventorySnapshot CaptureSnapshot(GameRunController run)
    {
        if (run == null || run.Player == null)
        {
            return null;
        }

        var snapshot = new TradeInventorySnapshot
        {
            Money = run.Money,
            ExhibitIds = new HashSet<string>(
                (run.Player.Exhibits ?? Enumerable.Empty<Exhibit>())
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
                    .Select(e => e.Id),
                StringComparer.Ordinal)
        };

        if (run.BaseDeck != null)
        {
            foreach (var card in run.BaseDeck)
            {
                if (card != null && card.InstanceId >= 0)
                {
                    snapshot.DeckCardsByInstanceId[card.InstanceId] = card;
                }
            }
        }

        return snapshot;
    }

    private static List<TradeInventoryChangeLogEntry> ReconcileChangesAgainstSnapshot(
        TradeInventorySnapshot snapshot,
        GameRunController run,
        TradeSettlementPlan plan,
        List<TradeInventoryChangeLogEntry> loggedEntries)
    {
        var result = new List<TradeInventoryChangeLogEntry>(loggedEntries ?? Enumerable.Empty<TradeInventoryChangeLogEntry>());
        if (snapshot == null || run == null || run.Player == null || plan == null)
        {
            return result;
        }

        // 1. 卡牌支出核对：检查 plan.SpentCards 中哪些实际已不在 BaseDeck 中
        foreach (var card in plan.SpentCards)
        {
            if (card == null || card.InstanceId < 0) continue;
            bool alreadyInLog = result.Any(e => e.ChangeType == TradeInventoryChangeType.CardRemoved && e.InstanceId == card.InstanceId);
            if (!alreadyInLog)
            {
                if (run.GetDeckCardByInstanceId(card.InstanceId) == null)
                {
                    snapshot.DeckCardsByInstanceId.TryGetValue(card.InstanceId, out var origInstance);
                    result.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.CardRemoved,
                        InstanceId = card.InstanceId,
                        CardId = card.CardId,
                        CardInstance = origInstance,
                    });
                }
            }
        }

        // 2 & 5. 金币核对（差分守恒对账）：
        // 目标是让经过后续补偿后，金币精确守恒恢复回 snapshot.Money。
        // 已记录的扣除补偿将增加 money（+loggedConsumed），已记录的获得补偿将扣除 money（-loggedGained）。
        // 预期未对账差额 diff = snapshot.Money - (run.Money + loggedConsumed - loggedGained)。
        int loggedConsumed = result.Where(e => e.ChangeType == TradeInventoryChangeType.MoneyConsumed).Sum(e => e.MoneyAmount);
        int loggedGained = result.Where(e => e.ChangeType == TradeInventoryChangeType.MoneyGained).Sum(e => e.MoneyAmount);
        int projectedAfterComp = run.Money + loggedConsumed - loggedGained;
        int moneyBalanceDiff = snapshot.Money - projectedAfterComp;

        if (moneyBalanceDiff > 0 && plan.SpentMoney > loggedConsumed)
        {
            int unloggedConsumed = Math.Min(plan.SpentMoney - loggedConsumed, moneyBalanceDiff);
            result.Add(new TradeInventoryChangeLogEntry
            {
                ChangeType = TradeInventoryChangeType.MoneyConsumed,
                MoneyAmount = unloggedConsumed,
            });
        }
        else if (moneyBalanceDiff < 0 && plan.GainedMoney > loggedGained)
        {
            int unloggedGained = Math.Min(plan.GainedMoney - loggedGained, -moneyBalanceDiff);
            result.Add(new TradeInventoryChangeLogEntry
            {
                ChangeType = TradeInventoryChangeType.MoneyGained,
                MoneyAmount = unloggedGained,
            });
        }

        // 3. 展品扣除核对：
        foreach (var ex in plan.SpentExhibits)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.ExhibitId)) continue;
            bool alreadyInLog = result.Any(e => e.ChangeType == TradeInventoryChangeType.ExhibitRemoved && string.Equals(e.ExhibitId, ex.ExhibitId, StringComparison.Ordinal));
            if (!alreadyInLog)
            {
                bool stillOwned = run.Player.Exhibits?.Any(e => e != null && string.Equals(e.Id, ex.ExhibitId, StringComparison.Ordinal)) == true;
                if (!stillOwned)
                {
                    result.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.ExhibitRemoved,
                        ExhibitId = ex.ExhibitId,
                    });
                }
            }
        }

        // 4. 展品获得核对：
        foreach (var ex in plan.GainedExhibits)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.ExhibitId)) continue;
            bool alreadyInLog = result.Any(e => e.ChangeType == TradeInventoryChangeType.ExhibitAdded && string.Equals(e.ExhibitId, ex.ExhibitId, StringComparison.Ordinal));
            if (!alreadyInLog)
            {
                Exhibit gainedInstance = run.Player.Exhibits?.FirstOrDefault(e => e != null && string.Equals(e.Id, ex.ExhibitId, StringComparison.Ordinal));
                if (gainedInstance != null && !snapshot.ExhibitIds.Contains(ex.ExhibitId))
                {
                    result.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.ExhibitAdded,
                        ExhibitId = ex.ExhibitId,
                        ExhibitInstance = gainedInstance,
                    });
                }
            }
        }

        // 6. 卡牌增加核对（通过快照实例引用排他比对与多重集核实，规避同名卡牌漏记与 InstanceId 碰撞）：
        if (run.BaseDeck != null)
        {
            var newlyAddedCardsInDeck = run.BaseDeck
                .Where(c => c != null && !snapshot.DeckCardsByInstanceId.Values.Any(orig => ReferenceEquals(orig, c)))
                .ToList();

            var matchedLoggedEntries = new HashSet<TradeInventoryChangeLogEntry>();

            foreach (var addedInstance in newlyAddedCardsInDeck)
            {
                // 优先按实例引用或有效 InstanceId 匹配已记录日志
                var matchingEntry = result.FirstOrDefault(e =>
                    e.ChangeType == TradeInventoryChangeType.CardAdded &&
                    !matchedLoggedEntries.Contains(e) &&
                    (ReferenceEquals(e.CardInstance, addedInstance) ||
                     (e.InstanceId >= 0 && e.InstanceId == addedInstance.InstanceId)));

                // 兼容兜底：若日志中实例引用和 InstanceId 均为空，则按相同 CardId 逐个配对
                if (matchingEntry == null)
                {
                    matchingEntry = result.FirstOrDefault(e =>
                        e.ChangeType == TradeInventoryChangeType.CardAdded &&
                        !matchedLoggedEntries.Contains(e) &&
                        e.CardInstance == null &&
                        e.InstanceId < 0 &&
                        string.Equals(e.CardId, addedInstance.Id, StringComparison.Ordinal));
                }

                if (matchingEntry != null)
                {
                    matchedLoggedEntries.Add(matchingEntry);
                }
                else
                {
                    result.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.CardAdded,
                        InstanceId = addedInstance.InstanceId,
                        CardId = addedInstance.Id,
                        CardInstance = addedInstance,
                    });
                }
            }
        }

        return result;
    }

    private static string VerifyInventoryAgainstSnapshot(GameRunController run, TradeInventorySnapshot snapshot)
    {
        if (snapshot == null || run == null || run.Player == null)
        {
            return null;
        }

        // 1. 金币核对
        if (run.Money != snapshot.Money)
        {
            return $"金币未恢复守恒: 预期 {snapshot.Money}, 实际 {run.Money}";
        }

        // 2. 展品核对
        var currentExhibitIds = new HashSet<string>(
            (run.Player.Exhibits ?? Enumerable.Empty<Exhibit>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
                .Select(e => e.Id),
            StringComparer.Ordinal);

        if (!currentExhibitIds.SetEquals(snapshot.ExhibitIds))
        {
            var missing = string.Join(", ", snapshot.ExhibitIds.Except(currentExhibitIds));
            var extra = string.Join(", ", currentExhibitIds.Except(snapshot.ExhibitIds));
            return $"展品未恢复守恒: 缺失=[{missing}], 多余=[{extra}]";
        }

        // 3. 牌库卡牌核对
        int currentDeckCount = run.BaseDeck?.Count ?? 0;
        int expectedDeckCount = snapshot.DeckCardsByInstanceId.Count;
        if (currentDeckCount != expectedDeckCount)
        {
            return $"牌库卡牌张数未恢复守恒: 预期 {expectedDeckCount}, 实际 {currentDeckCount}";
        }

        foreach (var pair in snapshot.DeckCardsByInstanceId)
        {
            int instId = pair.Key;
            Card origCard = pair.Value as Card;
            if (run.GetDeckCardByInstanceId(instId) == null)
            {
                bool hasEquivalent = run.BaseDeck?.Any(c => c != null && origCard != null &&
                    string.Equals(c.Id, origCard.Id, StringComparison.Ordinal) &&
                    c.IsUpgraded == origCard.IsUpgraded &&
                    c.UpgradeCounter == origCard.UpgradeCounter) == true;

                if (!hasEquivalent)
                {
                    return $"牌库卡牌未恢复守恒: 缺失卡牌 {origCard?.Id ?? "Unknown"} (#{instId})";
                }
            }
        }

        return null;
    }

    public GameRunTradeInventory(
        Func<GameRunController> gameRunProvider = null,
        Func<string, bool, int, Card> cardFactory = null,
        Func<string, Exhibit> exhibitFactory = null)
    {
        _gameRunProvider = gameRunProvider ?? GameStateUtils.GetCurrentGameRun;
        _cardFactory = cardFactory ?? ((id, upgraded, count) => Library.TryCreateCard(id, upgraded, count));
        _exhibitFactory = exhibitFactory ?? (id => Library.TryCreateExhibit(id));
    }

    private GameRunController GetGameRun() => _gameRunProvider?.Invoke();

    public TradeSettlementPlan BuildPlan(TradeSyncPatch.TradeSessionState state, string localPlayerId)
    {
        if (state == null || string.IsNullOrWhiteSpace(localPlayerId))
        {
            return null;
        }

        bool isA = string.Equals(state.PlayerAId, localPlayerId, StringComparison.OrdinalIgnoreCase);
        bool isB = string.Equals(state.PlayerBId, localPlayerId, StringComparison.OrdinalIgnoreCase);
        if (!isA && !isB)
        {
            return null;
        }

        var plan = new TradeSettlementPlan
        {
            TradeId = state.TradeId ?? string.Empty,
            CommitId = state.CommitId ?? string.Empty,
            LocalPlayerId = localPlayerId,
            PartnerPlayerId = isA ? state.PlayerBId : state.PlayerAId,
            IsLocalPlayerA = isA,
            SpentMoney = isA ? state.MoneyA : state.MoneyB,
            GainedMoney = isA ? state.MoneyB : state.MoneyA,
        };

        var spentCardsSource = isA ? state.OfferA : state.OfferB;
        if (spentCardsSource != null)
        {
            foreach (var card in spentCardsSource)
            {
                if (card == null) continue;
                plan.SpentCards.Add(new TradeCardItem
                {
                    InstanceId = card.InstanceId,
                    CardId = card.CardId,
                    IsUpgraded = card.IsUpgraded,
                    UpgradeCounter = card.UpgradeCounter,
                    DeckCounter = card.DeckCounter,
                    CardName = card.CardName,
                    CardType = card.CardType,
                });
            }
        }

        var gainedCardsSource = isA ? state.OfferB : state.OfferA;
        if (gainedCardsSource != null)
        {
            foreach (var card in gainedCardsSource)
            {
                if (card == null) continue;
                plan.GainedCards.Add(new TradeCardItem
                {
                    InstanceId = card.InstanceId,
                    CardId = card.CardId,
                    IsUpgraded = card.IsUpgraded,
                    UpgradeCounter = card.UpgradeCounter,
                    DeckCounter = card.DeckCounter,
                    CardName = card.CardName,
                    CardType = card.CardType,
                });
            }
        }

        var spentExhibitsSource = isA ? state.ExhibitsA : state.ExhibitsB;
        if (spentExhibitsSource != null)
        {
            foreach (var ex in spentExhibitsSource)
            {
                if (ex == null || string.IsNullOrWhiteSpace(ex.ExhibitId)) continue;
                plan.SpentExhibits.Add(new TradeExhibitItem { ExhibitId = ex.ExhibitId });
            }
        }

        var gainedExhibitsSource = isA ? state.ExhibitsB : state.ExhibitsA;
        if (gainedExhibitsSource != null)
        {
            foreach (var ex in gainedExhibitsSource)
            {
                if (ex == null || string.IsNullOrWhiteSpace(ex.ExhibitId)) continue;
                plan.GainedExhibits.Add(new TradeExhibitItem { ExhibitId = ex.ExhibitId });
            }
        }

        return plan;
    }

    public TradeSettlementValidationResult ValidatePlan(TradeSettlementPlan plan)
    {
        if (plan == null)
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.InvalidSessionState, "结算计划为空");
        }

        if (string.IsNullOrWhiteSpace(plan.LocalPlayerId))
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.NotParticipant, "本地玩家身份未指定");
        }

        GameRunController run = GetGameRun();
        if (run == null || run.Player == null)
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.GameRunUnavailable, "游戏状态或玩家不可用");
        }

        if (!plan.HasAnyAsset())
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.EmptyTrade, "交易双方报价均为空");
        }

        var spentInstanceIds = new HashSet<int>();
        foreach (var card in plan.SpentCards)
        {
            if (card == null) continue;
            if (card.InstanceId < 0)
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.InvalidInstanceId, $"支出卡牌实例ID非法: {card.CardId}");
            }

            if (!spentInstanceIds.Add(card.InstanceId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.DuplicateInstanceId, $"支出卡牌包含重复实例ID: {card.InstanceId}");
            }

            Card ownedCard = run.GetDeckCardByInstanceId(card.InstanceId);
            if (ownedCard == null)
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.MissingCard, $"牌库中未找到待支出卡牌: {card.CardId} (#{card.InstanceId})");
            }

            if (!string.Equals(ownedCard.Id, card.CardId, StringComparison.Ordinal) ||
                ownedCard.IsUpgraded != card.IsUpgraded)
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.CardMetadataMismatch, $"待支出卡牌元数据不匹配: #{card.InstanceId} 期望 {card.CardId}(Upgraded={card.IsUpgraded})，实际 {ownedCard.Id}(Upgraded={ownedCard.IsUpgraded})");
            }
        }

        if (plan.SpentMoney < 0)
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.InsufficientMoney, "支出金币必须非负");
        }

        if (plan.SpentMoney > run.Money)
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.InsufficientMoney, $"金币不足: 需要 {plan.SpentMoney}, 当前 {run.Money}");
        }

        if (plan.GainedMoney < 0 || (long)run.Money + plan.GainedMoney > TradeConstants.MaxMoneyOffer * 10L)
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.MoneyOverflow, "金币数值超出上限");
        }

        var spentExhibitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ex in plan.SpentExhibits)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.InvalidExhibitId, "待支出展品ID无效");
            }

            if (!spentExhibitIds.Add(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.ExhibitNotTradable, $"支出展品列表中包含重复展品: {ex.ExhibitId}");
            }

            Exhibit owned = run.Player.Exhibits?.FirstOrDefault(e => e != null && string.Equals(e.Id, ex.ExhibitId, StringComparison.Ordinal));
            if (owned == null)
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.MissingExhibit, $"未持有待支出展品: {ex.ExhibitId}");
            }

            if (!TradeExhibitRules.IsTradable(owned))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.ExhibitNotTradable, $"展品不可交易: {ex.ExhibitId}");
            }

            if (TradeExhibitRules.IsBlacklisted(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.ExhibitBlacklisted, $"展品处于黑名单: {ex.ExhibitId}");
            }
        }

        HashSet<string> gainedExhibitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ex in plan.GainedExhibits)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.InvalidExhibitId, "待接收展品ID无效");
            }

            if (TradeExhibitRules.IsBlacklisted(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.ExhibitBlacklisted, $"待接收展品处于黑名单: {ex.ExhibitId}");
            }

            if (!gainedExhibitIds.Add(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(
                    TradeFailureCodes.DuplicateExhibit,
                    $"待接收展品列表中包含重复展品: {ex.ExhibitId}",
                    conflictExhibitId: ex.ExhibitId);
            }

            bool alreadyOwned = run.Player.Exhibits?.Any(e => e != null && string.Equals(e.Id, ex.ExhibitId, StringComparison.Ordinal)) == true;
            if (alreadyOwned && !spentExhibitIds.Contains(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(
                    TradeFailureCodes.DuplicateExhibit,
                    $"本地已拥有同类型展品: {ex.ExhibitId}",
                    conflictExhibitId: ex.ExhibitId);
            }

            try
            {
                Exhibit testCreate = _exhibitFactory?.Invoke(ex.ExhibitId);
                if (testCreate == null)
                {
                    return TradeSettlementValidationResult.Fail(TradeFailureCodes.ExhibitCreationFailed, $"无法在本地创建展品: {ex.ExhibitId}");
                }
            }
            catch (Exception exCreate)
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.ExhibitCreationFailed, $"创建展品异常: {exCreate.Message}");
            }
        }

        foreach (var card in plan.GainedCards)
        {
            if (card == null || string.IsNullOrWhiteSpace(card.CardId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.CardCreationFailed, "待接收卡牌ID无效");
            }

            try
            {
                Card testCard = _cardFactory?.Invoke(card.CardId, card.IsUpgraded, card.UpgradeCounter);
                if (testCard == null)
                {
                    return TradeSettlementValidationResult.Fail(TradeFailureCodes.CardCreationFailed, $"无法在本地创建卡牌: {card.CardId}");
                }
            }
            catch (Exception exCard)
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.CardCreationFailed, $"创建卡牌异常: {exCard.Message}");
            }
        }

        return TradeSettlementValidationResult.Ok();
    }

    public TradeSettlementResult ApplyPlan(TradeSettlementPlan plan)
    {
        var validation = ValidatePlan(plan);
        if (!validation.Success)
        {
            return TradeSettlementResult.Fail(validation.FailureCode, validation.FailureStage, validation.Message);
        }

        GameRunController run = GetGameRun();
        if (run == null || run.Player == null)
        {
            return TradeSettlementResult.Fail(TradeFailureCodes.GameRunUnavailable, TradeFailureStage.Validation, "游戏状态不可用");
        }

        string commitKey = GetCommitKey(plan.TradeId, plan.CommitId);
        TradeInventorySnapshot snapshot;
        lock (_lock)
        {
            _commitStatuses[commitKey] = TradeCommitStatus.Applying;
            if (!string.IsNullOrEmpty(commitKey) && !_historyOrder.Contains(commitKey))
            {
                _historyOrder.Add(commitKey);
            }

            snapshot = CaptureSnapshot(run);
            if (!string.IsNullOrEmpty(commitKey) && snapshot != null)
            {
                _snapshots[commitKey] = snapshot;
            }

            TrimHistoryCapacity_NoLock();
        }

        var appliedLogs = new List<TradeInventoryChangeLogEntry>();

        string beforeExhibits = run.Player.Exhibits != null
            ? string.Join(",", run.Player.Exhibits.Where(e => e != null).Select(e => e.Id))
            : string.Empty;
        Plugin.Logger?.LogInfo($"[GameRunTradeInventory] 开始执行 ApplyPlan: {plan.ToSummaryString()} | 执行前库存: Money={run.Money}, Exhibits=[{beforeExhibits}]");

        using (TradeSyncPatch.EnterApplyingTradeScope())
        {
            try
            {
                // 1. SpendCards
                foreach (var card in plan.SpentCards)
                {
                    Card deckCard = run.GetDeckCardByInstanceId(card.InstanceId);
                    if (deckCard == null)
                    {
                        throw new InvalidOperationException($"牌库中缺失卡牌 #{card.InstanceId}");
                    }

                    run.RemoveDeckCard(deckCard, false);
                    appliedLogs.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.CardRemoved,
                        InstanceId = card.InstanceId,
                        CardId = card.CardId,
                        CardInstance = deckCard,
                    });

                    if (run.GetDeckCardByInstanceId(card.InstanceId) != null)
                    {
                        throw new InvalidOperationException($"后置条件失败: 卡牌 #{card.InstanceId} 移除后仍存在于牌库");
                    }
                }

                // 2. SpendMoney
                if (plan.SpentMoney > 0)
                {
                    int prevMoney = run.Money;
                    run.ConsumeMoney(plan.SpentMoney);
                    appliedLogs.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.MoneyConsumed,
                        MoneyAmount = plan.SpentMoney,
                    });

                    if (run.Money != prevMoney - plan.SpentMoney)
                    {
                        throw new InvalidOperationException($"后置条件失败: 扣减金币不符合预期 (原:{prevMoney}, 扣:{plan.SpentMoney}, 现:{run.Money})");
                    }
                }

                // 3. SpendExhibits
                foreach (var ex in plan.SpentExhibits)
                {
                    Exhibit owned = run.Player.Exhibits?.FirstOrDefault(e => e != null && string.Equals(e.Id, ex.ExhibitId, StringComparison.Ordinal));
                    if (owned == null)
                    {
                        throw new InvalidOperationException($"未找到待移除展品: {ex.ExhibitId}");
                    }

                    run.LoseExhibit(owned, true, true);
                    TryNotifyExhibitRemovedFromUi(owned);
                    appliedLogs.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.ExhibitRemoved,
                        ExhibitId = ex.ExhibitId,
                        ExhibitInstance = owned,
                    });

                    if (run.Player.Exhibits?.Any(e => e != null && string.Equals(e.Id, ex.ExhibitId, StringComparison.Ordinal)) == true)
                    {
                        throw new InvalidOperationException($"后置条件失败: 展品 {ex.ExhibitId} 移除后仍存在");
                    }
                }

                // 4. GainExhibits
                foreach (var ex in plan.GainedExhibits)
                {
                    Exhibit created = _exhibitFactory(ex.ExhibitId);
                    if (created == null)
                    {
                        throw new InvalidOperationException($"展品创建失败: {ex.ExhibitId}");
                    }

                    run.GainExhibitInstantly(created, true, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                    TryNotifyExhibitAddedToUi(created);
                    appliedLogs.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.ExhibitAdded,
                        ExhibitId = ex.ExhibitId,
                        ExhibitInstance = created,
                    });

                    if (run.Player.Exhibits?.Any(e => e != null && string.Equals(e.Id, ex.ExhibitId, StringComparison.Ordinal)) != true)
                    {
                        throw new InvalidOperationException($"后置条件失败: 展品 {ex.ExhibitId} 获得后不存在");
                    }
                }

                // 5. GainMoney
                if (plan.GainedMoney > 0)
                {
                    int prevMoney = run.Money;
                    run.GainMoney(plan.GainedMoney, true, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                    appliedLogs.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.MoneyGained,
                        MoneyAmount = plan.GainedMoney,
                    });

                    if (run.Money != prevMoney + plan.GainedMoney)
                    {
                        throw new InvalidOperationException($"后置条件失败: 增加金币不符合预期 (原:{prevMoney}, 加:{plan.GainedMoney}, 现:{run.Money})");
                    }
                }

                // 6. GainCards
                foreach (var card in plan.GainedCards)
                {
                    Card created = _cardFactory(card.CardId, card.IsUpgraded, card.UpgradeCounter);
                    if (created == null)
                    {
                        throw new InvalidOperationException($"卡牌创建失败: {card.CardId}");
                    }

                    run.AddDeckCard(created, true, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                    appliedLogs.Add(new TradeInventoryChangeLogEntry
                    {
                        ChangeType = TradeInventoryChangeType.CardAdded,
                        InstanceId = created.InstanceId,
                        CardId = created.Id,
                        CardInstance = created,
                    });

                    if (run.GetDeckCardByInstanceId(created.InstanceId) == null)
                    {
                        throw new InvalidOperationException($"后置条件失败: 卡牌 #{created.InstanceId} 加入后未在牌库中找到");
                    }
                }

                lock (_lock)
                {
                    _commitStatuses[commitKey] = TradeCommitStatus.Applied;
                }

                string afterExhibits = run.Player.Exhibits != null
                    ? string.Join(",", run.Player.Exhibits.Where(e => e != null).Select(e => e.Id))
                    : string.Empty;
                Plugin.Logger?.LogInfo($"[GameRunTradeInventory] ApplyPlan 执行成功: tradeId={plan.TradeId}, commitId={plan.CommitId} | 执行后库存: Money={run.Money}, Exhibits=[{afterExhibits}], 变更日志数={appliedLogs.Count}");

                return TradeSettlementResult.Ok(appliedLogs);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[GameRunTradeInventory] ApplyPlan 失败，开始自动逆序补偿: {ex.Message}");
                var reconciledLogs = ReconcileChangesAgainstSnapshot(snapshot, run, plan, appliedLogs);
                var compResult = Compensate(plan, reconciledLogs);
                lock (_lock)
                {
                    _commitStatuses[commitKey] = TradeCommitStatus.Failed;
                }

                if (!compResult.Success)
                {
                    return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation,
                        $"本地应用失败且补偿失败: {ex.Message}; 补偿错误: {compResult.Message}", reconciledLogs, compensated: false);
                }

                return TradeSettlementResult.Fail(TradeFailureCodes.PostConditionFailed, TradeFailureStage.Committing, ex.Message, reconciledLogs, compensated: true);
            }
        }
    }

    public TradeSettlementResult Compensate(TradeSettlementPlan plan, IReadOnlyList<TradeInventoryChangeLogEntry> appliedLogs)
    {
        string commitKey = plan != null ? GetCommitKey(plan.TradeId, plan.CommitId) : string.Empty;
        TradeInventorySnapshot snapshot = null;
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(commitKey))
            {
                _commitStatuses[commitKey] = TradeCommitStatus.Compensating;
                if (!_historyOrder.Contains(commitKey))
                {
                    _historyOrder.Add(commitKey);
                }
                _snapshots.TryGetValue(commitKey, out snapshot);
            }
        }

        GameRunController run = GetGameRun();
        if (run == null || run.Player == null)
        {
            if (!string.IsNullOrEmpty(commitKey))
            {
                lock (_lock)
                {
                    _commitStatuses[commitKey] = TradeCommitStatus.Failed;
                }
            }
            return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, "补偿时 GameRun 不可用");
        }

        if (appliedLogs == null || appliedLogs.Count == 0)
        {
            string verifyErr = VerifyInventoryAgainstSnapshot(run, snapshot);
            if (verifyErr != null)
            {
                if (!string.IsNullOrEmpty(commitKey))
                {
                    lock (_lock)
                    {
                        _commitStatuses[commitKey] = TradeCommitStatus.Failed;
                    }
                }
                return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, verifyErr);
            }

            if (!string.IsNullOrEmpty(commitKey))
            {
                lock (_lock)
                {
                    _commitStatuses[commitKey] = TradeCommitStatus.Compensated;
                }
            }
            return TradeSettlementResult.Ok();
        }

        Plugin.Logger?.LogWarning($"[GameRunTradeInventory] 开始执行 Compensate: tradeId={plan?.TradeId}, commitId={plan?.CommitId}, 待补偿项={appliedLogs?.Count ?? 0}");

        using (TradeSyncPatch.EnterApplyingTradeScope())
        {
            try
            {
                // 逆序回滚已变更的操作
                for (int i = appliedLogs.Count - 1; i >= 0; i--)
                {
                    var entry = appliedLogs[i];
                    switch (entry.ChangeType)
                    {
                        case TradeInventoryChangeType.CardAdded:
                            if (entry.CardInstance is Card addedCard)
                            {
                                run.RemoveDeckCard(addedCard, false);
                            }
                            else if (entry.InstanceId >= 0)
                            {
                                Card byId = run.GetDeckCardByInstanceId(entry.InstanceId);
                                if (byId != null)
                                {
                                    run.RemoveDeckCard(byId, false);
                                }
                            }
                            break;

                        case TradeInventoryChangeType.MoneyGained:
                            run.ConsumeMoney(entry.MoneyAmount);
                            break;

                        case TradeInventoryChangeType.ExhibitAdded:
                            if (entry.ExhibitInstance is Exhibit addedExhibit)
                            {
                                run.LoseExhibit(addedExhibit, true, true);
                                TryNotifyExhibitRemovedFromUi(addedExhibit);
                            }
                            else if (!string.IsNullOrWhiteSpace(entry.ExhibitId))
                            {
                                Exhibit byExId = run.Player.Exhibits?.FirstOrDefault(e => e != null && string.Equals(e.Id, entry.ExhibitId, StringComparison.Ordinal));
                                if (byExId != null)
                                {
                                    run.LoseExhibit(byExId, true, true);
                                    TryNotifyExhibitRemovedFromUi(byExId);
                                }
                            }
                            break;

                        case TradeInventoryChangeType.ExhibitRemoved:
                            if (entry.ExhibitInstance is Exhibit removedExhibit)
                            {
                                run.GainExhibitInstantly(removedExhibit, true, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                                TryNotifyExhibitAddedToUi(removedExhibit);
                            }
                            else if (!string.IsNullOrWhiteSpace(entry.ExhibitId))
                            {
                                Exhibit recreatedEx = _exhibitFactory?.Invoke(entry.ExhibitId);
                                if (recreatedEx != null)
                                {
                                    run.GainExhibitInstantly(recreatedEx, true, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                                    TryNotifyExhibitAddedToUi(recreatedEx);
                                }
                            }
                            break;

                        case TradeInventoryChangeType.MoneyConsumed:
                            run.GainMoney(entry.MoneyAmount, true, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                            break;

                        case TradeInventoryChangeType.CardRemoved:
                            if (entry.CardInstance is Card removedCard)
                            {
                                run.AddDeckCard(removedCard, false, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                            }
                            else if (!string.IsNullOrWhiteSpace(entry.CardId))
                            {
                                Card recreatedCard = _cardFactory?.Invoke(entry.CardId, false, 0);
                                if (recreatedCard != null)
                                {
                                    run.AddDeckCard(recreatedCard, false, new VisualSourceData { SourceType = VisualSourceType.CardSelect });
                                }
                            }
                            break;
                    }
                }

                // 后置对账校验
                string verifyError = VerifyInventoryAgainstSnapshot(run, snapshot);
                if (verifyError != null)
                {
                    Plugin.Logger?.LogError($"[GameRunTradeInventory] 补偿后置校验失败: {verifyError}");
                    if (!string.IsNullOrEmpty(commitKey))
                    {
                        lock (_lock)
                        {
                            _commitStatuses[commitKey] = TradeCommitStatus.Failed;
                        }
                    }
                    return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, verifyError);
                }

                if (!string.IsNullOrEmpty(commitKey))
                {
                    lock (_lock)
                    {
                        _commitStatuses[commitKey] = TradeCommitStatus.Compensated;
                    }
                }

                string compExhibits = run.Player?.Exhibits != null
                    ? string.Join(",", run.Player.Exhibits.Where(e => e != null).Select(e => e.Id))
                    : string.Empty;
                Plugin.Logger?.LogInfo($"[GameRunTradeInventory] Compensate 成功完成: tradeId={plan?.TradeId}, commitId={plan?.CommitId} | 补偿后库存: Money={run.Money}, Exhibits=[{compExhibits}]");

                return TradeSettlementResult.Ok();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"[GameRunTradeInventory] Compensate 异常: tradeId={plan?.TradeId}, commitId={plan?.CommitId}, error={ex.Message}");
                if (!string.IsNullOrEmpty(commitKey))
                {
                    lock (_lock)
                    {
                        _commitStatuses[commitKey] = TradeCommitStatus.Failed;
                    }
                }
                return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, ex.Message);
            }
        }
    }

    public int HistoryCount
    {
        get
        {
            lock (_lock)
            {
                return _commitStatuses.Count;
            }
        }
    }

    public bool HasSnapshot(string tradeId, string commitId)
    {
        lock (_lock)
        {
            return _snapshots.ContainsKey(GetCommitKey(tradeId, commitId));
        }
    }

    public TradeInventorySnapshot GetSnapshotForTest(string tradeId, string commitId)
    {
        lock (_lock)
        {
            return _snapshots.TryGetValue(GetCommitKey(tradeId, commitId), out var s) ? s : null;
        }
    }

    public void PruneTerminalState(string tradeId, string commitId, bool keepStatusOnly = true)
    {
        lock (_lock)
        {
            PruneTerminalState_NoLock(tradeId, commitId, keepStatusOnly);
        }
    }

    void ITradeInventory.PruneTerminalState(string tradeId, string commitId)
        => PruneTerminalState(tradeId, commitId, keepStatusOnly: true);

    private void PruneTerminalState_NoLock(string tradeId, string commitId, bool keepStatusOnly)
    {
        string commitKey = GetCommitKey(tradeId, commitId);
        if (_snapshots.TryGetValue(commitKey, out var snapshot))
        {
            snapshot.DeckCardsByInstanceId?.Clear();
            snapshot.ExhibitIds?.Clear();
            _snapshots.Remove(commitKey);
        }

        if (!keepStatusOnly)
        {
            _commitStatuses.Remove(commitKey);
            _historyOrder.Remove(commitKey);
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            foreach (var snap in _snapshots.Values)
            {
                snap.DeckCardsByInstanceId?.Clear();
                snap.ExhibitIds?.Clear();
            }
            _snapshots.Clear();
            _commitStatuses.Clear();
            _historyOrder.Clear();
        }
    }

    private void TrimHistoryCapacity_NoLock()
    {
        while (_historyOrder.Count > MaxHistoryCapacity)
        {
            string evictKey = null;
            // 1. 优先淘汰处于终态 (Compensated 或 Applied) 的最老记录
            for (int i = 0; i < _historyOrder.Count; i++)
            {
                string k = _historyOrder[i];
                if (_commitStatuses.TryGetValue(k, out var s) &&
                    (s == TradeCommitStatus.Compensated || s == TradeCommitStatus.Applied))
                {
                    evictKey = k;
                    break;
                }
            }

            // 2. 若无普通终态，寻找最老的 Failed 记录
            if (evictKey == null)
            {
                for (int i = 0; i < _historyOrder.Count; i++)
                {
                    string k = _historyOrder[i];
                    if (_commitStatuses.TryGetValue(k, out var s) && s == TradeCommitStatus.Failed)
                    {
                        evictKey = k;
                        break;
                    }
                }
            }

            // 3. 若均处于活动状态 (Applying / Compensating)，停止淘汰
            if (evictKey == null)
            {
                break;
            }

            if (_snapshots.TryGetValue(evictKey, out var snap))
            {
                snap.DeckCardsByInstanceId?.Clear();
                snap.ExhibitIds?.Clear();
                _snapshots.Remove(evictKey);
            }
            _commitStatuses.Remove(evictKey);
            _historyOrder.Remove(evictKey);
        }
    }

    public TradeCommitStatus GetCommitStatus(string tradeId, string commitId)
    {
        lock (_lock)
        {
            string commitKey = GetCommitKey(tradeId, commitId);
            return _commitStatuses.TryGetValue(commitKey, out var status) ? status : TradeCommitStatus.None;
        }
    }

    private static string GetCommitKey(string tradeId, string commitId)
        => $"{tradeId ?? string.Empty}:{commitId ?? string.Empty}";
}
