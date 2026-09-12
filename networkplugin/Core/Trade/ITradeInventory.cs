using System.Collections.Generic;
using NetworkPlugin.Patch.Network;

namespace NetworkPlugin.Core.Trade;

public interface ITradeInventory
{
    TradeSettlementPlan BuildPlan(TradeSyncPatch.TradeSessionState state, string localPlayerId);

    TradeSettlementValidationResult ValidatePlan(TradeSettlementPlan plan);

    TradeSettlementResult ApplyPlan(TradeSettlementPlan plan);

    TradeSettlementResult Compensate(TradeSettlementPlan plan, IReadOnlyList<TradeInventoryChangeLogEntry> appliedLogs);

    TradeCommitStatus GetCommitStatus(string tradeId, string commitId);

    void PruneTerminalState(string tradeId, string commitId);
}
