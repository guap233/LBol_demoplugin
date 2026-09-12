using System;
using System.Collections.Generic;
using System.Linq;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.UI.Rules;

namespace NetworkPlugin.Core.Trade;

public class InMemoryTradeInventory : ITradeInventory
{
    public Dictionary<int, TradeCardItem> Deck { get; } = new();
    public int Money { get; set; }
    public HashSet<string> Exhibits { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, TradeCommitStatus> _commitStatuses = new(StringComparer.Ordinal);
    private int _nextInstanceId = 10000;

    public TradeFailureStage? FailAtStage { get; set; }
    public string SimulatedFailureCode { get; set; } = TradeFailureCodes.PostConditionFailed;
    public bool FailDuringCompensation { get; set; }
    public Action OnBeforeApply { get; set; }

    public int NextInstanceId() => ++_nextInstanceId;

    public void AddCard(TradeCardItem card)
    {
        if (card != null)
        {
            Deck[card.InstanceId] = card;
        }
    }

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

            if (!Deck.TryGetValue(card.InstanceId, out var heldCard))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.MissingCard, $"牌库中未找到待支出卡牌: {card.CardId} (#{card.InstanceId})");
            }

            if (!string.Equals(heldCard.CardId, card.CardId, StringComparison.Ordinal))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.CardMetadataMismatch, $"待支出卡牌元数据不匹配: #{card.InstanceId} 期望 {card.CardId}，实际 {heldCard.CardId}");
            }
        }

        if (plan.SpentMoney < 0)
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.InsufficientMoney, "支出金币必须非负");
        }

        if (plan.SpentMoney > Money)
        {
            return TradeSettlementValidationResult.Fail(TradeFailureCodes.InsufficientMoney, $"金币不足: 需要 {plan.SpentMoney}, 当前 {Money}");
        }

        if (plan.GainedMoney < 0 || (long)Money + plan.GainedMoney > TradeConstants.MaxMoneyOffer * 10L)
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

            if (!Exhibits.Contains(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.MissingExhibit, $"未持有待支出展品: {ex.ExhibitId}");
            }

            if (TradeExhibitRules.IsBlacklisted(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.ExhibitBlacklisted, $"展品不可交易（黑名单）: {ex.ExhibitId}");
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

            if (Exhibits.Contains(ex.ExhibitId) && !spentExhibitIds.Contains(ex.ExhibitId))
            {
                return TradeSettlementValidationResult.Fail(
                    TradeFailureCodes.DuplicateExhibit,
                    $"本地已拥有同类型展品: {ex.ExhibitId}",
                    conflictExhibitId: ex.ExhibitId);
            }
        }

        foreach (var card in plan.GainedCards)
        {
            if (card == null || string.IsNullOrWhiteSpace(card.CardId))
            {
                return TradeSettlementValidationResult.Fail(TradeFailureCodes.CardCreationFailed, "待接收卡牌ID无效");
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

        string commitKey = GetCommitKey(plan.TradeId, plan.CommitId);
        _commitStatuses[commitKey] = TradeCommitStatus.Applying;

        var appliedLogs = new List<TradeInventoryChangeLogEntry>();
        OnBeforeApply?.Invoke();

        try
        {
            // 1. SpendCards
            if (FailAtStage == TradeFailureStage.SpendCards)
            {
                throw new InvalidOperationException($"Simulated failure at {FailAtStage}");
            }

            foreach (var card in plan.SpentCards)
            {
                if (!Deck.TryGetValue(card.InstanceId, out var existingCard))
                {
                    throw new InvalidOperationException($"Missing card {card.InstanceId}");
                }

                Deck.Remove(card.InstanceId);
                appliedLogs.Add(new TradeInventoryChangeLogEntry
                {
                    ChangeType = TradeInventoryChangeType.CardRemoved,
                    InstanceId = card.InstanceId,
                    CardId = card.CardId,
                    CardInstance = existingCard,
                });

                if (Deck.ContainsKey(card.InstanceId))
                {
                    throw new InvalidOperationException($"Postcondition failed: card {card.InstanceId} still in deck");
                }
            }

            // 2. SpendMoney
            if (FailAtStage == TradeFailureStage.SpendMoney)
            {
                throw new InvalidOperationException($"Simulated failure at {FailAtStage}");
            }

            if (plan.SpentMoney > 0)
            {
                int prevMoney = Money;
                Money -= plan.SpentMoney;
                appliedLogs.Add(new TradeInventoryChangeLogEntry
                {
                    ChangeType = TradeInventoryChangeType.MoneyConsumed,
                    MoneyAmount = plan.SpentMoney,
                });

                if (Money != prevMoney - plan.SpentMoney)
                {
                    throw new InvalidOperationException("Postcondition failed: money deduction mismatch");
                }
            }

            // 3. SpendExhibits
            if (FailAtStage == TradeFailureStage.SpendExhibits)
            {
                throw new InvalidOperationException($"Simulated failure at {FailAtStage}");
            }

            foreach (var ex in plan.SpentExhibits)
            {
                Exhibits.Remove(ex.ExhibitId);
                appliedLogs.Add(new TradeInventoryChangeLogEntry
                {
                    ChangeType = TradeInventoryChangeType.ExhibitRemoved,
                    ExhibitId = ex.ExhibitId,
                });

                if (Exhibits.Contains(ex.ExhibitId))
                {
                    throw new InvalidOperationException($"Postcondition failed: exhibit {ex.ExhibitId} still present");
                }
            }

            // 4. GainExhibits
            if (FailAtStage == TradeFailureStage.GainExhibits)
            {
                throw new InvalidOperationException($"Simulated failure at {FailAtStage}");
            }

            foreach (var ex in plan.GainedExhibits)
            {
                Exhibits.Add(ex.ExhibitId);
                appliedLogs.Add(new TradeInventoryChangeLogEntry
                {
                    ChangeType = TradeInventoryChangeType.ExhibitAdded,
                    ExhibitId = ex.ExhibitId,
                });

                if (!Exhibits.Contains(ex.ExhibitId))
                {
                    throw new InvalidOperationException($"Postcondition failed: exhibit {ex.ExhibitId} not added");
                }
            }

            // 5. GainMoney
            if (FailAtStage == TradeFailureStage.GainMoney)
            {
                throw new InvalidOperationException($"Simulated failure at {FailAtStage}");
            }

            if (plan.GainedMoney > 0)
            {
                int prevMoney = Money;
                Money += plan.GainedMoney;
                appliedLogs.Add(new TradeInventoryChangeLogEntry
                {
                    ChangeType = TradeInventoryChangeType.MoneyGained,
                    MoneyAmount = plan.GainedMoney,
                });

                if (Money != prevMoney + plan.GainedMoney)
                {
                    throw new InvalidOperationException("Postcondition failed: money addition mismatch");
                }
            }

            // 6. GainCards
            if (FailAtStage == TradeFailureStage.GainCards)
            {
                throw new InvalidOperationException($"Simulated failure at {FailAtStage}");
            }

            foreach (var card in plan.GainedCards)
            {
                int newInstanceId = card.InstanceId > 0 ? card.InstanceId : NextInstanceId();
                var cardToAdd = card.Clone();
                cardToAdd.InstanceId = newInstanceId;

                Deck[newInstanceId] = cardToAdd;
                appliedLogs.Add(new TradeInventoryChangeLogEntry
                {
                    ChangeType = TradeInventoryChangeType.CardAdded,
                    InstanceId = newInstanceId,
                    CardId = card.CardId,
                    CardInstance = cardToAdd,
                });

                if (!Deck.ContainsKey(newInstanceId))
                {
                    throw new InvalidOperationException($"Postcondition failed: card {newInstanceId} not added");
                }
            }

            _commitStatuses[commitKey] = TradeCommitStatus.Applied;
            return TradeSettlementResult.Ok(appliedLogs);
        }
        catch (Exception ex)
        {
            TradeFailureStage stage = FailAtStage ?? TradeFailureStage.Committing;
            string failureCode = string.IsNullOrWhiteSpace(SimulatedFailureCode) ? TradeFailureCodes.PostConditionFailed : SimulatedFailureCode;

            // 自动逆序补偿
            var compResult = Compensate(plan, appliedLogs);
            _commitStatuses[commitKey] = TradeCommitStatus.Failed;

            if (!compResult.Success)
            {
                return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation,
                    $"应用失败且补偿失败: {ex.Message}; 补偿错误: {compResult.Message}", appliedLogs, compensated: false);
            }

            return TradeSettlementResult.Fail(failureCode, stage, ex.Message, appliedLogs, compensated: true);
        }
    }

    public TradeSettlementResult Compensate(TradeSettlementPlan plan, IReadOnlyList<TradeInventoryChangeLogEntry> appliedLogs)
    {
        string commitKey = plan != null ? GetCommitKey(plan.TradeId, plan.CommitId) : string.Empty;
        if (!string.IsNullOrEmpty(commitKey))
        {
            _commitStatuses[commitKey] = TradeCommitStatus.Compensating;
        }

        if (FailDuringCompensation)
        {
            if (!string.IsNullOrEmpty(commitKey))
            {
                _commitStatuses[commitKey] = TradeCommitStatus.Failed;
            }
            return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, "Simulated compensation failure");
        }

        if (appliedLogs == null || appliedLogs.Count == 0)
        {
            if (!string.IsNullOrEmpty(commitKey))
            {
                _commitStatuses[commitKey] = TradeCommitStatus.Compensated;
            }
            return TradeSettlementResult.Ok();
        }

        try
        {
            // 逆序执行补偿
            for (int i = appliedLogs.Count - 1; i >= 0; i--)
            {
                var entry = appliedLogs[i];
                switch (entry.ChangeType)
                {
                    case TradeInventoryChangeType.CardAdded:
                        Deck.Remove(entry.InstanceId);
                        break;
                    case TradeInventoryChangeType.MoneyGained:
                        Money = Math.Max(0, Money - entry.MoneyAmount);
                        break;
                    case TradeInventoryChangeType.ExhibitAdded:
                        Exhibits.Remove(entry.ExhibitId);
                        break;
                    case TradeInventoryChangeType.ExhibitRemoved:
                        Exhibits.Add(entry.ExhibitId);
                        break;
                    case TradeInventoryChangeType.MoneyConsumed:
                        Money += entry.MoneyAmount;
                        break;
                    case TradeInventoryChangeType.CardRemoved:
                        if (entry.CardInstance is TradeCardItem cardItem)
                        {
                            Deck[entry.InstanceId] = cardItem;
                        }
                        break;
                }
            }

            if (!string.IsNullOrEmpty(commitKey))
            {
                _commitStatuses[commitKey] = TradeCommitStatus.Compensated;
            }

            return TradeSettlementResult.Ok();
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(commitKey))
            {
                _commitStatuses[commitKey] = TradeCommitStatus.Failed;
            }
            return TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, ex.Message);
        }
    }

    public TradeCommitStatus GetCommitStatus(string tradeId, string commitId)
    {
        string commitKey = GetCommitKey(tradeId, commitId);
        return _commitStatuses.TryGetValue(commitKey, out var status) ? status : TradeCommitStatus.None;
    }

    public HashSet<string> PrunedCommitKeys { get; } = new(StringComparer.Ordinal);

    public void PruneTerminalState(string tradeId, string commitId)
    {
        string commitKey = GetCommitKey(tradeId, commitId);
        PrunedCommitKeys.Add(commitKey);
    }

    private static string GetCommitKey(string tradeId, string commitId)
        => $"{tradeId ?? string.Empty}:{commitId ?? string.Empty}";
}
