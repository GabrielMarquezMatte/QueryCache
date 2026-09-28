using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using System.Collections;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Infrastructure;
using QueryCache.EFCore.Comparers;

namespace QueryCache.EFCore
{
    /// <summary>
    /// Represents a builder for caching query results.
    /// </summary>
    /// <typeparam name="T">The type of the entities in the query.</typeparam>
    public readonly record struct CacheQueryBuilder<T> : IQueryable<T> where T : class
    {
        private readonly bool _isSplitQuery;
        private readonly int _scope;
        private readonly IQueryable<T> _query;
        /// <inheritdoc/>
        public readonly Type ElementType => _query.ElementType;
        /// <inheritdoc/>
        public readonly Expression Expression => _query.Expression;
        /// <inheritdoc/>
        public readonly IQueryProvider Provider => _query.Provider;
        /// <summary>
        /// Represents a builder for caching query results.
        /// </summary>
        public CacheQueryBuilder(DbSet<T> query)
        {
            _query = query.AsNoTracking();
            _isSplitQuery = false;
            // Same SQL against another database (multi-tenant) must not share cache entries.
            var connectionString = ((IInfrastructure<IServiceProvider>)query).GetService<ICurrentDbContext>().Context.Database.GetConnectionString();
            _scope = StringComparer.Ordinal.GetHashCode(connectionString ?? string.Empty);
        }
        private CacheQueryBuilder(IQueryable<T> query, bool isSplitQuery, int scope)
        {
            _query = query;
            _isSplitQuery = isSplitQuery;
            _scope = scope;
        }
        /// <summary>
        /// Configures the query to be split into multiple queries.
        /// </summary>
        /// <returns>The <see cref="CacheQueryBuilder{T}"/> instance.</returns>
        public CacheQueryBuilder<T> AsSplitQuery()
        {
            return new(_query, true, _scope);
        }
        /// <summary>
        /// Adds a WHERE clause to the query builder.
        /// </summary>
        /// <param name="predicate">The predicate expression representing the condition.</param>
        /// <returns>The updated cache query builder.</returns>
        public CacheQueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
        {
            return new(_query.Where(predicate), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Orders the query results by the specified key selector.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="keySelector">The key selector expression.</param>
        /// <returns>The updated <see cref="CacheQueryBuilder{T}"/> instance.</returns>
        public CacheQueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            return new(_query.OrderBy(keySelector), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Orders the query results by the specified key selector.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="keySelector">The key selector expression.</param>
        /// <returns>The updated <see cref="CacheQueryBuilder{T}"/> instance.</returns>
        public CacheQueryBuilder<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            if (_query is not IOrderedQueryable<T> orderedQuery)
            {
                throw new InvalidOperationException("The query is not ordered.");
            }
            return new(orderedQuery.ThenBy(keySelector), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Specifies the ordering of the elements in the query result in descending order based on the specified key.
        /// /// </summary>
        /// <typeparam name="TKey">The type of the key used for ordering.</typeparam>
        /// <param name="keySelector">The expression used to extract the key from each element.</param>
        /// <returns>A reference to the current instance of the <see cref="CacheQueryBuilder{T}"/> class.</returns>
        public CacheQueryBuilder<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            if (_query is not IOrderedQueryable<T> orderedQuery)
            {
                throw new InvalidOperationException("The query is not ordered.");
            }
            return new(orderedQuery.ThenByDescending(keySelector), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Specifies the ordering of the elements in the query result in descending order based on the specified key.
        /// </summary>
        /// <typeparam name="TKey">The type of the key used for ordering.</typeparam>
        /// <param name="keySelector">The expression used to extract the key from each element.</param>
        /// <returns>A reference to the current instance of the <see cref="CacheQueryBuilder{T}"/> class.</returns>
        public CacheQueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            return new(_query.OrderByDescending(keySelector), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Applies a distinct operation to the query based on the specified key selector.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="keySelector">The key selector expression.</param>
        /// <returns>The updated cache query builder.</returns>
        public CacheQueryBuilder<T> DistinctBy<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            return new(_query.GroupBy(keySelector).Select(group => group.First()), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Sets the maximum number of items to be returned by the query.
        /// </summary>
        /// <param name="count">The maximum number of items to take.</param>
        /// <returns>The updated <see cref="CacheQueryBuilder{T}"/> instance.</returns>
        public CacheQueryBuilder<T> Take(int count)
        {
            return new(_query.Take(count), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Skips a specified number of elements in the query.
        /// </summary>
        /// <param name="count">The number of elements to skip.</param>
        /// <returns>A reference to the current instance of the <see cref="CacheQueryBuilder{T}"/> class.</returns>
        public CacheQueryBuilder<T> Skip(int count)
        {
            return new(_query.Skip(count), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Includes the specified path in the query.
        /// </summary>
        /// <param name="path">The path to include.</param>
        /// <returns>The updated <see cref="CacheQueryBuilder{T}"/> instance.</returns>
        public CacheQueryBuilder<T> Include(string path)
        {
            return new(_query.Include(path), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Includes the specified navigation property in the query.
        /// </summary>
        /// <param name="path">The expression representing the navigation property to include.</param>
        /// <returns>The updated cache query builder.</returns>
        public CacheQueryBuilder<T> Include<TProperty>(Expression<Func<T, TProperty>> path)
        {
            return new(_query.Include(path), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Includes the specified navigation property in the query.
        /// </summary>
        /// <typeparam name="TPreviousProperty">The type of the previous property.</typeparam>
        /// <typeparam name="TProperty">The type of the navigation property.</typeparam>
        /// <param name="path">The expression representing the navigation property to include.</param>
        /// <returns>The updated cache query builder.</returns>
        public CacheQueryBuilder<T> ThenInclude<TPreviousProperty, TProperty>(Expression<Func<TPreviousProperty, TProperty?>> path) where TPreviousProperty : class where TProperty : class
        {
            return _query switch
            {
                IIncludableQueryable<T, TPreviousProperty> query => new(query.ThenInclude(path), _isSplitQuery, _scope),
                IIncludableQueryable<T, IEnumerable<TPreviousProperty>> enumerableQuery => new(enumerableQuery.ThenInclude(path), _isSplitQuery, _scope),
                _ => new(((IIncludableQueryable<T, ICollection<TPreviousProperty>>)_query).ThenInclude(path), _isSplitQuery, _scope),
            };
        }
        /// <summary>
        /// Selects a specific property or properties from the query result.
        /// </summary>
        /// <typeparam name="TResult">The type of the selected property or properties.</typeparam>
        /// <param name="selector">The expression that specifies the property or properties to select.</param>
        /// <returns>A new instance of <see cref="CacheQueryBuilder{TResult}"/> with the updated query and cache key.</returns>
        public readonly CacheQueryBuilder<TResult> Select<TResult>(Expression<Func<T, TResult>> selector) where TResult : class
        {
            return new(_query.Select(selector), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Joins the query with another query based on the specified key selectors and projects the results into a new form.
        /// </summary>
        /// <typeparam name="TInner">The type of the inner query.</typeparam>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="inner">The inner query to join.</param>
        /// <param name="outerKeySelector">The key selector for the outer query.</param>
        /// <param name="innerKeySelector">The key selector for the inner query.</param>
        /// <param name="resultSelector">The selector expression for the result.</param>
        /// <returns>A new instance of <see cref="CacheQueryBuilder{TResult}"/> with the updated query and cache key.</returns>
        public readonly CacheQueryBuilder<TResult> Join<TInner, TKey, TResult>(IEnumerable<TInner?> inner,
                                                                               Expression<Func<T, TKey>> outerKeySelector,
                                                                               Expression<Func<TInner?, TKey>> innerKeySelector,
                                                                               Expression<Func<T, TInner?, TResult>> resultSelector) where TInner : class where TResult : class
        {
            var query = _query.Join(inner, outerKeySelector, innerKeySelector, resultSelector);
            return new(query, _isSplitQuery, _scope);
        }
        /// <summary>Groups the query by the given key.</summary>
        public readonly CacheQueryBuilder<IGrouping<TKey, T>> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            return new(_query.GroupBy(keySelector), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Projects the query results into a new form using the specified selector.
        /// </summary>
        /// <typeparam name="TSecond">The type of the second query.</typeparam>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="second">The second query to project.</param>
        /// <param name="resultSelector">The selector expression.</param>
        /// <returns>A new instance of <see cref="CacheQueryBuilder{TResult}"/> with the updated query and cache key.</returns>
        public readonly CacheQueryBuilder<TResult> Zip<TSecond, TResult>(IQueryable<TSecond> second, Expression<Func<T, TSecond, TResult>> resultSelector) where TSecond : class where TResult : class
        {
            return new(_query.Zip(second, resultSelector), _isSplitQuery, _scope);
        }
        /// <summary>Concatenates another query.</summary>
        public CacheQueryBuilder<T> Concat(CacheQueryBuilder<T> query)
        {
            return new(_query.Concat(query), _isSplitQuery, _scope);
        }
        /// <summary>
        /// Removes the specified operation from the cache.
        /// </summary>
        /// <param name="operation">The operation to remove from the cache.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> if the operation is removed from the cache; otherwise, <see langword="false"/>.</returns>
        public readonly async ValueTask<bool> RemoveFromCacheAsync(string operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dbCommand = _query.CreateDbCommand();
            await using (dbCommand.ConfigureAwait(false))
            {
                if (string.Equals(operation, "FirstOrDefaultAsync", StringComparison.Ordinal))
                {
                    return QueryCacheStore.Remove<T>(GetHash(dbCommand));
                }
                if (string.Equals(operation, "ToListAsync", StringComparison.Ordinal))
                {
                    return QueryCacheStore.Remove<List<T>>(GetHash(dbCommand));
                }
                throw new InvalidOperationException($"The operation {operation} is not supported.");
            }
        }
        private readonly int GetHash(DbCommand dbCommand)
        {
            return HashCode.Combine(DbCommandComparer.Instance.GetHashCode(dbCommand), _scope);
        }
        private readonly async ValueTask<TReturn> ExecuteAsync<TReturn>(Func<IQueryable<T>, CancellationToken, Task<TReturn>> action,
                                                                        IQueryable<T> query, TimeSpan expiration,
                                                                        CancellationToken cancellationToken)
        {
            var dbCommand = query.CreateDbCommand();
            int hash;
            await using (dbCommand.ConfigureAwait(false))
            {
                hash = GetHash(dbCommand);
            }
            if (QueryCacheStore.TryGet<TReturn>(hash, out var cached))
            {
                return cached;
            }
            if (_isSplitQuery)
            {
                query = query.AsSplitQuery();
            }
            return await QueryCacheStore.GetOrAddAsync(hash, expiration, ct => action(query, ct), cancellationToken).ConfigureAwait(false);
        }
        private readonly ValueTask<TReturn> ExecuteAsync<TReturn>(Func<IQueryable<T>, CancellationToken, Task<TReturn>> action,
                                                                  IQueryable<T> query, bool cache, TimeSpan expiration,
                                                                  CancellationToken cancellationToken)
        {
            if (!cache)
            {
                return new(action(query, cancellationToken));
            }
            return ExecuteAsync(action, query, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the query results as a list from the cache or executes the query and caches the results.
        /// </summary>
        /// <param name="expiration">The expiration time for the cached results</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the list of query results, or an empty list if the query results are not found in the cache.</returns>
        public readonly ValueTask<List<T>> ToListAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync((query, ct) => query.ToListAsync(ct), _query, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the query results as a list from the cache or executes the query and caches the results.
        /// </summary>
        /// <param name="cache">A value indicating whether to cache the query results.</param>
        /// <param name="expiration">The expiration time for the cached results</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the list of query results, or an empty list if the query results are not found in the cache.</returns>
        public readonly ValueTask<List<T>> ToListAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync((query, ct) => query.ToListAsync(ct), _query, cache, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the query results as an array from the cache or executes the query and caches the results.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="keySelector">The key selector expression.</param>
        /// <param name="expiration">The expiration time for the cached results</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the dictionary of query results, or an empty dictionary if the query results are not found in the cache.</returns>
        public readonly ValueTask<Dictionary<TKey, T>> ToDictionaryAsync<TKey>(Func<T, TKey> keySelector, TimeSpan expiration, CancellationToken cancellationToken) where TKey : notnull
        {
            return ExecuteAsync((query, cancellationToken) => query.ToDictionaryAsync(keySelector, cancellationToken), _query, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the query results as an array from the cache or executes the query and caches the results.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="keySelector">The key selector.</param>
        /// <param name="resultSelector">The value selector.</param>
        /// <typeparam name="TResult">The type of the values.</typeparam>
        /// <param name="expiration">The expiration time for the cached results</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the dictionary of query results, or an empty dictionary if the query results are not found in the cache.</returns>
        public readonly ValueTask<Dictionary<TKey, TResult>> ToDictionaryAsync<TKey, TResult>(Func<T, TKey> keySelector, Func<T, TResult> resultSelector, TimeSpan expiration, CancellationToken cancellationToken) where TKey : notnull
        {
            return ExecuteAsync((query, cancellationToken) => query.ToDictionaryAsync(keySelector, resultSelector, cancellationToken), _query, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the query results as an array from the cache or executes the query and caches the results.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="keySelector">The key selector expression.</param>
        /// <param name="equalityComparer">The equality comparer to use for comparing keys.</param>
        /// <param name="expiration">The expiration time for the cached results</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the dictionary of query results, or an empty dictionary if the query results are not found in the cache.</returns>
        public readonly ValueTask<Dictionary<TKey, T>> ToDictionaryAsync<TKey>(Func<T, TKey> keySelector,
                                                                               IEqualityComparer<TKey> equalityComparer,
                                                                               TimeSpan expiration,
                                                                               CancellationToken cancellationToken) where TKey : notnull
        {
            return ExecuteAsync((query, cancellationToken) => query.ToDictionaryAsync(keySelector, equalityComparer, cancellationToken), _query, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the first element of the query or the default value if the query is empty,
        /// and caches the result using the specified expiration time.
        /// </summary>
        /// <param name="expiration">The expiration time for the cached result</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the first element of the query or the default value.</returns>
        public readonly ValueTask<T?> FirstOrDefaultAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync((query, ct) => query.FirstOrDefaultAsync(ct), _query, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the first element of the query or the default value if the query is empty,
        /// and caches the result using the specified expiration time.
        /// </summary>
        /// <param name="cache">A value indicating whether to cache the query result.</param>
        /// <param name="expiration">The expiration time for the cached result</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the first element of the query or the default value.</returns>
        public readonly ValueTask<T?> FirstOrDefaultAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync((query, ct) => query.FirstOrDefaultAsync(ct), _query, cache, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the first element of the query that satisfies the specified condition asynchronously.
        /// </summary>
        /// <param name="predicate">The predicate expression representing the condition.</param>
        /// <param name="expiration">The expiration time for the cached result.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task{T}"/> representing the asynchronous operation that returns the first element of the query that satisfies the condition.</returns>
        public ValueTask<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, TimeSpan expiration, CancellationToken cancellationToken)
        {
            var query = _query.Where(predicate);
            return ExecuteAsync((query, ct) => query.FirstOrDefaultAsync(ct), query, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the first element of the query that satisfies the specified condition asynchronously.
        /// </summary>
        /// <param name="predicate">The predicate expression representing the condition.</param>
        /// <param name="cache">A value indicating whether to cache the query result.</param>
        /// <param name="expiration">The expiration time for the cached result.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task{T}"/> representing the asynchronous operation that returns the first element of the query that satisfies the condition.</returns>
        public ValueTask<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            var query = _query.Where(predicate);
            return ExecuteAsync((query, ct) => query.FirstOrDefaultAsync(ct), query, cache, expiration, cancellationToken);
        }
        /// <summary>
        /// Retrieves the first element of the query asynchronously.
        /// </summary>
        /// <param name="expiration">The expiration time for the cached result.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task{T}"/> representing the asynchronous operation that returns the first element of the query.</returns>
        public readonly ValueTask<T> FirstAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync((query, ct) => query.FirstAsync(ct), _query, expiration, cancellationToken);
        }
        /// <summary>
        /// Checks if the query has any elements asynchronously.
        /// </summary>
        /// <param name="expiration">The expiration time for the cache entry.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the query has any elements; otherwise, <see langword="false"/>.</returns>
        public readonly ValueTask<bool> AnyAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync((query, ct) => query.AnyAsync(ct), _query, expiration, cancellationToken);
        }
        /// <summary>
        /// Checks if the query has any elements asynchronously.
        /// </summary>
        /// <param name="cache">A value indicating whether to cache the query result.</param>
        /// <param name="expiration">The expiration time for the cache entry.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the query has any elements; otherwise, <see langword="false"/>.</returns>
        public readonly ValueTask<bool> AnyAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync((query, ct) => query.AnyAsync(ct), _query, cache, expiration, cancellationToken);
        }
        /// <summary>
        /// Checks if the query has any elements that satisfy the specified condition asynchronously.
        /// </summary>
        /// <param name="predicate">The predicate expression representing the condition.</param>
        /// <param name="expiration">The expiration time for the cache entry.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the query has any elements that satisfy the condition; otherwise, <see langword="false"/>.</returns>
        public ValueTask<bool> AnyAsync(Expression<Func<T, bool>> predicate, TimeSpan expiration, CancellationToken cancellationToken)
        {
            var query = _query.Where(predicate);
            return ExecuteAsync((query, ct) => query.AnyAsync(ct), query, expiration, cancellationToken);
        }
        /// <summary>
        /// Checks if the query has any elements that satisfy the specified condition asynchronously.
        /// </summary>
        /// <param name="predicate">The predicate expression representing the condition.</param>
        /// <param name="cache">A value indicating whether to cache the query result.</param>
        /// <param name="expiration">The expiration time for the cache entry.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the query has any elements that satisfy the condition; otherwise, <see langword="false"/>.</returns>
        public ValueTask<bool> AnyAsync(Expression<Func<T, bool>> predicate, bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            var query = _query.Where(predicate);
            return ExecuteAsync((query, ct) => query.AnyAsync(ct), query, cache, expiration, cancellationToken);
        }

#pragma warning disable HLQ006 // GetEnumerator() or GetAsyncEnumerator() should return a value type
        /// <inheritdoc/>
        public readonly IEnumerator<T> GetEnumerator()
#pragma warning restore HLQ006 // GetEnumerator() or GetAsyncEnumerator() should return a value type
        {
            return _query.GetEnumerator();
        }

        readonly IEnumerator IEnumerable.GetEnumerator()
        {
            return _query.GetEnumerator();
        }

        /// <inheritdoc/>
        public override readonly string ToString()
        {
            return _query.ToString() ?? string.Empty;
        }
    }
}