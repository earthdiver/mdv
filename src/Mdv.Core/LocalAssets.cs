namespace Mdv.Core;

/// <summary>Only serve image files physically located inside the open document's directory.</summary>
public static class LocalAssets
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml",
    };

    public static string? ResolvePath(string directory, string relativeUrl, bool imagesOnly = true)
    {
        try
        {
            var relative = Uri.UnescapeDataString(relativeUrl.Split('?', '#')[0]);
            // Reject Windows device paths, UNC, drive roots, alternate streams and separator ambiguity.
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOfAny(['\\', ':', '\0']) >= 0 ||
                Path.IsPathRooted(relative) || relative.StartsWith('/')) return null;
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Path.Combine(root, relative));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!target.StartsWith(root, comparison) || !File.Exists(target)) return null;
            if (imagesOnly && !MimeTypes.ContainsKey(Path.GetExtension(target))) return null;
            // Check every component: a junction/symlink inside the folder must not escape the root.
            var current = root.TrimEnd(Path.DirectorySeparatorChar);
            foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            }
            return target;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return null; }
    }

    public static string ContentType(string path) => MimeTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
}
