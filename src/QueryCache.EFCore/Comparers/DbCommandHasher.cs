using System.Collections;
using System.Data.Common;

namespace QueryCache.EFCore.Comparers
{
    internal static class DbCommandHasher
    {
        public static int Hash(DbCommand command)
        {
            HashCode hash = new();
            hash.Add(command.CommandText, StringComparer.Ordinal);
            foreach (DbParameter parameter in command.Parameters)
            {
                hash.Add(parameter.ParameterName, StringComparer.Ordinal);
                // Arrays (byte[], Npgsql array parameters) would otherwise hash by reference and never hit.
                if (parameter.Value is IEnumerable items and not string)
                {
                    foreach (var item in items)
                    {
                        hash.Add(item);
                    }
                    continue;
                }
                hash.Add(parameter.Value);
            }
            return hash.ToHashCode();
        }
    }
}
