using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Mdv.Core;

namespace Mdv.App;

public partial class MainWindow : Window
{
    private const string AppOrigin = "https://app.mdv.invalid";
    private readonly PortableEnvironment _portable = new();
    private readonly string _settingsPath = PortablePaths.SettingsPath;
    private readonly UserSettings _settings;
    private static readonly JsonSerializerOptions MessageJson = new(JsonSerializerDefaults.Web);
    private readonly DocumentWorkspace _workspace;
    private DocumentHistory _history => _workspace.Active.History;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string[] _arguments;
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private MarkdownDocument? _document => _workspace.Active.Document;
    private FileSystemWatcher? _watcher;
    private bool _ready;
    private bool _rendering;
    private bool _closed;
    private bool _closing;
    private bool _closeReady;
    private Task? _initialization;
    private bool _openingDocument;
    private bool _reloadPending;
    private TaskCompletionSource<bool>? _renderCompletion;
    private ViewPosition? _pendingView;
    private int _requestId;
    private string? _pendingAnchor;
    private string[] _diagnostics = [];
    internal TaskCompletionSource<bool> FirstRender { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string SettingsPath => _settingsPath;
    internal UserSettings Settings => _settings;
    internal PortableEnvironment Portable => _portable;
    internal bool NavigationReady => !_openingDocument && !_rendering;
    internal DocumentWorkspace Workspace => _workspace;

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        try
        {
            _settings = UserSettings.Load(_settingsPath);
            // Create/validate the portable INI immediately, including on the first launch.
            _settings.Save(_settingsPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("EXEと同じフォルダに設定INIを保存できません。EXEを書き込み可能なフォルダへ移動してください。\n\n" + _settingsPath, error);
        }
        _workspace = new(_settings.Mode, _settings.Zoom);
        InitializeComponent();
        DocumentTabs.ItemsSource = _workspace.Tabs;
        SyncTabSelection();
        GithubMode.IsChecked = _settings.Mode == "github";
        QiitaMode.IsChecked = _settings.Mode == "qiita";
        OutlineMenu.IsChecked = _settings.ShowOutline;
        SourceMenu.IsChecked = _settings.ShowSource;
        DarkMenu.IsChecked = _settings.Theme == "dark";
        RemoteImagesMenu.IsChecked = _settings.AllowRemoteImages;
        ApplyLayout();
        ApplyTheme();
        UpdateRecentMenu();
        UpdateNavigationControls();
        _reloadTimer.Tick += async (_, _) => { _reloadTimer.Stop(); await ReloadAsync(); };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => _initialization = InitializePreviewAsync();

    private async Task InitializePreviewAsync()
    {
        try
        {
            StatusText.Text = "同梱の描画資材を準備しています（初回は展開に時間がかかります）…";
            await _portable.PrepareAsync();
            if (_closed || _closing) return;
            var environment = await CoreWebView2Environment.CreateAsync(_portable.Runtime, _portable.UserData);
            if (_closed) return;
            await Preview.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var core = Preview.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping("app.mdv.invalid", Path.Combine(_portable.Assets, "Renderer"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += WebMessageReceived;
            core.NavigationStarting += (_, args) =>
            {
                if (args.Uri != AppOrigin + "/index.html") args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => { args.Handled = true; };
            core.DownloadStarting += (_, args) => { args.Cancel = true; };
            core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; };
            core.ProcessFailed += (_, args) => ShowPreviewError($"描画プロセスが終了しました ({args.ProcessFailedKind})。MDVを起動し直してください。");
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) ShowPreviewError($"描画資材を読み込めませんでした ({args.WebErrorStatus})。MDVを終了して一時フォルダ内のMDV/payloadを削除し、起動し直してください。");
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += WebResourceRequested;
            Preview.ZoomFactor = _settings.Zoom;
            UpdateZoomLabel();
            var files = _arguments.Contains("--smoke-test") ? [] : _arguments.Where(a => !a.StartsWith('-')).ToArray();
            await OpenDocumentAsync(files.FirstOrDefault() ?? SamplePath, remember: files.Length > 0);
            foreach (var file in files.Skip(1)) await OpenDocumentAsync(file, newTab: true);
            if (_closed) return;
            core.Navigate(AppOrigin + "/index.html");
            if (_arguments.Contains("--smoke-test")) _ = NativeSmoke.RunAsync(this, _arguments);
        }
        catch (Exception error)
        {
            if (_closed) return;
            ShowPreviewError((_portable.Bundled ? "同梱の描画エンジンを起動できませんでした。\n\n" : "開発版の実行にはWebView2 Runtimeが必要です。配布には scripts/build.ps1 で作る単一EXE版を使用してください。\n\n") + error.Message);
            if (_arguments.Contains("--smoke-test")) NativeSmoke.Fail(_arguments, error);
        }
    }

    private string SamplePath => Path.Combine(_portable.Assets, "Samples", "welcome.md");

    internal Task<bool> OpenDocumentAsync(string path, bool remember = true, bool quiet = false, string? anchor = null, bool newTab = false) =>
        NavigateDocumentAsync(path, remember, quiet, anchor, newTab: newTab);

    internal Task<bool> NavigateHistoryAsync(int direction) => _openingDocument || _rendering
        ? Task.FromResult(false) : NavigateDocumentAsync(null, false, true, direction: direction);

    private Task<bool> NavigateDocumentAsync(string? path, bool remember, bool quiet, string? anchor = null,
        int? direction = null, bool reload = false, bool newTab = false) => RunNavigationAsync(async () =>
    {
        if (newTab && path != null && _workspace.Find(path) is { } existing)
        {
            var selected = await ActivateTabCoreAsync(existing, anchor);
            if (selected && remember) { _settings.Remember(existing.Document!.FilePath); UpdateRecentMenu(); }
            return selected;
        }
        var target = direction.HasValue ? _history.Peek(direction.Value) : null;
        if (direction.HasValue && target == null) return false;
        path = target?.FilePath ?? (reload ? _document?.FilePath : path);
        if (path == null) return false;
        var document = await ReadDocumentAsync(path);
        var view = await SaveActiveTabAsync();
        if (_closed) return false;
        if (newTab && _document != null) _workspace.Add();
        var changedFile = !string.Equals(_document?.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase);
        var newVisit = changedFile || anchor != null;
        if (direction.HasValue) _history.Move(direction.Value);
        else if (newVisit) _history.Visit(document.FilePath, forceNew: anchor != null);
        _pendingView = direction.HasValue ? target?.View : newVisit ? null : view;
        _pendingAnchor = anchor;
        _workspace.Active.SetDocument(document);
        if (remember) { _settings.Remember(document.FilePath); UpdateRecentMenu(); }
        DisplayActiveTab();
        Render(newVisit || direction.HasValue);
        await WaitForRenderAsync();
        return true;
    }, quiet);

    private async Task<MarkdownDocument> ReadDocumentAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await DocumentReader.ReadAsync(path, _lifetime.Token); }
            catch (IOException) when (attempt < 2) { await Task.Delay(100, _lifetime.Token); }
        }
    }

