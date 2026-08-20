namespace SampleApp
{
    public enum AccountStatus
    {
        Pending = 0,
        Active = 1,
        Frozen = 2,
        Suspended = 3,
        Closed = 4,
    }

    public static class AccountStatusStorage
    {
        public static void ObserveRead(int storedStatus)
        {
        }
    }
}