using System.ComponentModel;

namespace QueryCache;

/// <summary>
/// What the <see cref="QueryCacheStore.HybridCache"/> stores, so a cached <see langword="null"/>, 0 or empty result is told apart from a miss.
/// Marked immutable so the cache's local layer hands back the same instance instead of deserializing it on every hit.
/// </summary>
[ImmutableObject(true)]
internal sealed class CachedValue<T>
{
    public T Value { get; init; } = default!;
}
