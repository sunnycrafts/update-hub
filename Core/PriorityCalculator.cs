using UpdateHub.Models;

namespace UpdateHub.Core;

/// <summary>
/// Turns "an update exists" into "how much should you care". Every list sorts on this, so
/// the first rows a user sees are the ones worth acting on rather than whatever happens to
/// start with the letter A.
/// </summary>
public static class PriorityCalculator
{
    public static UpdatePriority For(InstalledApp app)
    {
        if (app.IsIgnored) return UpdatePriority.Ignored;
        if (!app.UpdateAvailable) return UpdatePriority.None;

        // A published security fix you do not have outranks everything else.
        if (app.SecurityReleasesMissed > 0) return UpdatePriority.Critical;

        var years = app.Age.IsKnown ? app.Age.TotalDays / 365.0 : 0;

        // Badly outdated *and* a major version behind is the shape of software that has
        // been ignored for years rather than merely missed a release.
        if (app.MajorVersionsBehind > 0 && years >= 2) return UpdatePriority.Critical;

        if (app.MajorVersionsBehind > 0) return UpdatePriority.High;
        if (years >= 2) return UpdatePriority.High;
        if (years >= 1) return UpdatePriority.Recommended;

        // Security-sensitive software is held to a tighter schedule: a browser three
        // months behind matters more than a wallpaper tool three months behind.
        bool exposed = app.Category is AppCategory.Browser or AppCategory.Security
                                    or AppCategory.Communication;
        if (exposed && app.Age.IsKnown && app.Age.TotalDays >= 60) return UpdatePriority.Recommended;

        if (app.Age.IsKnown && app.Age.TotalDays >= 90) return UpdatePriority.Recommended;

        return UpdatePriority.Optional;
    }

    public static string Describe(UpdatePriority priority) => priority switch
    {
        UpdatePriority.Critical => "Critical",
        UpdatePriority.High => "High",
        UpdatePriority.Recommended => "Recommended",
        UpdatePriority.Optional => "Optional",
        UpdatePriority.Ignored => "Ignored",
        _ => "Up to date",
    };

    /// <summary>Plain-English reason, shown in the detail pane so the rating is auditable.</summary>
    public static string Explain(InstalledApp app)
    {
        if (app.IsIgnored) return "You have chosen to ignore this app.";
        if (!app.UpdateAvailable)
            return app.LatestVersion is null
                ? "No update source could be identified for this app."
                : "You are on the newest published version.";

        if (app.SecurityReleasesMissed > 0)
            return $"{app.SecurityReleasesMissed} release(s) published since yours mention " +
                   "security fixes or CVEs.";

        var years = app.Age.IsKnown ? app.Age.TotalDays / 365.0 : 0;

        if (app.MajorVersionsBehind > 0 && years >= 2)
            return $"{app.MajorVersionsBehind} major version(s) behind and more than " +
                   "two years old.";
        if (app.MajorVersionsBehind > 0)
            return $"{app.MajorVersionsBehind} major version(s) behind.";
        if (years >= 2) return "Your version is more than two years old.";
        if (years >= 1) return "Your version is more than a year old.";

        if (app.Category is AppCategory.Browser or AppCategory.Security or AppCategory.Communication
            && app.Age.IsKnown && app.Age.TotalDays >= 60)
            return "Internet-facing software is held to a tighter update schedule.";

        return app.Age.IsKnown
            ? $"A newer version has been available for {app.Age.Short.ToLowerInvariant()}."
            : "A newer version is available.";
    }
}
