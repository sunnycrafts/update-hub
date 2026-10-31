using System.Management;

namespace UpdateHub.Core;

public sealed class MachineInfo
{
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Cpu { get; set; } = "";
    public List<string> Gpus { get; set; } = new();
    public double RamGb { get; set; }
    public string Os { get; set; } = "";
    public string Bios { get; set; } = "";

    public string Summary =>
        $"{Manufacturer} {Model}".Trim() + Environment.NewLine +
        $"Processor: {Cpu}" + Environment.NewLine +
        $"Graphics: {(Gpus.Count == 0 ? "unknown" : string.Join("; ", Gpus))}" + Environment.NewLine +
        $"Memory: {RamGb:0.#} GB" + Environment.NewLine +
        $"Windows: {Os}" + Environment.NewLine +
        $"BIOS / firmware: {Bios}";
}

public sealed class DriverInfo
{
    public string Device { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? Date { get; set; }
    public bool NewerOnWindowsUpdate { get; set; }

    public string DateText => Date?.ToString("d MMM yyyy") ?? "Unknown";

    /// <summary>Plain-English age. Old is not automatically bad, so wording stays neutral.</summary>
    public string AgeText
    {
        get
        {
            if (NewerOnWindowsUpdate) return "Newer version available";
            if (Date is null) return "Unknown age";
            var years = (DateTime.Now - Date.Value).TotalDays / 365.25;
            return years < 1 ? "Recent" : years < 2 ? $"{years:0.#} years old"
                 : years < 4 ? $"{years:0.#} years old" : $"{years:0.#} years old (very old)";
        }
    }

    /// <summary>0 good, 1 worth a look, 2 attention.</summary>
    public int Level
    {
        get
        {
            if (NewerOnWindowsUpdate) return 2;
            if (Date is null) return 1;
            var years = (DateTime.Now - Date.Value).TotalDays / 365.25;
            return years < 2 ? 0 : years < 4 ? 1 : 2;
        }
    }

    public string VendorUrl => DriverScanner.VendorPage(Manufacturer, Device, DeviceClass);
}

public sealed class PendingDriver
{
    public string Title { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public DateTime? Date { get; set; }
}

/// <summary>
/// Reads this PC's hardware and driver inventory locally (WMI), and asks Windows Update
/// which driver updates it has for this PC. Nothing is sent anywhere by UpdateHub itself:
/// the only network traffic is Windows Update's own, exactly as when "Check for updates"
/// is pressed in Windows Settings. Serial numbers and hardware IDs are never read.
/// </summary>
public static class DriverScanner
{
    private static readonly string[] WantedClasses =
    {
        "DISPLAY", "NET", "MEDIA", "BLUETOOTH", "SYSTEM", "HDC", "SCSIADAPTER",
        "FIRMWARE", "CAMERA", "IMAGE", "HIDCLASS", "BATTERY", "PRINTER", "DISKDRIVE",
    };

    public static MachineInfo ReadMachine()
    {
        var m = new MachineInfo();
        string Q(string cls, string prop)
        {
            try
            {
                using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
                foreach (ManagementBaseObject o in s.Get())
                    return (o[prop]?.ToString() ?? "").Trim();
            }
            catch (Exception) { }
            return "";
        }

        m.Manufacturer = Q("Win32_ComputerSystem", "Manufacturer");
        m.Model = Q("Win32_ComputerSystem", "Model");
        m.Cpu = Q("Win32_Processor", "Name");
        m.Os = Q("Win32_OperatingSystem", "Caption") + " " + Q("Win32_OperatingSystem", "Version");
        if (double.TryParse(Q("Win32_ComputerSystem", "TotalPhysicalMemory"), out var bytes))
            m.RamGb = bytes / 1024 / 1024 / 1024;

        var biosVer = Q("Win32_BIOS", "SMBIOSBIOSVersion");
        var biosDate = Q("Win32_BIOS", "ReleaseDate");
        string? date = null;
        try { if (biosDate.Length > 0) date = ManagementDateTimeConverter.ToDateTime(biosDate).ToString("d MMM yyyy"); }
        catch (Exception) { }
        m.Bios = date is null ? biosVer : $"{biosVer} ({date})";

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementBaseObject o in s.Get())
            {
                var n = (o["Name"]?.ToString() ?? "").Trim();
                if (n.Length > 0) m.Gpus.Add(n);
            }
        }
        catch (Exception) { }
        return m;
    }

    public static List<DriverInfo> ReadDrivers()
    {
        var list = new List<DriverInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT DeviceName, DeviceClass, Manufacturer, DriverVersion, DriverDate " +
                "FROM Win32_PnPSignedDriver WHERE DriverVersion IS NOT NULL");
            foreach (ManagementBaseObject o in s.Get())
            {
                var cls = (o["DeviceClass"]?.ToString() ?? "").ToUpperInvariant();
                var name = (o["DeviceName"]?.ToString() ?? "").Trim();
                var maker = (o["Manufacturer"]?.ToString() ?? "").Trim();
                if (name.Length == 0 || !WantedClasses.Contains(cls)) continue;

                // Windows' own generic drivers are neither outdated nor actionable, except
                // for a basic display adapter, which means the real GPU driver is missing.
                var inbox = maker.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                            || maker.StartsWith("(Standard", StringComparison.OrdinalIgnoreCase);
                var basicDisplay = name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);
                if (inbox && !basicDisplay) continue;

