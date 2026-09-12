using System;
using System.Collections.Generic;
using BepInEx.Logging;
using NetworkPlugin.Patch.Network;
using NetworkPlugin.Utils;

namespace NetworkPlugin.Core.Trade;

public sealed class TradeSettlementCoordinator : IDisposable
{
    public static TradeSettlementCoordinator Instance { get; private set; }

    private readonly ITradeInventory _inventory;
    private readonly ManualLogSource _logger;
    private readonly object _lock = new();

    private sealed class SessionContext
    {
        public string TradeId { get; set; } = string.Empty;
        public string CommitId { get; set; } = string.Empty;
        public TradeSettlementPlan Plan { get; set; }
        public TradeCommitStatus Status { get; set; } = TradeCommitStatus.None;
        public List<TradeInventoryChangeLogEntry> AppliedLogs { get; set; } = new();
        public TradeSettlementResult LastResult { get; set; }
        public long LastHandledTimestamp { get; set; }
        public volatile bool IsAborted;
        public bool IsExecutingApply;
    }

    public const int MaxSessionHistory = 32;
    private readonly List<string> _sessionOrder = new();
    private readonly Dictionary<string, SessionContext> _contexts = new(StringComparer.Ordinal);
    private bool _disposed;

    public int SessionCount
    {
        get
        {
            lock (_lock)
            {
                return _contexts.Count;
            }
        }
    }

    public bool HasSession(string tradeId, string commitId)
    {
        lock (_lock)
        {
            return _contexts.ContainsKey(GetKey(tradeId, commitId));
        }
    }

    public event Action<string, TradeCommitStatus, TradeSettlementResult> OnSettlementStateChanged;

    public TradeSettlementCoordinator(ITradeInventory inventory, ManualLogSource logger = null)
    {
        if (Instance != null && !ReferenceEquals(Instance, this))
        {
            Instance.Dispose();
        }

        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _logger = logger ?? Plugin.Logger;

        TradeSyncPatch.OnTradeStateUpdated += OnTradeStateUpdated;
        Instance = this;
    }

    public static void EnsureInitialized(ITradeInventory inventory = null, ManualLogSource logger = null)
    {
        if (Instance != null)
        {
            return;
        }

        inventory ??= new GameRunTradeInventory();
        new TradeSettlementCoordinator(inventory, logger);
    }

    public static void ResetForTest()
    {
        Instance?.Dispose();
        Instance = null;
        TradeSyncPatch.ResetForTest();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TradeSyncPatch.OnTradeStateUpdated -= OnTradeStateUpdated;
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    public TradeCommitStatus GetCommitStatus(string tradeId, string commitId)
    {
        lock (_lock)
        {
            string key = GetKey(tradeId, commitId);
            return _contexts.TryGetValue(key, out var ctx) ? ctx.Status : TradeCommitStatus.None;
        }
    }

    public TradeCommitStatus GetCommitStatus(string tradeId)
    {
        return TryGetSessionStatus(tradeId, out var status, out _) ? status : TradeCommitStatus.None;
    }

    public bool TryGetSessionStatus(string tradeId, out TradeCommitStatus status, out TradeSettlementResult lastResult)
    {
        status = TradeCommitStatus.None;
        lastResult = null;
        if (string.IsNullOrWhiteSpace(tradeId))
        {
            return false;
        }

        lock (_lock)
        {
            SessionContext matched = null;
            foreach (var pair in _contexts)
            {
                if (string.Equals(pair.Value.TradeId, tradeId, StringComparison.OrdinalIgnoreCase))
                {
                    if (matched == null || pair.Value.LastHandledTimestamp >= matched.LastHandledTimestamp)
                    {
                        matched = pair.Value;
                    }
                }
            }

            if (matched != null)
            {
                status = matched.Status;
                lastResult = matched.LastResult;
                return true;
            }
        }
        return false;
    }

    private void OnTradeStateUpdated(TradeSyncPatch.TradeSessionState state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.TradeId))
        {
            return;
        }

        string selfPlayerId = NetworkIdentityTracker.GetSelfPlayerId();
        if (string.IsNullOrWhiteSpace(selfPlayerId))
        {
            selfPlayerId = GameStateUtils.GetCurrentPlayerId();
        }

        if (string.IsNullOrWhiteSpace(selfPlayerId) || !state.IsParticipant(selfPlayerId))
        {
            return;
        }

