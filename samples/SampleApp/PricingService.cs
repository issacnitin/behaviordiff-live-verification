namespace SampleApp
{
    public sealed class PricingService
    {
        private readonly PriceCache _cache = new PriceCache();

        public decimal GetPrice(int productId, string customerTier)
        {
            string key = BuildCacheKey(productId, customerTier);
            if (_cache.TryGet(key, out decimal cachedPrice))
            {
                return cachedPrice;
            }

            decimal price = customerTier == "Gold" ? 80m : 100m;
            _cache.Store(key, price);
            return price;
        }

        private string BuildCacheKey(int productId, string customerTier)
        {
            return _cache.BuildKey(productId, customerTier);
        }
    }
}