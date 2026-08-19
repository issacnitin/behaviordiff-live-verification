namespace SampleApp
{
    /// <summary>Consumes retry policy from another file and is never edited by the retry demo.</summary>
    public sealed class RetryEvaluator
    {
        public bool ShouldRetry(int failedAttempts)
        {
            return failedAttempts < RetrySettings.MaxAttempts;
        }

        public int DelayMilliseconds(int failedAttempts)
        {
            return ShouldRetry(failedAttempts) ? 100 : 0;
        }

        public string DescribeFailure(int failedAttempts)
        {
            int delay = DelayMilliseconds(failedAttempts);
            return delay == 0 ? "stop" : "retry in " + delay + "ms";
        }
    }
}
