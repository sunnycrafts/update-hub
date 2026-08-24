using UpdateHub.Models;

namespace UpdateHub.Core;

/// <summary>
/// Assigns each app a category and a size weight so a list of 300 entries can be read at a
/// glance — which rows are a 6 GB design suite and which are a 2 MB tray utility.
/// Classification is a heuristic over the signals the scanners already collect; it is
/// never used to decide anything, only to colour and group.
/// </summary>
public static class AppClassifier
{
    /// <summary>
    /// Ordered rules: the first match wins, so specific categories are listed before
    /// broad ones. Matched against the normalized name, publisher and winget id together.
    /// </summary>
    private static readonly (AppCategory Category, string[] Keywords)[] Rules =
    {
        (AppCategory.Runtime, new[]
        {
            "redistributable", "runtime", "netframework", "dotnet", "visualcpp", "vcredist",
            "directx", "openal", "javaruntime", "jre", "webview2", "appruntime", "vcruntime",
            "msodbc", "sqlnative", "windowssdk", "windowsappruntime",
        }),
        (AppCategory.Driver, new[]
        {
            "driver", "chipset", "audiodriver", "graphicsdriver", "realtek", "intelchipset",
            "nvidiagraphics", "amdchipset", "bluetooth", "wireless", "lan", "touchpad",
            "biometric", "cardreader", "windowsdriverpackage",
        }),
        (AppCategory.Development, new[]
        {
            "visualstudio", "vscode", "cursor", "intellij", "pycharm", "webstorm", "rider",
            "android", "sdk", "git", "github", "node", "python", "java", "jdk", "golang",
            "rust", "docker", "postman", "insomnia", "dbeaver", "heidisql", "sublime",
            "notepad", "vim", "neovim", "compiler", "innosetup", "cmake", "gradle", "maven",
            "terminal", "powershell", "wsl", "unity", "godot", "unreal", "arduino",
        }),
        (AppCategory.Creative, new[]
        {
            "photoshop", "illustrator", "premiere", "aftereffects", "indesign", "lightroom",
            "adobe", "affinity", "gimp", "inkscape", "krita", "blender", "autocad", "autodesk",
            "sketchup", "fusion", "freecad", "kicad", "revit", "3dsmax", "maya", "cinema4d",
            "davinci", "resolve", "obs", "audacity", "shotcut", "handbrake", "openshot",
            "kdenlive", "figma", "canva", "capcut", "clipforge", "vegas", "camtasia",
        }),
        (AppCategory.Game, new[]
        {
            "steam", "epicgames", "gog", "battlenet", "origin", "uplay", "ubisoft", "riot",
            "minecraft", "roblox", "callofduty", "cyberpunk", "hitman", "godofwar", "liesofp",
            "xbox", "gamebar", "playnite", "gameloop", "easyanticheat", "epiconline",
        }),
        (AppCategory.Security, new[]
        {
            "antivirus", "malwarebytes", "defender", "bitdefender", "kaspersky", "norton",
            "avast", "avg", "eset", "mcafee", "vpn", "wireguard", "openvpn", "protonvpn",
            "keepass", "bitwarden", "1password", "lastpass", "veracrypt", "cryptomator",
            "authenticator", "firewall",
        }),
        (AppCategory.Browser, new[]
        {
            "chrome", "firefox", "edge", "brave", "vivaldi", "opera", "tor", "librewolf",
            "chromium", "duckduckgo", "zenbrowser", "safari", "torbrowser",
        }),
        (AppCategory.Communication, new[]
        {
            "discord", "slack", "teams", "zoom", "skype", "telegram", "signal", "whatsapp",
            "element", "thunderbird", "outlook", "mail", "messenger", "anydesk", "teamviewer",
            "parsec", "webex", "instagram", "chatgpt", "claude", "anythingllm", "grammarly",
        }),
        (AppCategory.Media, new[]
        {
            "vlc", "mpv", "mpchc", "media", "player", "spotify", "itunes", "музыка", "musicbee",
            "foobar", "winamp", "plex", "kodi", "netflix", "codec", "klite", "ffmpeg",
            "smplayer", "potplayer", "asio", "voicemeeter", "equalizer", "eartrumpet",
        }),
        (AppCategory.Productivity, new[]
        {
            "office", "word", "excel", "powerpoint", "onenote", "access", "publisher",
            "libreoffice", "onlyoffice", "acrobat", "reader", "foxit", "pdf", "obsidian",
            "notion", "joplin", "logseq", "zotero", "evernote", "todo", "calendar",
            "qdaminer", "mendeley", "calibre", "aquile", "pdfgear", "pdf24", "sumatrapdf",
        }),
        (AppCategory.Utility, new[]
        {
            "7zip", "winrar", "peazip", "nanazip", "everything", "ccleaner", "revo",
            "treesize", "windirstat", "sharex", "greenshot", "screenshot", "rufus", "ventoy",
            "etcher", "putty", "winscp", "filezilla", "qbittorrent", "transmission", "sync",
            "backup", "cleaner", "uninstall", "rename", "duplicate", "unlocker", "crystaldisk",
            "hwinfo", "cpuz", "gpuz", "afterburner", "teracopy", "freefilesync", "downloader",
            "converter", "folderico", "hotkey", "flux", "printer", "scanner", "utility",
            "manager", "monitor", "installer", "setup", "tool",
        }),
    };

