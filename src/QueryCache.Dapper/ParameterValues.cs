using System.Collections.Concurrent;
using System.Reflection;
using Dapper;

namespace QueryCache.Dapper;

/// <summary>Lists Dapper parameter names and values (not references) for the cache key.</summary>
internal static class ParameterValues
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    // ponytail: Dapper keeps constructor/AddDynamicParams objects in a private list that ParameterNames does not expose.
    private static readonly FieldInfo Templates = typeof(DynamicParameters).GetField("templates", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(DynamicParameters), "templates");

    public static List<object?> Of(object? parameters)
    {
        var values = new List<object?>();
        switch (parameters)
        {
            case null:
                break;
            case DynamicParameters dynamicParameters:
                foreach (var name in dynamicParameters.ParameterNames.Order(StringComparer.Ordinal))
                {
                    values.Add(name);
                    values.Add(Value(dynamicParameters.Get<object?>(name)));
                }
                if (Templates.GetValue(dynamicParameters) is List<object> templates)
                {
                    foreach (var template in templates)
                    {
                        values.Add(Of(template));
                    }
                }
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var pair in pairs.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    values.Add(pair.Key);
                    values.Add(Value(pair.Value));
                }
                break;
            default:
                foreach (var property in Properties.GetOrAdd(parameters.GetType(), static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
                {
                    values.Add(property.Name);
                    values.Add(Value(property.GetValue(parameters)));
                }
                break;
        }
        return values;
    }

    private static object? Value(object? value)
    {
        return value is DbString dbString ? dbString.Value : value;
    }
}
