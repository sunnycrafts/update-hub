using System.IO;
using System.Text.Json;
using UpdateHub.Data;
using UpdateHub.Models;

namespace UpdateHub.Services;

/// <summary>settings.json next to the database, written atomically.</summary>
public sealed class SettingsService
{
    private readonly string _path;
    private readonly LogService _log;

    public AppSettings Current { get; private set; } = new();

    /// <summary>Set once the repository exists; used to read ignore rules during refresh.</summary>
    public AppRepository? Repo { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public SettingsService(string dataRoot, LogService log)
    {
        _log = log;
        // Qualified because this class exposes its own Path property.
        _path = System.IO.Path.Combine(dataRoot, "settings.json");
        Load();
    }

    public string Path => _path;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) { Current = new AppSettings(); return; }
            var json = File.ReadAllText(_path);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _log.Warn($"settings.json unreadable, using defaults: {ex.Message}");
            Current = new AppSettings();
        }

        MigrateStaleTheme();
    }

    /// <summary>
    /// The app briefly supported switching WPF-UI's native theme between Light/Dark/System
    /// before settling on one hardcoded bright design with no dark counterpart. A "Dark"
    /// or "System" value surviving from that period re-themes native controls (checkbox,
    /// combo box, number box, password box) to dark while every custom brush stays
    /// hardcoded light, rendering most Settings-page text as near-white-on-white. Existing
    /// installs self-heal here rather than staying broken until someone notices.
    /// </summary>
    private void MigrateStaleTheme()
    {
        if (Current.Theme == "Light") return;
        _log.Warn($"settings.json had Theme=\"{Current.Theme}\" from before the app " +
                  "settled on one fixed theme; migrating to \"Light\".");
        Current.Theme = "Light";
        Save();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            // Write-then-replace so a crash mid-write cannot leave a truncated file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Error("Could not save settings", ex);
        }
    }

    public void Reset()
    {
        Current = new AppSettings();
        Save();
    }
}
