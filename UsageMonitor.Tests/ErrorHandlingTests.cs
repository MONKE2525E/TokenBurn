using System.Text.Json;
using UsageMonitor.Core;
using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

/// <summary>
/// Error mapping and recovery behavior that users actually see: settings-file recovery,
/// re-auth action decisions, and secret redaction on the error path.
/// </summary>
public sealed class ErrorHandlingTests
{
    private static string NewTempSettingsDirectory()
        => Path.Combine(Path.GetTempPath(), "UsageMonitorTests", "settings", Guid.NewGuid().ToString("N"));

    private sealed class SettingsDirectoryScope : IDisposable
    {
        public string Path { get; } = NewTempSettingsDirectory();

        public SettingsDirectoryScope()
        {
            Directory.CreateDirectory(Path);
            SettingsStore.DirectoryOverride = Path;
            SettingsStore.ResetForTests();
        }

        public void Dispose()
        {
            SettingsStore.DirectoryOverride = null;
            SettingsStore.ResetForTests();
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CorruptSettingsFileIsBackedUpAndDefaultsAreUsed()
    {
        using var scope = new SettingsDirectoryScope();
        File.WriteAllText(Path.Combine(scope.Path, "settings.json"), "{ not valid json !!!");

        var settings = SettingsStore.Load();

        Assert.True(SettingsStore.LastLoadFailed, "the load must be reported as failed");
        Assert.True(File.Exists(Path.Combine(scope.Path, "settings.json.corrupt")),
            "the unreadable file must be preserved for recovery");
        Assert.False(File.Exists(Path.Combine(scope.Path, "settings.json")),
            "the corrupt file must not keep blocking every later launch");
        Assert.Equal(UserSettings.Default.SelectedMonitor, settings.SelectedMonitor);
        Assert.Equal(StatusSurfaceMode.TaskbarWidget, settings.StatusSurface);
    }

    [Fact]
    public void MissingSettingsFileIsNotAFailure()
    {
        using var scope = new SettingsDirectoryScope();

        var settings = SettingsStore.Load();

        Assert.False(SettingsStore.LastLoadFailed, "a missing file is a first-run, not a failure");
        Assert.Equal(UserSettings.Default.SelectedMonitor, settings.SelectedMonitor);
        Assert.False(File.Exists(Path.Combine(scope.Path, "settings.json.corrupt")));
    }

    [Fact]
    public void ValidSettingsRoundTripThroughLoad()
    {
        using var scope = new SettingsDirectoryScope();
        var source = new UserSettings { StartAtLogin = true, UsageDisplay = "Remaining" };
        SettingsStore.Save(source);

        var loaded = SettingsStore.Load();

        Assert.False(SettingsStore.LastLoadFailed);
        Assert.True(loaded.StartAtLogin);
        Assert.Equal("Remaining", loaded.UsageDisplay);
        Assert.Equal(StatusSurfaceMode.TaskbarWidget, loaded.StatusSurface);
        Assert.False(File.Exists(Path.Combine(scope.Path, "settings.json.corrupt")));
    }

    [Fact]
    public void SettingsSaveFailureDoesNotThrow()
    {
        using var scope = new SettingsDirectoryScope();
        // A file where the settings directory is expected makes every write fail with IOException.
        Directory.Delete(scope.Path, recursive: true);
        File.WriteAllText(scope.Path, "occupied");

        SettingsStore.Save(UserSettings.Default);

        Assert.False(File.Exists(Path.Combine(scope.Path, "settings.json")),
            "the failed save must not leave a partial settings file behind");
    }

    [Theory]
    [InlineData("Authentication")]
    [InlineData("Authorization")]
    [InlineData("NotConfigured")]
    public void ReauthActionIsOfferedForReauthCategories(string category)
    {
        Assert.True(ReauthActionResolver.ShouldOfferReauth(category, "unrelated text"),
            $"{category} must be treated as a re-auth state regardless of the message text");
    }

    [Theory]
    [InlineData("Network")]
    [InlineData("RateLimited")]
    [InlineData("Parse")]
    [InlineData("Unsupported")]
    [InlineData("Other")]
    public void ReauthActionIsNotOfferedForTransientCategories(string category)
    {
        Assert.False(ReauthActionResolver.ShouldOfferReauth(category, "Session expired. Sign in again."),
            $"{category} must never offer a sign-in action even when the text looks like auth");
    }

    [Fact]
    public void ReauthActionFallsBackToTextForCategoryLessCachedEnvelopes()
    {
        Assert.True(ReauthActionResolver.ShouldOfferReauth(null, "Claude session expired. Run `claude auth login`, then refresh."));
        Assert.False(ReauthActionResolver.ShouldOfferReauth(null, "Antigravity connection failed."));
        Assert.False(ReauthActionResolver.ShouldOfferReauth(null, null));
        Assert.False(ReauthActionResolver.ShouldOfferReauth(null, string.Empty));
    }
}
