using System;
using System.Collections.Generic;

namespace NetworkPlugin.Network.Security;

public sealed class ReplayGuard
{
    private readonly object _lock = new();
    private readonly int _maxWindowSeconds;
    private readonly int _maxCapacity;

    private long _lastSequence = 0;
    private readonly Dictionary<string, long> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _seenRequestIds = new(StringComparer.Ordinal);

    private readonly Func<DateTimeOffset> _clock;

    public long LastSequence
    {
        get
        {
            lock (_lock)
            {
                return _lastSequence;
            }
        }
    }

    public ReplayGuard(int maxWindowSeconds = 60, int maxCapacity = 5000, Func<DateTimeOffset>? clock = null)
    {
        _maxWindowSeconds = maxWindowSeconds > 0 ? maxWindowSeconds : 60;
        _maxCapacity = maxCapacity > 0 ? maxCapacity : 5000;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool ValidateAndRecord(string? messageId, string? requestId, long sequence, long timestamp, out string failureReason)
    {
        return ValidateAndRecord(messageId, requestId, sequence, timestamp, TimestampFormat.UnixSeconds, out failureReason, requireTimestamp: false);
    }

    public bool ValidateAndRecord(
        string? messageId,
        string? requestId,
        long sequence,
        long rawTimestamp,
        TimestampFormat format,
        out string failureReason,
        bool requireTimestamp = false)
    {
        failureReason = string.Empty;
        long now = _clock().ToUnixTimeSeconds();

        lock (_lock)
        {
            // 1. Timestamp freshness check
            if (format == TimestampFormat.UtcTicks || requireTimestamp || rawTimestamp > 0)
            {
                if (!TimestampConverter.TryConvertToUnixSeconds(rawTimestamp, format, out long targetUnixSeconds, out string convertError))
                {
                    failureReason = convertError;
                    return false;
                }

                long drift = now >= targetUnixSeconds ? (now - targetUnixSeconds) : (targetUnixSeconds - now);
                if (drift > _maxWindowSeconds)
                {
                    failureReason = $"消息时间戳已过期或超前: delta={drift}s (limit={_maxWindowSeconds}s)";
                    return false;
                }
            }

            // 2. Monotonic sequence check
            if (sequence > 0)
            {
                if (sequence <= _lastSequence)
                {
                    failureReason = $"检测到消息序列号倒退或重复: seq={sequence}, lastSeq={_lastSequence}";
                    return false;
                }
            }

            // 3. Deduplication check
            if (!string.IsNullOrEmpty(messageId) && _seenMessageIds.ContainsKey(messageId!))
            {
                failureReason = $"检测到重复 MessageId 重放攻击: {messageId}";
                return false;
            }

            if (!string.IsNullOrEmpty(requestId) && _seenRequestIds.ContainsKey(requestId!))
            {
                failureReason = $"检测到重复 RequestId 重放攻击: {requestId}";
                return false;
            }

            // Cleanup expired
            CleanupExpired_NoLock(now);

            // Record
            if (sequence > 0)
            {
                _lastSequence = sequence;
            }

            long expireAt = now + _maxWindowSeconds;
            if (!string.IsNullOrEmpty(messageId))
            {
                _seenMessageIds[messageId!] = expireAt;
            }

            if (!string.IsNullOrEmpty(requestId))
            {
                _seenRequestIds[requestId!] = expireAt;
            }

            // Capacity cap
            EnsureCapacity_NoLock();

            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastSequence = 0;
            _seenMessageIds.ClearNullable();
            _seenRequestIds.ClearNullable();
        }
    }

    private void CleanupExpired_NoLock(long now)
    {
        if (_seenMessageIds.Count > 0)
        {
            List<string>? toRemove = null;
            foreach (var kvp in _seenMessageIds)
            {
                if (kvp.Value < now)
                {
                    toRemove ??= new List<string>();
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var k in toRemove) _seenMessageIds.Remove(k);
            }
        }

        if (_seenRequestIds.Count > 0)
        {
            List<string>? toRemove = null;
            foreach (var kvp in _seenRequestIds)
            {
                if (kvp.Value < now)
                {
                    toRemove ??= new List<string>();
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var k in toRemove) _seenRequestIds.Remove(k);
            }
        }
    }

    private void EnsureCapacity_NoLock()
    {
        while (_seenMessageIds.Count > _maxCapacity)
        {
            using var enumerator = _seenMessageIds.Keys.GetEnumerator();
            if (enumerator.MoveNext())
            {
                _seenMessageIds.Remove(enumerator.Current);
            }
            else break;
        }

        while (_seenRequestIds.Count > _maxCapacity)
        {
            using var enumerator = _seenRequestIds.Keys.GetEnumerator();
            if (enumerator.MoveNext())
            {
                _seenRequestIds.Remove(enumerator.Current);
            }
            else break;
        }
    }
}

internal static class ReplayGuardExtensions
{
    public static void ClearNullable(this Dictionary<string, long> dict) => dict.Clear();
}
