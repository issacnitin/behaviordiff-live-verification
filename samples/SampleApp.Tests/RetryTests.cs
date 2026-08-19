using BehaviorDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class RetryTests
    {
        [Fact]
        public void Retry_decision_is_always_actionable()
        {
            RetryPolicyParser.Apply("region=eu");

            string decision = new RetryEvaluator().DescribeFailure(2);

            Assert.False(string.IsNullOrWhiteSpace(decision));
        }
    }
}
