using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UsageMonitor.Core;

/// <summary>
/// Stable logical names for user-entered provider secrets. Values are stored in Windows
/// Credential Manager and are never serialized into Usage Monitor settings or diagnostics.
/// </summary>
public static class ProviderSecretKeys
{
    public const string OpenRouterApiKey = "providers/openrouter/api-key";
    public const string ZaiApiKey = "providers/z-ai/api-key";
}

public sealed class NullSecretStore : ISecretStore
{
    public static NullSecretStore Instance { get; } = new();
    private NullSecretStore() { }
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Windows Credential Manager backed secret storage. The credential blob is additionally protected
/// with user-scoped DPAPI. On non-Windows systems, all operations safely behave like NullSecretStore.
/// </summary>
public sealed class CredentialManagerSecretStore : ISecretStore
{
    private readonly string _targetPrefix;
    private readonly IDiagnosticsLogger _logger;

    public CredentialManagerSecretStore(string targetPrefix = "UsageMonitor/", IDiagnosticsLogger? logger = null)
    {
        _targetPrefix = string.IsNullOrWhiteSpace(targetPrefix) ? "UsageMonitor/" : targetPrefix.TrimEnd('/') + "/";
        _logger = logger ?? NullDiagnosticsLogger.Instance;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult<string?>(null);
        try
        {
            var target = NormalizeTarget(key);
            if (!NativeMethods.CredRead(target, NativeMethods.CredentialTypeGeneric, 0, out var pointer)) return Task.FromResult<string?>(null);
            try
            {
                var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(pointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return Task.FromResult<string?>(null);
                var protectedBytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, protectedBytes, 0, protectedBytes.Length);
                return Task.FromResult(DpapiProtector.Unprotect(protectedBytes));
            }
            finally { NativeMethods.CredFree(pointer); }
        }
        catch (Exception ex) when (ex is Win32Exception or CryptographicException or InvalidOperationException)
        {
            _logger.Warning("Credential read failed", new Dictionary<string, object?> { ["credentialKey"] = key }, ex);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        try
        {
            var protectedBytes = DpapiProtector.Protect(Encoding.UTF8.GetBytes(secret));
            var blob = Marshal.AllocHGlobal(protectedBytes.Length);
            var target = Marshal.StringToCoTaskMemUni(NormalizeTarget(key));
            var comment = Marshal.StringToCoTaskMemUni("Usage Monitor protected secret");
            try
            {
                Marshal.Copy(protectedBytes, 0, blob, protectedBytes.Length);
                var credential = new NativeMethods.CREDENTIAL
                {
                    Type = NativeMethods.CredentialTypeGeneric,
                    TargetName = target,
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)protectedBytes.Length,
                    Persist = NativeMethods.CredentialPersistLocalMachine,
                    UserName = comment
                };
                if (!NativeMethods.CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
                Marshal.FreeCoTaskMem(target);
                Marshal.FreeCoTaskMem(comment);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or CryptographicException or InvalidOperationException)
        {
            _logger.Warning("Credential write failed", new Dictionary<string, object?> { ["credentialKey"] = key }, ex);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        try { NativeMethods.CredDelete(NormalizeTarget(key), NativeMethods.CredentialTypeGeneric, 0); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.Warning("Credential delete failed", new Dictionary<string, object?> { ["credentialKey"] = key }, ex);
        }
        return Task.CompletedTask;
    }

    private string NormalizeTarget(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var safe = key.Trim().Replace('\\', '/');
        return _targetPrefix + safe.TrimStart('/');
    }
}

/// <summary>
/// Reads an existing application's generic Windows Credential Manager value without applying
/// Usage Monitor's DPAPI envelope. This is intentionally read-only and is used only for provider
/// integrations whose companion app owns the credential (for example Antigravity/Gemini).
/// </summary>
public static class WindowsCredentialReader
{
    public static string? ReadGeneric(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!OperatingSystem.IsWindows()) return null;
        if (!NativeMethods.CredRead(target, NativeMethods.CredentialTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return DecodeBlob(bytes);
        }
        catch (Exception ex) when (ex is AccessViolationException or ArgumentException or MarshalDirectiveException)
        {
            return null;
        }
        finally { NativeMethods.CredFree(pointer); }
    }

    private static string? DecodeBlob(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
        if (utf8.Length > 0 && utf8.Count(c => c == '\0') < Math.Max(2, utf8.Length / 8)) return utf8;
        var unicode = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(unicode) ? null : unicode;
    }
}

public static class DpapiProtector
{
    public static string ProtectString(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(plaintext)));
    }

    public static string? UnprotectString(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        try { return Unprotect(Convert.FromBase64String(protectedValue)); }
        catch (FormatException) { return null; }
    }

    public static byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is available only on Windows.");
        return Transform(plaintext, protect: true);
    }

    public static string? Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        if (!OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(Transform(protectedBytes, protect: false)); }
        catch (CryptographicException) { return null; }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPtr = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            var inputBlob = new NativeMethods.DATA_BLOB { Size = (uint)input.Length, Data = inputPtr };
            var outputBlob = new NativeMethods.DATA_BLOB();
            var success = protect
                ? NativeMethods.CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob)
                : NativeMethods.CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, result, 0, result.Length);
                return result;
            }
            finally { NativeMethods.LocalFree(outputBlob.Data); }
        }
        finally { Marshal.FreeHGlobal(inputPtr); }
    }
}

internal static class NativeMethods
{
    public const uint CredentialTypeGeneric = 1;
    public const uint CredentialPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    public static extern void CredFree(IntPtr credential);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, ref DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr handle);
}
