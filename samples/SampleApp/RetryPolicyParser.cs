namespace SampleApp
{
    /// <summary>Applies retry defaults. Its trace stays identical when the default changes.</summary>
    public static class RetryPolicyParser
    {
        private const int DefaultMaxAttempts = 3;

        public static void Apply(string raw)
        {
            int maxAttempts = DefaultMaxAttempts;
            if (raw.StartsWith("maxAttempts=", System.StringComparison.Ordinal)
                && int.TryParse(raw.Substring("maxAttempts=".Length), out int parsed))
            {
                maxAttempts = parsed;
            }

            RetrySettings.MaxAttempts = maxAttempts;
        }
    }
}
