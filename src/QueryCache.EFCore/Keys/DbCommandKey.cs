using System.Data.Common;

namespace QueryCache.EFCore.Keys
{
    internal static class DbCommandKey
    {
        public static QueryKey Of(DbCommand command)
        {
            var values = new List<object?>(command.Parameters.Count * 2);
            foreach (DbParameter parameter in command.Parameters)
            {
                values.Add(parameter.ParameterName);
                values.Add(parameter.Value);
            }
            return new QueryKey($"{ConnectionScope.Of(command.Connection)}\n{command.CommandText}", values);
        }
    }
}
