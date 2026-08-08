using System.Collections.Concurrent;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageMonitor.Core;

public sealed class NullDiagnosticsLogger : IDiagnosticsLogger
{
    public static NullDiagnosticsLogger Instance { get; } = new();
    private NullDiagnosticsLogger() { }
    public void Debug(string message, IReadOnlyDictionary<string, object?>? data = null) { }
    public void Info(string message, IReadOnlyDictionary<string, object?>? data = null) { }
    public void Warning(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) { }
    public void Error(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) { }
}

/// <summary>Scrubs credentials and common personal identifiers before diagnostics are persisted.</summary>
public static partial class SensitiveDataRedactor
{
    private static readonly string[] SensitiveKeys =
    {
        "token", "access_token", "refresh_token", "api_key", "apikey", "authorization", "cookie",
        "password", "secret", "credential", "client_secret", "private_key", "account_id",
        "organization_id", "user_id", "email"
    };

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var result = value;
        result = EmailRegex().Replace(result, "[redacted-email]");
        result = WindowsUserPathRegex().Replace(result, "$1[redacted]");
        result = UnixUserPathRegex().Replace(result, "$1[redacted]");
        result = BearerRegex().Replace(result, "$1[redacted]");
        result = KeyValueRegex().Replace(result, "$1=[redacted]");
        return result.Length > 8_192 ? result[..8_192] + "…" : result;
    }

    public static object? RedactObject(object? value)
    {
        if (value is null) return null;
        if (value is string text) return Redact(text);
        if (value is Exception exception) return Redact(exception.Message);
        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            return dictionary.ToDictionary(
                pair => pair.Key,
                pair => IsSensitiveKey(pair.Key) ? "[redacted]" : RedactObject(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }
        if (value is IDictionary<string, object?> mutable)
        {
            return mutable.ToDictionary(
                pair => pair.Key,
                pair => IsSensitiveKey(pair.Key) ? "[redacted]" : RedactObject(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }
        if (value is IDictionary legacy)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in legacy)
            {
                var key = Convert.ToString(entry.Key) ?? string.Empty;
                result[key] = IsSensitiveKey(key) ? "[redacted]" : RedactObject(entry.Value);
            }
            return result;
        }
        if (value is IEnumerable<string> strings) return strings.Select(Redact).ToArray();
        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().Select(RedactObject).ToArray();
        }
        return value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset
            ? value
            : Redact(Convert.ToString(value));
    }

    public static bool IsSensitiveKey(string key) => SensitiveKeys.Any(x =>
        string.Equals(key, x, StringComparison.OrdinalIgnoreCase) ||
        key.Contains(x, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\\s]+")]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex(@"(?i)(/(?:Users|home)/)[^/\s]+")]
    private static partial Regex UnixUserPathRegex();

    [GeneratedRegex(@"(?i)(Bearer\s+)[^\s,;]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)(\b(?:token|access_token|refresh_token|api[_-]?key|authorization|cookie|password|secret|account[_-]?id|organization[_-]?id|user[_-]?id|email)\s*[:=]\s*)[^,;\s}]+")]
    private static partial Regex KeyValueRegex();
}

/// <summary>Line-delimited JSON logger with deterministic redaction and bounded message size.</summary>
public sealed class FileDiagnosticsLogger : IDiagnosticsLogger, IDisposable
{
    private readonly string _path;
    private readonly object _sync = new();
    private readonly long _maxBytes;
    private bool _disposed;

    public FileDiagnosticsLogger(string? path = null, long maxBytes = 2_000_000)
    {
        _path = path ?? UsageMonitorPaths.Current.DiagnosticsLogFile;
        _maxBytes = Math.Max(32_768, maxBytes);
    }

    public void Debug(string message, IReadOnlyDictionary<string, object?>? data = null) => Write("debug", message, data, null);
    public void Info(string message, IReadOnlyDictionary<string, object?>? data = null) => Write("info", message, data, null);
    public void Warning(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) => Write("warning", message, data, exception);
    public void Error(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) => Write("error", message, data, exception);

    private void Write(string level, string message, IReadOnlyDictionary<string, object?>? data, Exception? exception)
    {
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
                RotateIfNeeded();
                var entry = new Dictionary<string, object?>
                {
                    ["timestamp"] = DateTimeOffset.UtcNow,
                    ["level"] = level,
                    ["message"] = SensitiveDataRedactor.Redact(message),
                    ["data"] = data is null ? null : SensitiveDataRedactor.RedactObject(data),
                    ["exception"] = exception is null ? null : SensitiveDataRedactor.Redact(exception.ToString())
                };
                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            catch
            {
                // Diagnostics are best effort and must never break a provider refresh.
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < _maxBytes) return;
        var rotated = _path + ".1";
        try
        {
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(_path, rotated);
        }
        catch { }
    }

    public void Dispose() => _disposed = true;
}

public sealed class InMemoryDiagnosticsLogger : IDiagnosticsLogger
{
    private readonly ConcurrentQueue<DiagnosticEntry> _entries = new();
    public IReadOnlyCollection<DiagnosticEntry> Entries => _entries.ToArray();
    public void Debug(string message, IReadOnlyDictionary<string, object?>? data = null) => Add("debug", message, data, null);
    public void Info(string message, IReadOnlyDictionary<string, object?>? data = null) => Add("info", message, data, null);
    public void Warning(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) => Add("warning", message, data, exception);
    public void Error(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) => Add("error", message, data, exception);
    private void Add(string level, string message, IReadOnlyDictionary<string, object?>? data, Exception? exception) =>
        _entries.Enqueue(new DiagnosticEntry(level, SensitiveDataRedactor.Redact(message), data, exception is null ? null : SensitiveDataRedactor.Redact(exception.Message)));
}

public sealed record DiagnosticEntry(string Level, string Message, IReadOnlyDictionary<string, object?>? Data, string? ExceptionMessage);
