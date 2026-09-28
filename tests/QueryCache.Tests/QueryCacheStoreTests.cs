using System.Diagnostics.Metrics;

namespace QueryCache.Tests;

public sealed class QueryCacheStoreTests
{
    public sealed record TelemetryProbe(int Value);

    public sealed record CapacityProbe(int Value);

    [Fact]
    public async Task Capacity_applies_to_result_types_first_used_after_it_is_set()
    {
        QueryCacheStore.Capacity = 1000;
        for (var i = 0; i < 1000; i++)
        {
            var value = i;
            await QueryCacheStore.GetOrAddAsync(HashCode.Combine(nameof(CapacityProbe), i), TimeSpan.FromMinutes(1), _ => Task.FromResult(new CapacityProbe(value)), CancellationToken.None);
        }

        Assert.Equal(1000, Enumerable.Range(0, 1000).Count(i => QueryCacheStore.TryGet<CapacityProbe>(HashCode.Combine(nameof(CapacityProbe), i), out _)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Capacity_below_three_is_rejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryCacheStore.Capacity = capacity);
    }

    [Fact]
    public async Task Concurrent_misses_run_factory_once()
    {
        var calls = 0;
        var key = HashCode.Combine(nameof(Concurrent_misses_run_factory_once));
        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50, ct);
            return "v";
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), Factory, CancellationToken.None).AsTask()));
        Assert.All(results, r => Assert.Equal("v", r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Empty_collections_are_not_cached()
    {
        var key = HashCode.Combine(nameof(Empty_collections_are_not_cached));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(new List<int>()), CancellationToken.None);
        Assert.False(QueryCacheStore.TryGet<List<int>>(key, out _));
    }

    [Fact]
    public async Task Empty_lazy_enumerables_are_not_cached()
    {
        var key = HashCode.Combine(nameof(Empty_lazy_enumerables_are_not_cached));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(Enumerable.Range(0, 0).Select(i => i)), CancellationToken.None);
        Assert.False(QueryCacheStore.TryGet<IEnumerable<int>>(key, out _));
    }

    [Fact]
    public async Task Default_values_are_not_cached()
    {
        var key = HashCode.Combine(nameof(Default_values_are_not_cached));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult<string?>(null), CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(0), CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(false), CancellationToken.None);

        Assert.False(QueryCacheStore.TryGet<string?>(key, out _));
        Assert.False(QueryCacheStore.TryGet<int>(key, out _));
        Assert.False(QueryCacheStore.TryGet<bool>(key, out _));
    }

    [Fact]
    public async Task Empty_strings_are_cached()
    {
        var key = HashCode.Combine(nameof(Empty_strings_are_cached));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(string.Empty), CancellationToken.None);
        Assert.True(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public async Task Entries_expire()
    {
        var key = HashCode.Combine(nameof(Entries_expire));
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
        var key = HashCode.Combine(nameof(Infinite_expiration_caches_until_removed), ticks);
        await QueryCacheStore.GetOrAddAsync(key, ticks == -1 ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(ticks), _ => Task.FromResult("v"), CancellationToken.None);
        Assert.True(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_positive_expiration_is_rejected(int seconds)
    {
        var key = HashCode.Combine(nameof(Non_positive_expiration_is_rejected), seconds);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromSeconds(seconds), _ => Task.FromResult("v"), CancellationToken.None));
    }

    [Fact]
    public async Task Remove_evicts_entry()
    {
        var key = HashCode.Combine(nameof(Remove_evicts_entry));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult("v"), CancellationToken.None);
        Assert.True(QueryCacheStore.Remove<string>(key));
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));
        Assert.False(QueryCacheStore.Remove<string>(key));
    }

    [Fact]
    public async Task Remove_during_fill_keeps_the_stale_value_out_of_the_cache()
    {
        var key = HashCode.Combine(nameof(Remove_during_fill_keeps_the_stale_value_out_of_the_cache));
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var fill = QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), async _ =>
        {
            started.SetResult();
            await release.Task;
            return "stale";
        }, CancellationToken.None).AsTask();

        await started.Task;
        QueryCacheStore.Remove<string>(key);
        release.SetResult();

        Assert.Equal("stale", await fill);
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));
    }

    [Fact]
    public async Task Failed_factory_is_not_cached()
    {
        var key = HashCode.Combine(nameof(Failed_factory_is_not_cached));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await QueryCacheStore.GetOrAddAsync<string>(key, TimeSpan.FromMinutes(1), _ => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Equal("ok", await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult("ok"), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_token_is_honored_on_miss()
    {
        var key = HashCode.Combine(nameof(Cancelled_token_is_honored_on_miss));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult("v"), cts.Token));
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

        var key = HashCode.Combine(nameof(Hits_and_misses_are_published_as_metrics));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(new TelemetryProbe(1)), CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(new TelemetryProbe(2)), CancellationToken.None);

        Assert.Equal(1, misses);
        Assert.Equal(1, hits);
        Assert.Equal(1, fills);
    }
}
