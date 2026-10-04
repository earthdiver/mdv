using System.Globalization;
using System.Text;

namespace Mdv.Core;

public sealed class UserSettings
{
    public string Mode { get; set; } = "github";
    public string Theme { get; set; } = "light";
    public double Zoom { get; set; } = 1;
    public bool ShowSource { get; set; }
    public bool ShowOutline { get; set; } = true;
    public bool AllowRemoteImages { get; set; }
    public List<string> RecentFiles { get; set; } = [];

    public void Normalize()
    {
        if (Mode is not ("github" or "qiita")) Mode = "github";
        if (Theme is not ("light" or "dark")) Theme = "light";
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, 0.5, 2.5) : 1;
        RecentFiles = (RecentFiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }

    public void Remember(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        Normalize();
    }

    public static UserSettings Load(string path)
    {
        var settings = new UserSettings();
        if (!File.Exists(path)) return settings;
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException("設定ファイルが大きすぎます（上限128 KiB）。");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var source in File.ReadLines(path, Encoding.UTF8))
        {
            var line = source.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var value = line[(equals + 1)..].Trim();
            if (value.StartsWith('"'))
            {
                if (value.Length < 2 || !value.EndsWith('"')) continue;
                value = value[1..^1].Replace("\"\"", "\"");
            }
            values[section + "." + line[..equals].Trim()] = value;
        }
        settings.Mode = values.GetValueOrDefault("Viewer.Mode", settings.Mode).ToLowerInvariant();
        settings.Theme = values.GetValueOrDefault("Viewer.Theme", settings.Theme).ToLowerInvariant();
        if (double.TryParse(values.GetValueOrDefault("Viewer.Zoom"), NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)) settings.Zoom = zoom;
        settings.ShowSource = ReadBool("ShowSource", settings.ShowSource);
        settings.ShowOutline = ReadBool("ShowOutline", settings.ShowOutline);
        settings.AllowRemoteImages = ReadBool("AllowRemoteImages", settings.AllowRemoteImages);
        for (var i = 1; i <= 12; i++)
            if (values.TryGetValue($"RecentFiles.File{i}", out var file) && !string.IsNullOrWhiteSpace(file)) settings.RecentFiles.Add(file);
        settings.Normalize();
        return settings;

        bool ReadBool(string key, bool fallback) => values.GetValueOrDefault("Viewer." + key)?.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    public void Save(string path)
    {
        Normalize();
        var lines = new List<string>
        {
            "; MDV portable settings — UTF-8", "; Stored beside the executable. No registry settings.",
            "[Viewer]", "Mode=" + Mode, "Theme=" + Theme,
            "Zoom=" + Zoom.ToString("0.###", CultureInfo.InvariantCulture),
            "ShowSource=" + ShowSource.ToString().ToLowerInvariant(),
            "ShowOutline=" + ShowOutline.ToString().ToLowerInvariant(),
            "AllowRemoteImages=" + AllowRemoteImages.ToString().ToLowerInvariant(), "", "[RecentFiles]",
        };
        for (var i = 0; i < RecentFiles.Count; i++)
        {
            // Quoting preserves Japanese paths, spaces, semicolons, and equals signs.
            if (RecentFiles[i].IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("ファイル名に保存できない制御文字が含まれています。");
            lines.Add($"File{i + 1}=\"{RecentFiles[i].Replace("\"", "\"\"")}\"");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class PortablePaths
{
    public static string SettingsForExecutable(string executablePath) => Path.ChangeExtension(Path.GetFullPath(executablePath), ".ini");

    public static string SettingsPath
    {
        get
        {
            var executable = Environment.ProcessPath ?? throw new IOException("実行ファイルの場所を取得できませんでした。");
            return SettingsForExecutable(executable);
        }
    }
}
