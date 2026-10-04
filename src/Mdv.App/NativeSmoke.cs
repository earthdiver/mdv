using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Mdv.App;

/// <summary>Opt-in integration check run by scripts/smoke-windows.ps1 and Windows CI.</summary>
internal static class NativeSmoke
{
    private static string ReportPath(string[] args) => Path.GetFullPath(args[Array.IndexOf(args, "--smoke-test") + 1]);

    internal static async Task RunAsync(MainWindow window, string[] args)
    {
        var checks = new List<string>();
        try
        {
            await window.FirstRender.Task.WaitAsync(TimeSpan.FromSeconds(45));
            using var currentProcess = Process.GetCurrentProcess();
            var startupMilliseconds = (DateTime.UtcNow - currentProcess.StartTime.ToUniversalTime()).TotalMilliseconds;
            checks.Add("WPF window and WebView2 ready");
            if (!window.Portable.Bundled) throw new InvalidOperationException("The portable test requires an embedded Fixed Version runtime.");
            using var browser = Process.GetProcessById(checked((int)window.Preview.CoreWebView2.BrowserProcessId));
            var browserPath = browser.MainModule?.FileName ?? throw new IOException("Browser process path is unavailable.");
            if (!string.Equals(Path.GetDirectoryName(browserPath), window.Portable.Runtime, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The installed WebView2 runtime was used instead of the embedded runtime.");
            checks.Add("Embedded Fixed Version runtime is actually running");
            if (!File.Exists(window.SettingsPath) || Path.GetDirectoryName(window.SettingsPath) != Path.GetDirectoryName(Environment.ProcessPath))
                throw new IOException("INI was not created beside the executable.");
            checks.Add("INI created beside the executable");
            if (args.Contains("--smoke-restart"))
            {
                if (window.Settings.Mode != "qiita" || window.Settings.Theme != "dark" || window.Settings.Zoom != 1.3 ||
                    !window.Settings.ShowSource || window.Settings.ShowOutline) throw new InvalidOperationException("INI settings were not restored.");
                await WaitFor(window, "document.documentElement.dataset.theme === 'dark' && document.querySelector('.qiita .qiita-note-warn') !== null");
                checks.Add("Moved and renamed EXE restored its adjacent INI");
                await FinishAsync();
                return;
            }
            await WaitFor(window, "document.querySelector('.github .markdown-alert-note') !== null");
            checks.Add("GitHub alerts rendered");
            await WaitFor(window, "document.querySelector('.diagram svg') !== null");
            await WaitFor(window, "document.querySelector('.diagram svg text') !== null");
            checks.Add("Mermaid rendered offline");
            await WaitFor(window, "document.querySelector('.katex') !== null");
            checks.Add("Math rendered offline");
            await WaitFor(window, "[...document.querySelectorAll('img')].some(img => img.complete && img.naturalWidth > 0)");
            checks.Add("Local image served by native host");
            window.SetMode("qiita");
            await WaitFor(window, "document.querySelector('.qiita .qiita-note-warn') !== null && document.querySelector('.diagram svg') !== null");
            checks.Add("Native Qiita switch rendered notes and diagrams");
            await WaitFor(window, "window.mdv.find('Markdown').total > 0");
            checks.Add("Document search");
            await WaitFor(window, "window.mdv.scrollToAnchor('共通の記法')");
            checks.Add("Japanese heading navigation");
            await window.Preview.CoreWebView2.ExecuteScriptAsync("window.mdv.find(''); window.scrollTo(0,0);");
            var reportPath = ReportPath(args);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await using (var stream = File.Create(Path.ChangeExtension(reportPath, ".png")))
                await window.Preview.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            var pdf = Path.ChangeExtension(reportPath, ".pdf");
            if (!await window.Preview.CoreWebView2.PrintToPdfAsync(pdf)) throw new IOException("PrintToPdf failed");
            checks.Add("PDF export");
            var watchedPath = Path.Combine(Path.GetDirectoryName(reportPath)!, "watch-test.md");
            await File.WriteAllTextAsync(watchedPath, "# Before\n\n[次の文書](next.md#destination)");
            var paragraphs = string.Concat(Enumerable.Range(1, 70).Select(i => $"段落 {i}: 閲覧位置を確認するための文章です。\n\n"));
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(reportPath)!, "next.md"), "# Next\n\n" + paragraphs + "## Destination\n\n" + paragraphs);
            await window.OpenDocumentAsync(watchedPath, remember: false);
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'Before'");
            checks.Add("Open a local Markdown file");
            var watchedBody = paragraphs + "## 読書位置\n\n[次の文書](next.md#destination)\n\n" + paragraphs;
            await File.WriteAllTextAsync(watchedPath, "# After\n\n" + watchedBody);
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'After'");
            await WaitForNavigation(window);
            checks.Add("File watcher updates preview");
            window.SourceMenu.IsChecked = true;
            window.SourceMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(100);
            window.SourceText.ScrollToVerticalOffset(420);
            await window.Preview.CoreWebView2.ExecuteScriptAsync("window.mdv.scrollToAnchor('読書位置'); window.scrollBy(0,160)");
            var sourceY = window.SourceText.VerticalOffset;
            var beforeY = await ScrollY(window);
            if (sourceY < 300 || beforeY < 1000) throw new InvalidOperationException("Navigation fixture did not scroll both panes.");
            await window.Preview.CoreWebView2.ExecuteScriptAsync("document.querySelector('a[href=\"next.md#destination\"]').click()");
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'Next'");
            await WaitForNavigation(window);
            await WaitFor(window, "window.scrollY > 1000");
            checks.Add("Relative Markdown link handled by native host");
            await window.Preview.CoreWebView2.ExecuteScriptAsync("window.scrollBy(0,180)");
            var nextY = await ScrollY(window);
            window.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'After'");
            await WaitForNavigation(window);
            await CheckScroll(window, beforeY);
            if (Math.Abs(window.SourceText.VerticalOffset - sourceY) > 3 || !window.ForwardButton.IsEnabled)
                throw new InvalidOperationException("Source position or forward button was not restored.");
            checks.Add("Back toolbar restores the previous document, preview and source positions");
            await window.Preview.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', altKey: true, bubbles: true }))");
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'Next'");
            await WaitForNavigation(window);
            await CheckScroll(window, nextY);
            checks.Add("Alt+Right from preview restores the forward document and position");
            File.Delete(watchedPath);
            if (await window.NavigateHistoryAsync(-1)) throw new InvalidOperationException("Missing history target unexpectedly opened.");
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'Next'");
            await CheckScroll(window, nextY);
            if (!window.BackButton.IsEnabled || window.ForwardButton.IsEnabled) throw new InvalidOperationException("Failed navigation changed history.");
            checks.Add("Missing history target leaves current document, position and history intact");
            await File.WriteAllTextAsync(watchedPath, "# After\n\n" + watchedBody);
            if (!await window.NavigateHistoryAsync(-1)) throw new InvalidOperationException("Back did not recover after recreating the file.");
            await File.WriteAllTextAsync(watchedPath, "# Reloaded\n\n" + watchedBody);
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'Reloaded'");
            await WaitForNavigation(window);
            await CheckScroll(window, beforeY);
            if (!window.ForwardButton.IsEnabled) throw new InvalidOperationException("File watcher cleared forward history.");
            await window.OpenDocumentAsync(watchedPath, remember: false);
            if (!window.ForwardButton.IsEnabled) throw new InvalidOperationException("Reopening current file cleared forward history.");
            await window.NavigateHistoryAsync(-1);
            if (window.FileTitle.Text != "welcome.md" || window.BackButton.IsEnabled) throw new InvalidOperationException("Reload inserted duplicate history entries.");
            checks.Add("Automatic reload and reopening the same file preserve history without duplicates");
            await window.NavigateHistoryAsync(1);
            var branchPath = Path.Combine(Path.GetDirectoryName(reportPath)!, "branch.md");
            await File.WriteAllTextAsync(branchPath, "# Branch\n\nNew history branch.");
            await window.OpenDocumentAsync(branchPath, remember: false);
            if (window.ForwardButton.IsEnabled) throw new InvalidOperationException("New navigation retained a stale forward branch.");
            if (await window.OpenDocumentAsync(branchPath + ".missing", quiet: true, anchor: "destination"))
                throw new InvalidOperationException("Missing document unexpectedly opened.");
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'Branch'");
            await window.NavigateHistoryAsync(-1);
            await WaitFor(window, "document.querySelector('h1')?.textContent === 'Reloaded'");
            await CheckScroll(window, beforeY);
            checks.Add("New visits replace the forward branch; failed file opens do not create history");
            await CheckTabs(window, watchedPath, watchedBody, branchPath, checks);
            window.SetMode("qiita");
            window.DarkMenu.IsChecked = true;
            window.DarkMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.SourceMenu.IsChecked = true;
            window.SourceMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.OutlineMenu.IsChecked = false;
            window.OutlineMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.SetZoom(1.3);
            await WaitFor(window, "document.documentElement.dataset.theme === 'dark'");
            checks.Add("Native settings changed for restart verification");
            await FinishAsync();

            async Task FinishAsync()
            {
                var path = ReportPath(args);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { success = true, checks,
                    settingsPath = window.SettingsPath, runtime = window.Portable.Runtime, userData = window.Portable.UserData,
                    browserPath, assets = window.Portable.Assets, startupMilliseconds,
                    payloadPreparationMilliseconds = window.Portable.PreparationTime.TotalMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }
        }
        catch (Exception error) { Fail(args, error, checks); }
    }

    private static async Task CheckTabs(MainWindow window, string watchedPath, string watchedBody, string branchPath, List<string> checks)
    {
        var first = window.Workspace.Active;
        window.SetZoom(1.15);
        window.FindBar.Visibility = Visibility.Visible;
        window.FindInput.Text = "段落";
        await WaitFor(window, "CSS.highlights.get('mdv-search')?.size > 0");
        window.SourceText.ScrollToVerticalOffset(460);
        await window.Preview.CoreWebView2.ExecuteScriptAsync("window.mdv.scrollToAnchor('読書位置'); window.scrollBy(0,140)");
        var firstY = await ScrollY(window);
        var sourceY = window.SourceText.VerticalOffset;
        await window.Preview.CoreWebView2.ExecuteScriptAsync("document.querySelector('a[href=\"next.md#destination\"]').dispatchEvent(new MouseEvent('click', { ctrlKey: true, bubbles: true, cancelable: true }))");
        await WaitFor(window, "document.querySelector('h1')?.textContent === 'Next'");
        await WaitForNavigation(window);
        var second = window.Workspace.Active;
        if (window.Workspace.Tabs.Count != 2 || ReferenceEquals(first, second) || window.BackButton.IsEnabled ||
            !first.History.CanGoForward || window.FindBar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Ctrl+click did not create an independent tab.");
        checks.Add("Ctrl+click creates a tab and leaves the originating history intact");

        window.SetMode("github");
        await WaitForNavigation(window);
        window.SetZoom(.9);
        await window.Preview.CoreWebView2.ExecuteScriptAsync("window.mdv.scrollToAnchor('destination'); window.scrollBy(0,210)");
        var secondY = await ScrollY(window);
        await window.OpenDocumentAsync(branchPath, remember: false);
        await window.NavigateHistoryAsync(-1);
        await CheckScroll(window, secondY);
        window.DocumentTabs.SelectedItem = first;
        await WaitFor(window, "document.querySelector('h1')?.textContent === 'Reloaded'");
        await WaitForNavigation(window);
        await CheckScroll(window, firstY);
        if (window.Settings.Mode != "qiita" || Math.Abs(window.Preview.ZoomFactor - 1.15) > .001 ||
            window.FindInput.Text != "段落" || window.FindBar.Visibility != Visibility.Visible ||
            Math.Abs(window.SourceText.VerticalOffset - sourceY) > 3 || !window.ForwardButton.IsEnabled)
            throw new InvalidOperationException("Tab selection did not restore mode, zoom, search, source position or history.");
        await window.NavigateHistoryAsync(1);
        if (second.History.Current?.FilePath != Path.Combine(Path.GetDirectoryName(watchedPath)!, "next.md") || !second.History.CanGoForward)
            throw new InvalidOperationException("Navigation in the first tab changed the second tab's history.");
        await window.NavigateHistoryAsync(-1);
        checks.Add("Native tab selection restores preview/source positions, mode, zoom, search and independent history");

        var nextPath = Path.Combine(Path.GetDirectoryName(watchedPath)!, "next.md");
        await window.OpenDocumentAsync(nextPath, newTab: true);
        await CheckScroll(window, secondY);
        if (window.Workspace.Tabs.Count != 2 || !ReferenceEquals(window.Workspace.Active, second) ||
            window.Settings.Mode != "github" || Math.Abs(window.Preview.ZoomFactor - .9) > .001)
            throw new InvalidOperationException("Reopening a document duplicated its tab or lost its settings.");
        checks.Add("Reopening an existing file selects its tab without duplication");

        await File.WriteAllTextAsync(watchedPath, "# Tab refreshed\n\n" + watchedBody);
        await window.SelectTabAsync(first);
        await WaitFor(window, "document.querySelector('h1')?.textContent === 'Tab refreshed'");
        await CheckScroll(window, firstY);
        await window.SelectTabAsync(second);
        File.Delete(watchedPath);
        await window.SelectTabAsync(first);
        await WaitFor(window, "document.querySelector('h1')?.textContent === 'Tab refreshed'");
        await CheckScroll(window, firstY);
        if (!window.StatusText.Text.Contains("前回読み込んだ内容")) throw new InvalidOperationException("Missing inactive file lost its retained content.");
        checks.Add("Inactive tabs refresh changed files and retain their content when files disappear");

        if (await window.OpenDocumentAsync(branchPath + ".missing", newTab: true, quiet: true) || window.Workspace.Tabs.Count != 2 ||
            !ReferenceEquals(window.Workspace.Active, first)) throw new InvalidOperationException("Failed open created or switched a tab.");
        await File.WriteAllTextAsync(watchedPath, "# Tab refreshed\n\n" + watchedBody);
        await window.OpenFilesAsync([watchedPath, branchPath, nextPath]);
        if (window.Workspace.Tabs.Count != 3 || !ReferenceEquals(window.Workspace.Active, second))
            throw new InvalidOperationException("Opening multiple files did not reuse existing tabs.");
        checks.Add("Multi-file opening reuses existing tabs and failed opens leave tabs unchanged");

        await window.CloseTabAsync(first);
        await CheckScroll(window, secondY);
        if (!ReferenceEquals(window.Workspace.Active, second) || window.Workspace.Tabs.Count != 2)
            throw new InvalidOperationException("Closing an inactive tab changed the active document.");
        await TabKey(window, "Tab");
        await WaitFor(window, "document.querySelector('h1')?.textContent === 'Branch'");
        await WaitForNavigation(window);
        await TabKey(window, "Tab", shift: true);
        await WaitFor(window, "document.querySelector('h1')?.textContent === 'Next'");
        await WaitForNavigation(window);
        await CheckScroll(window, secondY);
        checks.Add("Ctrl+Tab and Ctrl+Shift+Tab cycle tabs after an inactive tab closes");

        var branch = window.Workspace.Find(branchPath)!;
        window.DocumentTabs.UpdateLayout();
        var item = (ListBoxItem)window.DocumentTabs.ItemContainerGenerator.ContainerFromItem(branch);
        var close = Descendants(item).OfType<Button>().Single();
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForNavigation(window);
        if (window.Workspace.Tabs.Count != 1 || !ReferenceEquals(window.Workspace.Active, second))
            throw new InvalidOperationException("The tab close button activated the tab being closed.");
        checks.Add("An inactive tab's close button leaves the current document selected");

        await TabKey(window, "t");
        await WaitFor(window, "document.querySelector('#document').textContent === ''");
        await WaitForNavigation(window);
        if (window.Workspace.Tabs.Count != 2 || window.EmptyPanel.Visibility != Visibility.Visible ||
            window.BackButton.IsEnabled || window.PrintMenu.IsEnabled || window.SourceText.Text.Length != 0)
            throw new InvalidOperationException("Ctrl+T did not create a clean empty tab.");
        await window.OpenFilesAsync([watchedPath]);
        if (window.Workspace.Tabs.Count != 2 || window.BackButton.IsEnabled || window.EmptyPanel.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Opening a file did not reuse the empty tab.");
        await window.CloseTabAsync(second);
        await TabKey(window, "w");
        await WaitFor(window, "document.querySelector('#document').textContent === ''");
        await WaitForNavigation(window);
        if (window.Workspace.Tabs.Count != 1 || window.Workspace.Active.Document != null || window.Workspace.Active.History.Current != null ||
            window.EmptyPanel.Visibility != Visibility.Visible || window.ForwardButton.IsEnabled)
            throw new InvalidOperationException("Closing the last tab did not leave a fresh empty tab.");
        checks.Add("Ctrl+T creates a reusable empty tab and Ctrl+W safely closes the last document");
    }

    private static Task TabKey(MainWindow window, string key, bool shift = false) =>
        window.Preview.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown', " +
            JsonSerializer.Serialize(new { key, ctrlKey = true, shiftKey = shift, bubbles = true }) + "))");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task WaitForNavigation(MainWindow window)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (window.NavigationReady) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Navigation did not finish.");
    }

    private static async Task<double> ScrollY(MainWindow window) =>
        JsonSerializer.Deserialize<double>(await window.Preview.CoreWebView2.ExecuteScriptAsync("window.scrollY"));

    private static Task CheckScroll(MainWindow window, double expected) =>
        WaitFor(window, $"Math.abs(window.scrollY - {expected.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}) < 3");

    private static async Task WaitFor(MainWindow window, string expression)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await window.Preview.CoreWebView2.ExecuteScriptAsync(expression) == "true") return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Failed: " + expression);
    }

    internal static void Fail(string[] args, Exception error, List<string>? checks = null)
    {
        try
        {
            var path = ReportPath(args);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Application.Current.Shutdown(1); }
    }
}
