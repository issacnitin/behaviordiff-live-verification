using BehaviorDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class PartitioningTests
    {
        [Fact]
        public void All_events_are_published()
        {
            var eventBus = new EventBus();
            eventBus.Publish(new OrderEvent("key-22", "key-15", OrderEventKind.Credit, 1000m));
            eventBus.Publish(new OrderEvent("key-22", "key-32", OrderEventKind.Debit, 500m));

            Assert.Equal(2, eventBus.Count);
        }

        [Fact]
        public void Balance_is_never_negative()
        {
            var eventBus = new EventBus();
            eventBus.Publish(new OrderEvent("key-22", "key-15", OrderEventKind.Credit, 1000m));
            eventBus.Publish(new OrderEvent("key-22", "key-32", OrderEventKind.Debit, 500m));
            var projection = new BalanceProjection();

            foreach (OrderEvent orderEvent in eventBus.Drain())
            {
                projection.Apply(orderEvent);
            }

            Assert.True(projection.Current().Amount >= 0);
        }

        [Fact]
        public void Balance_after_credit_and_debit()
        {
            var eventBus = new EventBus();
            eventBus.Publish(new OrderEvent("key-22", "key-15", OrderEventKind.Credit, 1000m));
            eventBus.Publish(new OrderEvent("key-22", "key-32", OrderEventKind.Debit, 500m));
            var projection = new BalanceProjection();

            foreach (OrderEvent orderEvent in eventBus.Drain())
            {
                projection.Apply(orderEvent);
            }

            Assert.Equal(500m, projection.Current().Amount);
        }
    }
}