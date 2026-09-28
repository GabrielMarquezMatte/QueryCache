using System.Data.Common;
using System.Transactions;
using Dapper;

namespace QueryCache.Dapper
{
    /// <summary>A Dapper command whose results are cached (see <see cref="QueryCacheStore.HybridCache"/>), keyed by connection (without password), SQL text and parameter values.</summary>
    /// <remarks>
    /// Commands with a transaction, or run inside a <see cref="TransactionScope"/>, skip the cache.
    /// Hit/miss metrics are published by <see cref="QueryCacheStore"/> on the <see cref="QueryCacheStore.MeterName"/> meter.
    /// </remarks>
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

        private QueryKey Key => new($"{ConnectionScope.Of(connection)}\n{command.CommandText}", ParameterValues.Of(command.Parameters));

        private ValueTask<TReturn> ExecuteAsync<TReturn>(Func<DbConnection, CommandDefinition, Task<TReturn>> action, CommandFlags flags,
                                                         TimeSpan expiration, CancellationToken cancellationToken)
        {
            if (command.Transaction is not null || Transaction.Current is not null)
            {
                return new(action(connection, WithToken(flags, cancellationToken)));
            }
            return QueryCacheStore.GetOrAddAsync(Key, expiration, ct => action(connection, WithToken(flags, ct)), cancellationToken);
        }

        /// <summary>
        /// Executes the query and returns results from cache, or fetches from the database and caches them.
        /// The list is read-only because the same instance is handed to every caller.
        /// </summary>
        public ValueTask<IReadOnlyList<T>> QueryAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync(static async (conn, cmd) => (IReadOnlyList<T>)(await conn.QueryAsync<T>(cmd).ConfigureAwait(false)).AsList().AsReadOnly(),
                                command.Flags | CommandFlags.Buffered, expiration, cancellationToken);
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
        /// Executes the query and returns a single value from cache, or fetches from the database and caches it. Returns <see langword="null"/> (or <see langword="default"/> for value types) when no rows match.
        /// </summary>
        /// <param name="expiration">The duration for which the result should be cached. After this time, the cache entry will expire and be removed on the next access.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the value returned by the query, or <see langword="null"/> (or <see langword="default"/> for value types) if no rows match.</returns>
        public ValueTask<T?> ExecuteScalarAsync(TimeSpan expiration, CancellationToken cancellationToken)
        {
            return ExecuteAsync(static (conn, cmd) => conn.ExecuteScalarAsync<T?>(cmd), command.Flags, expiration, cancellationToken);
        }

        /// <summary>Removes every cached result of this command (<see cref="QueryAsync(TimeSpan, CancellationToken)"/>,
        /// <see cref="QueryFirstOrDefaultAsync(TimeSpan, CancellationToken)"/> and <see cref="ExecuteScalarAsync(TimeSpan, CancellationToken)"/>).</summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async ValueTask InvalidateCacheAsync(CancellationToken cancellationToken)
        {
            var key = Key;
            await QueryCacheStore.RemoveAsync<IReadOnlyList<T>>(key, cancellationToken).ConfigureAwait(false);
            await QueryCacheStore.RemoveAsync<T>(key, cancellationToken).ConfigureAwait(false);
        }
        /// <inheritdoc/>
        public bool Equals(DapperCacheQuery<T>? other)
        {
            return other is not null && Key.Equals(other.Key);
        }
        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is DapperCacheQuery<T> other && Equals(other);
        }
        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return Key.GetHashCode();
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
