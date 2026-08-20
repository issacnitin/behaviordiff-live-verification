namespace SampleApp
{
    public static class CacheSettings
    {
        private static readonly PriceCacheKeyFields s_priceCacheKeyFields;

        public static PriceCacheKeyFields PriceCacheKeyFields
        {
            get { return s_priceCacheKeyFields; }
        }

        static CacheSettings()
        {
            s_priceCacheKeyFields = PriceCacheKeyFields.ProductId | PriceCacheKeyFields.CustomerTier;
        }
    }
}