        switch (state.Status)
        {
            case TradeSyncPatch.TradeStatus.Preparing:
                HandlePreparing(state, selfPlayerId);
                break;
            case TradeSyncPatch.TradeStatus.Committing:
                HandleCommitting(state, selfPlayerId);
                break;
            case TradeSyncPatch.TradeStatus.Failed:
                HandleFailed(state, selfPlayerId);
                break;
            case TradeSyncPatch.TradeStatus.Completed:
                HandleCompleted(state, selfPlayerId);
                break;
            case TradeSyncPatch.TradeStatus.Open:
                HandleOpen(state, selfPlayerId);
                break;
        }
    }

    private void HandleOpen(TradeSyncPatch.TradeSessionState state, string selfPlayerId)
    {
        lock (_lock)
        {
            // 如果退回到 Open 且没有执行中的 Commit，重置上下文
            string key = GetKey(state.TradeId, state.CommitId);
            if (_contexts.TryGetValue(key, out var ctx) && ctx.Status == TradeCommitStatus.Prepared)
            {
                ctx.Status = TradeCommitStatus.None;
            }
        }
    }

    private void HandlePreparing(TradeSyncPatch.TradeSessionState state, string selfPlayerId)
    {
        lock (_lock)
        {
            string key = GetKey(state.TradeId, state.CommitId);
            if (!_contexts.TryGetValue(key, out var ctx))
            {
                ctx = new SessionContext
                {
                    TradeId = state.TradeId,
                    CommitId = state.CommitId,
                };
                _contexts[key] = ctx;
                if (!_sessionOrder.Contains(key))
                {
                    _sessionOrder.Add(key);
                }
                TrimSessionCapacity_NoLock();
            }

            if (state.Timestamp > 0 && ctx.LastHandledTimestamp == state.Timestamp && ctx.Status == TradeCommitStatus.Prepared)
            {
                return;
            }
            ctx.LastHandledTimestamp = state.Timestamp;

            var plan = _inventory.BuildPlan(state, selfPlayerId);
            _logger?.LogInfo($"[TradeSettlementCoordinator] 交易 {state.TradeId} 生成结算计划: {plan?.ToSummaryString()}");
            var validation = _inventory.ValidatePlan(plan);

            if (!validation.Success)
            {
                _logger?.LogWarning($"[TradeSettlementCoordinator] 交易 {state.TradeId} 预检失败: {validation.FailureCode} - {validation.Message}");
                TradeSyncPatch.RequestPrepareResult(
                    state.TradeId,
                    selfPlayerId,
                    false,
                    validation.FailureCode,
                    conflictExhibitId: validation.ConflictExhibitId,
                    receiverPlayerId: selfPlayerId);
                return;
            }

            ctx.Plan = plan;
            ctx.Status = TradeCommitStatus.Prepared;
            _logger?.LogInfo($"[TradeSettlementCoordinator] 交易 {state.TradeId} 预检通过，发送成功回执");
            TradeSyncPatch.RequestPrepareResult(state.TradeId, selfPlayerId, true, null);
        }
    }

    private void HandleCommitting(TradeSyncPatch.TradeSessionState state, string selfPlayerId)
    {
        if (string.IsNullOrWhiteSpace(state.CommitId))
        {
            return;
        }

        SessionContext ctx;
        lock (_lock)
        {
            string key = GetKey(state.TradeId, state.CommitId);
            if (!_contexts.TryGetValue(key, out ctx))
            {
                // 从 Preparing 阶段迁移上下文（Preparing 阶段尚未分配 CommitId）
                string prepKey = GetKey(state.TradeId, null);
                if (_contexts.TryGetValue(prepKey, out var prepCtx) && prepCtx.Status == TradeCommitStatus.Prepared)
                {
                    _contexts.Remove(prepKey);
                    _sessionOrder.Remove(prepKey);
                    ctx = prepCtx;
                    ctx.CommitId = state.CommitId;
                    if (ctx.Plan != null)
                    {
                        ctx.Plan.CommitId = state.CommitId;
                    }
                    _contexts[key] = ctx;
                    _sessionOrder.Add(key);
                }
            }

            // 幂等去重检查（已处理过的终态或进行态）
            if (ctx != null && ctx.Status == TradeCommitStatus.Applied)
            {
                bool isA = string.Equals(state.PlayerAId, selfPlayerId, StringComparison.OrdinalIgnoreCase);
                bool alreadyRecordedOnHost = isA ? state.ACommitted : state.BCommitted;
                if (!alreadyRecordedOnHost)
                {
                    _logger?.LogInfo($"[TradeSettlementCoordinator] 重复收到 Committing 广播 ({state.TradeId}:{state.CommitId}) 且主机未记录本地提交，重发成功回执");
                    TradeSyncPatch.RequestCommitResult(state.TradeId, state.CommitId, selfPlayerId, true);
                }
                return;
            }

            if (ctx != null && ctx.Status == TradeCommitStatus.Failed)
            {
                _logger?.LogInfo($"[TradeSettlementCoordinator] 重复收到 Committing 广播 ({state.TradeId}:{state.CommitId})，重发失败回执");
                TradeSyncPatch.RequestCommitResult(state.TradeId, state.CommitId, selfPlayerId, false,
                    ctx.LastResult?.FailureCode, ctx.LastResult?.FailureStage.ToString());
                return;
            }

            if (ctx != null && (ctx.Status == TradeCommitStatus.Applying || ctx.Status == TradeCommitStatus.Compensating))
            {
                return;
            }

            // 严禁未完成预检或无合法 Prepared 上下文直接执行提交；强制校验双边 Prepared 记录
            if (ctx == null || ctx.Status != TradeCommitStatus.Prepared || ctx.Plan == null || !state.APrepared || !state.BPrepared)
            {
                _logger?.LogWarning($"[TradeSettlementCoordinator] 拒绝无合法双边 Prepared 上下文的 Committing 请求: trade={state.TradeId}, commit={state.CommitId}, APrep={state.APrepared}, BPrep={state.BPrepared}");
                TradeSyncPatch.RequestCommitResult(state.TradeId, state.CommitId, selfPlayerId, false, "NotPrepared", "Preparing");
                return;
            }

            ctx.Status = TradeCommitStatus.Applying;
            ctx.Plan.CommitId = state.CommitId;
        }

        // 调度至主线程执行实际库存操作
        Plugin.RunOnMainThread(() =>
        {
            lock (_lock)
            {
                var lastKnown = TradeSyncPatch.GetLastKnown(state.TradeId);
                bool isAuthFailed = lastKnown != null && lastKnown.Status == TradeSyncPatch.TradeStatus.Failed;
                if (ctx.IsAborted || ctx.Status != TradeCommitStatus.Applying || isAuthFailed)
                {
                    ctx.IsAborted = true;
                    _logger?.LogWarning($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 提交任务在主线程执行前已被中止 (IsAborted={ctx.IsAborted}, Status={ctx.Status}, AuthFailed={isAuthFailed})");
                    ctx.Status = TradeCommitStatus.Failed;
                    var abortedResult = TradeSettlementResult.Fail(TradeFailureCodes.CommitAborted, TradeFailureStage.Committing, "Commit aborted before execution");
                    ctx.LastResult = abortedResult;
                    TradeSyncPatch.RequestCommitResult(state.TradeId, state.CommitId, selfPlayerId, false,
                        abortedResult.FailureCode, abortedResult.FailureStage.ToString());
                    OnSettlementStateChanged?.Invoke(state.TradeId, ctx.Status, abortedResult);
                    return;
                }
                ctx.IsExecutingApply = true;
            }

            _logger?.LogInfo($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 开始执行 ApplyPlan: {ctx.Plan?.ToSummaryString()}");
            TradeSettlementResult result;
            try
            {
                using (TradeSyncPatch.EnterApplyingTradeScope())
                {
                    result = _inventory.ApplyPlan(ctx.Plan);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[TradeSettlementCoordinator] ApplyPlan 抛出未捕获异常: {ex.Message}");
                result = TradeSettlementResult.Fail(TradeFailureCodes.PostConditionFailed, TradeFailureStage.Committing, ex.Message);
            }

            lock (_lock)
            {
                ctx.IsExecutingApply = false;
                ctx.LastResult = result;
                ctx.AppliedLogs = result.AppliedLogs ?? new List<TradeInventoryChangeLogEntry>();

                var lastKnownAfter = TradeSyncPatch.GetLastKnown(state.TradeId);
                bool isAuthFailedAfter = lastKnownAfter != null && lastKnownAfter.Status == TradeSyncPatch.TradeStatus.Failed;
                if (ctx.IsAborted || isAuthFailedAfter)
                {
                    ctx.IsAborted = true;
                    _logger?.LogWarning($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 在落地期间已被中止");
                    TradeSettlementResult compResult;
                    if (result.Compensated)
                    {
                        _logger?.LogInfo($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 底层库存已自动完成自愈补偿，跳过二次补偿调用");
                        compResult = TradeSettlementResult.Ok(ctx.AppliedLogs);
                    }
                    else if (result.Success)
                    {
                        _logger?.LogWarning($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 曾落地成功但被中止，立即执行逆序补偿");
                        try
                        {
                            using (TradeSyncPatch.EnterApplyingTradeScope())
                            {
                                compResult = _inventory.Compensate(ctx.Plan, ctx.AppliedLogs);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError($"[TradeSettlementCoordinator] 中止后补偿抛出异常: {ex.Message}");
                            compResult = TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, ex.Message);
                        }
                    }
                    else
                    {
                        _logger?.LogError($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 本地落地失败且自愈补偿未成功: {result.Message}");
                        compResult = TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, result.Message);
                    }

                    ctx.Status = compResult.Success ? TradeCommitStatus.Compensated : TradeCommitStatus.Failed;
                    if (compResult.Success)
                    {
                        if (ctx.AppliedLogs != null)
                        {
                            foreach (var log in ctx.AppliedLogs)
                            {
                                log.CardInstance = null;
                                log.ExhibitInstance = null;
                            }
                        }
                        _inventory.PruneTerminalState(state.TradeId, state.CommitId);
                        TrimSessionCapacity_NoLock();
                    }
                    var abortedResult = TradeSettlementResult.Fail(
                        compResult.Success ? TradeFailureCodes.CommitAborted : TradeFailureCodes.CompensationFailed,
                        TradeFailureStage.Committing,
                        $"Commit aborted during execution; Compensation {(compResult.Success ? "succeeded" : "failed: " + compResult.Message)}",
                        ctx.AppliedLogs,
                        compensated: compResult.Success);
                    ctx.LastResult = abortedResult;
                    ctx.LastHandledTimestamp = DateTime.UtcNow.Ticks;
                    TradeSyncPatch.RequestCommitResult(state.TradeId, state.CommitId, selfPlayerId, false,
                        abortedResult.FailureCode, abortedResult.FailureStage.ToString());
                    if (!compResult.Success)
                    {
                        TradeSyncPatch.RequestCompensationResult(state.TradeId, state.CommitId, selfPlayerId, abortedResult.FailureCode, abortedResult.Message);
                    }
                    OnSettlementStateChanged?.Invoke(state.TradeId, ctx.Status, abortedResult);
                    return;
                }

                if (result.Success)
                {
                    ctx.Status = TradeCommitStatus.Applied;
                    ctx.LastHandledTimestamp = DateTime.UtcNow.Ticks;
                    _logger?.LogInfo($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 本地提交成功");
                    TradeSyncPatch.RequestCommitResult(state.TradeId, state.CommitId, selfPlayerId, true);
                }
                else
                {
                    ctx.Status = TradeCommitStatus.Failed;
                    ctx.LastHandledTimestamp = DateTime.UtcNow.Ticks;
                    _logger?.LogError($"[TradeSettlementCoordinator] 交易 {state.TradeId}:{state.CommitId} 本地提交失败: {result.FailureCode} - {result.Message}");
                    TradeSyncPatch.RequestCommitResult(state.TradeId, state.CommitId, selfPlayerId, false,
                        result.FailureCode, result.FailureStage.ToString());
                }

                OnSettlementStateChanged?.Invoke(state.TradeId, ctx.Status, result);
            }
        });
    }

    private void HandleFailed(TradeSyncPatch.TradeSessionState state, string selfPlayerId)
    {
        SessionContext ctx = null;
        lock (_lock)
        {
            string key = GetKey(state.TradeId, state.CommitId);
            if (!_contexts.TryGetValue(key, out ctx))
            {
                // 尝试查找该 tradeId 下已 Applied 或正在 Applying 的上下文
                foreach (var pair in _contexts)
                {
                    if (pair.Value.TradeId == state.TradeId)
                    {
                        if (pair.Value.Status == TradeCommitStatus.Applied || pair.Value.Status == TradeCommitStatus.Applying)
                        {
                            ctx = pair.Value;
                            break;
                        }
                    }
                }
            }

            if (ctx == null)
            {
                return;
            }

            if (ctx.Status == TradeCommitStatus.Applying)
            {
                ctx.IsAborted = true;
                _logger?.LogWarning($"[TradeSettlementCoordinator] 交易 {state.TradeId} 收到 Failed 广播 (Reason={state.Reason})，标记正在 Applying 的任务中止 (IsExecuting={ctx.IsExecutingApply})");
                return;
            }

            // 仅对已 Applied 成功的会话执行逆序补偿；已 Compensated 或 Failed 的忽略（幂等）
            if (ctx.Status != TradeCommitStatus.Applied)
            {
                return;
            }

            ctx.Status = TradeCommitStatus.Compensating;
            ctx.LastHandledTimestamp = DateTime.UtcNow.Ticks;
            _logger?.LogWarning($"[TradeSettlementCoordinator] 交易 {state.TradeId} 远端失败或超时 (Reason={state.Reason})，启动已提交资产的逆序补偿");
        }

        Plugin.RunOnMainThread(() =>
        {
            TradeSettlementResult compResult;
            try
            {
                using (TradeSyncPatch.EnterApplyingTradeScope())
                {
                    compResult = _inventory.Compensate(ctx.Plan, ctx.AppliedLogs);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[TradeSettlementCoordinator] 补偿时发生未捕获异常: {ex.Message}");
                compResult = TradeSettlementResult.Fail(TradeFailureCodes.CompensationFailed, TradeFailureStage.Compensation, ex.Message);
            }

            lock (_lock)
            {
                ctx.LastHandledTimestamp = DateTime.UtcNow.Ticks;
                ctx.LastResult = compResult;
                if (compResult.Success)
                {
                    ctx.Status = TradeCommitStatus.Compensated;
                    _logger?.LogInfo($"[TradeSettlementCoordinator] 交易 {state.TradeId} 资产逆序补偿成功完成");
                    if (ctx.AppliedLogs != null)
                    {
                        foreach (var log in ctx.AppliedLogs)
                        {
                            log.CardInstance = null;
                            log.ExhibitInstance = null;
                        }
                    }
                    _inventory.PruneTerminalState(state.TradeId, state.CommitId);
                    TrimSessionCapacity_NoLock();
                }
                else
                {
                    ctx.Status = TradeCommitStatus.Failed;
                    _logger?.LogError($"[TradeSettlementCoordinator] 交易 {state.TradeId} 资产补偿失败! 需要玩家人工核对库存: {compResult.Message}");
                    TradeSyncPatch.RequestCompensationResult(state.TradeId, state.CommitId, selfPlayerId, compResult.FailureCode ?? TradeFailureCodes.CompensationFailed, compResult.Message);
                }

                OnSettlementStateChanged?.Invoke(state.TradeId, ctx.Status, compResult);
            }
        });
    }

    private void HandleCompleted(TradeSyncPatch.TradeSessionState state, string selfPlayerId)
    {
        lock (_lock)
        {
            string key = GetKey(state.TradeId, state.CommitId);
            if (_contexts.TryGetValue(key, out var ctx) && ctx.Status == TradeCommitStatus.Applied)
            {
                _logger?.LogInfo($"[TradeSettlementCoordinator] 交易 {state.TradeId} 双方已全部完成结算");
                if (ctx.AppliedLogs != null)
                {
                    foreach (var log in ctx.AppliedLogs)
                    {
                        log.CardInstance = null;
                        log.ExhibitInstance = null;
                    }
                }
                _inventory.PruneTerminalState(state.TradeId, state.CommitId);
                TrimSessionCapacity_NoLock();

                OnSettlementStateChanged?.Invoke(state.TradeId, TradeCommitStatus.Applied, TradeSettlementResult.Ok(ctx.AppliedLogs));
            }
        }
    }

    private void TrimSessionCapacity_NoLock()
    {
        while (_contexts.Count > MaxSessionHistory)
        {
            string evictKey = null;
            // 1. 优先淘汰处于终态 (Compensated 或 Applied) 的最老会话
            for (int i = 0; i < _sessionOrder.Count; i++)
            {
                string k = _sessionOrder[i];
                if (_contexts.TryGetValue(k, out var ctx) &&
                    (ctx.Status == TradeCommitStatus.Compensated || ctx.Status == TradeCommitStatus.Applied))
                {
                    evictKey = k;
                    break;
                }
            }

            // 2. 若无普通终态，驱逐最老的 Failed 或 None
            if (evictKey == null)
            {
                for (int i = 0; i < _sessionOrder.Count; i++)
                {
                    string k = _sessionOrder[i];
                    if (_contexts.TryGetValue(k, out var ctx) &&
                        (ctx.Status == TradeCommitStatus.Failed || ctx.Status == TradeCommitStatus.None))
                    {
                        evictKey = k;
                        break;
                    }
                }
            }

            // 3. 若均处于活动状态 (Applying / Compensating / Prepared)，停止驱逐
            if (evictKey == null)
            {
                break;
            }

            if (_contexts.TryGetValue(evictKey, out var evictCtx) && evictCtx.AppliedLogs != null)
            {
                foreach (var log in evictCtx.AppliedLogs)
                {
                    log.CardInstance = null;
                    log.ExhibitInstance = null;
                }
            }
            _contexts.Remove(evictKey);
            _sessionOrder.Remove(evictKey);
        }
    }

    private static string GetKey(string tradeId, string commitId)
        => $"{tradeId ?? string.Empty}:{commitId ?? string.Empty}";
}
