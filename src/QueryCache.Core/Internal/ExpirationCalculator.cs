using BitFaster.Caching;

namespace QueryCache;

internal sealed class ExpirationCalculator<TResult> : IExpiryCalculator<QueryKey, Entry<TResult>>
{
    public static readonly ExpirationCalculator<TResult> Instance = new();

    public Duration GetExpireAfterCreate(QueryKey key, Entry<TResult> value)
    {
        return Duration.FromTimeSpan(value.Expiration);
    }

    public Duration GetExpireAfterRead(QueryKey key, Entry<TResult> value, Duration current)
    {
        return current;
    }

    public Duration GetExpireAfterUpdate(QueryKey key, Entry<TResult> value, Duration current)
    {
        return current;
    }
}
