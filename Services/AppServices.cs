using UpdateHub.Core;
using UpdateHub.Core.Scanning;
using UpdateHub.Data;
using UpdateHub.Sources;

namespace UpdateHub.Services;

/// <summary>
/// Composition root. Small enough that hand-wiring is clearer than a container, and it
/// makes the dependency order between the database, settings and providers explicit.
/// </summary>
public sealed class AppServices : IDisposable
{
    public LogService Log { get; }
    public SettingsService Settings { get; }
    public Database Db { get; }
    public AppRepository Repo { get; }
    public ReleaseHistoryStore History { get; }
    public CacheStore Cache { get; }
    public AppCatalog Catalog { get; }
    public ProviderRegistry Providers { get; }
    public ScanCoordinator Scanner { get; }
    public UpdateEngine Updater { get; }
    public UninstallEngine Uninstaller { get; }
    public ElevatedUpdateRunner ElevatedUpdater { get; }
    public ExportService Export { get; }
    public SchedulerService Scheduler { get; }
    public NotificationService Notifications { get; }
    public ThemeService Theme { get; }

    public AppServices(string dataRoot)
    {
        Log = new LogService(dataRoot);
        Settings = new SettingsService(dataRoot, Log);

        Db = new Database(dataRoot);
        if (Db.RecoveredFromCorruption)
            Log.Warn($"Database was damaged and has been rebuilt. " +
                     $"{Db.SalvagedIgnoreRules} ignore rule(s) recovered; cached version " +
                     "data will be refetched on the next scan. The damaged file was kept " +
                     "alongside the database with a .corrupt- suffix.");

        Repo = new AppRepository(Db);
        History = new ReleaseHistoryStore(Db);
        Cache = new CacheStore(Db);

        // The registry reads ignore rules during a refresh, so settings needs the repo.
        Settings.Repo = Repo;

        Catalog = new AppCatalog(dataRoot);
        Providers = new ProviderRegistry(Catalog, History, Cache, Settings, Log);
        Scanner = new ScanCoordinator(Settings, Log);
        Updater = new UpdateEngine(Repo, Log, Settings);
        Uninstaller = new UninstallEngine(Repo, Log);
        ElevatedUpdater = new ElevatedUpdateRunner(dataRoot, Log);
        Export = new ExportService(Log);
        Scheduler = new SchedulerService(Log);
        Notifications = new NotificationService(Log)
        {
            Enabled = Settings.Current.NotifyOnUpdates,
        };
        Theme = new ThemeService();

        Log.Info($"Services ready. Catalog: {Catalog.EntryCount} entries, " +
                 $"vendor rules: {Providers.Vendor.RuleCount}, arch: {ArchitectureHelper.Display}");
    }

    public void Dispose()
    {
        Notifications.Dispose();
        Db.Dispose();
    }
}
