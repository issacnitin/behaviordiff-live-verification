using System;
using System.IO;
using System.Threading.Tasks;
using BehaviorDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class RetryTests
    {
        private static PaymentClient CreateClient(int recoveryAttempt)
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "payment-config.json");
            ConfigParser.Apply(Diagnostics.RetryConfigFixture.Load(configPath));
            return new PaymentClient(
                new Diagnostics.TransientPaymentGateway(recoveryAttempt),
                new RetryPolicy());
        }

        [Fact]
        public async Task Payment_succeeds_under_transient_failure()
        {
            PaymentResult result = await CreateClient(recoveryAttempt: 2).ChargeAsync(125m);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.AttemptCount);
        }

        [Fact]
        public async Task Payment_survives_extended_outage()
        {
            PaymentResult result = await CreateClient(recoveryAttempt: 7).ChargeAsync(125m);

            Assert.True(result.Succeeded);
            Assert.Equal(7, result.AttemptCount);
        }
    }
}

namespace SampleApp.Diagnostics
{
    internal sealed class TransientPaymentGateway : IPaymentGateway
    {
        private readonly int _recoveryAttempt;

        internal TransientPaymentGateway(int recoveryAttempt)
        {
            _recoveryAttempt = recoveryAttempt;
        }

        public Task<int> ChargeAsync(decimal amount, int attempt)
        {
            return Task.FromResult(attempt >= _recoveryAttempt ? 200 : 503);
        }
    }
}
