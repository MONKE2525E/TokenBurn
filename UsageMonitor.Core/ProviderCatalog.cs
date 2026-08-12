namespace UsageMonitor.Core;

using UsageMonitor.Core.Providers.Antigravity;
using UsageMonitor.Core.Providers.OpenCode;
using UsageMonitor.Core.Providers.Cursor;
using UsageMonitor.Core.Providers.Copilot;
using UsageMonitor.Core.Providers.Devin;
using UsageMonitor.Core.Providers.Grok;

public static class ProviderIds
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
    public const string Antigravity = "antigravity";
    public const string Cursor = "cursor";
    public const string Copilot = "copilot";
    public const string Devin = "devin";
    public const string Grok = "grok";
    public const string OpenCode = "opencode";
    // Kept only for existing cache/API compatibility. These billing-key providers are not part of
    // the Windows product catalog and are intentionally absent from DefaultDescriptors.
    public const string OpenRouter = "openrouter";
    public const string Zai = "z-ai";
}

public static partial class ProviderCatalog
{
    public static IReadOnlyList<ProviderDescriptor> DefaultDescriptors { get; } = new[]
    {
        new ProviderDescriptor(ProviderIds.Codex, "Codex", "codex"),
        new ProviderDescriptor(ProviderIds.ClaudeCode, "Claude Code", "claude"),
        new ProviderDescriptor(ProviderIds.Antigravity, "Antigravity", "antigravity"),
        new ProviderDescriptor(ProviderIds.Cursor, "Cursor", "cursor"),
        new ProviderDescriptor(ProviderIds.Copilot, "Copilot", "copilot"),
        new ProviderDescriptor(ProviderIds.Devin, "Devin", "devin"),
        new ProviderDescriptor(ProviderIds.Grok, "Grok", "grok"),
        new ProviderDescriptor(ProviderIds.OpenCode, "OpenCode", "opencode")
    };

    public static string NormalizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        var descriptor = DefaultDescriptors.FirstOrDefault(candidate =>
            candidate.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
            candidate.DisplayName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        return descriptor?.Id ?? trimmed.ToLowerInvariant();
    }
}

public sealed class StubUsageProvider : IUsageProvider
{
    public StubUsageProvider(ProviderDescriptor descriptor) => Descriptor = descriptor;
    public ProviderDescriptor Descriptor { get; }

    public Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProviderSnapshot.Error(Descriptor,
            $"{Descriptor.DisplayName} is not configured yet.", ProviderErrorCategory.NotConfigured));
    }
}

public sealed class UsageProviderCatalog : IUsageProviderCatalog
{
    private readonly IReadOnlyList<IUsageProvider> _providers;

    public UsageProviderCatalog(IEnumerable<IUsageProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.GroupBy(p => p.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
    }

    public IReadOnlyList<IUsageProvider> Providers => _providers;
    public IUsageProvider? Find(string providerId) =>
        _providers.FirstOrDefault(provider => string.Equals(provider.Descriptor.Id, providerId, StringComparison.OrdinalIgnoreCase));
}

public static partial class ProviderCatalog
{
    public static UsageProviderCatalog CreateDefault(IEnumerable<IUsageProvider>? implementations = null)
    {
        var byId = (implementations ?? Array.Empty<IUsageProvider>())
            .ToDictionary(provider => provider.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        return new UsageProviderCatalog(DefaultDescriptors.Select(descriptor =>
        {
            if (byId.TryGetValue(descriptor.Id, out var implementation)) return implementation;
            if (descriptor.Id.Equals(ProviderIds.Antigravity, StringComparison.OrdinalIgnoreCase))
                return new AntigravityProvider();
            if (descriptor.Id.Equals(ProviderIds.OpenCode, StringComparison.OrdinalIgnoreCase))
                return new OpenCodeProvider();
            if (descriptor.Id.Equals(ProviderIds.Cursor, StringComparison.OrdinalIgnoreCase))
                return new CursorProvider();
            if (descriptor.Id.Equals(ProviderIds.Copilot, StringComparison.OrdinalIgnoreCase))
                return new CopilotProvider();
            if (descriptor.Id.Equals(ProviderIds.Devin, StringComparison.OrdinalIgnoreCase))
                return new DevinProvider();
            if (descriptor.Id.Equals(ProviderIds.Grok, StringComparison.OrdinalIgnoreCase))
                return new GrokProvider();
            if (descriptor.Id.Equals(ProviderIds.ClaudeCode, StringComparison.OrdinalIgnoreCase) &&
                byId.TryGetValue("claude", out implementation))
                return new ProviderIdAdapter(implementation, descriptor);
            return new StubUsageProvider(descriptor);
        }));
    }

    private sealed class ProviderIdAdapter(IUsageProvider inner, ProviderDescriptor descriptor) : IUsageProvider
    {
        public ProviderDescriptor Descriptor => descriptor;

        public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.RefreshAsync(context, cancellationToken).ConfigureAwait(false);
            return snapshot with { ProviderId = descriptor.Id, DisplayName = descriptor.DisplayName };
        }
    }
}
