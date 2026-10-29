namespace PCL.Network;

internal static class DownloadRecoveryPolicy
{
    internal const int SlowCheckSeconds = 8;
    internal const long SlowSpeedBytesPerSecond = 128L * 1024;
    internal const long MinimumRecoveryRemaining = 256L * 1024;

    internal static int GetMaxSlowRecoveries(int sourceCount) => Math.Max(2, sourceCount * 2);

    internal static bool IsSlow(long attemptBytes, double elapsedSeconds, long remaining)
        => ModNet.NetTaskSpeedLimitHigh <= 0 && elapsedSeconds >= SlowCheckSeconds &&
           (remaining < 0 || remaining >= MinimumRecoveryRemaining) &&
           attemptBytes / elapsedSeconds < SlowSpeedBytesPerSecond;
}
