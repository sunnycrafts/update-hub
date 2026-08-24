using System.IO;
using System.Text;

namespace UpdateHub.Services;

/// <summary>Rolling file log. Cheap, synchronous, and safe to call from any thread.</summary>
public sealed class LogService
{
    private readonly string _path;
    private readonly object _gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public LogService(string dataRoot)
    {
        // Qualified because this class exposes its own Path property.
        var dir = System.IO.Path.Combine(dataRoot, "Logs");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "updatehub.log");
        Roll();
    }

    public string Path => _path;

    private void Roll()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var archive = _path + ".1";
            if (File.Exists(archive)) File.Delete(archive);
            File.Move(_path, archive);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); }
            catch (IOException) { /* logging must never take the app down */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public string ReadTail(int lines = 400)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return "(no log yet)";
                var all = File.ReadAllLines(_path);
                return string.Join(Environment.NewLine, all.TakeLast(lines));
            }
            catch (IOException ex) { return "Could not read log: " + ex.Message; }
        }
    }
}
