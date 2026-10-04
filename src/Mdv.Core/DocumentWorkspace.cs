using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Mdv.Core;

public sealed class DocumentTab(string mode, double zoom) : INotifyPropertyChanged
{
    public MarkdownDocument? Document { get; private set; }
    public DocumentHistory History { get; } = new();
    public string Mode { get; set; } = mode;
    public double Zoom { get; set; } = zoom;
    public string SearchQuery { get; set; } = "";
    public bool ShowSearch { get; set; }
    public string Title => Document?.FileName ?? "新しいタブ";
    public string FilePath => Document?.FilePath ?? "Markdownファイルを開くか、ここにドロップしてください";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetDocument(MarkdownDocument document)
    {
        Document = document;
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(FilePath)));
    }
}

/// <summary>Open documents and their independent session histories. Always retains one tab.</summary>
public sealed class DocumentWorkspace
{
    private readonly ObservableCollection<DocumentTab> _tabs = [];
    public ReadOnlyObservableCollection<DocumentTab> Tabs { get; }
    public DocumentTab Active { get; private set; }

    public DocumentWorkspace(string mode, double zoom)
    {
        Tabs = new(_tabs);
        Active = new(mode, zoom);
        _tabs.Add(Active);
    }

    public DocumentTab Add()
    {
        Active = new(Active.Mode, Active.Zoom);
        _tabs.Add(Active);
        return Active;
    }

    public bool Select(DocumentTab tab)
    {
        if (!_tabs.Contains(tab)) return false;
        Active = tab;
        return true;
    }

    public DocumentTab? Find(string path)
    {
        path = Path.GetFullPath(path);
        bool Matches(DocumentTab tab) => string.Equals(tab.Document?.FilePath, path, StringComparison.OrdinalIgnoreCase);
        return Matches(Active) ? Active : _tabs.FirstOrDefault(Matches);
    }

    public bool Close(DocumentTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return false;
        _tabs.RemoveAt(index);
        if (_tabs.Count == 0) Add();
        else if (ReferenceEquals(Active, tab)) Active = _tabs[Math.Min(index, _tabs.Count - 1)];
        return true;
    }
}