                DateTime? date = null;
                try
                {
                    var raw = o["DriverDate"]?.ToString();
                    if (!string.IsNullOrEmpty(raw)) date = ManagementDateTimeConverter.ToDateTime(raw);
                }
                catch (Exception) { }

                list.Add(new DriverInfo
                {
                    Device = name,
                    DeviceClass = Friendly(cls),
                    Manufacturer = maker,
                    Version = (o["DriverVersion"]?.ToString() ?? "").Trim(),
                    Date = date,
                });
            }
        }
        catch (Exception) { }

        return list
            .GroupBy(d => (d.Device, d.Version))
            .Select(g => g.First())
            .OrderByDescending(d => d.Level)
            .ThenBy(d => d.DeviceClass)
            .ThenBy(d => d.Device)
            .ToList();
    }

    /// <summary>
    /// Asks the Windows Update Agent for driver updates that apply to this PC and are not
    /// installed. This is Microsoft's own catalogue, so it only covers drivers Microsoft
    /// distributes; newer vendor-site drivers (often GPUs) will not appear here.
    /// </summary>
    public static List<PendingDriver> ReadPendingFromWindowsUpdate()
    {
        var result = new List<PendingDriver>();
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type is null) return result;
            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic found = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            foreach (dynamic u in found.Updates)
            {
                DateTime? d = null;
                try { d = (DateTime)u.DriverVerDate; } catch (Exception) { }
                result.Add(new PendingDriver
                {
                    Title = (string)u.Title,
                    Manufacturer = SafeString(() => (string)u.DriverManufacturer),
                    Model = SafeString(() => (string)u.DriverModel),
                    DeviceClass = SafeString(() => (string)u.DriverClass),
                    Date = d,
                });
            }
        }
        catch (Exception) { }
        return result;
    }

    /// <summary>Marks installed drivers that Windows Update has a newer version for.</summary>
    public static void Flag(List<DriverInfo> installed, List<PendingDriver> pending)
    {
        foreach (var d in installed)
        {
            d.NewerOnWindowsUpdate = pending.Any(p =>
                p.Model.Length > 0 &&
                (d.Device.Contains(p.Model, StringComparison.OrdinalIgnoreCase)
                 || p.Model.Contains(d.Device, StringComparison.OrdinalIgnoreCase)) &&
                (p.Date is null || d.Date is null || p.Date > d.Date));
        }
    }

    private static string SafeString(Func<string> get)
    {
        try { return get() ?? ""; } catch (Exception) { return ""; }
    }

    private static string Friendly(string cls) => cls switch
    {
        "DISPLAY" => "Graphics",
        "NET" => "Network",
        "MEDIA" => "Sound",
        "BLUETOOTH" => "Bluetooth",
        "SYSTEM" => "System / chipset",
        "HDC" or "SCSIADAPTER" or "DISKDRIVE" => "Storage",
        "FIRMWARE" => "Firmware",
        "CAMERA" or "IMAGE" => "Camera",
        "HIDCLASS" => "Input",
        "BATTERY" => "Battery",
        "PRINTER" => "Printer",
        _ => cls,
    };

    /// <summary>
    /// The right official download page for a maker. Only the maker and device name are
    /// ever put in a link: no serial number, service tag or other machine identifier.
    /// </summary>
    public static string VendorPage(string maker, string device, string deviceClass)
    {
        var m = (maker + " " + device).ToLowerInvariant();
        if (m.Contains("nvidia")) return "https://www.nvidia.com/Download/index.aspx";
        if (m.Contains("advanced micro") || m.Contains("amd ") || m.Contains("radeon"))
            return "https://www.amd.com/en/support/download/drivers.html";
        if (m.Contains("intel")) return "https://www.intel.com/content/www/us/en/support/detect.html";
        if (m.Contains("realtek")) return "https://www.realtek.com/Download/List?cate_id=593";
        if (m.Contains("qualcomm") || m.Contains("atheros"))
            return "https://www.qualcomm.com/support";
        if (m.Contains("mediatek")) return "https://www.mediatek.com/support";
        if (m.Contains("lenovo")) return "https://support.lenovo.com/";
        if (m.Contains("dell")) return "https://www.dell.com/support/home/";
        if (m.Contains("hewlett") || m.Contains("hp ")) return "https://support.hp.com/drivers";
        if (m.Contains("asus")) return "https://www.asus.com/support/download-center/";
        if (m.Contains("acer")) return "https://www.acer.com/support";
        if (m.Contains("msi") || m.Contains("micro-star")) return "https://www.msi.com/support/download";
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString($"{maker} {device} driver download");
    }

    /// <summary>The PC maker's own support page, which is where laptop-specific drivers live.</summary>
    public static string OemPage(MachineInfo m) =>
        VendorPage(m.Manufacturer, m.Model, "");
}
