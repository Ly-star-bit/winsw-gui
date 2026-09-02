# Graphical console

`WinSW.Gui` is a desktop companion for the wrapper. It finds every service on the machine
that is hosted by a WinSW executable, shows their state live, and drives the wrapper's own
commands from buttons instead of a terminal.

It is a separate program. The wrapper binary is unchanged and does not depend on it.

## What it does

| Page | Purpose |
| --- | --- |
| **Services** | Lists WinSW-managed services with live status and process ID, the process tree of the running service, and Start / Stop / Restart / Apply config / Terminate / Uninstall. |
| **Configuration** | A form over the XML configuration file with live validation and a preview of exactly what will be written. Comments and formatting in an existing file are preserved. Can push the change to an installed service with `winsw refresh`. |
| **Logs** | Tails the service's log files (including rolled files) with filtering, follow mode and pause. |
| **New service** | A four-step wizard: pick the wrapper and the program, describe the service, choose logging and recovery, then write the configuration and install it — one elevation prompt covers both install and start. |

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

## Building

Tests live in `WinSW.Gui.Tests` (configuration round-trip, command-line splitting, log
tailing and encoding detection) and run on the Windows CI runner:

```powershell
cd src
dotnet test WinSW.Gui.Tests
```

The project targets `net7.0-windows`, the same SDK the repository's CI uses, and lives in
the main solution.

```powershell
cd src
dotnet build WinSW.Gui -c Release
dotnet run --project WinSW.Gui
```

To produce one self-contained executable:

```powershell
dotnet publish WinSW.Gui -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The result lands under `artifacts\bin\WinSW.Gui\Release\net7.0-windows\win-x64\publish\`. CI also publishes a `win-arm64` build.

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

The interface is available in English and Simplified Chinese. The picker at the bottom of the
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
