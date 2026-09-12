using System;

namespace NetworkPlugin.Network.Security;

public sealed class ConnectionRateLimiter
{
    private readonly object _lock = new();
    private readonly int _maxMessagesPerSecond;
    private readonly int _maxBytesPerSecond;
    private readonly int _maxConsecutiveViolations;

    private long _currentWindowSecond = 0;
    private int _messagesInCurrentSecond = 0;
    private int _bytesInCurrentSecond = 0;
    private int _consecutiveViolations = 0;

    public int ConsecutiveViolations
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveViolations;
            }
        }
    }

    public bool IsThrottled => ConsecutiveViolations >= _maxConsecutiveViolations;

    public ConnectionRateLimiter(
        int maxMessagesPerSecond = NetworkConstants.MaxMessagesPerSecond,
        int maxBytesPerSecond = NetworkConstants.MaxBytesPerSecond,
        int maxConsecutiveViolations = NetworkConstants.MaxConsecutiveRateViolations)
    {
        _maxMessagesPerSecond = maxMessagesPerSecond > 0 ? maxMessagesPerSecond : 100;
        _maxBytesPerSecond = maxBytesPerSecond > 0 ? maxBytesPerSecond : 512 * 1024;
        _maxConsecutiveViolations = maxConsecutiveViolations > 0 ? maxConsecutiveViolations : 3;
    }

    public bool TryAcquire(int messageBytes, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        long nowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_lock)
        {
            if (nowSecond != _currentWindowSecond)
            {
                _currentWindowSecond = nowSecond;
                _messagesInCurrentSecond = 0;
                _bytesInCurrentSecond = 0;
            }

            if (_messagesInCurrentSecond + 1 > _maxMessagesPerSecond)
            {
                _consecutiveViolations++;
                rejectionReason = $"连接超出每秒最大消息频率配额: current={_messagesInCurrentSecond + 1}, max={_maxMessagesPerSecond}";
                return false;
            }

            if (_bytesInCurrentSecond + messageBytes > _maxBytesPerSecond)
            {
                _consecutiveViolations++;
                rejectionReason = $"连接超出每秒最大传输字节配额: current={_bytesInCurrentSecond + messageBytes}, max={_maxBytesPerSecond}";
                return false;
            }

            _messagesInCurrentSecond++;
            _bytesInCurrentSecond += messageBytes;
            _consecutiveViolations = 0;
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _currentWindowSecond = 0;
            _messagesInCurrentSecond = 0;
            _bytesInCurrentSecond = 0;
            _consecutiveViolations = 0;
        }
    }
}
