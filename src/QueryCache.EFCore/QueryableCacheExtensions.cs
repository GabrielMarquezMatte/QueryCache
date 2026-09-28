using System.Linq.Expressions;
using System.Reflection;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
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
        private const string ListOperation = "list";
        private const string FirstOperation = "first";
        private const string SingleOperation = "single";
        private const string AnyOperation = "any";
        private const string CountOperation = "count";
        private const string SumOperation = "sum";
        private const string MaxOperation = "max";

        private static readonly MethodInfo AsNoTrackingMethod = typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!;

        /// <summary>Returns the rows from the cache, or runs the query and caches them for <paramref name="expiration"/>. Empty results are not cached.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The rows, read-only because the same list is handed to every caller.</returns>
        public static ValueTask<IReadOnlyList<T>> ToListCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, ListOperation, static async (q, ct) => (IReadOnlyList<T>)(await NoTracking(q).ToListAsync(ct).ConfigureAwait(false)).AsReadOnly(), expiration, cancellationToken);
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
            return Cached(query, FirstOperation, static (q, ct) => NoTracking(q).FirstOrDefaultAsync(ct), expiration, cancellationToken);
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
            return Cached(query, AnyOperation, static (q, ct) => q.AnyAsync(ct), expiration, cancellationToken);
        }

        /// <summary>Returns the row count, from the cache or by running the query and caching it for <paramref name="expiration"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of rows.</returns>
        public static ValueTask<int> CountCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, CountOperation, static (q, ct) => q.CountAsync(ct), expiration, cancellationToken);
        }

        /// <summary>Returns the only row (or <see langword="default"/>) from the cache, or runs the query and caches it for <paramref name="expiration"/>.</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query to run.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The only row, or <see langword="default"/> when there is none.</returns>
        /// <exception cref="InvalidOperationException">The query returned more than one row.</exception>
        public static ValueTask<T?> SingleOrDefaultCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, SingleOperation, static (q, ct) => NoTracking(q).SingleOrDefaultAsync(ct), expiration, cancellationToken);
        }

        /// <summary>Returns the sum of the values, from the cache or by running the query and caching it for <paramref name="expiration"/>.</summary>
        /// <typeparam name="T">A type <see cref="Queryable.Sum(IQueryable{int})"/> supports: <see cref="int"/>, <see cref="long"/>, <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/> or their nullable forms.</typeparam>
        /// <param name="query">The values to add, such as <c>db.Orders.Select(o =&gt; o.Total)</c>.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The sum.</returns>
        /// <exception cref="NotSupportedException"><typeparamref name="T"/> cannot be summed.</exception>
        public static ValueTask<T> SumCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            if (SumOf<T>.Method is null)
            {
                throw new NotSupportedException($"Sum is not supported for {typeof(T)}.");
            }
            return Cached(query, SumOperation, static (q, ct) => ((IAsyncQueryProvider)q.Provider).ExecuteAsync<Task<T>>(Expression.Call(SumOf<T>.Method, q.Expression), ct), expiration, cancellationToken);
        }

        /// <summary>Returns the largest value, from the cache or by running the query and caching it for <paramref name="expiration"/>.</summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="query">The values to compare, such as <c>db.Orders.Select(o =&gt; o.CreatedAt)</c>.</param>
        /// <param name="expiration">How long the result lives in the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The largest value.</returns>
        /// <exception cref="InvalidOperationException">The query returned no rows and <typeparamref name="T"/> is not nullable.</exception>
        public static ValueTask<T> MaxCachedAsync<T>(this IQueryable<T> query, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return Cached(query, MaxOperation, static (q, ct) => q.MaxAsync(ct), expiration, cancellationToken);
        }

        /// <summary>Removes every cached result of <paramref name="query"/> (list, first and single row, any, count, sum and max).</summary>
        /// <typeparam name="T">The row type.</typeparam>
        /// <param name="query">The query whose results to remove.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public static async ValueTask InvalidateCacheAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken)
        {
            QueryKey list, any, count;
            QueryKey[] rows;
            using (var command = query.CreateDbCommand())
            {
                list = DbCommandKey.Of(command, ListOperation);
                any = DbCommandKey.Of(command, AnyOperation);
                count = DbCommandKey.Of(command, CountOperation);
                rows = [DbCommandKey.Of(command, FirstOperation), DbCommandKey.Of(command, SingleOperation), DbCommandKey.Of(command, SumOperation), DbCommandKey.Of(command, MaxOperation)];
            }
            await QueryCacheStore.RemoveAsync<IReadOnlyList<T>>(list, cancellationToken).ConfigureAwait(false);
            await QueryCacheStore.RemoveAsync<bool>(any, cancellationToken).ConfigureAwait(false);
            await QueryCacheStore.RemoveAsync<int>(count, cancellationToken).ConfigureAwait(false);
            foreach (var key in rows)
            {
                await QueryCacheStore.RemoveAsync<T>(key, cancellationToken).ConfigureAwait(false);
            }
        }

        private static ValueTask<TReturn> Cached<T, TReturn>(IQueryable<T> query, string operation, Func<IQueryable<T>, CancellationToken, Task<TReturn>> run,
                                                             TimeSpan expiration, CancellationToken cancellationToken)
        {
            QueryKey key;
            string scope;
            bool inTransaction;
            using (var command = query.CreateDbCommand())
            {
                inTransaction = command.Transaction is not null || Transaction.Current is not null;
                key = DbCommandKey.Of(command, operation);
                scope = ConnectionScope.Of(command.Connection);
            }
            if (inTransaction)
            {
                return new(run(query, cancellationToken));
            }
            return QueryCacheStore.GetOrAddAsync(key, expiration, ct => run(query, ct), () => TableTags.ForQuery(query.Expression, scope), cancellationToken);
        }

        private static IQueryable<T> NoTracking<T>(IQueryable<T> query)
        {
            return NoTrackingOf<T>.Apply(query);
        }

        private static class NoTrackingOf<T>
        {
            public static readonly Func<IQueryable<T>, IQueryable<T>> Apply = typeof(T).IsValueType
                ? static query => query
                : AsNoTrackingMethod.MakeGenericMethod(typeof(T)).CreateDelegate<Func<IQueryable<T>, IQueryable<T>>>();
        }

        private static class SumOf<T>
        {
            public static readonly MethodInfo? Method = typeof(Queryable).GetMethods()
                .SingleOrDefault(static method => string.Equals(method.Name, nameof(Queryable.Sum), StringComparison.Ordinal)
                                                  && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(IQueryable<T>));
        }
    }
}
