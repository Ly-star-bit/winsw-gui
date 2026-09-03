# Graphical console

`WinSW.Gui` is a desktop companion for the wrapper. It finds every service on the machine
that is hosted by a WinSW executable, shows their state live, and drives the wrapper's own
commands from buttons instead of a terminal.

It is a separate program. The wrapper binary is unchanged and does not depend on it.

## What it does

| Page | Purpose |
| --- | --- |
| **Services** | Lists WinSW-managed services with live status and process ID, the process tree of the running service, and Start / Stop / Restart / Apply config / Terminate / Uninstall. |
| **Configuration** | A form over the XML configuration file with live validation and a preview of exactly what will be written. Comments and formatting in an existing file are preserved. A configuration that is not yet a service can be installed as one from here — the bundled wrapper is placed beside it if there is not one already; a configuration that already belongs to a service can be pushed to it with `winsw refresh`. |
| **Logs** | Tails the service's log files (including rolled files) with filtering, follow mode and pause. |
| **New service** | A four-step wizard: pick the program, describe the service, choose logging and recovery, then write the configuration and install it — one elevation prompt covers both install and start. Each service gets a folder of its own under an install root (`%ProgramData%\WinSW` by default, changeable in Settings), all sharing one WinSW executable in `bin`; the program's own folder is left untouched unless asked for. |

Across the pages:

- **Encoding-aware logs.** Console programs on Windows write the system code page (GBK on
  Chinese systems) unless they opt into UTF-8, and the wrapper stores their bytes verbatim.
  The viewer detects UTF-8 versus the ANSI page per file and lets you force either.
- **Windows events.** The Logs page has a tab with the Application and System log records
  about the service, which is where "why did it not start" is usually answered.
- **Careful stops.** `stop` and `restart` do not pass `--force`; if other services depend on
  the one being stopped, the GUI asks before stopping them too. A service that does not
  finish within its `stoptimeout` plus a margin is reported as stuck, with Terminate offered.
- **Background rescan** every 30 seconds picks up services installed by other tools.
- **Notifications** when a service stops without the GUI having asked it to, optionally
  with the window minimised to the tray.
- **Light, dark or system theme**, remembered window placement, F5 / Ctrl+S / Esc.
- **Elevated save**: when a configuration lives somewhere a standard user cannot write, the
  file is staged and copied into place with one elevation prompt. There is also a
  "Restart as administrator" button in the rail for prompt-free sessions.
- **Try run**: the Configuration page can launch the program with the configured arguments,
  working directory and environment as the current user, without installing anything, and
  show its output — the fastest way to find a bad path or argument. The panel also spells out
  where a try run and a service run differ (account, environment, mapped drives, desktop),
  which is where most first starts fail; **Install as a service** on the same page is the
  step after it.
- **Full configuration coverage**: lifecycle hooks (`prestart` … `poststop`), network drive
  mappings, and the `<extensions>` element as raw XML. The preview pane can also be switched
  to a raw XML editor and applied back to the form.
- **Runtime metrics** in the detail panel: uptime, CPU, memory, handles, last exit code and a
  CPU sparkline, plus what the service depends on and what depends on it.
- **Batch operations**: Ctrl/Shift-select several services and start, stop or restart them
  under one elevation prompt.
- **Clone**: the wizard can start from an existing service; it can also brand the wrapper
  (`winsw customize`) so the service appears under its own name in Task Manager.
- **Export**: copies the wrapper, the configuration and a generated `install.ps1` to a folder
  for a machine without the GUI.
- **Diagnostics bundle**: one zip with the configuration, log tails, Windows events and
  versions, for bug reports.
- **Wrapper upgrades**: the detail panel compares an installed service's wrapper against the
  one bundled with the console and can swap it in (stop → replace → start, one prompt), with
  no network involved. Crossing a major version, or replacing a self-contained wrapper with
  the bundled .NET Framework build, is called out in the confirmation.
- **Remote**: read-only status of services on another computer, through the SCM's RPC
  interface, with your current credentials.
- **Command line and Explorer**: `WinSW.Gui.exe myapp.xml` opens that service (or the file in
  the editor if it is not installed); an optional "Open in WinSW" verb on .xml files is
  registered per user from the rail.
- **Accessibility**: high-contrast mode is honoured automatically; controls carry automation
  names for screen readers.
- **First run**: an empty dashboard offers to create the first service or open a file, and
  the wizard can download the right WinSW build for the machine and install everything
  into the program's own folder.
- **Everyday polish**: filter by clicking the stat cards, sort by status, double-click a
  service for its logs, Ctrl+C / Ctrl+wheel / wrap in the log viewer, a pick-a-service box
  on the Logs page, transient notices for action results, red borders on invalid fields,
  masked passwords with a reveal toggle, number-and-unit editors for durations, a
  collapsible icon-only rail, a resizable preview pane, and a Settings page that gathers
  language, theme, tray and the shell verb. Closing with unsaved edits asks first.
