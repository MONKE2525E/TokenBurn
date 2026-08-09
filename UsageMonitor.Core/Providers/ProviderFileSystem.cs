namespace UsageMonitor.Core.Providers;

/// <summary>File boundary used by credential readers and JSONL scanners for deterministic tests.</summary>
public interface IProviderFileSystem
{
    bool FileExists(string path);
    string? ReadAllText(string path);
    IEnumerable<string> EnumerateFiles(string root, string pattern, SearchOption searchOption);

    /// <summary>
    /// Streams a text file one line at a time. The default keeps fixture file systems source
    /// compatible while the local implementation avoids loading large provider histories into
    /// memory all at once.
    /// </summary>
    IEnumerable<string> ReadLines(string path)
    {
        var text = ReadAllText(path);
        return string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Split('\n');
    }

    /// <summary>
    /// Returns only JSONL records containing one of the requested ASCII markers. The local
    /// implementation finds the matching byte ranges before decoding them, so a multi-megabyte
    /// transcript/tool-result record that cannot affect usage never becomes a managed string.
    /// Test file systems retain the simple ReadLines implementation above.
    /// </summary>
    IEnumerable<string> ReadLinesContaining(string path, params string[] markers)
    {
        if (markers is null || markers.Length == 0) return ReadLines(path);
        return ReadLines(path).Where(line => markers.Any(marker =>
            !string.IsNullOrEmpty(marker) && line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

}

public sealed class LocalProviderFileSystem : IProviderFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public string? ReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public IEnumerable<string> EnumerateFiles(string root, string pattern, SearchOption searchOption)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        try { return Directory.EnumerateFiles(root, pattern, searchOption); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public IEnumerable<string> ReadLines(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        try { return File.ReadLines(path); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public IEnumerable<string> ReadLinesContaining(string path, params string[] markers)
    {
        if (string.IsNullOrWhiteSpace(path) || markers is null || markers.Length == 0)
            return markers is null || markers.Length == 0 ? ReadLines(path) : Array.Empty<string>();

        var patterns = markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Select(marker => System.Text.Encoding.UTF8.GetBytes(marker.ToLowerInvariant()))
            .Where(pattern => pattern.Length > 0)
            .ToArray();
        if (patterns.Length == 0) return Array.Empty<string>();
        return ReadMatchingLines(path, patterns);
    }

    private static IEnumerable<string> ReadMatchingLines(string path, byte[][] patterns)
    {
        List<LineRange> ranges;
        try { ranges = FindMatchingRanges(path, patterns); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var range in ranges)
        {
            string? line;
            try { line = ReadRange(stream, range); }
            catch (IOException) { yield break; }
            catch (UnauthorizedAccessException) { yield break; }
            if (line is not null) yield return line;
        }
    }

    private static List<LineRange> FindMatchingRanges(string path, byte[][] patterns)
    {
        var ranges = new List<LineRange>();
        var matchedLengths = new int[patterns.Length];
        var buffer = new byte[64 * 1024];
        long lineStart = 0;
        long offset = 0;
        var matched = false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++, offset++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (matched) ranges.Add(new LineRange(lineStart, offset - lineStart));
                    lineStart = offset + 1;
                    Array.Clear(matchedLengths);
                    matched = false;
                    continue;
                }

                if (value is >= (byte)'A' and <= (byte)'Z') value = (byte)(value + 32);
                for (var patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
                {
                    var pattern = patterns[patternIndex];
                    var matchedLength = matchedLengths[patternIndex];
                    matchedLength = value == pattern[matchedLength]
                        ? matchedLength + 1
                        : value == pattern[0] ? 1 : 0;
                    if (matchedLength == pattern.Length)
                    {
                        matched = true;
                        matchedLength = 0;
                    }
                    matchedLengths[patternIndex] = matchedLength;
                }
            }
        }
        if (offset > lineStart && matched) ranges.Add(new LineRange(lineStart, offset - lineStart));
        return ranges;
    }

    private static string? ReadRange(FileStream stream, LineRange range)
    {
        if (range.Length <= 0 || range.Length > int.MaxValue) return null;
        var bytes = new byte[(int)range.Length];
        stream.Position = range.Start;
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) return null;
            read += count;
        }
        var length = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
        return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
    }

    private readonly record struct LineRange(long Start, long Length);

}
