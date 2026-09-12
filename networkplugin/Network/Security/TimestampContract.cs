using System;

namespace NetworkPlugin.Network.Security;

/// <summary>
/// 消息时间戳格式契约。
/// </summary>
public enum TimestampFormat
{
    /// <summary>
    /// Unix 秒时间戳（自 1970-01-01 00:00:00 UTC 起的整秒数）。
    /// </summary>
    UnixSeconds = 0,

    /// <summary>
    /// UTC Ticks 时间戳（自公元 0001-01-01 00:00:00 起的 100 纳秒计数）。
    /// .NET 中 DateTime.UtcNow.Ticks 采用此表示。
    /// </summary>
    UtcTicks = 1,
}

/// <summary>
/// 时间戳转换与校验引擎。
/// </summary>
public static class TimestampConverter
{
    /// <summary>
    /// Unix 纪元（1970-01-01 00:00:00 UTC）对应的 Ticks。
    /// </summary>
    public const long UnixEpochTicks = 621355968000000000L;

    /// <summary>
    /// 每秒包含的 Ticks 数量（10,000,000）。
    /// </summary>
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>
    /// 将原始时间戳依据指定的格式契约转换为 Unix 秒数。
    /// 包含纪元偏移、比例换算及上下界溢出防护。
    /// </summary>
    public static bool TryConvertToUnixSeconds(long rawTimestamp, TimestampFormat format, out long unixSeconds, out string failureReason)
    {
        unixSeconds = 0;
        failureReason = string.Empty;

        switch (format)
        {
            case TimestampFormat.UnixSeconds:
                if (rawTimestamp <= 0)
                {
                    failureReason = $"Unix 秒时间戳无效: {rawTimestamp} (必须大于 0)";
                    return false;
                }
                unixSeconds = rawTimestamp;
                return true;

            case TimestampFormat.UtcTicks:
                if (rawTimestamp <= 0)
                {
                    failureReason = $"UTC Ticks 时间戳缺失或无效: {rawTimestamp} (必须大于 0)";
                    return false;
                }

                if (rawTimestamp < UnixEpochTicks)
                {
                    failureReason = $"UTC Ticks 时间戳早于 Unix 纪元 (1970-01-01): {rawTimestamp}";
                    return false;
                }

                if (rawTimestamp > DateTime.MaxValue.Ticks)
                {
                    failureReason = $"UTC Ticks 时间戳超出最大有效范围: {rawTimestamp}";
                    return false;
                }

                try
                {
                    long ticksSinceEpoch = checked(rawTimestamp - UnixEpochTicks);
                    unixSeconds = ticksSinceEpoch / TicksPerSecond;
                    return true;
                }
                catch (OverflowException)
                {
                    failureReason = $"UTC Ticks 纪元转换计算溢出: {rawTimestamp}";
                    return false;
                }

            default:
                failureReason = $"不支持的时间戳格式契约: {format}";
                return false;
        }
    }
}
