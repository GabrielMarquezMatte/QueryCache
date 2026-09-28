using System.Reflection;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using QueryCache.EFCore.Keys;

namespace QueryCache.EFCore
{
    /// <summary>
    /// Cached terminal operators for EF Core queries: <c>db.Users.Where(...).Include(...).ToListCachedAsync(expiration, ct)</c>.
    /// </summary>
    /// <remarks>
    /// The cache key is the generated SQL, its parameter values and the connection (without password), so any LINQ/EF operator
    /// (Include, AsSplitQuery, Select...) can precede these calls. Results are loaded with no tracking and shared
    /// between callers; do not mutate the entities. Inside a transaction the cache is skipped.
    /// Entries remember the tables they read, so <see cref="QueryCacheInvalidation.UseQueryCacheInvalidation(DbContextOptionsBuilder)"/>
    /// can drop them on <c>SaveChanges</c>. Only relational providers are supported (the key comes from <c>CreateDbCommand</c>).
    /// </remarks>
    public static class QueryableCacheExtensions
    {
        private static readonly MethodInfo AsNoTrackingMethod = typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!;

        /// <summary>Returns the rows from the cache, or runs the query and caches them for <paramref name="expiration"/>. Empty results are not cached.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The rows, read-only because the same list is handed to every caller.</returns>
        public static ValueTask<IReadOnlyList<T>> ToListCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, static async (q, ct) => (IReadOnlyList<T>)(await NoTracking(q).ToListAsync(ct).ConfigureAwait(false)).AsReadOnly(), expiration, cancellationToken);
        }

        /// <summary>Builds a dictionary from the rows cached by <see cref="ToListCachedAsync{T}"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="keySelector">Selects each row's key.</param>
        /// <param name="expiration">How long the rows live in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The rows keyed by <paramref name="keySelector"/>.</returns>
        public static async ValueTask<Dictionary<TKey, T>> ToDictionaryCachedAsync<T, TKey>(this IQueryable<T> query, Func<T, TKey> keySelector, TimeSpan expiration, CancellationToken cancellationToken) where TKey : notnull
        {
            return (await query.ToListCachedAsync(expiration, cancellationToken).ConfigureAwait(false)).ToDictionary(keySelector);
        }

        /// <summary>Builds a dictionary from the rows cached by <see cref="ToListCachedAsync{T}"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="keySelector">Selects each row's key.</param>
        /// <param name="valueSelector">Selects each row's value.</param>
        /// <param name="expiration">How long the rows live in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The selected values keyed by <paramref name="keySelector"/>.</returns>
        public static async ValueTask<Dictionary<TKey, TValue>> ToDictionaryCachedAsync<T, TKey, TValue>(this IQueryable<T> query, Func<T, TKey> keySelector, Func<T, TValue> valueSelector, TimeSpan expiration, CancellationToken cancellationToken) where TKey : notnull
        {
            return (await query.ToListCachedAsync(expiration, cancellationToken).ConfigureAwait(false)).ToDictionary(keySelector, valueSelector);
        }

        /// <summary>Builds a dictionary from the rows cached by <see cref="ToListCachedAsync{T}"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="keySelector">Selects each row's key.</param>
        /// <param name="comparer">Compares keys.</param>
        /// <param name="expiration">How long the rows live in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The rows keyed by <paramref name="keySelector"/>.</returns>
        public static async ValueTask<Dictionary<TKey, T>> ToDictionaryCachedAsync<T, TKey>(this IQueryable<T> query, Func<T, TKey> keySelector, IEqualityComparer<TKey> comparer, TimeSpan expiration, CancellationToken cancellationToken) where TKey : notnull
        {
            return (await query.ToListCachedAsync(expiration, cancellationToken).ConfigureAwait(false)).ToDictionary(keySelector, comparer);
        }

        /// <summary>Returns the first row (or <see langword="default"/>) from the cache, or runs the query and caches it for <paramref name="expiration"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The first row, or <see langword="default"/> when there is none.</returns>
        public static ValueTask<T?> FirstOrDefaultCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, static (q, ct) => NoTracking(q).FirstOrDefaultAsync(ct), expiration, cancellationToken);
        }

        /// <summary>Like <see cref="FirstOrDefaultCachedAsync{T}"/> (and sharing its cache entry), but throws when there is no row.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The first row.</returns>
        /// <exception cref="InvalidOperationException">The query returned no rows.</exception>
        public static async ValueTask<T> FirstCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken) where T : class
        {
            return await query.FirstOrDefaultCachedAsync(expiration, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Sequence contains no elements.");
        }

        /// <summary>Returns whether the query has rows, from the cache or by running it and caching the answer for <paramref name="expiration"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> if the query has rows.</returns>
        public static ValueTask<bool> AnyCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, static (q, ct) => q.AnyAsync(ct), expiration, cancellationToken);
        }

        /// <summary>Returns the row count, from the cache or by running the query and caching it for <paramref name="expiration"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of rows.</returns>
        public static ValueTask<int> CountCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, static (q, ct) => q.CountAsync(ct), expiration, cancellationToken);
        }

        /// <summary>Removes every cached result of <paramref name="query"/> (list, first row, any and count).</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query whose results to remove.</param>
        /// <returns><see langword="true"/> if anything was removed.</returns>
        public static bool InvalidateCache<T>(this IQueryable<T> query)
        {
            QueryKey key;
            using (var command = query.CreateDbCommand())
            {
                key = DbCommandKey.Of(command);
            }
            return QueryCacheStore.Remove<IReadOnlyList<T>>(key)
                | QueryCacheStore.Remove<T>(key)
                | QueryCacheStore.Remove<bool>(key)
                | QueryCacheStore.Remove<int>(key);
        }

        private static ValueTask<TReturn> Cached<T, TReturn>(IQueryable<T> query, Func<IQueryable<T>, CancellationToken, Task<TReturn>> run,
                                                             TimeSpan expiration, CancellationToken cancellationToken)
        {
            QueryKey key;
            string[] tags;
            bool inTransaction;
            using (var command = query.CreateDbCommand())
            {
                inTransaction = command.Transaction is not null || Transaction.Current is not null;
                key = DbCommandKey.Of(command);
                tags = TableTags.ForQuery(query.Expression, ConnectionScope.Of(command.Connection));
            }
            if (inTransaction)
            {
                return new(run(query, cancellationToken));
            }
            return QueryCacheStore.GetOrAddAsync(key, expiration, ct => run(query, ct), tags, cancellationToken);
        }

        private static IQueryable<T> NoTracking<T>(IQueryable<T> query)
        {
            return typeof(T).IsValueType ? query : (IQueryable<T>)AsNoTrackingMethod.MakeGenericMethod(typeof(T)).Invoke(null, [query])!;
        }
    }
}
