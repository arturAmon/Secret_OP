using System.Collections;

namespace LeagueOverlay.Core.Analysis;

/// <summary>Read-only list with value equality, so records holding lists compare by content.</summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    public static readonly ValueList<T> Empty = new(Array.Empty<T>());
    private readonly T[] _items;

    public ValueList(IEnumerable<T> items) => _items = items.ToArray();

    public T this[int index] => _items[index];
    public int Count => _items.Length;
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && _items.SequenceEqual(other._items);
    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
}
