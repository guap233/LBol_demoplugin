using System;
using System.Collections.Generic;
using System.Linq;

namespace NetworkPlugin.Core.Trade;

public static class TradeFailureCodes
{
    public const string None = "None";
    public const string GameRunUnavailable = "GameRunUnavailable";
    public const string NotParticipant = "NotParticipant";
    public const string InvalidTradeId = "InvalidTradeId";
    public const string InvalidCommitId = "InvalidCommitId";
    public const string EmptyTrade = "EmptyTrade";
    public const string InvalidInstanceId = "InvalidInstanceId";
    public const string DuplicateInstanceId = "DuplicateInstanceId";
    public const string MissingCard = "MissingCard";
    public const string InsufficientMoney = "InsufficientMoney";
    public const string MoneyOverflow = "MoneyOverflow";
    public const string InvalidExhibitId = "InvalidExhibitId";
    public const string MissingExhibit = "MissingExhibit";
    public const string ExhibitNotTradable = "ExhibitNotTradable";
    public const string ExhibitBlacklisted = "ExhibitBlacklisted";
    public const string DuplicateExhibit = "DuplicateExhibit";
    public const string CardCreationFailed = "CardCreationFailed";
    public const string ExhibitCreationFailed = "ExhibitCreationFailed";
    public const string PostConditionFailed = "PostConditionFailed";
    public const string CompensationFailed = "CompensationFailed";
    public const string CommitTimeout = "CommitTimeout";
    public const string RemoteCommitFailed = "RemoteCommitFailed";
    public const string ProtocolIncompatible = "ProtocolIncompatible";
    public const string InvalidSessionState = "InvalidSessionState";
    public const string CommitAborted = "CommitAborted";
    public const string CardMetadataMismatch = "CardMetadataMismatch";
}

public enum TradeFailureStage
{
    None = 0,
    Validation = 1,
    SpendCards = 2,
    SpendMoney = 3,
    SpendExhibits = 4,
    GainExhibits = 5,
    GainMoney = 6,
    GainCards = 7,
    Compensation = 8,
    Committing = 9,
}

public enum TradeCommitStatus
{
    None = 0,
    Prepared = 1,
    Applying = 2,
    Applied = 3,
    Compensating = 4,
    Compensated = 5,
    Failed = 6,
}

public sealed class TradeCardItem
{
    public int InstanceId { get; set; } = -1;
    public string CardId { get; set; } = string.Empty;
    public bool IsUpgraded { get; set; }
    public int UpgradeCounter { get; set; }
    public int? DeckCounter { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string CardType { get; set; } = string.Empty;

    public bool IsTool => string.Equals(CardType, "Tool", StringComparison.OrdinalIgnoreCase);

    public TradeCardItem Clone()
    {
        return new TradeCardItem
        {
            InstanceId = InstanceId,
            CardId = CardId,
            IsUpgraded = IsUpgraded,
            UpgradeCounter = UpgradeCounter,
            DeckCounter = DeckCounter,
            CardName = CardName,
            CardType = CardType,
        };
    }
}

public sealed class TradeExhibitItem
{
    public string ExhibitId { get; set; } = string.Empty;

    public TradeExhibitItem Clone()
    {
        return new TradeExhibitItem
        {
            ExhibitId = ExhibitId,
        };
    }
}

public sealed class TradeSettlementPlan
{
    public string TradeId { get; set; } = string.Empty;
    public string CommitId { get; set; } = string.Empty;
    public string LocalPlayerId { get; set; } = string.Empty;
    public string PartnerPlayerId { get; set; } = string.Empty;
    public bool IsLocalPlayerA { get; set; }

    public List<TradeCardItem> SpentCards { get; set; } = new();
    public List<TradeCardItem> GainedCards { get; set; } = new();

    public int SpentMoney { get; set; }
    public int GainedMoney { get; set; }

    public List<TradeExhibitItem> SpentExhibits { get; set; } = new();
    public List<TradeExhibitItem> GainedExhibits { get; set; } = new();

    public bool HasAnyAsset()
    {
        return SpentCards.Count > 0 ||
               GainedCards.Count > 0 ||
               SpentMoney > 0 ||
               GainedMoney > 0 ||
               SpentExhibits.Count > 0 ||
               GainedExhibits.Count > 0;
    }

