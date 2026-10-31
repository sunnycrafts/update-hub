using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHub.Core;
using UpdateHub.Data;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.UI.ViewModels;

/// <summary>
/// Single view model shared by every page, so a scan started on one page is reflected on
/// all of them without any cross-page plumbing.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private CancellationTokenSource? _cts;

    public ObservableCollection<InstalledApp> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public ObservableCollection<UpdateRecord> History { get; } = new();

    public MainViewModel(AppServices services)
    {
        _s = services;
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;
        ApplySort();

        LoadCached();
    }

    // ---- State -----------------------------------------------------------

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate = true;
    [ObservableProperty] private InstalledApp? _selectedApp;
    [ObservableProperty] private string _currentPage = "dashboard";

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value)
    {
        _query = SearchQuery.Parse(value);
        AppsView.Refresh();
        OnPropertyChanged(nameof(ResultCountText));
    }

    private SearchQuery _query = SearchQuery.Parse(null);

    /// <summary>all | attention | updates | current | unknown | ignored</summary>
    [ObservableProperty] private string _filterMode = "all";
    partial void OnFilterModeChanged(string value)
    {
        AppsView.Refresh();
        OnPropertyChanged(nameof(ResultCountText));
    }

    /// <summary>priority | name | age | size</summary>
    [ObservableProperty] private string _sortMode = "priority";
    partial void OnSortModeChanged(string value) => ApplySort();

    public string ResultCountText
    {
        get
        {
            var shown = AppsView.Cast<InstalledApp>().Count();
            return shown == Apps.Count
                ? $"{Apps.Count} applications"
                : $"{shown} of {Apps.Count} applications";
        }
    }

    /// <summary>
    /// Default ordering is by urgency, not alphabet: the first rows should be the ones
    /// worth acting on. Applied to the collection view so it survives filtering.
    /// </summary>
    private void ApplySort()
    {
        using (AppsView.DeferRefresh())
        {
            AppsView.SortDescriptions.Clear();
            switch (SortMode)
            {
                case "name":
                    AppsView.SortDescriptions.Add(
                        new SortDescription(nameof(InstalledApp.Name), ListSortDirection.Ascending));
                    break;
                case "age":
                    AppsView.SortDescriptions.Add(
                        new SortDescription("Age.TotalDays", ListSortDirection.Descending));
                    break;
                case "size":
                    AppsView.SortDescriptions.Add(new SortDescription(
                        nameof(InstalledApp.EstimatedSizeKb), ListSortDirection.Descending));
                    break;
                default:
                    AppsView.SortDescriptions.Add(new SortDescription(
                        nameof(InstalledApp.Priority), ListSortDirection.Descending));
                    AppsView.SortDescriptions.Add(new SortDescription(
                        nameof(InstalledApp.SecurityReleasesMissed), ListSortDirection.Descending));
                    AppsView.SortDescriptions.Add(
                        new SortDescription("Age.TotalDays", ListSortDirection.Descending));
                    AppsView.SortDescriptions.Add(
                        new SortDescription(nameof(InstalledApp.Name), ListSortDirection.Ascending));
                    break;
            }
        }
        OnPropertyChanged(nameof(ResultCountText));
    }

    partial void OnSelectedAppChanged(InstalledApp? value)
    {
        SelectedHistory.Clear();
        if (value is null) return;
        try
        {
            foreach (var rel in _s.History.GetHistory(Sources.ProviderRegistry.ProductKey(value)).Take(40))
                SelectedHistory.Add(rel);
        }
        catch (Exception ex)
        {
            // Selecting a row must never take the app down; the history panel is
            // supplementary and the rest of the detail pane still renders.
            _s.Log.Error($"Could not load release history for {value.Name}", ex);
        }
        OnPropertyChanged(nameof(HasSelection));
    }

    public ObservableCollection<ReleaseInfo> SelectedHistory { get; } = new();
    public bool HasSelection => SelectedApp is not null;

    // ---- Dashboard statistics -------------------------------------------

    public int TotalApps => Apps.Count;
    public int NeedUpdate => Apps.Count(a => a.UpdateAvailable && !a.IsIgnored);
    public int UpToDate => Apps.Count(a => a.LatestVersion is not null && !a.UpdateAvailable);
    public int VeryOutdated => Apps.Count(a => a.Status is AppStatus.VeryOutdated or AppStatus.Major);
    public int IgnoredCount => Apps.Count(a => a.IsIgnored);
    public int UnknownCount => Apps.Count(a => a.LatestVersion is null);
    public int SecurityMissed => Apps.Sum(a => a.SecurityReleasesMissed);

    public int CriticalCount => Apps.Count(a => a.Priority == UpdatePriority.Critical);
    public int HighCount => Apps.Count(a => a.Priority == UpdatePriority.High);
    public int RecommendedCount => Apps.Count(a => a.Priority == UpdatePriority.Recommended);
    public int OptionalCount => Apps.Count(a => a.Priority == UpdatePriority.Optional);
    public int OverTwoYears => Apps.Count(a => a.Age.IsKnown && a.Age.TotalDays >= 730);

    private HealthReport _health = new();
    public int HealthScore => _health.Score;
    public string HealthBandText => _health.BandText;
    public string HealthSummary => _health.Summary;
    public List<HealthFactor> HealthFactors => _health.Factors;

    // ---- Insights --------------------------------------------------------

    public InstalledApp? LargestApp => Apps
        .Where(a => a.EstimatedSizeKb > 0)
        .OrderByDescending(a => a.EstimatedSizeKb).FirstOrDefault();

    public InstalledApp? MostReleasesMissed => Apps
        .Where(a => a.ReleasesMissed > 0)
        .OrderByDescending(a => a.ReleasesMissed).FirstOrDefault();

    public int NoSourceCount => Apps.Count(a => a.LatestVersion is null);

    public List<CategoryCount> CategoryBreakdown => Apps
        .GroupBy(a => a.CategoryText)
        .Select(g => new CategoryCount(g.Key, g.Count(),
            g.Count(x => x.UpdateAvailable), g.Sum(x => x.EstimatedSizeKb)))
        .OrderByDescending(x => x.Count)
        .ToList();

    public string TotalFootprint
    {
        get
        {
            var kb = Apps.Sum(a => a.EstimatedSizeKb);
            return kb <= 0 ? "Unknown" : $"{kb / 1024.0 / 1024.0:F1} GB";
        }
    }

    /// <summary>Mean age across apps whose age is actually known, in months.</summary>
    public string AverageAge
    {
        get
        {
            var known = Apps.Where(a => a.Age.IsKnown && a.Age.TotalDays > 0).ToList();
            if (known.Count == 0) return "—";
            var avgDays = known.Average(a => a.Age.TotalDays);
            return avgDays < 60
                ? $"{avgDays:F0} days"
                : $"{avgDays / 30.44:F1} months";
        }
    }

    public InstalledApp? OldestApp => Apps
        .Where(a => a.Age.IsKnown && a.Age.TotalDays > 0)
        .OrderByDescending(a => a.Age.TotalDays)
        .FirstOrDefault();

    public InstalledApp? NewestInstalled => Apps
        .Where(a => a.InstallDate is not null)
        .OrderByDescending(a => a.InstallDate)
        .FirstOrDefault();

    public string LastScanText => _s.Settings.Current.LastScan is { } d
        ? Relative(d) : "Never";

    public string LastUpdateText => _s.Settings.Current.LastUpdate is { } d
        ? Relative(d) : "Never";

    public string GitHubBudget => _s.Providers.GitHub.RateStatusText;
    public string CacheText => $"{_s.History.CountAll():N0} releases · {_s.Cache.Count():N0} responses · {_s.Db.SizeBytes / 1024:N0} KB";
    public string CatalogText => $"{_s.Catalog.EntryCount} products, {_s.Providers.Vendor.RuleCount} vendor rules";
    public string ArchitectureText => Sources.ArchitectureHelper.Display;

    private static string Relative(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{span.TotalMinutes:F0} min ago";
        if (span.TotalHours < 24) return $"{span.TotalHours:F0} hr ago";
        if (span.TotalDays < 30) return $"{span.TotalDays:F0} days ago";
        return when.ToString("d MMM yyyy");
    }

    /// <summary>Everything worth looking at today, worst first.</summary>
    public List<InstalledApp> NeedsAttention => Apps
        .Where(a => a.Priority >= UpdatePriority.High)
        .OrderByDescending(a => a.Priority)
        .ThenByDescending(a => a.Age.IsKnown ? a.Age.TotalDays : 0)
        .Take(8)
        .ToList();

    /// <summary>Recomputes the derived dashboard/insights figures.</summary>
    public void RefreshStats() => RaiseStats();

    private void RaiseStats()
    {
        _health = HealthCalculator.Compute(Apps);
        foreach (var name in new[]
        {
            nameof(TotalApps), nameof(NeedUpdate), nameof(UpToDate), nameof(VeryOutdated),
            nameof(IgnoredCount), nameof(UnknownCount), nameof(SecurityMissed),
            nameof(AverageAge), nameof(OldestApp), nameof(NewestInstalled),
            nameof(LastScanText), nameof(LastUpdateText), nameof(GitHubBudget),
            nameof(CacheText), nameof(TopOutdated), nameof(CriticalCount), nameof(HighCount),
            nameof(RecommendedCount), nameof(OptionalCount), nameof(OverTwoYears),
            nameof(HealthScore), nameof(HealthBandText), nameof(HealthSummary),
            nameof(HealthFactors), nameof(LargestApp), nameof(MostReleasesMissed),
            nameof(NoSourceCount), nameof(CategoryBreakdown), nameof(TotalFootprint),
            nameof(ResultCountText), nameof(NeedsAttention),
        }) OnPropertyChanged(name);
    }

    /// <summary>The worst offenders, for the dashboard list.</summary>
    public List<InstalledApp> TopOutdated => Apps
        .Where(a => a.UpdateAvailable && !a.IsIgnored && a.Age.IsKnown && a.Age.TotalDays > 0)
        .OrderByDescending(a => a.Age.TotalDays)
        .Take(8)
        .ToList();

    // ---- Filtering -------------------------------------------------------

    private bool FilterApp(object obj)
    {
        if (obj is not InstalledApp app) return false;

        var mode = FilterMode;
        // The Ignored page is the only place ignored apps belong.
        if (mode != "ignored" && app.IsIgnored) return false;

        bool modeOk = mode switch
        {
            "updates" => app.UpdateAvailable,
            "attention" => app.Priority >= UpdatePriority.High,
            "current" => app.LatestVersion is not null && !app.UpdateAvailable,
            "unknown" => app.LatestVersion is null,
            "ignored" => app.IsIgnored,
            _ => true,
        };
        if (!modeOk) return false;

        return _query.IsEmpty || _query.Matches(app);
    }

    // ---- Loading ---------------------------------------------------------

    private void LoadCached()
    {
        try
        {
            var cached = _s.Repo.LoadApps();
            if (cached.Count == 0) return;
            var ignores = _s.Repo.LoadIgnores();
            foreach (var app in cached.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (ignores.TryGetValue(app.Key, out var rule))
                {
                    app.IsIgnored = rule.Reason is not null || rule.Skipped is null;
                    app.IgnoreReason = rule.Reason;
                    app.SkippedVersion = rule.Skipped;
                }
                Apps.Add(app);
            }
            StatusText = $"{Apps.Count} applications from the last scan";
            RaiseStats();
            ReloadHistory();
        }
        catch (Exception ex)
        {
            _s.Log.Error("Could not load cached inventory", ex);
        }
    }

    public void ReloadHistory()
    {
        History.Clear();
        foreach (var record in _s.Repo.LoadHistory()) History.Add(record);
    }

    // ---- Commands --------------------------------------------------------

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        await RunAsync(async ct =>
        {
            ProgressIndeterminate = true;
            StatusText = "Scanning installed software…";

            var found = await Task.Run(() => _s.Scanner.ScanAllAsync(ct,
                new Progress<string>(m => StatusText = m)), ct);

            var ignores = _s.Repo.LoadIgnores();
            Apps.Clear();
            foreach (var app in found)
            {
                if (ignores.TryGetValue(app.Key, out var rule))
                {
                    app.IsIgnored = rule.Reason is not null || rule.Skipped is null;
                    app.IgnoreReason = rule.Reason;
                    app.SkippedVersion = rule.Skipped;
                }
                Apps.Add(app);
            }
            RaiseStats();

            ProgressIndeterminate = false;
            await RefreshCoreAsync(found, forceRefresh: false, ct);

            _s.Settings.Current.LastScan = DateTime.Now;
            _s.Settings.Save();
            // Preserves versions learned earlier when this scan was offline or rate limited.
            _s.Repo.CarryForwardKnownVersions(found);
            _s.Repo.SaveApps(found);

            StatusText = $"{Apps.Count} applications · {NeedUpdate} need updating";
            RaiseStats();

            if (_s.Settings.Current.NotifyOnUpdates && NeedUpdate > 0)
                _s.Notifications.Show("UpdateHub", $"{NeedUpdate} application(s) need updating.");
        });
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (IsBusy || Apps.Count == 0) return;
        await RunAsync(async ct =>
        {
            ProgressIndeterminate = false;
            await RefreshCoreAsync(Apps.ToList(), forceRefresh: true, ct);
            _s.Repo.SaveApps(Apps.ToList());
            StatusText = $"Refreshed · {NeedUpdate} need updating";
            RaiseStats();
        });
    }

    [RelayCommand]
    private async Task RefreshSelectedAsync()
    {
        if (IsBusy || SelectedApp is null) return;
        var app = SelectedApp;
        await RunAsync(async ct =>
        {
            ProgressIndeterminate = true;
            StatusText = $"Refreshing {app.Name}…";
            await _s.Providers.RefreshOneAsync(app, true, _s.Repo.LoadIgnores(), ct);
            RefreshRow(app);
            _s.Repo.SaveApps(Apps.ToList());
            StatusText = $"{app.Name} refreshed";
            RaiseStats();
        });
    }

    private async Task RefreshCoreAsync(List<InstalledApp> targets, bool forceRefresh, CancellationToken ct)
    {
        var progress = new Progress<(int Done, int Total, string Label)>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            StatusText = $"Checking versions… {p.Done}/{p.Total}";
        });

        await Task.Run(() => _s.Providers.RefreshAllAsync(targets, forceRefresh, progress, ct), ct);

        foreach (var app in targets) RefreshRow(app);
    }

    /// <summary>
    /// Providers mutate the app objects off the UI thread; the grid only re-reads a row
    /// when the item is replaced or a change is announced, so nudge each one.
    /// </summary>
    private void RefreshRow(InstalledApp app)
    {
        var index = Apps.IndexOf(app);
        if (index >= 0)
        {
            Apps.RemoveAt(index);
            Apps.Insert(index, app);
        }
        if (ReferenceEquals(app, SelectedApp)) OnSelectedAppChanged(app);
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        if (IsBusy || SelectedApp is null) return;
        await UpdateManyAsync(new[] { SelectedApp });
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var targets = Apps.Where(a => a.UpdateAvailable && !a.IsIgnored
                                   && a.UpdateMethod is UpdateMethod.Winget
                                      or UpdateMethod.Chocolatey or UpdateMethod.Scoop
                                      or UpdateMethod.MsStore)
                          .ToList();
        if (targets.Count == 0)
        {
            StatusText = "Nothing to update automatically";
            return;
        }
        await UpdateManyAsync(targets);
    }

    public async Task UpdateManyAsync(IReadOnlyList<InstalledApp> targets)
    {
        if (IsBusy || targets.Count == 0) return;
        await RunAsync(async ct =>
        {
            ProgressIndeterminate = false;
            var outcomes = new Dictionary<string, UpdateOutcome>();

            // winget-capable apps go through the single-prompt elevated batch when
            // enabled; everything else (Chocolatey, Scoop, direct installers) still goes
            // through the inline engine, which elevates reactively per app as before.
            var useElevatedBatch = _s.Settings.Current.UseElevatedBackgroundUpdates;
            var wingetTargets = useElevatedBatch
                ? targets.Where(a => a.UpdateMethod == UpdateMethod.Winget
                                   && !string.IsNullOrWhiteSpace(a.WingetId)).ToList()
                : new List<InstalledApp>();
            var inlineTargets = targets.Except(wingetTargets).ToList();

            if (wingetTargets.Count > 0)
            {
                StatusText = wingetTargets.Count == 1
                    ? $"Requesting administrator approval for {wingetTargets[0].Name}…"
                    : $"Requesting administrator approval for {wingetTargets.Count} updates…";

                var elevatedProgress = new Progress<ElevatedBatchProgress>(p =>
                {
                    StatusText = p.Total > 1
                        ? $"[{p.Done}/{p.Total}] {p.CurrentName}"
                        : $"{p.CurrentName}…";
                    if (p.Total > 0) ProgressValue = 100.0 * p.Done / p.Total;
                });

                var elevatedResults = await Task.Run(
                    () => _s.ElevatedUpdater.RunAsync(wingetTargets, elevatedProgress, ct), ct);

                foreach (var result in elevatedResults)
                {
                    var app = wingetTargets.FirstOrDefault(a =>
                        string.Equals(a.WingetId, result.WingetId, StringComparison.OrdinalIgnoreCase));
                    if (app is null) continue;
                    _s.Updater.RecordElevatedResult(app, result.Outcome, result.Message);
                    outcomes[app.Key] = result.Outcome;
                }
            }

            if (inlineTargets.Count > 0)
            {
                var inlineProgress = new Progress<UpdateProgress>(p =>
                {
                    StatusText = p.Total > 1
                        ? $"[{p.Index}/{p.Total}] {p.App.Name}: {p.Message}"
                        : $"{p.App.Name}: {p.Message}";
                    if (p.Total > 0) ProgressValue = 100.0 * p.Index / p.Total;
                });

                var inlineResults = await Task.Run(
                    () => _s.Updater.UpdateManyAsync(inlineTargets, inlineProgress, ct), ct);
                foreach (var kv in inlineResults) outcomes[kv.Key] = kv.Value;
            }

            foreach (var app in targets) RefreshRow(app);
            _s.Repo.SaveApps(Apps.ToList());
            ReloadHistory();

            int ok = outcomes.Count(r => r.Value == UpdateOutcome.Success);
            int failed = outcomes.Count(r => r.Value is UpdateOutcome.Failed or UpdateOutcome.RequiresElevation);
            StatusText = $"Updated {ok} of {targets.Count}" + (failed > 0 ? $", {failed} failed" : "");
            RaiseStats();

            if (_s.Settings.Current.NotifyOnUpdates)
                _s.Notifications.Show("UpdateHub", StatusText);
        });
    }

    [RelayCommand]
    private void ToggleIgnore(InstalledApp? app)
    {
        app ??= SelectedApp;
        if (app is null) return;
        var nowIgnored = !app.IsIgnored;
        _s.Repo.SetIgnored(app, nowIgnored, nowIgnored ? "Ignored by user" : null);
        app.IsIgnored = nowIgnored;
        app.IgnoreReason = nowIgnored ? "Ignored by user" : null;
        RefreshRow(app);
        AppsView.Refresh();
        RaiseStats();
        StatusText = nowIgnored ? $"{app.Name} ignored" : $"{app.Name} no longer ignored";
    }

    [RelayCommand]
    private void SkipVersion(InstalledApp? app)
    {
        app ??= SelectedApp;
        if (app?.LatestVersion is null) return;
        _s.Repo.SkipVersion(app, app.LatestVersion);
        app.SkippedVersion = app.LatestVersion;
        app.UpdateAvailable = false;
        RefreshRow(app);
        AppsView.Refresh();
        RaiseStats();
        StatusText = $"Skipping {app.Name} {app.SkippedVersion}";
    }

    public async Task RollbackAsync(InstalledApp app, string version)
    {
        await RunAsync(async ct =>
        {
            ProgressIndeterminate = true;
            StatusText = $"Rolling {app.Name} back to {version}…";
            var (outcome, message) = await Task.Run(
                () => _s.Updater.RollbackAsync(app, version, ct), ct);
            RefreshRow(app);
            _s.Repo.SaveApps(Apps.ToList());
            ReloadHistory();
            StatusText = $"{app.Name}: {message}";
            RaiseStats();
            if (outcome != UpdateOutcome.Success)
                _s.Log.Warn($"Rollback of {app.Name} to {version} did not succeed: {message}");
        });
    }

    /// <summary>
    /// Removes an app. Confirmation is the caller's job — this is irreversible and the
    /// view model does not put up dialogs.
    /// </summary>
    public async Task UninstallAsync(InstalledApp app)
    {
        await RunAsync(async ct =>
        {
            ProgressIndeterminate = true;
            StatusText = $"Uninstalling {app.Name}…";

            var (outcome, message) = await Task.Run(
                () => _s.Uninstaller.UninstallAsync(app, ct), ct);

            ReloadHistory();

            if (outcome == UpdateOutcome.Success)
            {
                Apps.Remove(app);
                if (ReferenceEquals(SelectedApp, app)) SelectedApp = null;
                _s.Repo.SaveApps(Apps.ToList());
            }
            else RefreshRow(app);

            StatusText = $"{app.Name}: {message}";
            RaiseStats();
        });
    }

    /// <summary>Writes the current (filtered) view to disk in the requested format.</summary>
    public void Export(string path, ExportFormat format, bool filteredOnly)
    {
        var rows = filteredOnly
            ? AppsView.Cast<InstalledApp>().ToList()
            : Apps.ToList();
        _s.Export.Export(rows, path, format);
        StatusText = $"Exported {rows.Count} applications to {System.IO.Path.GetFileName(path)}";
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
    }

    public bool CanCancel => _cts is { IsCancellationRequested: false } && IsBusy;

    /// <summary>Wraps a long operation with busy state, cancellation and error reporting.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        OnPropertyChanged(nameof(CanCancel));
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            _s.Log.Error("Operation failed", ex);
            StatusText = "Failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            ProgressIndeterminate = true;
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    public AppServices Services => _s;
}

/// <summary>One row of the Insights category table.</summary>
public sealed record CategoryCount(string Label, int Count, int Outdated, long SizeKb)
{
    public string SizeText => SizeKb <= 0
        ? "—"
        : SizeKb >= 1024 * 1024 ? $"{SizeKb / 1024.0 / 1024.0:F1} GB"
                                : $"{SizeKb / 1024.0:F0} MB";
}
