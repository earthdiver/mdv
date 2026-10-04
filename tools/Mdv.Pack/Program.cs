using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Diagnostics;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Mdv.Pack <payload-directory> <output.br>");
    return 1;
}
var source = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
foreach (var name in new[] { "Renderer/index.html", "Samples/welcome.md", "WebView2Runtime/msedgewebview2.exe", "LICENSE" })
    if (!File.Exists(Path.Combine(source, name))) throw new FileNotFoundException("Missing payload file: " + name);
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
var temporary = destination + ".tmp";
var timer = Stopwatch.StartNew();
long originalBytes = 0;
try
{
    if (File.Exists(temporary)) File.Delete(temporary);
    using (var output = File.Create(temporary))
    using (var compressor = new BrotliStream(output, new BrotliCompressionOptions { Quality = 9 }))
    using (var archive = new TarWriter(compressor, TarEntryFormat.Pax))
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var input = File.OpenRead(file);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetRelativePath(source, file).Replace('\\', '/'),
                new Dictionary<string, string> { ["atime"] = "1767225600", ["ctime"] = "1767225600" })
            {
                ModificationTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                DataStream = input,
            };
            archive.WriteEntry(entry);
            originalBytes += input.Length;
        }
    }
    File.Move(temporary, destination, true);
    using var stream = File.OpenRead(destination);
    File.WriteAllText(Path.ChangeExtension(destination, ".sha256"), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }
Console.WriteLine($"Embedded payload: {originalBytes / 1048576d:F1} -> {new FileInfo(destination).Length / 1048576d:F1} MiB (Brotli, {timer.Elapsed.TotalSeconds:F1}s)");
return 0;
