using BehaviorDiff.Tracer;
using SampleApp;
using Xunit;

namespace SampleApp.Tests
{
    [TraceTest]
    public sealed class PricingCacheTests
    {
        [Fact]
        public void Gold_customer_gets_discount()
        {
            var service = new PricingService();

            decimal price = service.GetPrice(123, "Gold");

            Assert.Equal(80m, price);
        }

        [Fact]
        public void Price_is_never_negative()
        {
            var service = new PricingService();
            service.GetPrice(123, "Gold");

            decimal price = service.GetPrice(123, "Standard");

            Assert.True(price >= 0m);
        }

        [Fact]
        public void Standard_customer_pays_full_price()
        {
            var service = new PricingService();
            service.GetPrice(123, "Gold");

            decimal price = service.GetPrice(123, "Standard");

            Assert.Equal(100m, price);
        }
    }
}