    public static void Classify(InstalledApp app)
    {
        app.Category = Categorize(app);
        app.SizeTier = TierFor(app.EstimatedSizeKb);
    }

    private static AppCategory Categorize(InstalledApp app)
    {
        // OS components are their own bucket regardless of what they are named.
        if (app.IsSystemComponent) return AppCategory.System;

        var tokens = Tokenize(app);

        foreach (var (category, keywords) in Rules)
            foreach (var keyword in keywords)
                if (Matches(tokens, keyword))
                    return category;

        // A Store app that matched nothing is more likely a consumer app than a tool.
        if (app.Sources.HasFlag(DetectionSource.MsStore)) return AppCategory.Productivity;

        return AppCategory.Other;
    }

    /// <summary>
    /// Splits the app's identifying text into words. Matching against one run-together
    /// string was wrong: short keywords hit inside longer words, so "tor" classified
    /// "Claude Code Usage Monitor" and "Crucial Storage Executive" as browsers, and "lan"
    /// would have caught anything mentioning a language.
    /// </summary>
    private static List<string> Tokenize(InstalledApp app)
    {
        var tokens = new List<string>(12);

        void AddWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var current = new System.Text.StringBuilder();
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch)) current.Append(char.ToLowerInvariant(ch));
                else if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            if (current.Length > 0) tokens.Add(current.ToString());
        }

        AddWords(app.Name);
        AddWords(app.Publisher);

        if (!string.IsNullOrWhiteSpace(app.WingetId))
        {
            // Both the dotted parts and the whole id: "Microsoft.VisualStudioCode" should
            // offer "visualstudiocode" so a keyword like "visualstudio" can prefix-match.
            foreach (var part in app.WingetId.Split('.', StringSplitOptions.RemoveEmptyEntries))
                tokens.Add(part.ToLowerInvariant());
            tokens.Add(app.WingetId.Replace(".", "").ToLowerInvariant());
        }

        // The squashed name catches products written as one word in the catalog.
        if (app.NormalizedName.Length > 0) tokens.Add(app.NormalizedName);

        return tokens;
    }

    /// <summary>
    /// Whole-word match, widening to prefix and substring only as the keyword gets long
    /// enough for an accidental hit to be unlikely.
    /// </summary>
    private static bool Matches(List<string> tokens, string keyword)
    {
        foreach (var token in tokens)
        {
            if (token.Equals(keyword, StringComparison.Ordinal)) return true;
            if (keyword.Length >= 5 && token.StartsWith(keyword, StringComparison.Ordinal)) return true;
            if (keyword.Length >= 8 && token.Contains(keyword, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Size bands chosen so the tiers separate meaningfully on a real machine: most
    /// utilities land under 50 MB, and only genuine suites pass 2 GB.
    /// </summary>
    public static SizeTier TierFor(long sizeKb) => sizeKb switch
    {
        <= 0 => SizeTier.Unknown,
        < 50L * 1024 => SizeTier.Small,
        < 250L * 1024 => SizeTier.Medium,
        < 2048L * 1024 => SizeTier.Large,
        _ => SizeTier.VeryLarge,
    };

    public static string Describe(SizeTier tier) => tier switch
    {
        SizeTier.Small => "Small",
        SizeTier.Medium => "Medium",
        SizeTier.Large => "Large",
        SizeTier.VeryLarge => "Very large",
        _ => "Unknown size",
    };

    public static string Describe(AppCategory category) => category switch
    {
        AppCategory.Development => "Development",
        AppCategory.Creative => "Creative",
        AppCategory.Game => "Games",
        AppCategory.Media => "Media",
        AppCategory.Productivity => "Productivity",
        AppCategory.Communication => "Communication",
        AppCategory.Browser => "Browser",
        AppCategory.Security => "Security",
        AppCategory.Utility => "Utility",
        AppCategory.Runtime => "Runtime",
        AppCategory.Driver => "Driver",
        AppCategory.System => "System",
        _ => "Other",
    };
}
