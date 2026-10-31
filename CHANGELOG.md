# Changelog

All notable changes to UpdateHub. Versions follow semantic versioning.

## [0.10.2] — 2026-10-31

Reported as "updates are failing". Ten updates in the batch had actually installed; the rest
were mislabelled.

### Fixed
- **winget limits are no longer shown as failures.** A real batch log showed apps reported as
  "Failed" when winget itself had said "No newer package versions are available" (its
  catalogue lags the publisher: it lists Chrome 156.0.8078.48 while 156.0.8078.74 is
  installed), "the install technology is different" (Edge), "cannot be upgraded using WinGet"
  (Android Studio), or "No installed package found" (an ID that does not match the install).
  These now get their own result, "Not via winget", with a plain explanation and a pointer to
  Check manually. Genuine installer failures still show as failures.
- Apps that hit one of these limits are no longer offered to "Update all" again (they would
  fail identically every time); they stay flagged as behind.
- A wrong message for winget code 0x8A15002B ("no applicable installer for this
  architecture") was corrected: it means no newer applicable version exists.
- The summary now says how many were "not available through winget yet" instead of lumping
  them in with failures.
- **Update selected skips programs that cannot be updated automatically** (for example
  portable or Start Menu-only entries) and says so, instead of trying and failing.
- **Duplicate rows from the Start Menu scan.** "Visual Studio Code" (a shortcut) is now
  merged with "Microsoft Visual Studio Code" (the install) when one name contains the other,
  instead of appearing as a second program with an unreliable file version.

### Tests
- 133 assertions: the classification uses the exact messages from the failing batch.

## [0.10.1] — 2026-10-31

### Added
- **Update selected.** The Updates and Applications lists now have a visible
  "Update selected (N)" button, plus "Select all with updates" and "Clear selection".
  Ctrl-click or Shift-click rows to choose exactly which programs to update; the button counts
  only rows that actually have an update and are not ignored. Multi-select already worked, but
  the only visible buttons were "Update all" and a hidden right-click item.

- **Install driver updates.** On the Drivers page, newer drivers that Windows Update offers
  are listed and can be installed selectively ("Install selected") or all at once ("Install
  all"). One administrator prompt, a confirmation first, and a system restore point is
  attempted before anything is installed (the result says if Windows did not allow one).
  Only Microsoft-distributed, signed drivers go through this, via the Windows Update Agent;
  the script is static and driver titles travel in a JSON file, never executed. The result
  lists each driver as installed, installed with warnings or failed, and says if a restart
  is needed.

### Tests
- 122 assertions, including a real unelevated dry run of the driver script (finds nothing
  to install, installs nothing) and the result-parsing rules.

## [0.10.0] — 2026-10-31

### Added
- **Drivers page.** Reads this PC's hardware (maker, model, processor, graphics, memory,
  Windows, BIOS) and its drivers (graphics, network, sound, Bluetooth, chipset, storage,
  firmware, camera), shows each driver's version, date and age, and asks Windows Update
  whether newer drivers exist for this PC. Drivers with a newer version available, or very
  old ones, are highlighted. Double-click a driver to open its maker's official download
  page; buttons open Windows optional updates and the PC maker's support page; on Intel
  machines it can silently install Intel Driver & Support Assistant through winget.
- **Privacy by design.** Everything is read locally with WMI. Nothing about the hardware is
  uploaded, serial numbers and hardware IDs are never read, links contain only the maker
  and device name, and the only network contact is Windows Update itself.

### Notes
- Windows Update only covers drivers Microsoft distributes. Newer graphics drivers
  (NVIDIA, AMD) and BIOS updates must be checked on the maker's page, which the app links to.
  Drivers are not installed automatically: a bad driver can stop a PC booting, so the app
  finds and compares but leaves installing to Windows Update or the maker's own tool.

### Tests
- 111 assertions (13 new), including a real local scan and the privacy rule that links never
  contain the machine name.

## [0.9.0] — 2026-10-31

### Added
- **Start Menu scan.** UpdateHub now reads the all-users and per-user Start Menu shortcuts
  and compares them with the installed list. Each program is marked "In the Start Menu",
  "Not in the Start Menu" or "Start Menu only (no install record)", which finds portable
  and hand-copied programs that have no uninstall entry. New search filter:
  `startmenu:only`, `startmenu:yes`, `startmenu:no`.
- **Microsoft Store updates.** Store apps that winget reports an update for are now
  updated silently through winget's Store source, and are included in "Update all".
- **"No update source" explanation** in the detail pane for programs with no known latest
  version: a plain-English reason and a "Check manually" button that opens the publisher's
  page or a web search.
- **Backup and restore** (Settings): save the winget-managed program list to a file, and
  silently reinstall everything from a backup on a new or reset PC.
- **Launch options** (right-click a program > "Launch options…"): run it as administrator
  every time, run it in a Windows compatibility mode (95 through 8), disable fullscreen
  optimizations, override high-DPI scaling, 256 colours, 640 x 480, and open it right now
  normally or as administrator. Stored in the same registry place as the Windows
  Compatibility tab, so it applies from any shortcut and needs no admin rights to set.
- **Shortcut key** for any program: a global key such as CTRL+ALT+F, saved on a Start Menu
  shortcut (created if the program has none) so Windows handles it even when UpdateHub is
  closed. At least two modifier keys are required so ordinary typing can never be hijacked.
- MIT `LICENSE`.

### Changed
- **The GitHub token is now stored encrypted** (Windows DPAPI, current user only) instead of
  in plain text. A plain-text token from an older version is migrated automatically on first
  launch.

### Tests
- 98 assertions (32 new): Start Menu search, no-source reason, manual-check link, launch
  option parsing, shortcut-key validation, and real registry and Start Menu shortcut round
  trips that clean up after themselves.

## [0.8.3] — 2026-10-28

### Changed
- The window title and title bar now show the version ("UpdateHub 0.8.3") at startup.
- The version shown in Settings > About now comes from the project version
  (`App.VersionText`) instead of a hand-typed string, so it can no longer drift.
- Every control on the Settings page now has a hover tooltip.

## [0.8.2] — 2026-08-26

Reported as "issue with font in Settings." It wasn't a font — it was near-total text
invisibility on that one page, caused by a leftover setting from before the app committed
to its current design.

### Fixed
- **Settings.json still had `"Theme": "Dark"` from before the app switched to one fixed
  bright design**, and that value was still live: applying it re-themes WPF-UI's *native*
  control resources (checkbox, combo box, number box, password box — the exact controls
  Settings is dense with) to dark, while every custom brush the rest of the app uses stays
  hardcoded to the light palette. The result was most Settings-page text rendering as
  near-white-on-white — confirmed by temporarily letting the screenshot tool read the real
  persisted theme instead of its usual hardcoded "Light" override (which is precisely why
  this had gone uncaught all session: every previous diagnostic screenshot silently forced
  Light, masking the bug).
- Three-layer fix, so this class of bug can't resurface through this path again:
  `SettingsService` now migrates any non-"Light" persisted theme back to "Light" on load
  (existing installs, including this machine's, self-heal on next launch — confirmed
  against the real on-disk settings.json); the app's real startup path applies "Light"
  unconditionally rather than trusting the stored value; and `ThemeService` no longer has
  Dark/System branches to apply in the first place. The Theme picker was removed from
  Settings entirely — offering a choice that only ever had one safe answer is what let a
  stale value get set in the first place.
- Confirmed via reflection that every control type on Settings (plain WPF `CheckBox`/
  `ComboBox`/`ListBox` and WPF-UI's `NumberBox`/`PasswordBox`/`Button`) resolves to the
  identical `Segoe UI` at the identical size under the theme — there was never an actual
  font-family mismatch to fix, which is why chasing that framing first would have gone
  nowhere.

## [0.8.1] — 2026-08-25

### Fixed
- **The window's close button was indistinguishable from Minimize and Maximize at rest.**
  WPF-UI's own `TitleBar` template only colours the close glyph on hover (white on a red
  fill via `MouseOverButtonsForeground`/`MouseOverBackground`) — at rest all three caption
  buttons render as plain ink-coloured symbols, which is standard Windows chrome but
  didn't fit this app's brighter, higher-contrast look and made Close impossible to pick
  out until the cursor was already on it. `TitleBarButton` exposes `ButtonsForeground`
  per instance, so the close button alone is now retinted to the app's own Critical red
  (#C02A2A) at rest, found via `Template.FindName("PART_CloseButton", ...)` in
  `MainWindow.xaml.cs` — no WPF-UI template override, Minimize/Maximize and the built-in
  hover behaviour untouched. Verified with a real screenshot crop of the rendered title
  bar, not just that nothing threw.

## [0.8.0] — 2026-08-03

### Added
- **Elevated background updates.** winget updates can now run through a single hidden,
  elevated PowerShell session instead of the old reactive path, which launched a separate
  `runas` — and a separate UAC prompt — for every app that turned out to need one. One
  approval covers the whole batch; the session runs invisibly (`-WindowStyle Hidden`, no
  console flash) and every winget call inside it uses `--silent --disable-interactivity`.
  On by default (Settings → Updating), with a plain-language explanation of the trade-off:
  always one prompt up front, versus prompting only for the apps that actually need it.
  Package IDs travel to the elevated script through a JSON file rather than being
  interpolated into the script text, so there is nothing for a malformed app name to
  inject into.
- Non-winget updates (Chocolatey, Scoop, direct installers) are unaffected — they still go
  through the existing per-app engine, which continues to elevate reactively.
- `UpdateEngine.MarkSuccess` / `RecordElevatedResult` factor out the "apply a successful
  update" bookkeeping so both paths leave identical state and identical History rows,
  regardless of which one performed the update. The elevated PowerShell process itself
  never touches the SQLite database — only the unelevated caller does, once it has read
  the result back.

### Verified
- The embedded PowerShell script parses clean under `PSParser::Tokenize` (0 syntax
  errors) and was dry-run end to end against a stub `winget.exe` standing in for both a
  successful and a failing package, confirming the running → success/failed JSON protocol
  the C# side parses.
- 8 new unit assertions cover the status-mapping logic (`InterpretStatus`,
  `DescribeOutcome`) against the exact JSON shape the script emits — 66 total, all passing.
- `--uitest`: 9/9 pages, 0 binding errors, including the new Settings controls.

### Distribution
- Rebuilt both distributables per the numbered-folder convention:
  `../portable/16 - update-hub Portable.exe` and `../executable/16 - update-hub.exe`.

## [0.7.3] (distribution) — 2026-08-02

Desktop root was reorganized with numbered project folders; rebuilt distributables to
match the new convention.

### Changed
- Source moved to `16 - update-hub`. Rebuilt the portable single-file build fresh from
  this location and verified it still runs correctly from the new path (`--uitest`,
  0 binding errors).
- **`install.ps1` retired in favour of a proper installer.** Added `installer.iss`
  (Inno Setup 6): a per-user, no-elevation-required install wrapping the same
  `publish/UpdateHub.exe` the portable build uses, so both distributables are built from
  one binary instead of two configurations that could drift apart.
- Outputs now published to the shared root folders per project convention:
  `../portable/16 - update-hub Portable.exe` and `../executable/16 - update-hub.exe`.

## [0.7.3] — 2026-07-29

A dedupe audit of the live inventory. 288 applications turned out to include 18 phantom
rows, and chasing them surfaced a worse bug underneath.

### Fixed
- **The same product was listed twice whenever winget and the registry disagreed about
  version precision.** The registry records "8.0.29.36224" where winget reports "8.0.29";
  an exact version comparison treated those as different products, and with several
  candidates under one name (.NET 6 installed alongside .NET 8) the matcher gave up and
  added the winget row as a new, sizeless application. Matching now ignores trailing
  precision that only one side carries, requiring at least two shared segments so "8"
  cannot match "8.0.29". Affected .NET Runtime, Windows Desktop Runtime (x64 and x86),
  Python and several redistributables.
- **A single install registered under two registry views produced two rows.** Scope is part
  of the dedupe key, so an app present in both the 32- and 64-bit views — or in both HKLM
  and HKCU — survived twice. Rows identical in name *and* version are now collapsed, with
  the richest kept and the others' identifiers folded in. Genuinely parallel installs stay
  separate because their versions differ, and bitness is retained in the display name.
  Together these took the inventory from 288 to 270, and repeated names from 12 to 5 — all
  five remaining being real side-by-side installs such as VC++ 2008's three service-pack
  levels.
- **A stale series-pinned winget ID could point at the wrong package.** The catalog
  hardcoded `Python.Python.3.13`, so an installed Python 3.14 was mapped to the 3.13
  package — comparing against another release line's latest version, and an update would
  have installed the wrong package. Series-pinned IDs are now rejected when their version
  line contradicts what is installed, and the Python entry no longer pins one; winget's own
  detection supplies the correct `Python.Python.3.14`.
- **Installed versions showed a winget annotation.** winget prefixes the version with
  "> " when several versions of a package are installed and it is showing one of them.
  That marker was stored verbatim, so the app displayed "> 1.8.9" as the installed version
  of WindowsAppRuntime.1.8.

### Tests
- 58 assertions, all passing, including the precision-tolerance boundaries: that
  "8.0.29.36224" matches "8.0.29" but "8" does not match "8.0.29", and that Python's
  "3.14.6150.0" is deliberately *not* treated as "3.14.6" — merging on a mismatched
  segment could just as easily have merged 3.14.6 with 3.14.7150.0.

## [0.7.2] — 2026-07-29

Two accuracy bugs found by auditing the app's own exported data. Both were cases of
overstating, which matters more here than elsewhere: the product's whole claim is that it
does not exaggerate how far behind you are.

### Fixed
- **Missed-release counts were unbounded above.** The count included every published
  version newer than the installed one, with no upper limit, so builds from a separate
  pre-stable branch that sit *ahead* of the latest release were counted as releases the
  user had missed. Everything 1.4.1.1030 to 1.4.1.1032 — a two-build patch bump — was
  reported as **57 missed releases** because the 1.5.x alpha line was swept in; Chrome
  reported 55. The count is now taken over the half-open interval (installed, latest],
  which is the interval the question actually refers to. Everything now reports 2 and
  Chrome 10.
- **Commit hashes were accepted as version numbers.** Some projects tag releases with a
  SHA, and `4114c11a9ec5320e` parses happily as 4114.11.9.5320, so it was shown to the
  user as the latest available version of Uninstall Tool. Tags are now rejected when they
  look like a hex hash or lead with a date-stamp-length number. Uninstall Tool now
  correctly reports 3.8.0 at 100% confidence, and genuine oddities such as Git's
  `2.55.0.windows.3` are still accepted.

### Tests
- The unit suite covers both: that the bounded interval yields 2 where the old unbounded
  logic yielded 5, and that SHAs and date stamps are rejected while real versions —
  including four-digit-major calendar versions and Chrome's four-part numbers — are kept.
  49 assertions, all passing.

## [0.7.1] — 2026-07-27

Verified the 0.7.0 uninstall fix against real software, which exposed the same gap on the
update path.

### Verified
- Epic Online Services and Epic Games Launcher — the two entries that could not be
  uninstalled — were removed successfully through the new elevated path, both exit code 0.
  These were per-machine MSIs with no quiet uninstall string, the exact case that failed.

### Fixed
- **Updates needing elevation reported a bare failure instead of elevating.** Windows
  Subsystem for Linux, flagged Critical for missed security releases, failed with
  `0x80073d28 — administrator privileges are required`. The elevation check only matched
  "requires elevation" and "Access is denied", so MSIX's different wording fell through to
  a generic failure and the user was left with a Critical item they could not act on.
  Detection now covers the MSIX and MSI privilege HRESULTs and several phrasings, and a
  winget upgrade that needs elevation is retried through UAC rather than merely reported.
  WSL then updated 2.7.8.0 → 2.7.11.0 in 20 seconds.

### Measured
- The concurrency change was benchmarked without installing anything, using
  `winget download` into a temporary folder: the same three packages took **17 s
  serially and 5 s across three lanes**. That measures the download and resolve phase,
  which is where most of the wall-clock time goes; the install phase is still serialised
  by Windows Installer, so a real "Update all" gains proportionally rather than by a flat
  3.4x.

### Note on a non-bug
- An earlier reading suggested winget returned exit code 0 on a failed install, which would
  have meant silently reporting success. That was a measurement error — the exit status
  belonged to a piped `tail`, not to winget. Measured directly, winget returns 40. No
  silent-success bug exists.

## [0.7.0] — 2026-07-27

Design revamp acting on external review, plus two reported bugs.

### Fixed
- **Per-machine MSI products could not be uninstalled.** Epic Games Launcher and Epic
  Online Services register `MsiExec.exe /X{GUID}` with no quiet uninstall string; removing
  them needs elevation, and running unelevated failed with a Windows Installer privilege
  code that was never mapped, so the app reported a bare exit number. MSI uninstalls now
  go through UAC when not already elevated, and codes 1925/1730/1260/5/740 are recognised
  as "needs administrator" rather than a generic failure. 1618, 1619/1620, 1641 and 1614
  are handled too.
- **Updates were slow.** They ran strictly one at a time, so every download waited for the
  previous install to finish. Updates now run three at a time by default; Windows Installer
  still serialises the install phase itself and returns 1618 when busy, which is retried
  with backoff instead of failing. Pinning `--source winget` also removes msstore source
  resolution from every invocation — measured 830 ms → 590 ms per call, and several
  seconds on a cold start.
- A carried-forward row could claim "up to date" while still flagged as behind: the
  carry-forward path recomputed the verdict but never re-ran the date-consistency check.

### Changed — the review's structural points
- **Bright silver theme** replaces the dark one. Depth now comes from layering — soft
  gradient page, white cards with a hairline border and a low shadow — rather than dark
  fills. Status colours were re-tuned so white text on a pill stays legible and each hue
  still reads against white.
- **Home is a dashboard, not a spreadsheet.** It opens with a Software Health score and
  every deduction that produced it, then priority counts, then the list of what actually
  needs attention with the reason for each.
- **Update priority** — Critical / High / Recommended / Optional, derived from missed
  security releases, major versions behind and age, with internet-facing software held to
  a tighter schedule. Every rating carries a plain-English reason.
- **Priority-first sorting** is the default; alphabetical is now one option among four.
- **Colour discipline.** Categories were as loud as status, which trained the eye to stop
  noticing urgency. They are now a monochrome Fluent glyph plus a desaturated stripe;
  saturated colour is reserved for priority and status.
- **Smart search**: `behind>365`, `publisher:Microsoft`, `category:Development`,
  `size>2GB`, `priority:critical`, `confidence<50`, plus bare `security` / `outdated` /
  `unknown`. Unrecognised terms fall through to plain text rather than erroring.
- **Version timeline** in the detail pane, with security releases marked.
- **Insights page** — oldest version, largest application, most releases missed, apps with
  no update source, and a category breakdown with counts and footprint.
- Navigation reorganised to Home / Updates / Applications / Insights / History / Ignored /
  Settings.

## [0.5.0] — 2026-07-26

### Added
- **Application icon.** A six-segment gauge sweeping green to red — the same age scale the
  app shows beside every application — with an update arrow through it. Generated by
  `tools/make_icon.py`: rendered once at 1024px and downsampled with LANCZOS to ten sizes,
  with the `.ico` assembled by hand from PNG payloads rather than left to a library resize.
  Sizes below 32px get **separate simplified artwork** (three fat segments, larger arrow),
  because six thin segments turn into an unreadable smear at 16px.
- Icon wired into the executable, the window, the title bar, the shortcuts and the tray
  notification.
- `install.ps1` — per-user silent install to `%LOCALAPPDATA%\Programs\UpdateHub` with Start
  Menu and Desktop shortcuts and an Apps & features entry. `-Uninstall` reverses it,
  keeping settings and cache.

### Fixed
- **The SQLite database could corrupt itself, permanently breaking the app.** The
  connection string used shared-cache mode, which Microsoft.Data.Sqlite discourages and
  which interacts badly with WAL across processes — and UpdateHub is genuinely
  multi-process, since the scheduled scan can run while the window is open. Switched to a
  private cache.
- Added recovery for an already-damaged file: `PRAGMA quick_check` on open, and on failure
  the database is quarantined with a `.corrupt-<timestamp>` suffix and rebuilt. Ignore
  rules are salvaged first where still readable; everything else is a rebuildable cache.
- Selecting an application no longer takes the whole app down if its release history cannot
  be read. This surfaced as a hard crash on `SelectedApp` — the exact path a user hits by
  clicking a row.

## [0.4.0] — 2026-07-26

Visual review pass. Every page was rendered to PNG and inspected, which surfaced defects
that compile cleanly and bind without error but look wrong or report wrong counts.

### Fixed
- **Store apps were counted twice.** winget reports an MSIX *full* name
  (`Name_Version_Arch__PublisherId`) while `Get-AppxPackage` reports the *family* name
  (`Name_PublisherId`, single underscore), so the two sources never joined. Converting one
  to the other removed 69 phantom entries — the inventory went from 356 to 287.
- **The age gauge was unreadable.** Drawing it with block-drawing characters left it at the
  mercy of glyph metrics and font fallback; it rendered as a ~20px smudge. It is now six
  real rectangles, so it reads as a scale at any DPI.
- **Confidence bars looked like stray underlines** at 3px. Replaced with a track-and-fill
  pair.
- **Bitness is preserved in display names.** "Apple Application Support (32-bit)" and its
  64-bit twin are separate installs; stripping the tag rendered two rows identical. Display
  keeps it, matching still ignores it. This required setting the tag aside before the
  trailing-version strip, since `7-Zip 25.00 (x64)` otherwise kept "25.00" as part of the
  name and stopped matching the catalog.
- **MSIX products showed a package GUID where the publisher belongs**; the real name from
  `Get-AppxPackage` now wins.
- **Adjacent `<Run>` elements concatenate with no separator**, so the dashboard read
  "Last scan:Never".
- `--report` now records the scan time, instead of showing "Last scan: Never" beside the
  results of the run that just finished.
- Settings' empty custom-folder list explained rather than left as a blank bordered box.

### Added
- `--screenshot <dir>` renders every page to PNG. The window is composited onto an opaque
  backdrop first, because a FluentWindow's own background is transparent — DWM paints the
  Mica material behind it, and that material is not in the visual tree.

## [0.3.0] — 2026-07-26

First complete build. Phases 1–4 of the plan are implemented.

### Phase 1 — Core
- Five scanners (registry across HKLM 32/64 and HKCU, `winget list`, MSIX/Store via
  `Get-AppxPackage`, portable executables, Chocolatey and Scoop) merged by a coordinator
  that joins winget's `ARP\…\{guid}` IDs back to the exact registry subkey rather than
  matching on display names.
- `VersionComparer` for real-world Windows version strings — `ad 9.7.9`, `26.1.5.0370`,
  `25.00`, `2025.3`, `1.0.0-beta` all parse. Returns "incomparable" rather than a bogus
  answer when two schemes do not line up.
- winget invoked directly as a process; no PowerShell layer in the update path.
- Windows 11 Fluent UI (WPF-UI 3.0.5, Mica, dark/light/system) with six pages.

### Phase 2 — Release dates and age
- **Days / months / years behind**, calculated on the calendar rather than by dividing
  elapsed days.
- GitHub Releases provider: exact dates, full version history, release notes, and
  architecture-matched download assets.
- winget-pkgs manifest history: one commits query per package reconstructs a dated history
  dozens of versions deep, keyed to each version's oldest commit.
- Release history archived permanently in SQLite; releases missed and security releases
  missed counted from it.
- Batch updates with per-app progress.

### Phase 3 — Breadth
- Portable-app detection and user-defined scan folders.
- Vendor providers for software that publishes no GitHub releases (Firefox, Thunderbird,
  Chrome).
- Ignore-forever and skip-this-version rules, stored separately from the inventory so they
  survive a rescan.
- Export to CSV, JSON, XLSX and PDF. The spreadsheet and PDF writers are hand-rolled, so
  the portable executable carries no native export dependencies.
- Scheduled scans (daily/weekly/monthly) via Windows Task Scheduler, optionally installing
  updates unattended.

### Phase 4
- **Plugin architecture** — declarative JSON plugins add catalog entries and vendor rules.
  Plugins are data only; no code is loaded, and rule URLs are restricted to `https`.
- **Community catalog** import, layered over the built-in one.
- **Auto-discovery** — `owner/repo` extracted from registry `URLInfoAbout` / `HelpLink`
  and from winget release-notes URLs, so software absent from the catalog still resolves.
  Found 24 repositories unprompted on the development machine.
- **Signature verification** — Authenticode through `WinVerifyTrust`. Downloaded installers
  are verified, and checked against the expected publisher, before anything is executed.
- **Rollback** to any winget-published version, with a version picker.
- **Architecture-aware installer selection** (x64 / ARM64 / x86).

### Notable correctness work
These were found by testing against a real 356-application machine, and each would have
produced confidently wrong output:

- **Calendar arithmetic.** Field-wise date subtraction with a single borrow underflows
  whenever the start day is longer than the month being borrowed — 31 Jan → 1 Mar silently
  lost a day. Whole months are now counted by advancing from the original anchor, which
  also avoids the compounding month-end clamp that repeated `AddMonths` introduces.
- **Age contradicting the version verdict.** Identical versions reported a three-year gap,
  and apps whose "latest" was *older* than the installed build reported an age anyway. The
  version comparison is now the authority on direction; dates only quantify a gap it has
  already established.
- **Mixed-source dates.** When the installed date comes from manifest history and the
  latest from a vendor API, the two can disagree about which build is newer. Such rows are
  now flagged and the age withheld rather than guessed, with a confidence penalty.
- **Edition years.** `AutoCAD 2022` and `AutoCAD 2024` are different products; the trailing
  year is no longer stripped as if it were a version.
- **WPF cannot bind to methods.** `Age.Short`, `Age.Long` and `Age.Stacked` were methods,
  so the entire "Version age" column and the headline age panel silently rendered blank.
  A `--uitest` mode now builds every page and fails on binding diagnostics.
- **JSON export carried a UTF-8 BOM** (.NET's `Encoding.UTF8` default), which RFC 8259
  forbids and strict parsers reject. CSV keeps its BOM deliberately — Excel needs it.
- **MSIX system packages** leaked into the inventory as GUID-named entries.
- **Column-aligned winget output** is parsed by terminal cell width, not character index,
  so CJK program names do not shear every row.

### Known limits
- GitHub allows 60 requests/hour anonymously, which is not enough to date a large library
  in one pass. Results fill in across runs and cache permanently; a read-only token raises
  the limit to 5,000/hour.
- Manifest-derived dates trail the vendor's actual release by days to a couple of weeks and
  are labelled approximate throughout.
- Microsoft Store apps are serviced by the Store; UpdateHub reports on them but does not
  update them.
- Rollback requires a winget package, since no other source exposes older versions.
