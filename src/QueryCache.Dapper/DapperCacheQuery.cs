using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;

namespace QueryCache.Dapper
{
    /// <summary>A Dapper command whose results are cached in-process, keyed by connection string, SQL text and parameter values.</summary>
    /// <typeparam name="T">Row (or scalar) type.</typeparam>
    /// <param name="connection">Connection used to run the command on a cache miss.</param>
    /// <param name="command">The command to run.</param>
    /// <param name="logger">Logger for hit/miss/timing events.</param>
    public sealed partial class DapperCacheQuery<T>(DbConnection connection, CommandDefinition command, ILogger<DapperCacheQuery<T>> logger) : IEquatable<DapperCacheQuery<T>> where T : notnull
    {
        [LoggerMessage(0, LogLevel.Information, "Cache miss")]
        private partial void LogCacheMiss();
        [LoggerMessage(1, LogLevel.Information, "Cache hit. Elapsed: {Elapsed}")]
        private partial void LogCacheHit(TimeSpan elapsed);
        [LoggerMessage(2, LogLevel.Information, "Executed query in {Elapsed}")]
        private partial void LogExecutedQuery(TimeSpan elapsed);
        private async ValueTask<TReturn> ExecuteAsync<TReturn>(Func<DbConnection, CommandDefinition, Task<TReturn>> action,
                                                               TimeSpan expiration, CancellationToken cancellationToken)
        {
            var hash = GetHashCode();
            KeyValuePair<string, object?>[] scopeParams = [
                new("Hash", hash),
                new("CommandText", command.CommandText),
                new("@Parameters", command.Parameters),
            ];
            using var _1 = logger.BeginScope(scopeParams);
            var start = Stopwatch.GetTimestamp();
            if (QueryCacheStore.TryGet<TReturn>(hash, out var cached))
            {
                LogCacheHit(Stopwatch.GetElapsedTime(start));
                return cached;
            }
            LogCacheMiss();
            return await QueryCacheStore.GetOrAddAsync(hash, expiration, async _ =>
            {
                var result = await action(connection, command).ConfigureAwait(false);
                LogExecutedQuery(Stopwatch.GetElapsedTime(start));
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes the query and returns results from cache, or fetches from the database and caches them.
        /// </summary>
        public ValueTask<IEnumerable<T>> QueryAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync(static (conn, cmd) => conn.QueryAsync<T>(cmd), expiration, cancellationToken);
        }

        /// <summary>
        /// Executes the query, optionally using cache.
        /// </summary>
        public ValueTask<IEnumerable<T>> QueryAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            if (!cache)
            {
                return new(connection.QueryAsync<T>(command));
            }
            return QueryAsync(expiration, cancellationToken);
        }

        /// <summary>
        /// Executes the query and returns the first result from cache, or fetches from the database and caches it.
        /// Returns <see langword="null"/> (or <see langword="default"/> for value types) when no rows match.
        /// </summary>
        public ValueTask<T?> QueryFirstOrDefaultAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync(static (conn, cmd) => conn.QueryFirstOrDefaultAsync<T>(cmd), expiration, cancellationToken);
        }

        /// <summary>
        /// Executes the query, optionally using cache, and returns the first result or default.
        /// </summary>
        public ValueTask<T?> QueryFirstOrDefaultAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            if (!cache)
            {
                return new(connection.QueryFirstOrDefaultAsync<T>(command));
            }
            return QueryFirstOrDefaultAsync(expiration, cancellationToken);
        }
        /// <summary>
        /// Executes the query and returns a single value from cache, or fetches from the database and caches it. Returns <see langword="null"/> (or <see langword="default"/> for value types) when no rows match.
        /// </summary>
        /// <param name="expiration">The duration for which the result should be cached. After this time, the cache entry will expire and be removed on the next access.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the value returned by the query, or <see langword="null"/> (or <see langword="default"/> for value types) if no rows match.</returns>
        public ValueTask<T?> ExecuteScalarAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync(static (conn, cmd) => conn.ExecuteScalarAsync<T?>(cmd), expiration, cancellationToken);
        }

        /// <summary>
        /// Executes the query, optionally using cache, and returns a single value. Returns <see langword="null"/> (or <see langword="default"/> for value types) when no rows match.
        /// </summary>
        /// <param name="cache">Whether to use cache or not. If <see langword="false"/>, the query will be executed against the database without caching the result.</param>
        /// <param name="expiration">The duration for which the result should be cached if caching is enabled. Ignored if <paramref name="cache"/> is <see langword="false"/>.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the value returned by the query, or <see langword="null"/> (or <see langword="default"/> for value types) if no rows match.</returns>
        public ValueTask<T?> ExecuteScalarAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            if (!cache)
            {
                return new(connection.ExecuteScalarAsync<T?>(command));
            }
            return ExecuteScalarAsync(expiration, cancellationToken);
        }

        /// <summary>
        /// Removes the cached result for the given operation.
        /// </summary>
        /// <param name="operation">
        /// The operation whose cache entry should be invalidated.
        /// Use <c>nameof(QueryAsync)</c> or <c>nameof(QueryFirstOrDefaultAsync)</c>.
        /// </param>
        /// <returns><see langword="true"/> if the entry was found and removed; otherwise, <see langword="false"/>.</returns>
        public bool RemoveFromCache(string operation)
        {
            var hash = GetHashCode();
            if (string.Equals(operation, nameof(QueryAsync), StringComparison.Ordinal))
            {
                return QueryCacheStore.Remove<IEnumerable<T>>(hash);
            }
            if (string.Equals(operation, nameof(QueryFirstOrDefaultAsync), StringComparison.Ordinal))
            {
                return QueryCacheStore.Remove<T>(hash);
            }
            throw new InvalidOperationException($"The operation '{operation}' is not supported. Use nameof({nameof(QueryAsync)}) or nameof({nameof(QueryFirstOrDefaultAsync)}).");
        }
        /// <inheritdoc/>
        public bool Equals(DapperCacheQuery<T>? other)
        {
            return other is not null && GetHashCode() == other.GetHashCode();
        }
        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is DapperCacheQuery<T> other && Equals(other);
        }
        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return HashCode.Combine(connection.ConnectionString, command.CommandText, ParameterHasher.Hash(command.Parameters));
        }

        /// <summary>Equality by cache key.</summary>
        public static bool operator ==(DapperCacheQuery<T> left, DapperCacheQuery<T> right)
        {
            return left.Equals(right);
        }

        /// <summary>Inequality by cache key.</summary>
        public static bool operator !=(DapperCacheQuery<T> left, DapperCacheQuery<T> right)
        {
            return !(left == right);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"DapperCacheQuery: {command.CommandText} with parameters {command.Parameters}";
        }
    }
}
