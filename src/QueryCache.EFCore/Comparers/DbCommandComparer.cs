using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
namespace QueryCache.EFCore.Comparers
{
    internal sealed class DbCommandComparer : IEqualityComparer<DbCommand>
    {
        public static readonly DbCommandComparer Instance = new();
        public bool Equals(DbCommand? x, DbCommand? y)
        {
            if (x is null && y is null)
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }
            var dbCommand1 = x;
            var dbCommand2 = y;
            if (!string.Equals(dbCommand1.CommandText, dbCommand2.CommandText, StringComparison.Ordinal) || dbCommand1.Parameters.Count != dbCommand2.Parameters.Count)
            {
                return false;
            }
            for (int i = 0; i < dbCommand1.Parameters.Count; i++)
            {
                var parameter1 = dbCommand1.Parameters[i];
                var parameter2 = dbCommand2.Parameters[i];
                if (!string.Equals(parameter1.ParameterName, parameter2.ParameterName, StringComparison.Ordinal) || !Equals(parameter1.Value, parameter2.Value))
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode([DisallowNull] DbCommand obj)
        {
            HashCode hash = new();
            hash.Add(obj.CommandText, StringComparer.Ordinal);
            foreach (DbParameter parameter in obj.Parameters)
            {
                hash.Add(parameter.ParameterName, StringComparer.Ordinal);
                hash.Add(parameter.Value);
            }
            return hash.ToHashCode();
        }
    }
}