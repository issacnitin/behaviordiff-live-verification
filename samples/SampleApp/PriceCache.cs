using System;
using System.Collections.Generic;

namespace SampleApp
{
    [Flags]
    public enum PriceCacheKeyFields
    {
        ProductId = 1,
        CustomerTier = 2,
    }

    public sealed class PriceCache
    {
        private readonly Dictionary<string, decimal> _prices = new Dictionary<string, decimal>();

        public string BuildKey(int productId, string customerTier)
        {
            string key = "P:" + productId;
            if ((CacheSettings.PriceCacheKeyFields & PriceCacheKeyFields.CustomerTier) != 0)
            {
                key += "|T:" + customerTier;
            }

            return key;
        }

        public bool TryGet(string key, out decimal price)
        {
            return _prices.TryGetValue(key, out price);
        }

        public void Store(string key, decimal price)
        {
            _prices[key] = price;
        }
    }
}