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

}
