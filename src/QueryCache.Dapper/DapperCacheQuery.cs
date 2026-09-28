using System.Data.Common;
using Dapper;

namespace QueryCache.Dapper
{
    /// <summary>A Dapper command whose results are cached in-process, keyed by connection string, SQL text and parameter values.</summary>
    /// <remarks>Hit/miss metrics are published by <see cref="QueryCacheStore"/> on the <see cref="QueryCacheStore.MeterName"/> meter.</remarks>
    /// <typeparam name="T">Row (or scalar) type.</typeparam>
    /// <param name="connection">Connection used to run the command on a cache miss.</param>
    /// <param name="command">The command to run.</param>
    public sealed class DapperCacheQuery<T>(DbConnection connection, CommandDefinition command) : IEquatable<DapperCacheQuery<T>> where T : notnull
    {
        private CommandDefinition WithToken(CommandFlags flags, CancellationToken cancellationToken)
        {
            return new(command.CommandText, command.Parameters, command.Transaction, command.CommandTimeout, command.CommandType, flags,
                       cancellationToken.CanBeCanceled ? cancellationToken : command.CancellationToken);
        }

        private ValueTask<TReturn> ExecuteAsync<TReturn>(Func<DbConnection, CommandDefinition, Task<TReturn>> action, CommandFlags flags,
                                                         TimeSpan expiration, CancellationToken cancellationToken)
        {
            return QueryCacheStore.GetOrAddAsync(GetHashCode(), expiration, ct => action(connection, WithToken(flags, ct)), cancellationToken);
        }

        /// <summary>
        /// Executes the query and returns results from cache, or fetches from the database and caches them.
        /// </summary>
        public ValueTask<IEnumerable<T>> QueryAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync(static (conn, cmd) => conn.QueryAsync<T>(cmd), command.Flags | CommandFlags.Buffered, expiration, cancellationToken);
        }

        /// <summary>
        /// Executes the query, optionally using cache.
        /// </summary>
        public ValueTask<IEnumerable<T>> QueryAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            if (!cache)
            {
                return new(connection.QueryAsync<T>(WithToken(command.Flags, cancellationToken)));
            }
            return QueryAsync(expiration, cancellationToken);
        }

        /// <summary>
        /// Executes the query and returns the first result from cache, or fetches from the database and caches it.
        /// Returns <see langword="null"/> (or <see langword="default"/> for value types) when no rows match.
        /// </summary>
        public ValueTask<T?> QueryFirstOrDefaultAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync(static (conn, cmd) => conn.QueryFirstOrDefaultAsync<T>(cmd), command.Flags, expiration, cancellationToken);
        }

        /// <summary>
        /// Executes the query, optionally using cache, and returns the first result or default.
        /// </summary>
        public ValueTask<T?> QueryFirstOrDefaultAsync(bool cache, TimeSpan expiration, CancellationToken cancellationToken)
        {
            if (!cache)
            {
                return new(connection.QueryFirstOrDefaultAsync<T>(WithToken(command.Flags, cancellationToken)));
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
            return ExecuteAsync(static (conn, cmd) => conn.ExecuteScalarAsync<T?>(cmd), command.Flags, expiration, cancellationToken);
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
                return new(connection.ExecuteScalarAsync<T?>(WithToken(command.Flags, cancellationToken)));
            }
            return ExecuteScalarAsync(expiration, cancellationToken);
        }

        /// <summary>
        /// Removes the cached result for the given operation.
        /// </summary>
        /// <param name="operation">
        /// The operation whose cache entry should be invalidated.
        /// Use <c>nameof(QueryAsync)</c>, <c>nameof(QueryFirstOrDefaultAsync)</c> or <c>nameof(ExecuteScalarAsync)</c>
        /// (the last two share one entry).
        /// </param>
        /// <returns><see langword="true"/> if the entry was found and removed; otherwise, <see langword="false"/>.</returns>
        public bool RemoveFromCache(string operation)
        {
            return operation switch
            {
                nameof(QueryAsync) => QueryCacheStore.Remove<IEnumerable<T>>(GetHashCode()),
                nameof(QueryFirstOrDefaultAsync) or nameof(ExecuteScalarAsync) => QueryCacheStore.Remove<T>(GetHashCode()),
                _ => throw new InvalidOperationException($"The operation '{operation}' is not supported. Use nameof({nameof(QueryAsync)}), nameof({nameof(QueryFirstOrDefaultAsync)}) or nameof({nameof(ExecuteScalarAsync)})."),
            };
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
        public static bool operator ==(DapperCacheQuery<T>? left, DapperCacheQuery<T>? right)
        {
            return left?.Equals(right) ?? right is null;
        }

        /// <summary>Inequality by cache key.</summary>
        public static bool operator !=(DapperCacheQuery<T>? left, DapperCacheQuery<T>? right)
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
