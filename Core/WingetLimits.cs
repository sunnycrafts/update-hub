using UpdateHub.Models;

namespace UpdateHub.Core;

/// <summary>
/// Recognises the winget results that are not real failures: winget cannot do this one
/// (its catalogue is behind the vendor, or the app only updates itself). Reporting those as
/// "Failed" made a mostly successful batch look broken, and kept retrying them every time.
/// </summary>
public static class WingetLimits
{
    /// <summary>The friendly message for a recognised limit, or null for a genuine failure.</summary>
    public static string? Recognise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No newer package versions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No applicable update", StringComparison.OrdinalIgnoreCase)
            || text.Contains("-1978335189", StringComparison.Ordinal)
            || text.Contains("0x8A15002B", StringComparison.OrdinalIgnoreCase))
            return "winget does not have a newer version of this yet (its catalogue lags the " +
                   "publisher). Use Check manually, or wait for winget to catch up.";

        if (text.Contains("No installed package found", StringComparison.OrdinalIgnoreCase))
            return "winget does not manage this installation (the package it matched does not " +
                   "correspond to what is installed), so it cannot update it. Use Check manually.";

        if (text.Contains("install technology is different", StringComparison.OrdinalIgnoreCase))
            return "winget cannot upgrade this in place because the new version uses a different " +
                   "installer type. It normally updates itself.";

        if (text.Contains("cannot be upgraded using WinGet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("method provided by the publisher", StringComparison.OrdinalIgnoreCase))
            return "This app has to be updated with its own updater, not through winget.";

        return null;
    }

    /// <summary>
    /// After a recognised limit, stop offering this app to "Update all" (it would fail the
    /// same way again) while leaving the update flag on, so it still shows as behind.
    /// </summary>
    public static void MarkNotAutomatable(InstalledApp app) => app.UpdateMethod = UpdateMethod.Manual;
}
