namespace NetworkPlugin.Core.Trade;

public static class TradeConstants
{
    public const int ProtocolVersionUnknown = 0;
    public const int CurrentProtocolVersion = 2;
    public const int MinSupportedProtocolVersion = 2;
    public const int LegacyProtocolVersion = 1;
    public const int MaxMoneyOffer = 99999;
    public const int DefaultCommitTimeoutSeconds = 15;
    public const float TradeCompleteWaitSeconds = 2.0f;
    public const int MaxConcurrentTradeSessions = 50;
    public const string ReasonProtocolVersionIncompatible = "ProtocolVersionIncompatible";
}
