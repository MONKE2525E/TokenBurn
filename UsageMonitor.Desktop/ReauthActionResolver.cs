using System.Text.RegularExpressions;
using UsageMonitor.Core;

namespace UsageMonitor.Desktop;

/// <summary>
/// Decides when a provider card may offer a re-sign-in action. The backend already classifies
/// failures (ProviderErrorCategory), so the UI must not re-derive "auth" from free text. The
/// text fallback only covers cached envelopes persisted by older builds that carry a message
/// but no category. The popup frontend mirrors this rule in reauthActionFor.
/// </summary>
internal static class ReauthActionResolver
{
    private static readonly HashSet<string> ReauthCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ProviderErrorCategory.Authentication),
        nameof(ProviderErrorCategory.Authorization),
        nameof(ProviderErrorCategory.NotConfigured)
    };

    public static bool ShouldOfferReauth(string? errorCategory, string? errorText)
    {
        if (!string.IsNullOrWhiteSpace(errorCategory))
            return ReauthCategories.Contains(errorCategory);
        return !string.IsNullOrWhiteSpace(errorText) &&
               Regex.IsMatch(errorText, "auth|login|expired|signed out|not configured|sign.?in",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
