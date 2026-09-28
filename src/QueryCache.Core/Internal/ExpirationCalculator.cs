using BitFaster.Caching;

namespace QueryCache;

internal sealed class ExpirationCalculator<TResult> : IExpiryCalculator<int, (TResult, TimeSpan)>
{
    public static readonly ExpirationCalculator<TResult> Instance = new();

    public Duration GetExpireAfterCreate(int key, (TResult, TimeSpan) value)
    {
        return Duration.FromTimeSpan(value.Item2);
    }

    public Duration GetExpireAfterRead(int key, (TResult, TimeSpan) value, Duration current)
    {
        return current;
    }

    public Duration GetExpireAfterUpdate(int key, (TResult, TimeSpan) value, Duration current)
    {
        return current;
    }
}
