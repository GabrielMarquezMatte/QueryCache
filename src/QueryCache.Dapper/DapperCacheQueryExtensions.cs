using System.Data.Common;
using Dapper;

namespace QueryCache.Dapper
{
    /// <summary>Entry points for building cached Dapper queries.</summary>
    public static class DapperCacheQueryExtensions
    {
        /// <summary>Wraps <paramref name="command"/> in a cached query: <c>connection.ToCacheQuery&lt;User&gt;(command).QueryAsync(expiration, ct)</c>.</summary>
        /// <typeparam name="T">Row (or scalar) type.</typeparam>
        /// <param name="connection">Connection used to run the command on a cache miss.</param>
        /// <param name="command">The command to run.</param>
        /// <returns>A <see cref="DapperCacheQuery{T}"/> for <paramref name="command"/>.</returns>
        public static DapperCacheQuery<T> ToCacheQuery<T>(this DbConnection connection, CommandDefinition command) where T : notnull
        {
            return new(connection, command);
        }
    }
}
