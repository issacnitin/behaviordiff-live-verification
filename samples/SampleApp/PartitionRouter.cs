using System;
using System.Security.Cryptography;
using System.Text;

namespace SampleApp
{
    public sealed class PartitionRouter
    {
        public int PartitionFor(OrderEvent orderEvent)
        {
            string key = PartitioningOptions.PartitionKeySelector == KeySelector.CustomerId
                ? orderEvent.CustomerId
                : orderEvent.OrderId;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return (int)(BitConverter.ToUInt32(hash, 0) % PartitioningOptions.PartitionCount);
        }
    }
}