- **Services keep to themselves**: the wizard installs into `<root>\<service id>\`, so the
  configuration and its logs never land in a folder something else owns — a Python or JDK
  installation whose next upgrade would take them along. One WinSW executable in `<root>\bin`
  serves them all; asking for a branded wrapper gives that service a copy of its own. The
  working directory suggested for an interpreter follows the script named in the arguments,
  not the interpreter's own folder.
- **The wrapper travels with the console**: WinSW itself is embedded in the executable, so
  the wizard installs a service on a machine that has never seen WinSW and cannot reach
  GitHub — no download, no file to hunt for. The bundled build is the 0.65 MB net462 one,
  which runs on x86, x64 and ARM64 alike against .NET Framework 4.6.2 or later; the Download and Browse buttons are still there
  for anyone who wants a specific wrapper version or the self-contained .NET build.
- **XML reference, ready for an assistant**: the Configuration page has an *XML rules*
  button that opens the complete configuration specification
  ([English](xml-config-cheatsheet.md), [中文](xml-config-cheatsheet.zh-CN.md)) inside the
  application — searchable contents, a copy button on every example, and *Copy as AI prompt*,
  which puts the specification, the file currently open in the editor and a filled-in task
  description on the clipboard. Paste that into an assistant, paste the XML it returns into
  the preview pane's raw editor, and apply it back to the form. The document is embedded in
  the executable, so it works with no network.
- **Updates**: the Settings page shows when a newer GUI release exists. Each release also carries
  winget manifests (`winget-manifests.zip`) ready for submission to winget-pkgs.

## Building

Tests live in `WinSW.Gui.Tests` (configuration round-trip, command-line splitting, log
tailing and encoding detection) and run on the Windows CI runner:

```powershell
cd src
dotnet test WinSW.Gui.Tests
```

The project targets `net8.0-windows` — the LTS release, and the one whose WPF carries a
native folder picker — and lives in the main solution. The wrapper it embeds is built from
`WinSW.csproj` first; see the workflow.

```powershell
cd src
dotnet build WinSW.Gui -c Release
dotnet run --project WinSW.Gui
```

To produce one self-contained executable:

```powershell
dotnet publish WinSW.Gui -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The result lands under `artifacts\bin\WinSW.Gui\Release\net8.0-windows\win-x64\publish\`. CI also publishes a `win-arm64` build.

## How it finds services

The wrapper installs a service whose image path is `"<wrapper.exe>"`, optionally followed by
`"<config.xml>"`. The GUI reads that path back from
`HKLM\SYSTEM\CurrentControlSet\Services\<name>\ImagePath` for every Win32 service and claims
the ones whose configuration file exists and declares a `<service>` root. When the file is
missing but the executable's version information identifies it as WinSW, the service is
still listed, flagged as needing attention.

This works for a standard user and does not need any particular wrapper executable to be at hand,
which is why it is used instead of `winsw dev list`.

## Elevation

The GUI runs as the invoking user (`asInvoker`). Browsing, reading configuration and tailing
logs need no elevation. Each command that changes a service — install, uninstall, start, stop,
restart, refresh, kill — re-launches the wrapper through ShellExecute with the `runas` verb,
so you see one UAC prompt per action. Start the GUI from an elevated prompt to avoid the
prompts altogether; the rail shows which mode you are in.

## Language

The interface is available in English, Simplified Chinese, Traditional Chinese and Japanese. The picker at the bottom of the
navigation rail switches the whole UI in place, without a restart, and the choice is remembered
in `%LOCALAPPDATA%\WinSW.Gui\settings.json`. With no saved choice the GUI follows the
Windows display language.

To add a language, copy `WinSW.Gui/Localization/Strings.en.xaml` to `Strings.<code>.xaml`,
translate the values (keep the keys and the `{n}` placeholders), and add the code to
`Localizer.Languages`.

## Notes for contributors

- The GUI does not reference `WinSW.Core`. `XmlServiceConfig`'s constructor sets process-wide
  environment variables (`BASE`, `SERVICE_ID`, every `<env>` entry), which would leak between
  the many configurations a GUI loads. `WinSW.Gui/Model/ServiceConfigModel.cs` mirrors the
  parser's element names instead and must be kept in step with it.
- Log files are discovered by scanning the log directory for `<logname>*.log`, not by
  reproducing each appender's naming, because the naming differs by mode.
- Every user-visible string is a keyed resource in `Localization/Strings.<code>.xaml`. XAML uses
  `DynamicResource`; code uses `Localizer.Get`/`Localizer.Format` and re-raises computed
  properties on `Localizer.Changed`. Hard-coded English in a view or view model is a bug.
- WPF-UI provides the Fluent styles for standard controls and the Mica window; everything
  else is defined in `WinSW.Gui/Theme/`. The GUI intentionally uses none of the library's
  resource keys, so upgrading it should not change the look.
