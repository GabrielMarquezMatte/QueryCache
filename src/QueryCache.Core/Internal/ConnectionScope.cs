using System.Collections.Concurrent;
using System.Data.Common;

namespace QueryCache;

internal static class ConnectionScope
{
    private static readonly ConcurrentDictionary<string, string> Normalized = new(StringComparer.Ordinal);

    public static string Of(DbConnection? connection)
    {
        return Of(connection?.ConnectionString);
    }

    public static string Of(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return string.Empty;
        }
        return Normalized.GetOrAdd(connectionString, static value =>
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = value };
            builder.Remove("password");
            builder.Remove("pwd");
            return builder.ConnectionString;
        });
    }
}
