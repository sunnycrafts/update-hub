namespace UpdateHub.Models;

/// <summary>
/// The signature calculation: how far behind the installed version is, expressed the way
/// a person would say it ("4 years 6 months 12 days") rather than as a raw day count.
/// </summary>
public sealed class AgeBehind
{
    public int Years { get; init; }
    public int Months { get; init; }
    public int Days { get; init; }

    /// <summary>Total elapsed days between the two release dates.</summary>
    public int TotalDays { get; init; }

    /// <summary>Whole calendar months elapsed, for sorting and averages.</summary>
    public int TotalMonths { get; init; }

    public bool IsKnown { get; init; }

    public static readonly AgeBehind Unknown = new() { IsKnown = false };

    /// <summary>Installed version is at or ahead of the newest published one.</summary>
    public static readonly AgeBehind UpToDate = new() { IsKnown = true, TotalDays = 0 };

    /// <summary>
    /// Decomposes the gap between two release dates into calendar years/months/days.
    /// Walks the calendar rather than dividing by 365.25 so "1 year" means the same date
    /// a year later, which is what the number is claiming.
    /// </summary>
    public static AgeBehind Between(DateTime? installedRelease, DateTime? latestRelease)
    {
        if (installedRelease is not { } from || latestRelease is not { } to)
            return Unknown;

        from = from.Date;
        to = to.Date;
        if (to < from) return new AgeBehind { IsKnown = true, TotalDays = 0 };

        // Count whole months by advancing from the original anchor each time. Field-wise
        // subtraction with a single borrow gets this wrong whenever the start day is
        // longer than the month being borrowed (31 Jan -> 1 Mar borrows a 28-day
        // February and underflows), and re-adding to a moving probe compounds the
        // month-end clamp. Anchoring on `from` avoids both.
        int totalMonths = 0;
        while (from.AddMonths(totalMonths + 1) <= to) totalMonths++;
        int days = (int)(to - from.AddMonths(totalMonths)).TotalDays;

        return new AgeBehind
        {
            IsKnown = true,
            Years = totalMonths / 12,
            Months = totalMonths % 12,
            Days = days,
            TotalDays = (int)(to - from).TotalDays,
            TotalMonths = totalMonths,
        };
    }

    public AgeBand Band => !IsKnown ? AgeBand.Unknown
        : TotalDays <= 30 ? AgeBand.Current
        : TotalDays <= 92 ? AgeBand.Recent
        : TotalDays <= 365 ? AgeBand.GettingOld
        : TotalDays <= 730 ? AgeBand.Old
        : AgeBand.VeryOutdated;

    // These are properties rather than methods because WPF data binding can only resolve
    // properties; as methods they bound to nothing and rendered as empty cells.

    /// <summary>Short form for table cells: "42 days", "2 years 5 months".</summary>
    public string Short
    {
        get
        {
            if (!IsKnown) return "—";
            if (TotalDays <= 0) return "Up to date";
            // Under a quarter, a plain day count reads better than "1 mo 12 d" — being
            // "42 days behind" is the more useful sentence at that range.
            if (TotalDays <= 90) return $"{TotalDays} day{(TotalDays == 1 ? "" : "s")}";
            if (Years > 0)
                return Months > 0 ? $"{Years} yr {Months} mo" : $"{Years} yr";
            return $"{TotalMonths} mo";
        }
    }

    /// <summary>Full form for the detail pane: "4 years, 6 months, 12 days".</summary>
    public string Long =>
        !IsKnown ? "Unknown"
        : TotalDays <= 0 ? "Up to date"
        : Parts.Count == 0 ? "Up to date"
        : string.Join(", ", Parts);

    /// <summary>The stacked form used on the app detail card, one unit per line.</summary>
    public string[] Stacked =>
        !IsKnown ? new[] { "Unknown" }
        : TotalDays <= 0 ? new[] { "Up to date" }
        : Parts.ToArray();

    private List<string> Parts
    {
        get
        {
            var parts = new List<string>(3);
            if (Years > 0) parts.Add($"{Years} year{(Years == 1 ? "" : "s")}");
            if (Months > 0) parts.Add($"{Months} month{(Months == 1 ? "" : "s")}");
            if (Days > 0) parts.Add($"{Days} day{(Days == 1 ? "" : "s")}");
            return parts;
        }
    }

    public override string ToString() => Short;
}
