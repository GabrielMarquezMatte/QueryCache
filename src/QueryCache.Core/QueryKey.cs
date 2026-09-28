using System.Collections;

namespace QueryCache;

/// <summary>
/// Exact cache key: a text (SQL plus connection scope) and the parameter values, compared by value.
/// Sequences (arrays, lists; not strings) are compared item by item.
/// </summary>
public sealed class QueryKey : IEquatable<QueryKey>
{
    private readonly string _text;
    private readonly object?[] _values;
    private readonly int _hashCode;

    /// <summary>Creates a key.</summary>
    /// <param name="text">The query text, including anything that scopes it (such as the database).</param>
    /// <param name="values">Parameter names and values, in a stable order.</param>
    public QueryKey(string text, params IEnumerable<object?> values)
    {
        _text = text;
        var flat = new List<object?>();
        foreach (var value in values)
        {
            Flatten(value, flat);
        }
        _values = [.. flat];
        var hash = new HashCode();
        hash.Add(text, StringComparer.Ordinal);
        foreach (var value in _values)
        {
            hash.Add(value);
        }
        _hashCode = hash.ToHashCode();
    }

    private static void Flatten(object? value, List<object?> into)
    {
        if (value is not IEnumerable items || value is string)
        {
            into.Add(value);
            return;
        }
        var marker = into.Count;
        into.Add(null);
        var count = 0;
        foreach (var item in items)
        {
            Flatten(item, into);
            count++;
        }
        into[marker] = new Sequence(count);
    }

    private sealed record Sequence(int Count);

    /// <inheritdoc/>
    public bool Equals(QueryKey? other)
    {
        return other is not null
            && _hashCode == other._hashCode
            && string.Equals(_text, other._text, StringComparison.Ordinal)
            && _values.AsSpan().SequenceEqual(other._values);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is QueryKey other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return _hashCode;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return _text;
    }
}
