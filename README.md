# UpdateHub

I kept finding software on my PC that hadn't been updated in years because
"update available" doesn't tell you if you're a week behind or five years
behind with a dozen known vulnerabilities in between. So this one puts an
actual age on every install instead of just a yes/no.

A Windows 11 software update manager that answers the question other updaters skip:
not *"is there an update?"* but **"how far behind are you?"**

```
ASIO4ALL      2.14   released ~31 May 2021
              2.22   released ~22 Jul 2026
              → 5 years, 1 month behind
```

`UpdateHub.exe` is a portable, self-contained single file. No installer, no runtime to
install. All data lives in `%LOCALAPPDATA%\UpdateHub`.

---

## What it does

**Scans** five sources and merges them into one inventory:

| Source | What it contributes |
|---|---|
| Registry (HKLM 32/64, HKCU) | The authoritative list: publisher, install date, install location, uninstall strings |
| `winget list` | Package IDs and winget's own upgrade-available verdict |
| `Get-AppxPackage` | MSIX/Store display names, publishers, install dates |
| Portable-app folders | Standalone `.exe` files, versioned from their PE resources |
| Chocolatey / Scoop | Packages managed outside winget |

The same product often appears in three of these. `winget list` embeds the registry
subkey in its `ARP\Machine\X64\{guid}` IDs, which gives an exact join back to the registry
scan rather than guessing from display names.

**Dates every version** it can, from four tiers:

| Source | Date quality | Base confidence |
|---|---|---|
| GitHub Releases | Exact, full history, release notes | 100% |
| Vendor endpoint (Mozilla, Google, plugins) | Exact | 90% |
| winget-pkgs manifest history | Approximate (±1–2 weeks), shown with `~` | 80% |
| Registry only | No latest version known | 30% |

**Calculates** days / months / years behind, major and minor versions behind, releases
missed, and security releases missed.

**Updates** via winget, Chocolatey, Scoop, or a signature-verified vendor installer.

---

## The bit that makes it work

Release dates are the expensive part. Asking GitHub for each version separately would cost
one request per version, and an anonymous caller gets **60 requests an hour**.

Instead, one query against a package's folder in the `winget-pkgs` repository returns up to
100 commits, and each commit message names the version it added:

```
GET /repos/microsoft/winget-pkgs/commits?path=manifests/v/VideoLAN/VLC&per_page=100
  → "New version: VideoLAN.VLC version 3.0.21 (#123456)"  2024-08-12
  → "Update VideoLAN.VLC to 3.0.20 (#118902)"             2024-04-30
  ...
```

One request reconstructs a dated history dozens of versions deep — 58 versions for VLC in
the real case. Commits arrive newest-first and each version is mapped to its **oldest**
commit, because that is when the manifest first appeared; later commits are metadata edits
and would date a version far newer than it is.

Every learned date is cached permanently in SQLite. A version's release date never changes,
so it is fetched once and never again. On this machine the first full run took 49 seconds
and the second took 8.

---

## Honesty about the numbers

The age figure is only as good as the two dates behind it, so:

- **The version comparison is the authority on direction.** Dates only quantify a gap that
  the version numbers already established. If the versions say you are behind but the dates
  disagree — which happens when the installed date came from manifest history and the latest
  from a vendor API — the age is withheld rather than guessed, and confidence drops.
- **Approximate dates are marked** with `~`, estimated ones with `~ (est.)`.
- **Incomparable version schemes return "can't tell", never "equal".** An app reporting
  `2022.1.3` against a vendor publishing `24.1` would otherwise look 2,000 major versions
  behind.
- **Confidence is shown per row**, with the reasoning spelled out in the detail pane.

---

## Command line

```
UpdateHub.exe                       Open the window
UpdateHub.exe --scan-silent         Scan and notify, no window (used by the scheduled task)
UpdateHub.exe --scan-silent --auto-update   ... and install what it finds
UpdateHub.exe --report <path>       Write a plain-text diagnostic report
UpdateHub.exe --report <path> --no-network  ... scanners only, no provider lookups
UpdateHub.exe --export <path>       Export the last scan (.csv .xlsx .json .pdf)
UpdateHub.exe --uitest              Build every page, report XAML and binding failures
```

---

## Plugins

