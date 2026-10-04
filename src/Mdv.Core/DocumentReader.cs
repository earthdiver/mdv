using System.Text;

namespace Mdv.Core;

public sealed record MarkdownDocument(string FilePath, string Text, string EncodingName)
{
    public string FileName => Path.GetFileName(FilePath);
    public int LineCount => Text.Length == 0 ? 0 : Text.Count(c => c == '\n') + 1;
}

public static class DocumentReader
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxCharacters = 4 * 1024 * 1024;

    public static async Task<MarkdownDocument> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        filePath = Path.GetFullPath(filePath);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaxBytes) throw new IOException("16 MiBを超えるファイルは開けません。");
        // Bound reads even when another process grows the file while it is being read.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new IOException("16 MiBを超えるファイルは開けません。");
            buffer.Write(chunk, 0, count);
        }
        var (text, name) = Decode(buffer.ToArray());
        if (text.Length > MaxCharacters) throw new IOException("4 Mi文字を超える文書は開けません。");
        return new MarkdownDocument(filePath, text.Replace("\r\n", "\n").Replace('\r', '\n'), name);
    }

    public static (string Text, string Name) Decode(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
            return (new UTF32Encoding(false, true, true).GetString(bytes, 4, bytes.Length - 4), "UTF-32 LE");
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            return (new UTF32Encoding(true, true, true).GetString(bytes, 4, bytes.Length - 4), "UTF-32 BE");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        try { return (new UTF8Encoding(false, true).GetString(bytes), "UTF-8"); }
        catch (DecoderFallbackException)
        {
            var shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (shiftJis.GetString(bytes), "Shift_JIS (CP932)");
        }
    }
}
