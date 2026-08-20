using System.Collections.Generic;

namespace SampleApp
{
    public enum KeySelector
    {
        CustomerId,
        OrderId,
    }

    public enum OrderEventKind
    {
        Credit,
        Debit,
    }

    public sealed class OrderEvent
    {
        public OrderEvent(
            string customerId,
            string orderId,
            OrderEventKind kind,
            decimal amount)
        {
            CustomerId = customerId;
            OrderId = orderId;
            Kind = kind;
            Amount = amount;
        }

        public string CustomerId { get; }

        public string OrderId { get; }

        public OrderEventKind Kind { get; }

        public decimal Amount { get; }
    }

    public sealed class EventBus
    {
        private readonly Queue<OrderEvent>[] _partitions;
        private readonly PartitionRouter _router = new PartitionRouter();

        public EventBus()
        {
            _partitions = new Queue<OrderEvent>[PartitioningOptions.PartitionCount];
            for (int index = 0; index < _partitions.Length; index++)
            {
                _partitions[index] = new Queue<OrderEvent>();
            }
        }

        public int Count { get; private set; }

        public void Publish(OrderEvent orderEvent)
        {
            int partition = SelectPartition(orderEvent);
            _partitions[partition].Enqueue(orderEvent);
            Count++;
        }

        private int SelectPartition(OrderEvent orderEvent)
        {
            return _router.PartitionFor(orderEvent);
        }

        public IReadOnlyList<OrderEvent> Drain()
        {
            var delivered = new List<OrderEvent>();
            while (delivered.Count < Count)
            {
                for (int index = 0; index < _partitions.Length; index++)
                {
                    if (_partitions[index].Count > 0)
                    {
                        delivered.Add(_partitions[index].Dequeue());
                    }
                }
            }

            return delivered;
        }
    }
}