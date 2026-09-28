using System.Diagnostics.Metrics;

namespace QueryCache.Tests;

public sealed class QueryCacheStoreTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public sealed record TelemetryProbe(int Value);

    public sealed record CapacityProbe(int Value);

    public sealed record CollisionProbe(string Value);

    private static QueryKey Key(string name, params object?[] values)
    {
        return new QueryKey(name, values);
    }

    [Fact]
    public async Task Capacity_applies_to_result_types_first_used_after_it_is_set()
    {
        QueryCacheStore.Capacity = 1000;
        for (var i = 0; i < 1000; i++)
        {
            var value = i;
            await QueryCacheStore.GetOrAddAsync(Key(nameof(CapacityProbe), i), Minute, _ => Task.FromResult(new CapacityProbe(value)), CancellationToken.None);
        }

        Assert.Equal(1000, Enumerable.Range(0, 1000).Count(i => QueryCacheStore.TryGet<CapacityProbe>(Key(nameof(CapacityProbe), i), out _)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Capacity_below_three_is_rejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryCacheStore.Capacity = capacity);
    }

    [Fact]
    public void Keys_compare_text_and_values_including_sequences()
    {
        Assert.Equal(Key("q", 1, new[] { 1, 2 }), Key("q", 1, new List<int> { 1, 2 }));
        Assert.NotEqual(Key("q", 1), Key("q", 2));
        Assert.NotEqual(Key("q", 1), Key("r", 1));
        Assert.NotEqual(Key("q", new[] { 1, 2 }, new[] { 3 }), Key("q", new[] { 1 }, new[] { 2, 3 }));
        Assert.NotEqual(Key("q", "a", "b"), Key("q", (object)new[] { "a", "b" }));
        Assert.True(Key("q", 1).Equals((object)Key("q", 1)));
        Assert.False(Key("q", 1).Equals("q"));
        Assert.Equal("q", Key("q", 1).ToString());
    }

    private static IEnumerable<int> Lazy(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
        }
    }

    [Fact]
    public async Task Lazy_enumerables_are_cached_only_when_they_have_items()
    {
        var empty = Key(nameof(Lazy_enumerables_are_cached_only_when_they_have_items), 0);
        var full = Key(nameof(Lazy_enumerables_are_cached_only_when_they_have_items), 1);

        await QueryCacheStore.GetOrAddAsync(empty, Minute, _ => Task.FromResult(Lazy(0)), CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(full, Minute, _ => Task.FromResult(Lazy(2)), CancellationToken.None);

        Assert.False(QueryCacheStore.TryGet<IEnumerable<int>>(empty, out _));
        Assert.True(QueryCacheStore.TryGet<IEnumerable<int>>(full, out _));
    }

    [Fact]
    public async Task Keys_with_equal_hash_codes_do_not_share_entries()
    {
        var seen = new Dictionary<int, QueryKey>();
        QueryKey? first = null;
        QueryKey? second = null;
        for (var i = 0; second is null; i++)
        {
            var key = Key(nameof(Keys_with_equal_hash_codes_do_not_share_entries), $"v{i}");
            if (!seen.TryAdd(key.GetHashCode(), key))
            {
                first = seen[key.GetHashCode()];
                second = key;
            }
        }

        await QueryCacheStore.GetOrAddAsync(first!, Minute, _ => Task.FromResult(new CollisionProbe("first")), CancellationToken.None);
        var value = await QueryCacheStore.GetOrAddAsync(second, Minute, _ => Task.FromResult(new CollisionProbe("second")), CancellationToken.None);

        Assert.Equal("second", value.Value);
        Assert.True(QueryCacheStore.TryGet<CollisionProbe>(first!, out var cached));
        Assert.Equal("first", cached.Value);
    }

    [Fact]
    public async Task Concurrent_misses_run_factory_once()
    {
        var calls = 0;
        var key = Key(nameof(Concurrent_misses_run_factory_once));
        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50, ct);
            return "v";
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => QueryCacheStore.GetOrAddAsync(key, Minute, Factory, CancellationToken.None).AsTask()));
        Assert.All(results, r => Assert.Equal("v", r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Concurrent_waiters_share_the_first_failure()
    {
        var calls = 0;
        var key = Key(nameof(Concurrent_waiters_share_the_first_failure));
        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50, ct);
            throw new InvalidOperationException("db down");
        }

        var calls20 = Enumerable.Range(0, 20).Select(_ => QueryCacheStore.GetOrAddAsync(key, Minute, Factory, CancellationToken.None).AsTask()).ToList();

        foreach (var call in calls20)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Waiter_survives_the_first_callers_cancellation()
    {
        var calls = 0;
        var key = Key(nameof(Waiter_survives_the_first_callers_cancellation));
        var release = new TaskCompletionSource();
        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(ct);
            return "v";
        }
        using var firstCaller = new CancellationTokenSource();

        var first = QueryCacheStore.GetOrAddAsync(key, Minute, Factory, firstCaller.Token).AsTask();
        var second = QueryCacheStore.GetOrAddAsync(key, Minute, Factory, CancellationToken.None).AsTask();
        await firstCaller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal("v", await second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Empty_collections_are_not_cached()
    {
        var key = Key(nameof(Empty_collections_are_not_cached));
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult(new List<int>()), CancellationToken.None);
        Assert.False(QueryCacheStore.TryGet<List<int>>(key, out _));
    }

    [Fact]
    public async Task Empty_lazy_enumerables_are_not_cached()
    {
        var key = Key(nameof(Empty_lazy_enumerables_are_not_cached));
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult(Enumerable.Range(0, 0).Select(i => i)), CancellationToken.None);
        Assert.False(QueryCacheStore.TryGet<IEnumerable<int>>(key, out _));
    }

    [Fact]
    public async Task Default_values_are_not_cached()
    {
        var key = Key(nameof(Default_values_are_not_cached));
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult<string?>(null), CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult(0), CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult(false), CancellationToken.None);

        Assert.False(QueryCacheStore.TryGet<string?>(key, out _));
        Assert.False(QueryCacheStore.TryGet<int>(key, out _));
        Assert.False(QueryCacheStore.TryGet<bool>(key, out _));
    }

    [Fact]
    public async Task Empty_strings_are_cached()
    {
        var key = Key(nameof(Empty_strings_are_cached));
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult(string.Empty), CancellationToken.None);
        Assert.True(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public async Task Entries_expire()
    {
        var key = Key(nameof(Entries_expire));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMilliseconds(50), _ => Task.FromResult(1), CancellationToken.None);
        Assert.True(QueryCacheStore.TryGet<int>(key, out _));
        await Task.Delay(200);
        Assert.False(QueryCacheStore.TryGet<int>(key, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task Infinite_expiration_caches_until_removed(long ticks)
    {
        var key = Key(nameof(Infinite_expiration_caches_until_removed), ticks);
        await QueryCacheStore.GetOrAddAsync(key, ticks == -1 ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(ticks), _ => Task.FromResult("v"), CancellationToken.None);
        Assert.True(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_positive_expiration_is_rejected(int seconds)
    {
        var key = Key(nameof(Non_positive_expiration_is_rejected), seconds);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromSeconds(seconds), _ => Task.FromResult("v"), CancellationToken.None));
    }

    [Fact]
    public async Task Remove_evicts_entry()
    {
        var key = Key(nameof(Remove_evicts_entry));
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult("v"), CancellationToken.None);
        await QueryCacheStore.RemoveAsync<string>(key, CancellationToken.None);
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public async Task Remove_during_fill_keeps_the_stale_value_out_of_the_cache()
    {
        var key = Key(nameof(Remove_during_fill_keeps_the_stale_value_out_of_the_cache));
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var fill = QueryCacheStore.GetOrAddAsync(key, Minute, async _ =>
        {
            started.SetResult();
            await release.Task;
            return "stale";
        }, CancellationToken.None).AsTask();

        await started.Task;
        await QueryCacheStore.RemoveAsync<string>(key, CancellationToken.None);
        release.SetResult();

        Assert.Equal("stale", await fill);
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public async Task InvalidateTags_evicts_entries_carrying_the_tag()
    {
        var tagged = Key(nameof(InvalidateTags_evicts_entries_carrying_the_tag), 1);
        var other = Key(nameof(InvalidateTags_evicts_entries_carrying_the_tag), 2);
        await QueryCacheStore.GetOrAddAsync(tagged, Minute, _ => Task.FromResult("v"), ["tests.users"], CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(other, Minute, _ => Task.FromResult("v"), ["tests.orders"], CancellationToken.None);

        await QueryCacheStore.InvalidateTagsAsync(["tests.users"], CancellationToken.None);

        Assert.False(QueryCacheStore.TryGet<string>(tagged, out _));
        Assert.True(QueryCacheStore.TryGet<string>(other, out _));
    }

    [Fact]
    public async Task Tags_are_computed_only_on_a_miss()
    {
        var key = Key(nameof(Tags_are_computed_only_on_a_miss));
        var computed = 0;
        IReadOnlyCollection<string> Tags()
        {
            computed++;
            return ["tests.lazy"];
        }

        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult("v"), Tags, CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult("v"), Tags, CancellationToken.None);
        await QueryCacheStore.InvalidateTagsAsync(["tests.lazy"], CancellationToken.None);

        Assert.Equal(1, computed);
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public async Task Tag_invalidated_during_fill_keeps_the_stale_value_out_of_the_cache()
    {
        var key = Key(nameof(Tag_invalidated_during_fill_keeps_the_stale_value_out_of_the_cache));
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var fill = QueryCacheStore.GetOrAddAsync(key, Minute, async _ =>
        {
            started.SetResult();
            await release.Task;
            return "stale";
        }, ["tests.during-fill"], CancellationToken.None).AsTask();

        await started.Task;
        await QueryCacheStore.InvalidateTagsAsync(["tests.during-fill"], CancellationToken.None);
        release.SetResult();

        Assert.Equal("stale", await fill);
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public void Connection_scope_ignores_passwords()
    {
        Assert.Equal(ConnectionScope.Of("Server=x;Database=y;User Id=u;Password=secret"), ConnectionScope.Of("Server=x;Database=y;User Id=u"));
        Assert.NotEqual(ConnectionScope.Of("Server=x;Database=y"), ConnectionScope.Of("Server=x;Database=z"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Failed_factory_is_not_cached()
    {
        var key = Key(nameof(Failed_factory_is_not_cached));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await QueryCacheStore.GetOrAddAsync<string>(key, Minute, _ => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Equal("ok", await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult("ok"), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_token_is_honored_on_miss()
    {
        var key = Key(nameof(Cancelled_token_is_honored_on_miss));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await QueryCacheStore.GetOrAddAsync(key, Minute, ct => Task.FromCanceled<string>(ct), cts.Token));
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public async Task Hits_and_misses_are_published_as_metrics()
    {
        long hits = 0, misses = 0, fills = 0;
        var probeType = typeof(TelemetryProbe).ToString();
        bool IsProbe(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "querycache.type", StringComparison.Ordinal) && string.Equals(tag.Value as string, probeType, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, "QueryCache", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!IsProbe(tags))
            {
                return;
            }
            if (string.Equals(instrument.Name, "querycache.hits", StringComparison.Ordinal))
            {
                Interlocked.Add(ref hits, value);
            }
            else if (string.Equals(instrument.Name, "querycache.misses", StringComparison.Ordinal))
            {
                Interlocked.Add(ref misses, value);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (IsProbe(tags) && string.Equals(instrument.Name, "querycache.fill.duration", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fills);
            }
        });
        listener.Start();

        var key = Key(nameof(Hits_and_misses_are_published_as_metrics));
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult(new TelemetryProbe(1)), CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult(new TelemetryProbe(2)), CancellationToken.None);

        Assert.Equal(1, misses);
        Assert.Equal(1, hits);
        Assert.Equal(1, fills);
    }
}
