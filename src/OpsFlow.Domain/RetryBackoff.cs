namespace OpsFlow.Domain;

public static class RetryBackoff
{
    public static TimeSpan Delay(int attemptCount, double jitterFactor)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 8);
        var seconds = Math.Min(300, 2 * Math.Pow(2, exponent));
        var jitter = Math.Clamp(jitterFactor, 0.5, 1.5);
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}
