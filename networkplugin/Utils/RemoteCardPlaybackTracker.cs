using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NetworkPlugin.Utils;

/// <summary>
/// 追踪远端卡牌视效打击回放生命周期的标记器，支持全局视窗与基于 (AttackId, TargetId) 的精确目标去重，
/// 避免全局抑制导致未受击敌人的伤害飘字被误吞。
/// </summary>
public static class RemoteCardPlaybackTracker
{
    private static int _activeVisualCount;
    private static long _playbackWindowUntilTick;

    private static readonly object _syncLock = new();
    private static readonly Dictionary<string, AttackRecord> _activeAttacks = new(StringComparer.Ordinal);

    private sealed class AttackRecord
    {
        public string AttackId { get; set; }
        public HashSet<string> TargetIds { get; set; }
        public long TimeoutTick { get; set; }
    }

    /// <summary>
    /// 当前是否正处于远端卡牌视效打击回放视窗内（全局保底判断）。
    /// </summary>
    public static bool IsInCardPlaybackWindow
    {
        get
        {
            if (Volatile.Read(ref _activeVisualCount) > 0)
            {
                long until = Interlocked.Read(ref _playbackWindowUntilTick);
                return DateTime.UtcNow.Ticks < until;
            }
            return false;
        }
    }

    /// <summary>
    /// 判断指定目标是否正处于被攻击锁定的打击视窗内。
    /// 若存在精准攻击记录，仅对记录内的目标返回 true；若无精准记录但处于全局视窗，回退为全局状态。
    /// </summary>
    public static bool IsTargetUnderAttack(string targetId)
    {
        long now = DateTime.UtcNow.Ticks;

        lock (_syncLock)
        {
            CleanupExpiredAttacksLocked(now);

            if (_activeAttacks.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    return true;
                }

                foreach (var record in _activeAttacks.Values)
                {
                    if (record.TargetIds != null && record.TargetIds.Contains(targetId))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        return IsInCardPlaybackWindow;
    }

    /// <summary>
    /// 开启特定攻击与目标的打击视窗，记录其关联的目标列表。
    /// </summary>
    public static void NotifyAttackStarted(string attackId, IEnumerable<string> targetIds, float timeoutSeconds = 2.5f)
    {
        NotifyCardVisualStarted(timeoutSeconds);

        if (string.IsNullOrWhiteSpace(attackId))
        {
            return;
        }

        long now = DateTime.UtcNow.Ticks;
        long targetTick = DateTime.UtcNow.AddSeconds(Math.Max(0.5f, timeoutSeconds)).Ticks;

        HashSet<string> targets = new(StringComparer.Ordinal);
        if (targetIds != null)
        {
            foreach (var t in targetIds)
            {
                if (!string.IsNullOrWhiteSpace(t))
                {
                    targets.Add(t.Trim());
                }
            }
        }

        lock (_syncLock)
        {
            CleanupExpiredAttacksLocked(now);
            _activeAttacks[attackId] = new AttackRecord
            {
                AttackId = attackId,
                TargetIds = targets,
                TimeoutTick = targetTick,
            };
        }
    }

    /// <summary>
    /// 结束特定攻击的打击视窗。
    /// </summary>
    public static void NotifyAttackEnded(string attackId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(attackId))
            {
                lock (_syncLock)
                {
                    _activeAttacks.Remove(attackId);
                    CleanupExpiredAttacksLocked(DateTime.UtcNow.Ticks);
                }
            }
        }
        finally
        {
            NotifyCardVisualEnded();
        }
    }

    /// <summary>
    /// 开启卡牌视效打击回放视窗（默认 2.5 秒超时保底，防止极端异常下永久静默）。
    /// </summary>
    public static void NotifyCardVisualStarted(float timeoutSeconds = 2.5f)
    {
        Interlocked.Increment(ref _activeVisualCount);
        long targetTick = DateTime.UtcNow.AddSeconds(Math.Max(0.5f, timeoutSeconds)).Ticks;
        Interlocked.Exchange(ref _playbackWindowUntilTick, targetTick);
    }

    /// <summary>
    /// 结束卡牌视效打击回放视窗。
    /// </summary>
    public static void NotifyCardVisualEnded()
    {
        int count = Interlocked.Decrement(ref _activeVisualCount);
        if (count <= 0)
        {
            Interlocked.Exchange(ref _activeVisualCount, 0);
            Interlocked.Exchange(ref _playbackWindowUntilTick, 0);
        }
    }

    private static void CleanupExpiredAttacksLocked(long nowTicks)
    {
        if (_activeAttacks.Count == 0) return;

        List<string> toRemove = null;
        foreach (var kvp in _activeAttacks)
        {
            if (nowTicks >= kvp.Value.TimeoutTick)
            {
                toRemove ??= new List<string>();
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove != null)
        {
            foreach (var key in toRemove)
            {
                _activeAttacks.Remove(key);
            }
        }
    }

    /// <summary>
    /// 供单元测试重置内部状态。
    /// </summary>
    public static void ResetForTest()
    {
        Interlocked.Exchange(ref _activeVisualCount, 0);
        Interlocked.Exchange(ref _playbackWindowUntilTick, 0);
        lock (_syncLock)
        {
            _activeAttacks.Clear();
        }
    }

    internal static int GetActiveAttackCountForTest()
    {
        lock (_syncLock)
        {
            CleanupExpiredAttacksLocked(DateTime.UtcNow.Ticks);
            return _activeAttacks.Count;
        }
    }
}