    private async Task<bool> RunNavigationAsync(Func<Task<bool>> navigate, bool quiet = true)
    {
        await _navigationGate.WaitAsync();
        try
        {
            if (_closed) return false;
            await WaitForRenderAsync();
            if (_closed) return false;
            _openingDocument = true;
            UpdateNavigationControls();
            return await navigate();
        }
        catch (OperationCanceledException) when (_closed) { return false; }
        catch (Exception error) when (_closed && error is System.Runtime.InteropServices.COMException or ObjectDisposedException or InvalidOperationException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.DecoderFallbackException or NotSupportedException)
        {
            if (_closed) return false;
            StatusText.Text = "読み込み失敗: " + error.Message;
            if (!quiet) MessageBox.Show(this, error.Message, "ファイルを開けません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _openingDocument = false;
            SyncTabSelection();
            UpdateNavigationControls();
            ResumePendingReload();
            _navigationGate.Release();
        }
    }

    private async Task WaitForRenderAsync()
    {
        while (_rendering && _renderCompletion is { } completion)
            await completion.Task.WaitAsync(_lifetime.Token);
    }

    private async Task<ViewPosition?> CaptureViewAsync()
    {
        if (!_ready || _rendering || _document == null) return null;
        var json = await Preview.CoreWebView2.ExecuteScriptAsync("({ requestId: window.mdv.requestId, view: window.mdv.captureView() })");
        using var snapshot = JsonDocument.Parse(json);
        if (snapshot.RootElement.GetProperty("requestId").GetInt32() != _requestId) return null;
        var view = snapshot.RootElement.GetProperty("view").Deserialize<ViewPosition>(MessageJson);
        return view is null ? null : view with { SourceX = SourceText.HorizontalOffset, SourceY = SourceText.VerticalOffset };
    }

    private void UpdateNavigationControls()
    {
        var available = _ready && !_closed && !_openingDocument && !_rendering;
        BackButton.IsEnabled = BackMenu.IsEnabled = available && _history.CanGoBack;
        ForwardButton.IsEnabled = ForwardMenu.IsEnabled = available && _history.CanGoForward;
        DocumentTabs.IsEnabled = NewTabButton.IsEnabled = NewTabMenu.IsEnabled = available;
        CloseTabMenu.IsEnabled = available && (_workspace.Tabs.Count > 1 || _document != null);
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(-1);
    private async void Forward_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(1);

    private void WatchDocument(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (path == null) return;
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += FileChanged;
            _watcher.Created += FileChanged;
            _watcher.Renamed += FileChanged;
            _watcher.Deleted += FileChanged;
            _watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { if (!_closed) StatusText.Text = "更新の監視が中断されました。F5で再読み込みできます。"; });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        { StatusText.Text = "更新の監視を開始できません。F5で再読み込みできます。"; }
    }

    private void FileChanged(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed || !ReferenceEquals(sender, _watcher)) return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    private Task ReloadAsync()
    {
        if (_openingDocument || _rendering) { _reloadPending = true; return Task.CompletedTask; }
        return NavigateDocumentAsync(null, false, true, reload: true);
    }

    private void ResumePendingReload()
    {
        if (_reloadPending && !_openingDocument && !_rendering && !_closed)
        {
            _reloadPending = false;
            _reloadTimer.Start();
        }
    }

    private void Render(bool resetScroll = false)
    {
        if (!_ready || _closed) return;
        _rendering = true;
        _renderCompletion?.TrySetResult(false);
        _renderCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateNavigationControls();
        PrintMenu.IsEnabled = PdfMenu.IsEnabled = false;
        StatusText.Text = "描画中…";
        Send(new { type = "render", requestId = ++_requestId, markdown = _document?.Text ?? "",
            mode = _settings.Mode, theme = _settings.Theme, allowRemoteImages = _settings.AllowRemoteImages, resetScroll,
            view = _pendingView, anchor = _pendingAnchor });
    }

    private void Send(object message)
    {
        if (_ready && !_closed) Preview.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, MessageJson));
    }

    private async void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || e.Source != AppOrigin + "/index.html") return;
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready": _ready = true; Render(true); break;
                case "rendered":
                    if (root.GetProperty("requestId").GetInt32() != _requestId) return;
                    _rendering = false;
                    PrintMenu.IsEnabled = PdfMenu.IsEnabled = _document != null;
                    OutlineList.ItemsSource = root.GetProperty("outline").EnumerateArray().Select(h => new OutlineEntry(
                        h.GetProperty("id").GetString()!, h.GetProperty("text").GetString()!, h.GetProperty("level").GetInt32())).ToList();
                    HeadingCount.Text = $"{OutlineList.Items.Count} 個の見出し";
                    _diagnostics = root.GetProperty("diagnostics").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    DiagnosticsButton.Content = _diagnostics.Length == 0 ? "対応範囲" : $"表示上の注意 {_diagnostics.Length}";
                    StatusText.Text = _document == null ? "ファイルを開くか、ドラッグ＆ドロップしてください" :
                        $"{(_settings.Mode == "qiita" ? "Qiita" : "GitHub")}  ·  {root.GetProperty("duration").GetInt32()} ms  ·  更新を自動反映";
                    if (_pendingView is { } restored)
                    {
                        SourceText.ScrollToHorizontalOffset(restored.SourceX);
                        SourceText.ScrollToVerticalOffset(restored.SourceY);
                    }
                    _pendingView = null;
                    _pendingAnchor = null;
                    if (FindBar.Visibility == Visibility.Visible) Send(new { type = "find", query = FindInput.Text, direction = 0, scroll = false });
                    _renderCompletion?.TrySetResult(true);
                    UpdateNavigationControls();
                    ResumePendingReload();
                    FirstRender.TrySetResult(true);
                    break;
                case "error":
                    if (root.GetProperty("requestId").GetInt32() != _requestId) return;
                    _rendering = false;
                    _renderCompletion?.TrySetResult(false);
                    _pendingView = null;
                    _pendingAnchor = null;
                    UpdateNavigationControls();
                    StatusText.Text = "描画エラー: " + root.GetProperty("message").GetString();
                    FirstRender.TrySetException(new InvalidOperationException(StatusText.Text));
                    break;
                case "findResult": FindCount.Text = $"{root.GetProperty("index")} / {root.GetProperty("total")}"; break;
                case "copy":
                    var text = root.GetProperty("text").GetString();
                    if (text is { Length: > 0 and <= DocumentReader.MaxCharacters }) Clipboard.SetText(text);
                    break;
                case "openLink":
                    if (_openingDocument || _rendering || root.GetProperty("requestId").GetInt32() != _requestId) break;
                    await OpenLinkAsync(root.GetProperty("href").GetString() ?? "", root.GetProperty("newTab").GetBoolean());
                    break;
                case "shortcut": Shortcut(root.GetProperty("key").GetString() ?? ""); break;
                case "drop":
                    var files = e.AdditionalObjects.OfType<CoreWebView2File>().Select(file => file.Path).ToArray();
                    await OpenFilesAsync(files);
                    break;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { StatusText.Text = "操作を完了できませんでした: " + error.Message; }
    }

    private async void WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (uri.Host == "app.mdv.invalid" && uri.Scheme == "https") return;
        var core = Preview.CoreWebView2;
        if (uri.Host == "document.mdv.invalid" && uri.Scheme == "https" && uri.AbsolutePath.StartsWith("/image/") &&
            e.ResourceContext == CoreWebView2WebResourceContext.Image && _document != null)
        {
            var deferral = e.GetDeferral();
            try
            {
                var relative = Uri.UnescapeDataString(uri.AbsolutePath[7..]);
                var path = LocalAssets.ResolvePath(Path.GetDirectoryName(_document.FilePath)!, relative);
                if (path != null && new FileInfo(path).Length <= 32 * 1024 * 1024)
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                        $"Content-Type: {LocalAssets.ContentType(path)}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'");
                    return;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            finally
            {
                try
                {
                    if (!_closed) e.Response ??= core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
                    deferral.Complete();
                }
                catch (Exception error) when (_closed && error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            }
            return;
        }
        else if (_settings.AllowRemoteImages && uri.Scheme is "https" or "http" &&
                 e.ResourceContext == CoreWebView2WebResourceContext.Image && !uri.Host.EndsWith(".mdv.invalid")) return;
        e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
    }

    private async Task OpenLinkAsync(string href, bool newTab)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "https" or "http" or "mailto" && !uri.Host.EndsWith(".mdv.invalid")) OpenExternal(href);
            return;
        }
        if (_document == null) return;
        if (href.StartsWith('#'))
        {
            await OpenDocumentAsync(_document.FilePath, anchor: href[1..], newTab: newTab);
            return;
        }
        var path = LocalAssets.ResolvePath(Path.GetDirectoryName(_document.FilePath)!, href, imagesOnly: false);
        if (path != null && new[] { ".md", ".markdown", ".mdown", ".txt" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            var anchor = href.Contains('#') ? href[(href.IndexOf('#') + 1)..] : null;
            await OpenDocumentAsync(path, anchor: anchor, newTab: newTab);
        }
        else StatusText.Text = "文書フォルダ内のMarkdownリンクのみ開けます。別の場所の文書は「ファイルを開く」を使ってください。";
    }

    private void OpenExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { StatusText.Text = error.Message; }
    }

    private void ShowPreviewError(string message)
    {
        ErrorText.Text = message;
        _rendering = false;
        _renderCompletion?.TrySetResult(false);
        Preview.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        PrintMenu.IsEnabled = PdfMenu.IsEnabled = false;
        StatusText.Text = "プレビューを使用できません";
    }

    private void UpdateRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = path.Replace("_", "__"), ToolTip = path };
            item.Click += async (_, _) => await OpenDocumentAsync(path, newTab: true);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { AddToRecent = false, Multiselect = true, Filter = "Markdown (*.md;*.markdown;*.mdown;*.txt)|*.md;*.markdown;*.mdown;*.txt|すべてのファイル (*.*)|*.*", Title = "Markdownを開く" };
        if (dialog.ShowDialog(this) == true) await OpenFilesAsync(dialog.FileNames);
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void Sample_Click(object sender, RoutedEventArgs e) => await OpenDocumentAsync(SamplePath, remember: false, newTab: true);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingTabControls) return;
        if (sender is RadioButton { Tag: string mode })
        {
            _settings.Mode = _workspace.Active.Mode = mode;
            if (IsLoaded) { ApplyTheme(); Render(); }
        }
    }
    internal void SetMode(string mode) { if (mode == "qiita") QiitaMode.IsChecked = true; else GithubMode.IsChecked = true; }

    private void ApplyLayout()
    {
        OutlineColumn.Width = new GridLength(_settings.ShowOutline ? 230 : 0);
        OutlinePanel.Visibility = _settings.ShowOutline ? Visibility.Visible : Visibility.Collapsed;
        SourceColumn.Width = _settings.ShowSource ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SourcePanel.Visibility = _settings.ShowSource ? Visibility.Visible : Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(_settings.ShowSource ? 5 : 0);
    }
    private void Outline_Click(object sender, RoutedEventArgs e) { _settings.ShowOutline = OutlineMenu.IsChecked; ApplyLayout(); }
    private void Source_Click(object sender, RoutedEventArgs e) { _settings.ShowSource = SourceMenu.IsChecked; ApplyLayout(); }
    private void Theme_Click(object sender, RoutedEventArgs e) { _settings.Theme = DarkMenu.IsChecked ? "dark" : "light"; ApplyTheme(); Render(); }
    private void RemoteImages_Click(object sender, RoutedEventArgs e) { _settings.AllowRemoteImages = RemoteImagesMenu.IsChecked; Render(); }
    private void ApplyTheme()
    {
        var dark = _settings.Theme == "dark";
        var colors = new Dictionary<string, string> {
            ["WindowBrush"] = dark ? "#151B23" : "#F6F8FA", ["PanelBrush"] = dark ? "#0D1117" : "#FFFFFF",
            ["TextBrush"] = dark ? "#E6EDF3" : "#1F2328", ["MutedBrush"] = dark ? "#9198A1" : "#656D76",
            ["BorderBrush"] = dark ? "#3D444D" : "#D1D9E0", ["SelectionBrush"] = dark ? "#233B53" : "#DDF4FF",
            ["AccentBrush"] = _settings.Mode == "qiita" ? (dark ? "#8DCD49" : "#4D8D00") : (dark ? "#58A6FF" : "#0969DA") };
        foreach (var (key, value) in colors) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        Preview.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(13, 17, 23) : System.Drawing.Color.White;
    }

    private void Outline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (OutlineList.SelectedItem is OutlineEntry entry) Send(new { type = "scroll", anchor = entry.Id }); }
    private void Find_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Visible; FindInput.Focus(); FindInput.SelectAll(); }
    private void Find(int direction) => Send(new { type = "find", query = FindInput.Text, direction });
    private void FindInput_Changed(object sender, TextChangedEventArgs e) { if (_ready && !_updatingTabControls) Find(0); }
    private void FindInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Find(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; } }
    private void FindNext_Click(object sender, RoutedEventArgs e) => Find(1);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => Find(-1);
    private void FindClose_Click(object sender, RoutedEventArgs e) { FindInput.Clear(); FindBar.Visibility = Visibility.Collapsed; Preview.Focus(); }
    internal void SetZoom(double zoom) { _settings.Zoom = _workspace.Active.Zoom = Math.Clamp(zoom, .5, 2.5); if (_ready) Preview.ZoomFactor = _settings.Zoom; UpdateZoomLabel(); }
    private void UpdateZoomLabel() => ZoomLabel.Text = $"{_settings.Zoom:P0}";
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_settings.Zoom + .1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_settings.Zoom - .1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1);

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _rendering || _document == null) return;
        try { Preview.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { StatusText.Text = "印刷を開始できません: " + error.Message; }
    }
    private async void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _rendering || _document == null) return;
        var dialog = new SaveFileDialog { AddToRecent = false, Filter = "PDF (*.pdf)|*.pdf", FileName = Path.GetFileNameWithoutExtension(_document.FileName) + ".pdf", DefaultExt = ".pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            PdfMenu.IsEnabled = false;
            var settings = Preview.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            StatusText.Text = await Preview.CoreWebView2.PrintToPdfAsync(dialog.FileName, settings) ? "PDFを保存しました: " + dialog.FileName : "PDFを保存できませんでした。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { MessageBox.Show(this, error.Message, "PDF保存に失敗しました", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { PdfMenu.IsEnabled = !_rendering && _document != null; }
    }

    private void Compatibility_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        (_diagnostics.Length > 0 ? string.Join("\n", _diagnostics) + "\n\n" : "") +
        "共通: 見出し、表、タスクリスト、脚注、絵文字、HTMLの折りたたみ、コードの色分け、数式、Mermaid。\n\n" +
        "GitHub: GFMを基本とし、NOTE / TIP / IMPORTANT / WARNING / CAUTIONに対応。\nQiita: 通常改行、:::note、ファイル名付きコード、diff_言語、リンクカードの枠に対応。\n\n" +
        "完全互換ではありません。MathJaxとKaTeXの差、コードの色やサイトのCSS、埋め込みサービス、GitHubのIssue参照・地図・3D、PlantUML等に差があります。Qiitaの補足枠は文書直下が対象です。リンクカードのOGP情報は取得しません。\n\n" +
        "本文は外部APIへ送信しません。外部画像は「表示」メニューで有効にできます。画像・文書の相対パスは開いたファイルのフォルダ内が対象です。",
        "MDV — 互換性・対応範囲", MessageBoxButton.OK, MessageBoxImage.Information);
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "MDV " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3) + " Portable\nGitHub / Qiita Markdown Viewer\n\n設定: " + _settingsPath + "\n\n依存ライブラリのライセンスは、ヘルプの「同梱ライセンス」から確認できます。",
        "MDVについて", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = new[] { Path.Combine(_portable.Assets, "LICENSE"), Path.Combine(_portable.Assets, "Renderer", "THIRD-PARTY-NOTICES.txt") }
                .Concat(Directory.Exists(Path.Combine(_portable.Assets, "Licenses")) ? Directory.GetFiles(Path.Combine(_portable.Assets, "Licenses")) : []);
            var text = string.Join("\n\n", paths.Where(File.Exists).Select(path => Path.GetFileName(path) + "\n\n" + File.ReadAllText(path)));
            new Window { Title = "MDV — 同梱ライセンス", Owner = this, Width = 850, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20) } }.ShowDialog();
        }
        catch (IOException error) { MessageBox.Show(this, error.Message, "ライセンスを表示できません"); }
    }

    private void Window_DragOver(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } }
    private async void Window_Drop(object sender, DragEventArgs e)
    { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) { e.Handled = true; await OpenFilesAsync(files); } }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        string? shortcut = null;
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            shortcut = key switch { Key.Left => "back", Key.Right => "forward", _ => null };
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) shortcut = e.Key switch {
            Key.O => "open", Key.T => "newTab", Key.W => "closeTab",
            Key.Tab => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "previousTab" : "nextTab",
            Key.F => "find", Key.P => "print", Key.U => "source", Key.D1 => "github", Key.D2 => "qiita",
            Key.D0 or Key.NumPad0 => "zoomReset", Key.OemPlus or Key.Add => "zoomIn", Key.OemMinus or Key.Subtract => "zoomOut", _ => null };
        if (e.Key == Key.F5) shortcut = "reload";
        if (e.Key == Key.Escape) shortcut = "escape";
        if (e.Key == Key.F3) { Find(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        if (shortcut != null) { e.Handled = true; Shortcut(shortcut); }
    }
    private void Shortcut(string key)
    {
        var e = new RoutedEventArgs();
        switch (key)
        {
            case "back": _ = NavigateHistoryAsync(-1); break;
            case "forward": _ = NavigateHistoryAsync(1); break;
            case "newTab": _ = NewTabAsync(); break;
            case "closeTab": _ = CloseTabAsync(_workspace.Active); break;
            case "nextTab": _ = CycleTabAsync(1); break;
            case "previousTab": _ = CycleTabAsync(-1); break;
            case "open": Open_Click(this, e); break;
            case "find": Find_Click(this, e); break;
            case "print": Print_Click(this, e); break;
            case "reload": Reload_Click(this, e); break;
            case "source": SourceMenu.IsChecked = !SourceMenu.IsChecked; Source_Click(this, e); break;
            case "github": SetMode("github"); break;
            case "qiita": SetMode("qiita"); break;
            case "zoomIn": SetZoom(_settings.Zoom + .1); break;
            case "zoomOut": SetZoom(_settings.Zoom - .1); break;
            case "zoomReset": SetZoom(1); break;
            case "escape": FindClose_Click(this, e); break;
            case "findNext": Find(1); break;
            case "findPrevious": Find(-1); break;
        }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _closed = true;
        _lifetime.Cancel();
        _reloadTimer.Stop();
        _watcher?.Dispose();
        Hide();
        // Let an in-flight startup finish before disposing its controller or removing its profile.
        if (_initialization != null) await _initialization;
        uint? browserProcessId = Preview.CoreWebView2?.BrowserProcessId;
        Preview.Dispose();
        try { _settings.Save(_settingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (_arguments.Contains("--smoke-test")) { NativeSmoke.Fail(_arguments, error); return; }
            MessageBox.Show(this, "設定INIを保存できませんでした。設定は次回起動時に引き継がれません。\n\n" + _settingsPath + "\n" + error.Message,
                "MDV — 設定の保存に失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await _portable.CleanupAsync(browserProcessId);
        _closeReady = true;
        // Cleanup can complete synchronously when startup failed; leave this Closing event first.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}

public sealed record OutlineEntry(string Id, string Text, int Level)
{
    public Thickness Indent => new((Level - 1) * 12, 0, 4, 0);
}
