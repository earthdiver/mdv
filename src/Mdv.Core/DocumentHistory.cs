namespace Mdv.Core;

public sealed record ViewPosition(double X = 0, double Y = 0, string? Anchor = null, double Offset = 0,
    double Ratio = 0, int[]? OpenDetails = null, double SourceX = 0, double SourceY = 0);

public sealed record HistoryEntry(string FilePath, ViewPosition? View = null);

/// <summary>Session-only history. Commit a visit/move only after its file was read successfully.</summary>
public sealed class DocumentHistory(int capacity = 100)
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<HistoryEntry> _entries = [];
    private int _index = -1;

    public HistoryEntry? Current => _index >= 0 ? _entries[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index + 1 < _entries.Count;

    public HistoryEntry? Peek(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var target = _index + direction;
        return _index >= 0 && target >= 0 && target < _entries.Count ? _entries[target] : null;
    }

    public void SaveView(ViewPosition view)
    {
        if (Current is { } current) _entries[_index] = current with { View = view };
    }

    public void Visit(string path, bool forceNew = false)
    {
        path = Path.GetFullPath(path);
        if (!forceNew && string.Equals(Current?.FilePath, path, StringComparison.OrdinalIgnoreCase)) return;
        if (_index + 1 < _entries.Count) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(new HistoryEntry(path));
        if (_entries.Count > _capacity) _entries.RemoveAt(0);
        _index = _entries.Count - 1;
    }

    public HistoryEntry? Move(int direction)
    {
        var target = Peek(direction);
        if (target != null) _index += direction;
        return target;
    }
}
