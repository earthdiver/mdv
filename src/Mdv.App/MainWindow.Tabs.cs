using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mdv.Core;

namespace Mdv.App;

public partial class MainWindow
{
    private bool _updatingTabControls;

    private void SyncTabSelection()
    {
        var updating = _updatingTabControls;
        _updatingTabControls = true;
        DocumentTabs.SelectedItem = _workspace.Active;
        DocumentTabs.ScrollIntoView(_workspace.Active);
        _updatingTabControls = updating;
    }

    private async Task<ViewPosition?> SaveActiveTabAsync()
    {
        await WaitForRenderAsync();
        var view = await CaptureViewAsync();
        if (_closed) return null;
        if (view != null) _history.SaveView(view);
        _workspace.Active.SearchQuery = FindInput.Text;
        _workspace.Active.ShowSearch = FindBar.Visibility == Visibility.Visible;
        return view;
    }

    private void DisplayActiveTab()
    {
        var tab = _workspace.Active;
        _updatingTabControls = true;
        try
        {
            _settings.Mode = tab.Mode;
            _settings.Zoom = tab.Zoom;
            GithubMode.IsChecked = tab.Mode == "github";
            QiitaMode.IsChecked = tab.Mode == "qiita";
            if (_ready) Preview.ZoomFactor = tab.Zoom;
            UpdateZoomLabel();
            FindInput.Text = tab.SearchQuery;
            FindBar.Visibility = tab.ShowSearch ? Visibility.Visible : Visibility.Collapsed;
            FindCount.Text = "0 / 0";
            Title = $"{tab.Title} — MDV";
            FileTitle.Text = tab.Title;
            FileSubtitle.Text = tab.FilePath;
            FileSubtitle.ToolTip = tab.FilePath;
            SourceText.Text = _document?.Text ?? "";
            SourceText.ScrollToHome();
            FileStats.Text = _document is { } document
                ? $"{document.EncodingName}  ·  {document.LineCount:N0} 行  ·  {document.Text.Length:N0} 文字" : "";
            EmptyPanel.Visibility = _document == null ? Visibility.Visible : Visibility.Collapsed;
            SyncTabSelection();
        }
        finally { _updatingTabControls = false; }
        ApplyTheme();
        _reloadTimer.Stop();
        _reloadPending = false;
        WatchDocument(_document?.FilePath);
    }

    // One shared renderer keeps the portable app lightweight. Inactive tabs retain
    // their text and reading state; files are refreshed when the tab becomes active.
    private async Task<bool> ActivateTabCoreAsync(DocumentTab tab, string? anchor = null, bool saveCurrent = true)
    {
        if (!_workspace.Tabs.Contains(tab)) return false;
        var document = tab.Document;
        string? refreshError = null;
        if (document != null)
        {
            try { document = await ReadDocumentAsync(document.FilePath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            { refreshError = error.Message; }
        }
        if (saveCurrent) await SaveActiveTabAsync();
        if (_closed) return false;
        _workspace.Select(tab);
        if (document != null) tab.SetDocument(document);
        if (anchor != null && document != null) tab.History.Visit(document.FilePath, forceNew: true);
        _pendingView = anchor == null ? tab.History.Current?.View : null;
        _pendingAnchor = anchor;
        DisplayActiveTab();
        Render(true);
        await WaitForRenderAsync();
        if (refreshError != null) StatusText.Text = "前回読み込んだ内容を表示中（ファイルを再読み込みできません）: " + refreshError;
        return true;
    }

    internal Task<bool> SelectTabAsync(DocumentTab tab) => RunNavigationAsync(async () =>
        ReferenceEquals(tab, _workspace.Active) || await ActivateTabCoreAsync(tab));

    internal Task<bool> NewTabAsync() => RunNavigationAsync(async () =>
    {
        await SaveActiveTabAsync();
        if (_closed) return false;
        _workspace.Add();
        _pendingView = null;
        _pendingAnchor = null;
        DisplayActiveTab();
        Render(true);
        await WaitForRenderAsync();
        return true;
    });

    internal Task<bool> CloseTabAsync(DocumentTab tab) => RunNavigationAsync(async () =>
    {
        if (!_workspace.Tabs.Contains(tab)) return false;
        if (_workspace.Tabs.Count == 1 && tab.Document == null) return true;
        var active = ReferenceEquals(tab, _workspace.Active);
        // Ignore collection selection events until the new active tab is displayed.
        _updatingTabControls = true;
        try { _workspace.Close(tab); }
        finally { _updatingTabControls = false; }
        if (active) return await ActivateTabCoreAsync(_workspace.Active, saveCurrent: false);
        SyncTabSelection();
        return true;
    });

    private Task<bool> CycleTabAsync(int direction) => RunNavigationAsync(async () =>
    {
        if (_workspace.Tabs.Count == 1) return false;
        var index = _workspace.Tabs.IndexOf(_workspace.Active);
        var tab = _workspace.Tabs[(index + direction + _workspace.Tabs.Count) % _workspace.Tabs.Count];
        return await ActivateTabCoreAsync(tab);
    });

    internal async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_closed) return;
            await OpenDocumentAsync(path, newTab: true);
        }
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTabControls || DocumentTabs.SelectedItem is not DocumentTab tab) return;
        await SelectTabAsync(tab);
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) => await NewTabAsync();
    private async void CloseCurrentTab_Click(object sender, RoutedEventArgs e) => await CloseTabAsync(_workspace.Active);
    private async void NextTab_Click(object sender, RoutedEventArgs e) => await CycleTabAsync(1);
    private async void PreviousTab_Click(object sender, RoutedEventArgs e) => await CycleTabAsync(-1);
    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: DocumentTab tab }) await CloseTabAsync(tab);
    }

    private async void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { DataContext: DocumentTab tab }) return;
        e.Handled = true;
        await CloseTabAsync(tab);
    }
}
