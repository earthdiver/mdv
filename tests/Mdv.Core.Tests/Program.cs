using System.Text;
using System.Globalization;
using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using Mdv.Core;

var failures = new List<string>();
var checks = 0;
void Check(string name, Action body)
{
    checks++;
    try { body(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures.Add(name + ": " + e.Message); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Throws<T>(Action body) where T : Exception
{
    try { body(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
foreach (var encoding in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
{
    Check("Decode " + encoding.WebName + " BOM " + Convert.ToHexString(encoding.GetPreamble()), () =>
        Equal("日本語 🌸", DocumentReader.Decode([.. encoding.GetPreamble(), .. encoding.GetBytes("日本語 🌸")]).Text));
}
Check("UTF-8 without BOM", () => Equal(("日本語", "UTF-8"), DocumentReader.Decode(Encoding.UTF8.GetBytes("日本語"))));
Check("Shift_JIS fallback", () => Equal(("日本語です", "Shift_JIS (CP932)"), DocumentReader.Decode(Encoding.GetEncoding(932).GetBytes("日本語です"))));
Check("Corrupt BOM-encoded input is not silently replaced", () => Throws<DecoderFallbackException>(() => DocumentReader.Decode([0xEF, 0xBB, 0xBF, 0xFF])));
Check("Empty document", () => Equal(("", "UTF-8"), DocumentReader.Decode([])));

var temp = Path.Combine(Path.GetTempPath(), "mdv-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var root = Path.Combine(temp, "docs");
    Directory.CreateDirectory(Path.Combine(root, "images"));
    var image = Path.Combine(root, "images", "日本 語.png");
    File.WriteAllBytes(image, [0]);
    File.WriteAllText(Path.Combine(root, "readme.md"), "# タイトル\r\n本文\r次");
    File.WriteAllBytes(Path.Combine(temp, "outside.png"), [0]);
    Check("Document read and CRLF normalization", () => {
        var document = DocumentReader.ReadAsync(Path.Combine(root, "readme.md")).GetAwaiter().GetResult();
        Equal("# タイトル\n本文\n次", document.Text); Equal(3, document.LineCount);
    });
    Check("URL-encoded local image", () => Equal(image, LocalAssets.ResolvePath(root, "images/%E6%97%A5%E6%9C%AC%20%E8%AA%9E.png")));
    foreach (var bad in new[] { "../outside.png", "%2e%2e/outside.png", "images/../../outside.png", "/outside.png", "C:/outside.png", "\\\\server\\share\\file.png", "images/..\\..\\outside.png", "readme.md", "readme.md:stream", "missing.png" })
        Check("Reject unsafe or non-image path: " + bad, () => Equal<string?>(null, LocalAssets.ResolvePath(root, bad)));
    Check("Local Markdown navigation", () => Equal(Path.Combine(root, "readme.md"), LocalAssets.ResolvePath(root, "readme.md#title", imagesOnly: false)));
    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), temp);
        Check("Reject symlinks escaping document folder", () => Equal<string?>(null, LocalAssets.ResolvePath(root, "escape/outside.png")));
    }
    Check("Bound file size before allocation", () => {
        var path = Path.Combine(root, "large.md");
        using (var stream = File.Create(path)) stream.SetLength(DocumentReader.MaxBytes + 1L);
        Throws<IOException>(() => DocumentReader.ReadAsync(path).GetAwaiter().GetResult());
    });
    Check("Bound decoded character count", () => {
        var path = Path.Combine(root, "long.md"); File.WriteAllText(path, new string('x', DocumentReader.MaxCharacters + 1));
        Throws<IOException>(() => DocumentReader.ReadAsync(path).GetAwaiter().GetResult());
    });
    Check("Missing INI uses defaults", () => Equal("github", UserSettings.Load(Path.Combine(temp, "none.ini")).Mode));
    Check("Corrupt preferences recover", () => {
        var path = Path.Combine(temp, "bad.ini"); File.WriteAllText(path, "{\n[Viewer]\nZoom=broken\nShowOutline=invalid\nMode=other");
        var settings = UserSettings.Load(path); Equal(1d, settings.Zoom); Equal(true, settings.ShowOutline); Equal("github", settings.Mode);
    });
    Check("Preferences round-trip and recent file deduplication", () => {
        var settings = new UserSettings { Mode = "qiita", Theme = "dark", Zoom = 1.5 }; settings.Remember("C:\\a.md"); settings.Remember("C:\\b.md"); settings.Remember("C:\\a.md");
        var path = Path.Combine(temp, "settings.ini"); settings.Save(path); var loaded = UserSettings.Load(path);
        Equal("qiita", loaded.Mode); Equal("dark", loaded.Theme); Equal(1.5, loaded.Zoom); Equal(2, loaded.RecentFiles.Count); Equal("C:\\a.md", loaded.RecentFiles[0]);
        Equal(true, File.ReadAllText(path).Contains("[Viewer]"));
    });
    Check("Invalid preferences are constrained", () => {
        var settings = new UserSettings { Mode = "other", Theme = "bad", Zoom = double.NaN }; settings.Normalize();
        Equal("github", settings.Mode); Equal("light", settings.Theme); Equal(1d, settings.Zoom);
    });
    Check("INI tolerates BOM, comments, spaces, sections and case", () => {
        var path = Path.Combine(temp, "comments.ini");
        File.WriteAllText(path, "; comment\n# comment\n [viewer] \n mode = QIITA\nTHEME=dark\nShowSource=1\nShowOutline=no\nAllowRemoteImages=YES\n[unknown]\nMode=github", new UTF8Encoding(true));
        var settings = UserSettings.Load(path);
        Equal("qiita", settings.Mode); Equal("dark", settings.Theme); Equal(true, settings.ShowSource); Equal(false, settings.ShowOutline); Equal(true, settings.AllowRemoteImages);
    });
    Check("INI preserves Japanese and special characters in file names", () => {
        var settings = new UserSettings(); settings.Remember("C:\\日本 語\\#draft;[x]=1.md"); settings.Remember("C:\\quoted\"name.md");
        var path = Path.Combine(temp, "paths.ini"); settings.Save(path);
        Equal(string.Join('|', settings.RecentFiles), string.Join('|', UserSettings.Load(path).RecentFiles));
    });
    Check("INI numbers are culture-independent", () => {
        var previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var path = Path.Combine(temp, "culture.ini"); new UserSettings { Zoom = 1.25 }.Save(path);
            Equal(true, File.ReadAllText(path).Contains("Zoom=1.25")); Equal(1.25, UserSettings.Load(path).Zoom);
        } finally { CultureInfo.CurrentCulture = previous; }
    });
    Check("INI duplicate keys use the last value", () => {
        var path = Path.Combine(temp, "duplicate.ini"); File.WriteAllText(path, "[Viewer]\nMode=github\nmode=qiita\nZoom=99");
        var settings = UserSettings.Load(path); Equal("qiita", settings.Mode); Equal(2.5, settings.Zoom);
    });
    Check("INI rejects injected sections in file names without overwriting settings", () => {
        var path = Path.Combine(temp, "preserved.ini"); var settings = new UserSettings(); settings.Save(path);
        var original = File.ReadAllText(path); settings.RecentFiles.Add("evil\n[Viewer]\nMode=qiita");
        Throws<ArgumentException>(() => settings.Save(path)); Equal(original, File.ReadAllText(path));
    });
    Check("INI refuses unreasonable file sizes", () => {
        var path = Path.Combine(temp, "huge.ini"); File.WriteAllText(path, new string('x', 128 * 1024 + 1));
        Throws<IOException>(() => UserSettings.Load(path));
    });
    Check("Settings follow the EXE name and directory regardless of working directory", () => {
        var executable = Path.Combine(root, "Renamed Viewer.exe"); var previous = Environment.CurrentDirectory;
        try { Environment.CurrentDirectory = temp; Equal(Path.Combine(root, "Renamed Viewer.ini"), PortablePaths.SettingsForExecutable(executable)); }
        finally { Environment.CurrentDirectory = previous; }
    });
    Check("INI save leaves no temporary sidecars", () => {
        var path = Path.Combine(temp, "clean.ini"); new UserSettings().Save(path); new UserSettings { Mode = "qiita" }.Save(path);
        Equal(0, Directory.GetFiles(temp, "clean.ini.*.tmp").Length);
    });
    var firstDocument = Path.Combine(root, "first.md");
    var secondDocument = Path.Combine(root, "second.md");
    var thirdDocument = Path.Combine(root, "third.md");
    Check("History restores each document's latest reading position in both directions", () => {
        var history = new DocumentHistory(); Equal(false, history.CanGoBack); Equal(false, history.CanGoForward);
        history.Visit(firstDocument); history.SaveView(new ViewPosition(Y: 420, Anchor: "intro", Offset: 32));
        history.Visit(secondDocument); history.SaveView(new ViewPosition(Y: 960));
        Equal(firstDocument, history.Move(-1)!.FilePath); Equal(420d, history.Current!.View!.Y);
        history.SaveView(new ViewPosition(Y: 600));
        Equal(secondDocument, history.Move(1)!.FilePath); Equal(960d, history.Current!.View!.Y);
        Equal(600d, history.Move(-1)!.View!.Y);
    });
    Check("Failed history targets can be inspected without changing history", () => {
        var history = new DocumentHistory(); history.Visit(firstDocument); history.Visit(secondDocument);
        Equal(firstDocument, history.Peek(-1)!.FilePath); Equal(secondDocument, history.Current!.FilePath);
        Equal(true, history.CanGoBack); Equal(false, history.CanGoForward);
    });
    Check("Reloading the current file retains forward history", () => {
        var history = new DocumentHistory(); history.Visit(firstDocument); history.Visit(secondDocument); history.Move(-1);
        history.Visit(firstDocument); Equal(false, history.CanGoBack); Equal(true, history.CanGoForward);
        Equal(secondDocument, history.Peek(1)!.FilePath);
    });
    Check("A new visit after going back replaces the forward branch", () => {
        var history = new DocumentHistory(); history.Visit(firstDocument); history.Visit(secondDocument); history.Move(-1);
        history.Visit(thirdDocument); Equal(false, history.CanGoForward);
        Equal(firstDocument, history.Move(-1)!.FilePath); Equal(thirdDocument, history.Move(1)!.FilePath);
    });
    Check("Same-file fragment visits retain distinct reading positions", () => {
        var history = new DocumentHistory(); history.Visit(firstDocument); history.SaveView(new ViewPosition(Y: 140));
        history.Visit(firstDocument, forceNew: true); history.SaveView(new ViewPosition(Y: 1200));
        Equal(140d, history.Move(-1)!.View!.Y); Equal(1200d, history.Move(1)!.View!.Y);
        Equal<HistoryEntry?>(null, history.Move(1)); Equal(firstDocument, history.Current!.FilePath);
    });
    Check("History bounds its session memory and preserves the remaining order", () => {
        var history = new DocumentHistory(2); history.Visit(firstDocument); history.Visit(secondDocument); history.Visit(thirdDocument);
        Equal(secondDocument, history.Move(-1)!.FilePath); Equal(false, history.CanGoBack);
        Equal(thirdDocument, history.Move(1)!.FilePath); Throws<ArgumentOutOfRangeException>(() => history.Move(0));
    });
    Check("Tabs retain independent navigation, view, mode, zoom and search state", () => {
        var workspace = new DocumentWorkspace("github", 1);
        var first = workspace.Active;
        first.History.Visit(firstDocument); first.History.SaveView(new ViewPosition(Y: 300));
        first.History.Visit(secondDocument); first.History.SaveView(new ViewPosition(Y: 800));
        first.SetDocument(new(secondDocument, "# Second", "UTF-8"));
        first.SearchQuery = "Second"; first.ShowSearch = true;
        var second = workspace.Add(); second.Mode = "qiita"; second.Zoom = 1.4;
        second.History.Visit(thirdDocument); second.History.SaveView(new ViewPosition(Y: 1200));
        second.SetDocument(new(thirdDocument, "# Third", "UTF-8"));
        workspace.Select(first); first.History.Move(-1);
        Equal(300d, first.History.Current!.View!.Y); Equal(true, first.History.CanGoForward);
        Equal(1200d, second.History.Current!.View!.Y); Equal(false, second.History.CanGoBack);
        Equal("github", first.Mode); Equal(1d, first.Zoom); Equal("Second", first.SearchQuery); Equal(true, first.ShowSearch);
        workspace.Select(second); Equal("qiita", workspace.Active.Mode); Equal(1.4, workspace.Active.Zoom);
        Equal("", second.SearchQuery); Equal(false, second.ShowSearch);
    });
    Check("Duplicate lookup uses full paths and distinguishes equal file names", () => {
        var workspace = new DocumentWorkspace("github", 1);
        var first = workspace.Active; first.SetDocument(new(firstDocument, "one", "UTF-8"));
        var otherPath = Path.Combine(root, "subdirectory", "first.md");
        var second = workspace.Add(); second.SetDocument(new(otherPath, "two", "UTF-8"));
        Equal(first, workspace.Find(Path.Combine(root, ".", "first.md")));
        Equal(second, workspace.Find(otherPath.ToUpperInvariant()));
        Equal<DocumentTab?>(null, workspace.Find(thirdDocument));
    });
    Check("Closing an inactive tab preserves the selected document and history", () => {
        var workspace = new DocumentWorkspace("github", 1); var first = workspace.Active;
        var second = workspace.Add(); second.History.Visit(secondDocument); second.History.SaveView(new ViewPosition(Y: 640));
        Equal(true, workspace.Close(first)); Equal(second, workspace.Active); Equal(640d, second.History.Current!.View!.Y);
        Equal(false, workspace.Select(first)); Equal(false, workspace.Close(first)); Equal(1, workspace.Tabs.Count);
    });
    Check("Closing the active tab selects the right neighbor then the previous one", () => {
        var workspace = new DocumentWorkspace("github", 1); var first = workspace.Active;
        var second = workspace.Add(); var third = workspace.Add(); workspace.Select(second);
        workspace.Close(second); Equal(third, workspace.Active);
        workspace.Close(third); Equal(first, workspace.Active);
    });
    Check("Closing the last document leaves a blank tab with no old history or search", () => {
        var workspace = new DocumentWorkspace("qiita", 1.2); var closed = workspace.Active;
        closed.SetDocument(new(firstDocument, "# First", "UTF-8")); closed.History.Visit(firstDocument);
        closed.SearchQuery = "First"; closed.ShowSearch = true;
        workspace.Close(closed);
        Equal(1, workspace.Tabs.Count); Equal<MarkdownDocument?>(null, workspace.Active.Document);
        Equal(false, workspace.Active.History.CanGoBack); Equal<HistoryEntry?>(null, workspace.Active.History.Current);
        Equal("qiita", workspace.Active.Mode); Equal(1.2, workspace.Active.Zoom);
        Equal("", workspace.Active.SearchQuery); Equal(false, workspace.Active.ShowSearch);
    });
    byte[] Archive(params (string Path, string Content)[] files) {
        using var memory = new MemoryStream();
        using (var compressed = new BrotliStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(compressed))
            foreach (var (name, content) in files) {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
            }
        return memory.ToArray();
    }
    var payload = Archive(("Renderer/index.html", "preview"), ("WebView2Runtime/msedgewebview2.exe", "runtime"));
    var hash = Convert.ToHexString(SHA256.HashData(payload));
    var cache = Path.Combine(temp, "cache");
    Check("Portable resources extract to a versioned temporary cache", () => {
        using var stream = new MemoryStream(payload); var extracted = PortablePayload.Extract(stream, cache, hash);
        Equal(Path.Combine(cache, hash.ToLowerInvariant()), extracted); Equal("preview", File.ReadAllText(Path.Combine(extracted, "Renderer", "index.html")));
    });
    Check("Completed payload cache is reused", () => {
        using var stream = new MemoryStream([]); Equal(Path.Combine(cache, hash.ToLowerInvariant()), PortablePayload.Extract(stream, cache, hash));
    });
    Check("Incomplete payload cache is repaired", () => {
        var index = Path.Combine(cache, hash.ToLowerInvariant(), "Renderer", "index.html"); File.Delete(index);
        using var stream = new MemoryStream(payload); PortablePayload.Extract(stream, cache, hash); Equal(true, File.Exists(index));
    });
    Check("Cache repair tolerates a temporary Windows file lock", () => {
        var lockedCache = Path.Combine(temp, "locked-cache");
        var target = Path.Combine(lockedCache, hash.ToLowerInvariant()); Directory.CreateDirectory(target);
        var locked = new FileStream(Path.Combine(target, "locked.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(400); locked.Dispose(); });
        try
        {
            using var stream = new MemoryStream(payload);
            var extracted = PortablePayload.Extract(stream, lockedCache, hash);
            Equal("preview", File.ReadAllText(Path.Combine(extracted, "Renderer", "index.html")));
            Equal(0, Directory.GetDirectories(lockedCache, "*.pending-*").Length);
        }
        finally { release.GetAwaiter().GetResult(); locked.Dispose(); }
    });
    Check("Payload tampering is detected", () => {
        using var stream = new MemoryStream(payload); Throws<InvalidDataException>(() => PortablePayload.Extract(stream, cache, new string('0', 64)));
    });
    Check("Payload traversal cannot write outside the staging directory", () => {
        var malicious = Archive(("../outside.txt", "unsafe")); var maliciousHash = Convert.ToHexString(SHA256.HashData(malicious));
        using var stream = new MemoryStream(malicious); Throws<IOException>(() => PortablePayload.Extract(stream, cache, maliciousHash));
        Equal(false, File.Exists(Path.Combine(cache, "outside.txt"))); Equal(0, Directory.GetDirectories(cache, "*.pending-*").Length);
    });
    foreach (var name in new[] { "/outside.txt", "C:/outside.txt", "..\\outside.txt", "Renderer/index.html:stream" })
        Check("Compressed payload rejects platform-specific escape: " + name, () => {
            var invalid = Archive((name, "unsafe")); var invalidHash = Convert.ToHexString(SHA256.HashData(invalid));
            using var stream = new MemoryStream(invalid); Throws<IOException>(() => PortablePayload.Extract(stream, cache, invalidHash));
            Equal(0, Directory.GetDirectories(cache, "*.pending-*").Length);
        });
    Check("Compressed payload rejects TAR links", () => {
        using var memory = new MemoryStream();
        using (var compressed = new BrotliStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(compressed))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = ".." });
        var data = memory.ToArray(); var invalidHash = Convert.ToHexString(SHA256.HashData(data));
        using var stream = new MemoryStream(data); Throws<InvalidDataException>(() => PortablePayload.Extract(stream, cache, invalidHash));
        Equal(0, Directory.GetDirectories(cache, "*.pending-*").Length);
    });
    Check("Incomplete compressed payload is not marked complete", () => {
        var data = Archive(("Renderer/index.html", "preview")); var invalidHash = Convert.ToHexString(SHA256.HashData(data));
        using var stream = new MemoryStream(data); Throws<InvalidDataException>(() => PortablePayload.Extract(stream, cache, invalidHash));
        Equal(false, Directory.Exists(Path.Combine(cache, invalidHash.ToLowerInvariant())));
    });
    Check("Compressed payload preserves long Unicode file names and contents", () => {
        var name = "Samples/" + new string('あ', 80) + ".md";
        var data = Archive(("Renderer/index.html", "preview"), ("WebView2Runtime/msedgewebview2.exe", "runtime"), (name, "# 日本語 🌸\n"));
        var dataHash = Convert.ToHexString(SHA256.HashData(data)); using var stream = new MemoryStream(data);
        var extracted = PortablePayload.Extract(stream, cache, dataHash); Equal("# 日本語 🌸\n", File.ReadAllText(Path.Combine(extracted, name)));
        Equal(0, Directory.GetFiles(extracted, "*.tar", SearchOption.AllDirectories).Length);
    });
    Check("Concurrent payload extraction produces one complete cache", () => {
        var parallelCache = Path.Combine(temp, "parallel-cache");
        Task.WaitAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => { using var stream = new MemoryStream(payload); PortablePayload.Extract(stream, parallelCache, hash); })).ToArray());
        Equal(1, Directory.GetDirectories(parallelCache).Length);
    });
}
finally { Directory.Delete(temp, true); }
Console.WriteLine($"{checks - failures.Count}/{checks} core checks passed");
return failures.Count == 0 ? 0 : 1;
