using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;

namespace Mdv.Core;

public static class PortablePayload
{
    public static string Extract(Stream archive, string cacheRoot, string expectedHash)
    {
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid payload hash.");
        expectedHash = expectedHash.ToLowerInvariant();
        var target = Path.Combine(Path.GetFullPath(cacheRoot), expectedHash);
        var marker = Path.Combine(target, ".complete");
        // One extractor per payload; an interrupted process cannot leave a usable partial cache.
        using var mutex = new Mutex(false, "MDV.Payload." + expectedHash);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("描画資材の展開待ちがタイムアウトしました。もう一度起動してください。");
            if (File.Exists(marker) && File.ReadAllText(marker) == expectedHash &&
                File.Exists(Path.Combine(target, "Renderer", "index.html")) &&
                File.Exists(Path.Combine(target, "WebView2Runtime", "msedgewebview2.exe"))) return target;
            var actualHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            if (actualHash != expectedHash) throw new InvalidDataException("EXE内の描画資材が破損しています。配布ファイルを取得し直してください。");
            archive.Position = 0;
            Directory.CreateDirectory(cacheRoot);
            var staging = target + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(staging);
                // Stream the compressed TAR straight to its destination, without an intermediate
                // uncompressed archive or a full-payload allocation in memory.
                using (var decompressor = new BrotliStream(archive, CompressionMode.Decompress, leaveOpen: true))
                using (var reader = new TarReader(decompressor))
                {
                    TarEntry? entry;
                    while ((entry = reader.GetNextEntry()) != null)
                    {
                        // Our packer emits regular files only. Do not allow TAR links/devices or
                        // platform-specific paths to escape the new staging directory.
                        if (entry.EntryType != TarEntryType.RegularFile) throw new InvalidDataException("同梱資材のファイル形式が不正です。");
                        var name = entry.Name.Replace('\\', '/');
                        if (name.Contains(':') || name.Split('/').Any(part => part is "" or "." or ".."))
                            throw new IOException("同梱資材のファイルパスが不正です。");
                        var file = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        entry.ExtractToFile(file, overwrite: false);
                    }
                }
                if (!File.Exists(Path.Combine(staging, "Renderer", "index.html")) ||
                    !File.Exists(Path.Combine(staging, "WebView2Runtime", "msedgewebview2.exe")))
                    throw new InvalidDataException("同梱の描画資材が不足しています。");
                File.WriteAllText(Path.Combine(staging, ".complete"), expectedHash);
                if (Directory.Exists(target)) RetryTransientAccess(() => Directory.Delete(target, true));
                RetryTransientAccess(() => Directory.Move(staging, target));
                return target;
            }
            finally { if (Directory.Exists(staging)) RetryTransientAccess(() => Directory.Delete(staging, true)); }
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static void RetryTransientAccess(Action operation)
    {
        // Newly extracted Windows binaries can be held briefly by another process.
        // Keep publication atomic, but give transient access/sharing locks time to clear.
        for (var attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 7 &&
                error is IOException or UnauthorizedAccessException && (error.HResult & 0xFFFF) is 5 or 32 or 33)
            { Thread.Sleep(100 * (attempt + 1)); }
        }
    }
}
