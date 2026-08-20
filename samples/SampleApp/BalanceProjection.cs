namespace SampleApp
{
    public enum ProjectionResult
    {
        Applied,
        RejectedInsufficientFunds,
    }

    public sealed class BalanceSnapshot
    {
        public decimal Amount;
        public int RejectedDebits;
    }

    public sealed class BalanceProjection
    {
        private decimal _balance;
        private int _rejectedDebits;

        public ProjectionResult Apply(OrderEvent orderEvent)
        {
            if (orderEvent.Kind == OrderEventKind.Credit)
            {
                _balance += orderEvent.Amount;
                return ProjectionResult.Applied;
            }

            if (_balance - orderEvent.Amount < 0)
            {
                _rejectedDebits++;
                return ProjectionResult.RejectedInsufficientFunds;
            }

            _balance -= orderEvent.Amount;
            return ProjectionResult.Applied;
        }

        public BalanceSnapshot Current()
        {
            return new BalanceSnapshot
            {
                Amount = _balance,
                RejectedDebits = _rejectedDebits,
            };
        }
    }
}