    public string ToSummaryString()
    {
        string spentCardsStr = SpentCards.Count > 0 ? string.Join(",", SpentCards.Select(c => $"#{c.InstanceId}:{c.CardId}")) : "none";
        string gainedCardsStr = GainedCards.Count > 0 ? string.Join(",", GainedCards.Select(c => c.CardId)) : "none";
        string spentExStr = SpentExhibits.Count > 0 ? string.Join(",", SpentExhibits.Select(e => e.ExhibitId)) : "none";
        string gainedExStr = GainedExhibits.Count > 0 ? string.Join(",", GainedExhibits.Select(e => e.ExhibitId)) : "none";
        return $"[Plan trade={TradeId}, commit={CommitId}, local={LocalPlayerId}, partner={PartnerPlayerId}, spentMoney={SpentMoney}, gainedMoney={GainedMoney}, spentCards=[{spentCardsStr}], gainedCards=[{gainedCardsStr}], spentExhibits=[{spentExStr}], gainedExhibits=[{gainedExStr}]]";
    }
}

public enum TradeInventoryChangeType
{
    CardRemoved,
    CardAdded,
    MoneyConsumed,
    MoneyGained,
    ExhibitRemoved,
    ExhibitAdded,
}

public sealed class TradeInventoryChangeLogEntry
{
    public TradeInventoryChangeType ChangeType { get; set; }
    public int InstanceId { get; set; } = -1;
    public string CardId { get; set; } = string.Empty;
    public object CardInstance { get; set; }
    public int MoneyAmount { get; set; }
    public string ExhibitId { get; set; } = string.Empty;
    public object ExhibitInstance { get; set; }
    public long Timestamp { get; set; } = DateTime.UtcNow.Ticks;
}

public sealed class TradeSettlementValidationResult
{
    public bool Success { get; set; }
    public string FailureCode { get; set; } = TradeFailureCodes.None;
    public TradeFailureStage FailureStage { get; set; } = TradeFailureStage.None;
    public string Message { get; set; } = string.Empty;
    public string ConflictExhibitId { get; set; }

    public static TradeSettlementValidationResult Ok()
        => new() { Success = true };

    public static TradeSettlementValidationResult Fail(
        string failureCode,
        string message = null,
        TradeFailureStage stage = TradeFailureStage.Validation,
        string conflictExhibitId = null)
        => new()
        {
            Success = false,
            FailureCode = string.IsNullOrWhiteSpace(failureCode) ? TradeFailureCodes.InvalidSessionState : failureCode,
            FailureStage = stage,
            Message = message ?? failureCode,
            ConflictExhibitId = conflictExhibitId,
        };
}

public sealed class TradeSettlementResult
{
    public bool Success { get; set; }
    public string FailureCode { get; set; } = TradeFailureCodes.None;
    public TradeFailureStage FailureStage { get; set; } = TradeFailureStage.None;
    public string Message { get; set; } = string.Empty;
    public List<TradeInventoryChangeLogEntry> AppliedLogs { get; set; } = new();
    public bool Compensated { get; set; }

    public static TradeSettlementResult Ok(List<TradeInventoryChangeLogEntry> appliedLogs = null)
        => new()
        {
            Success = true,
            AppliedLogs = appliedLogs ?? new List<TradeInventoryChangeLogEntry>(),
            Compensated = false,
        };

    public static TradeSettlementResult Fail(string failureCode, TradeFailureStage stage, string message = null, List<TradeInventoryChangeLogEntry> appliedLogs = null, bool compensated = false)
        => new()
        {
            Success = false,
            FailureCode = string.IsNullOrWhiteSpace(failureCode) ? TradeFailureCodes.InvalidSessionState : failureCode,
            FailureStage = stage,
            Message = message ?? failureCode,
            AppliedLogs = appliedLogs ?? new List<TradeInventoryChangeLogEntry>(),
            Compensated = compensated,
        };
}

public sealed class TradeInventorySnapshot
{
    public int Money { get; set; }
    public Dictionary<int, object> DeckCardsByInstanceId { get; set; } = new();
    public HashSet<string> ExhibitIds { get; set; } = new(StringComparer.Ordinal);
    public long Timestamp { get; set; } = DateTime.UtcNow.Ticks;
}

