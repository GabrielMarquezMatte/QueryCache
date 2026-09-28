using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Dapper;

namespace QueryCache.Dapper;

/// <summary>Hashes Dapper parameter objects by value, not by reference.</summary>
internal static class ParameterHasher
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static int Hash(object? parameters)
    {
        var hash = new HashCode();
        switch (parameters)
        {
            case null:
                break;
            case DynamicParameters dynamicParameters:
                foreach (var name in dynamicParameters.ParameterNames.Order(StringComparer.Ordinal))
                {
                    hash.Add(name, StringComparer.Ordinal);
                    AddValue(ref hash, dynamicParameters.Get<object?>(name));
                }
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var pair in pairs.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    hash.Add(pair.Key, StringComparer.Ordinal);
                    AddValue(ref hash, pair.Value);
                }
                break;
            default:
                foreach (var property in Properties.GetOrAdd(parameters.GetType(), static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
                {
                    hash.Add(property.Name, StringComparer.Ordinal);
                    AddValue(ref hash, property.GetValue(parameters));
                }
                break;
        }
        return hash.ToHashCode();
    }

    private static void AddValue(ref HashCode hash, object? value)
    {
        if (value is IEnumerable items and not string)
        {
            foreach (var item in items)
            {
                hash.Add(item);
            }
            return;
        }
        hash.Add(value);
    }
}
