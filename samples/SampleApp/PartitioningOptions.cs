namespace SampleApp
{
    public static class PartitioningOptions
    {
        private static readonly int s_partitionCount;
        private static readonly KeySelector s_partitionKeySelector;

        public static int PartitionCount
        {
            get { return s_partitionCount; }
        }

        public static KeySelector PartitionKeySelector
        {
            get { return s_partitionKeySelector; }
        }

        static PartitioningOptions()
        {
            s_partitionCount = 10;
            s_partitionKeySelector = KeySelector.CustomerId;
        }
    }
}