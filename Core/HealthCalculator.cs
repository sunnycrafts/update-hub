using UpdateHub.Models;

namespace UpdateHub.Core;

public sealed record HealthFactor(string Label, int Points, string Detail);

public sealed class HealthReport
{
    public int Score { get; init; }
    public HealthBand Band { get; init; }
    public List<HealthFactor> Factors { get; init; } = new();

    public string BandText => Band switch
    {
        HealthBand.Excellent => "Excellent",
        HealthBand.Good => "Good",
        HealthBand.Fair => "Fair",
        _ => "Needs attention",
    };

    public string Summary => Band switch
    {
        HealthBand.Excellent => "Your software is current.",
        HealthBand.Good => "A few things could be updated.",
        HealthBand.Fair => "Several applications are falling behind.",
        _ => "Some applications are seriously out of date.",
    };
}

/// <summary>
/// A single 0-100 figure for the state of installed software, in the spirit of the
/// Windows Security summary. Every deduction is recorded with its reason so the number can
/// be explained rather than merely asserted — an unexplained score is just decoration.
/// </summary>
public static class HealthCalculator
{
    public static HealthReport Compute(IReadOnlyCollection<InstalledApp> apps)
    {
        var considered = apps.Where(a => !a.IsIgnored).ToList();
        if (considered.Count == 0)
            return new HealthReport { Score = 100, Band = HealthBand.Excellent };

        var factors = new List<HealthFactor>();
        double score = 100;

        // Missed security releases are the heaviest single signal.
        var securityApps = considered.Where(a => a.SecurityReleasesMissed > 0).ToList();
        if (securityApps.Count > 0)
        {
            var penalty = Math.Min(30, securityApps.Count * 6);
            score -= penalty;
            factors.Add(new HealthFactor("Security updates missing", -penalty,
                $"{securityApps.Count} application(s) are missing releases that mention " +
                "security fixes."));
        }

        var veryOld = considered.Count(a => a.Age.IsKnown && a.Age.TotalDays >= 730);
        if (veryOld > 0)
        {
            var penalty = Math.Min(25, veryOld * 3);
            score -= penalty;
            factors.Add(new HealthFactor("Very outdated software", -penalty,
                $"{veryOld} application(s) are more than two years behind."));
        }

        var yearOld = considered.Count(a => a.Age.IsKnown
                                         && a.Age.TotalDays is >= 365 and < 730);
        if (yearOld > 0)
        {
            var penalty = Math.Min(15, yearOld * 2);
            score -= penalty;
            factors.Add(new HealthFactor("Over a year old", -penalty,
                $"{yearOld} application(s) are more than a year behind."));
        }

        // Ordinary pending updates: scaled by proportion, so a large library is not
        // punished simply for being large.
        var pending = considered.Count(a => a.UpdateAvailable);
        if (pending > 0)
        {
            var share = (double)pending / considered.Count;
            var penalty = (int)Math.Round(Math.Min(20, share * 60));
            if (penalty > 0)
            {
                score -= penalty;
                factors.Add(new HealthFactor("Pending updates", -penalty,
                    $"{pending} of {considered.Count} applications have a newer version " +
                    $"available ({share:P0})."));
            }
        }

        // Not a fault, but the score should not read as authoritative when a large share
        // of the library could not be checked at all.
        var unknown = considered.Count(a => a.LatestVersion is null);
        if (unknown > 0)
        {
            var share = (double)unknown / considered.Count;
            if (share > 0.25)
            {
                var penalty = (int)Math.Round(Math.Min(10, (share - 0.25) * 20));
                score -= penalty;
                factors.Add(new HealthFactor("Coverage gap", -penalty,
                    $"{unknown} application(s) have no update source, so their state is " +
                    "unknown and not reflected above."));
            }
        }

        var final = (int)Math.Round(Math.Clamp(score, 0, 100));

        if (factors.Count == 0)
            factors.Add(new HealthFactor("Everything current", 0,
                "No pending updates, missed security releases or outdated software."));

        return new HealthReport
        {
            Score = final,
            Band = final >= 90 ? HealthBand.Excellent
                 : final >= 75 ? HealthBand.Good
                 : final >= 55 ? HealthBand.Fair
                 : HealthBand.NeedsAttention,
            Factors = factors,
        };
    }
}