Drop a JSON file into `%LOCALAPPDATA%\UpdateHub\Plugins`. Plugins are **data, not code** —
nothing is executed, and rule URLs must be `https`. *Settings → Write example plugin*
generates a template.

```json
{
  "entries": [
    { "match": ["myapp"], "name": "My App",
      "winget": "Publisher.MyApp", "github": "owner/repo" }
  ],
  "providers": [
    { "name": "My App", "match": ["myapp"],
      "url": "https://example.com/api/latest.json",
      "json": "releases[0].version",
      "dateJson": "releases[0].date", "dateFormat": "yyyy-MM-dd" }
  ]
}
```

`entries` map a product to identifiers. `providers` describe a version endpoint and how to
read it — either a dotted `json` path (`versions[0].version`) or a `regex` with one capture
group against an HTML page.

Apps not in the catalog are still handled: UpdateHub extracts `owner/repo` from registry
`URLInfoAbout` / `HelpLink` values and from winget's release-notes URLs, which covers a
large share of open-source software automatically.

---

## Rate limits

Without a token, GitHub allows 60 requests an hour. That is not enough to date a large
library in one pass, so results fill in gradually across runs and are cached permanently.
The client reserves the last few requests for interactive lookups, serves stale data rather
than nothing when exhausted, and uses ETags so unchanged responses cost no quota.

Adding a read-only token in *Settings → Version sources* raises the limit to 5,000/hour and
a full library resolves in one pass.

---

## Testing

```bash
dotnet run --project tests/UpdateHub.Tests
```

58 assertions over the logic that has to stay correct, exiting non-zero on failure. It is a
plain console app rather than a test framework, compiling the same source files the
application uses so there is nothing to keep in sync.

Each group exists because the corresponding bug actually shipped once:

| Group | Pins |
|---|---|
| Version compare | messy real-world strings; incomparable schemes return "can't tell", never "equal" |
| Age behind | calendar arithmetic — 31 Jan to 1 Mar must not lose a day |
| Name normalize | bitness kept for display, ignored for matching; edition years preserved |
| Releases missed | counted over (installed, latest], not everything newer |
| Version shape | commit SHAs and date stamps rejected as version numbers |
| Precision tolerance | `8.0.29.36224` matches `8.0.29`, but `8` does not |

## Building

Needs the .NET 8 SDK.

```bash
dotnet build
```

```bash
dotnet publish -c Release -o ./publish
```

Produces a self-contained single-file `UpdateHub.exe` (~75 MB, x64) — the portable build.

### Installer

`installer.iss` (Inno Setup 6) wraps that same `publish/UpdateHub.exe`, so the installer
and the portable build always come from one binary rather than two independently
configured outputs that could drift apart. Per-user install, no elevation required —
`ArchitecturesAllowed=x64compatible`.

```bash
dotnet publish -c Release -o publish
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
```

Produces `installer-output\UpdateHub-Setup.exe`.

Both distributables are copied to the repo root, per [project convention](../Claude_Code_Build_Instructions.md):
`../portable/16 - update-hub Portable.exe` and `../executable/16 - update-hub.exe`.

---

## Layout

```
update-hub/
├── Core/
│   ├── Scanning/          RegistryScanner, WingetScanner, MsStoreScanner,
│   │                      PortableScanner, PackageManagerScanners, ScanCoordinator
│   ├── VersionComparer.cs Parsing and comparison for messy Windows version strings
│   ├── NameNormalizer.cs  Display-name cleanup and fuzzy matching
│   ├── UpdateEngine.cs    winget/choco/scoop/installer updates, rollback
│   ├── SignatureVerifier.cs  Authenticode via WinVerifyTrust
│   └── ProcessRunner.cs   Console-tool invocation with ANSI stripping
├── Sources/               GitHub, Vendor, Winget, Chocolatey, Scoop providers,
│                          AppCatalog, GitHubClient, ProviderRegistry
├── Data/                  SQLite schema, repositories, release history, cache
├── Models/                InstalledApp, AgeBehind, ReleaseInfo, enums, settings
├── Services/              Settings, export (CSV/XLSX/PDF/JSON), scheduler,
│                          notifications, logging, theming
└── UI/                    MainWindow, six pages, detail pane, converters, view model
```

XLSX and PDF are written directly rather than through a library — both exports are flat
table reports, and hand-writing them keeps the portable executable free of native
dependencies.
