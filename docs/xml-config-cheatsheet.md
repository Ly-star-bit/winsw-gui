# WinSW XML cheat sheet

A single-page, machine-readable specification of the WinSW 3.x configuration file.

It is written to be **pasted into an AI assistant** together with a description of your
program, so the assistant can emit a correct `<service>` document in one shot. Everything
below is derived from the wrapper's own parser, so element names, defaults and accepted
values are exact.

---

## 1. Prompt template

Copy this whole page, then append a block like the one below.

```text
You are generating a WinSW 3.x service configuration file.
Follow the specification above exactly. Rules:
  - Output ONE complete XML document and nothing else, no prose around it.
  - Use only the element and attribute names listed in the specification. They are
    case-sensitive; a misspelled or wrongly-cased element is silently ignored.
  - Include only the elements I actually need. Do not emit empty elements.
  - Add a short XML comment above any non-obvious element.
  - Finish with a "Checklist" section verifying every item in section 9.

My program:
  - What it is:            <e.g. a Spring Boot jar>
  - Command line:          <e.g. java -Xmx1g -jar app.jar --server.port=8080>
  - Installation folder:   <e.g. D:\apps\myapp>
  - Runs as:               <LocalSystem | a domain account | NetworkService>
  - Start mode:            <Automatic | Automatic (delayed) | Manual>
  - On crash:              <e.g. restart after 10s, then after 60s>
  - Logs:                  <e.g. keep 30 days, roll daily>
  - Extra requirements:    <env vars, dependencies, mapped drives, pre-start command…>
```

---

## 2. Skeleton

The root element must be `<service>`. Child order does not matter.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<service>
  <id>myapp</id>
  <name>My Application</name>
  <description>What this service does.</description>
  <executable>%BASE%\bin\myapp.exe</executable>
  <arguments>--config %BASE%\conf\app.yml</arguments>
  <log mode="roll-by-size">
    <sizeThreshold>10240</sizeThreshold>
    <keepFiles>8</keepFiles>
  </log>
</service>
```

Only `<id>` and `<executable>` are required. Everything else has a default.

**File placement.** The classic layout puts the configuration next to the (usually renamed)
WinSW executable, sharing its base name: `myapp.exe` → `myapp.xml`. That is not a
requirement in 3.x — `winsw install <config>` records both paths, so one wrapper can serve
configurations that live elsewhere. What matters is that **everything is derived from the
configuration file, not from the wrapper**: `%BASE%` is the configuration's folder, and the
log files are named after the configuration (`myapp.xml` → `myapp.out.log`).

---

## 3. Global rules

| Rule | Detail |
| --- | --- |
| Encoding | Any encoding an `XmlDocument` accepts. UTF-8 with an `<?xml?>` declaration is the safe choice; use it whenever the file contains non-ASCII text. |
| Case sensitivity | Element and attribute names are matched with XPath, i.e. **case-sensitively**. `<delayedautostart>` does nothing; `<delayedAutoStart>` works. See the exact spelling of every name in section 4. |
| Unknown elements | Ignored without a warning. A typo therefore fails silently — this is the single most common mistake. |
| `%VAR%` expansion | Every element's text and every `<env value="…">` is passed through Windows environment-variable expansion. Undefined variables are left as-is. There is no escape for a literal `%`. |
| Injected variables | `%BASE%` — folder holding the wrapper executable. `%SERVICE_ID%` and `%WINSW_SERVICE_ID%` — the value of `<id>`. `%WINSW_EXECUTABLE%` — full path of the wrapper executable. |
| Relative paths | Resolved against the working directory, which defaults to the folder holding the configuration file. Prefer `%BASE%\…` over bare relative paths. |
| Booleans | Parsed by .NET `bool.Parse`: only `true` / `false` (any casing). `1`, `0`, `yes`, `on` throw. |
| Durations | Integer + optional unit suffix: `ms`, `sec`, `secs`, `min`, `mins`, `hr`, `hrs`, `hour`, `hours`, `day`, `days`. Space before the unit is optional. No suffix means **milliseconds**. Fractions (`1.5 min`) are rejected — write `90 sec`. |
| XML escaping | `&` `<` `>` must be escaped (`&amp;` `&lt;` `&gt;`) inside element text, including in `<arguments>`. |
| Comments | Preserved by the graphical editor; use them freely. |

---

## 4. Element index

Cardinality: `1` required once, `?` optional once, `*` repeatable.

### Identity — install time

| Element | | Type | Default | Notes |
| --- | --- | --- | --- | --- |
| `id` | 1 | string | — | Windows service name. Unique per machine, alphanumeric. Cannot be changed without reinstalling. |
| `name` | ? | string | *empty* | Display name shown in services.msc. May contain spaces. |
| `description` | ? | string | *empty* | Description shown in services.msc. |
| `startmode` | ? | `Automatic` \| `Manual` \| `Disabled` \| `Boot` \| `System` | `Automatic` | Case-insensitive. `Boot`/`System` are for driver services only. |
| `delayedAutoStart` | ? | bool | `false` | Only meaningful with `startmode` `Automatic`. |
| `depend` | * | string | — | Service **id** (not display name) that must start first. One element per dependency. |
| `serviceaccount` | ? | element | LocalSystem | See section 6. |
| `onfailure` | * | element | — | See section 7. |
| `resetfailure` | ? | duration | `1 day` | Uptime after which the SCM failure counter resets. |
| `securityDescriptor` | ? | SDDL string | — | Service security descriptor. |
| `preshutdown` | ? | bool | `false` | Register for pre-shutdown notification, giving the service extra time at system shutdown. |
| `preshutdownTimeout` | ? | duration | system default (3 min) | Only used with `preshutdown`. |

> Elements in this group are applied by `install` / `refresh`. Editing them and merely
> restarting the service changes nothing — run `winsw refresh myapp.xml` (or *Save & apply*
> in the console) afterwards.

### Process

| Element | | Type | Default | Notes |
| --- | --- | --- | --- | --- |
| `executable` | 1 | path | — | Absolute path, or a name resolved through `PATH`. Services often have a different `PATH` than your shell — prefer an absolute path. |
| `arguments` | ? | string | *empty* | Passed verbatim. Line breaks and indentation inside the element are preserved, so a multi-line form is allowed but every line becomes part of the command line. |
| `startarguments` | ? | string | — | Overrides `arguments` on start. **Required** when `stoparguments` is used. |
| `workingdirectory` | ? | path | folder of the config file | |
| `priority` | ? | `Idle` \| `BelowNormal` \| `Normal` \| `AboveNormal` \| `High` \| `RealTime` | `Normal` | Case-insensitive. Raising it above `Normal` is rarely a good idea. |
| `hidewindow` | ? | bool | `false` | Starts the child with `CreateNoWindow`. |
| `interactive` | ? | bool | `false` | Allows desktop interaction (largely neutered since Vista). |
| `beeponshutdown` | ? | bool | `false` | Debug aid. |
| `env` | * | attributes | — | `<env name="KEY" value="VALUE" />`. Both attributes are required. Values are `%VAR%`-expanded. |
| `download` | * | attributes | — | See section 8. |
| `sharedDirectoryMapping` | ? | element | — | Maps UNC paths to drive letters before start. `<map label="N:" uncpath="\\server\share" />`, repeatable. Both attributes required. |
| `autoRefresh` | ? | bool | `true` | Re-applies install-time settings from the XML whenever the service starts, stops or restarts. |

### Stopping

| Element | | Type | Default | Notes |
| --- | --- | --- | --- | --- |
| `stoptimeout` | ? | duration | `15 sec` | Grace period after Ctrl+C / WM_CLOSE before the process is killed. |
| `stopexecutable` | ? | path | value of `executable` | Only used when `stoparguments` is present. |
| `stoparguments` | ? | string | — | Switches shutdown from "signal the process" to "run a stop command and wait". Requires `startarguments` instead of `arguments`. |

### Lifecycle hooks

`prestart`, `poststart`, `prestop`, `poststop` — each optional, each with the same shape:

```xml
<prestart>
  <executable>%BASE%\hooks\prepare.cmd</executable>
  <arguments>--verbose</arguments>
  <stdoutPath>%BASE%\logs\prestart.out.log</stdoutPath>
  <stderrPath>NUL</stderrPath>
</prestart>
```

| Hook | Runs |
| --- | --- |
| `prestart` | while starting, before the main process |
| `poststart` | while starting, after the main process |
| `prestop` | while stopping, before the main process is stopped |
| `poststop` | while stopping, after the main process has stopped |

`stdoutPath` / `stderrPath` redirect the hook's own output; `NUL` discards it. Note the
camelCase — `<stdoutpath>` is ignored.

### Logging

Top-level elements (these are **not** nested inside `<log>`):

| Element | | Type | Default | Notes |
| --- | --- | --- | --- | --- |
| `logpath` | ? | path | folder of the config file | Directory for the wrapper's log files. |
| `logname` | ? | string | base name of the config file | File name stem, e.g. `myapp` → `myapp.out.log`. |
| `outfiledisabled` | ? | bool | `false` | Discard stdout entirely. |
| `errfiledisabled` | ? | bool | `false` | Discard stderr entirely. |
| `outfilepattern` | ? | string | `.out.log` | Suffix appended to `logname`. **Honoured only in `roll-by-size`, `roll-by-time`, `roll-by-size-time` and `rotate` modes** — the `append`, `reset` and `roll` appenders hardcode `.out.log` / `.err.log`. |
| `errfilepattern` | ? | string | `.err.log` | Same restriction. |

The mode itself lives on `<log mode="…">` (the legacy spelling `<logmode>` still works):

| Mode | Behaviour | Nested elements |
| --- | --- | --- |
| `append` *(default)* | Append forever to `<logname>.out.log` / `.err.log`. Grows without bound. | — |
| `reset` | Truncate both files at every start. | — |
| `none` | Discard output; no files created. | — |
| `roll` | Like `append`, plus the previous file is moved to `*.old.log` at start. | — |
| `roll-by-size` | Roll to `myapp.1.out.log`, `myapp.2.out.log`… once the file exceeds a size. | `sizeThreshold` (KB, default `10240`), `keepFiles` (default `8`) |
| `roll-by-time` | One file per period, named by a timestamp pattern. | `pattern` **(required)**, `period` (days, default `1`), `keepFiles` (default: keep everything) |
| `roll-by-size-time` | Roll on size, name by timestamp, optionally also roll at a fixed clock time and zip old files. | `sizeThreshold` (KB, default `10240`), `pattern` **(required)**, `autoRollAtTime` (`HH:mm:ss`), `zipOlderThanNumDays` (int), `zipDateFormat` (default `yyyyMM`) |
| `rotate` | Deprecated alias of `roll-by-size` with its defaults. Use `roll-by-size`. | — |

`pattern` uses .NET `DateTime.ToString` format strings, e.g. `yyyyMMdd`.
`zipOlderThanNumDays` and `zipDateFormat` only take effect together with `autoRollAtTime`.

```xml
<logpath>%BASE%\logs</logpath>
<logname>myapp</logname>
<log mode="roll-by-time">
  <pattern>yyyyMMdd</pattern>
  <keepFiles>30</keepFiles>
</log>
```

### Extensions

```xml
<extensions>
  <extension enabled="true" id="mapNetworkDrives" className="…">
    <!-- extension-specific configuration -->
  </extension>
</extensions>
```

WinSW 3.x ships no bundled extensions; shared-drive mapping moved to the top-level
`<sharedDirectoryMapping>` element. Only emit `<extensions>` if you are shipping a custom
build that contains one.

---

## 5. Duration and boolean values

```xml
<stoptimeout>30 sec</stoptimeout>   <!-- ok -->
<stoptimeout>30sec</stoptimeout>    <!-- ok, space is optional -->
<stoptimeout>1 min</stoptimeout>    <!-- ok -->
<stoptimeout>30000</stoptimeout>    <!-- ok: bare number = milliseconds -->
<stoptimeout>0.5 min</stoptimeout>  <!-- ERROR: not an integer -->
<stoptimeout>30 s</stoptimeout>     <!-- ERROR: 's' is not a unit -->
<stoptimeout>30 SEC</stoptimeout>   <!-- ERROR: units are lowercase -->

<hidewindow>true</hidewindow>       <!-- ok -->
<hidewindow>1</hidewindow>          <!-- ERROR -->
```

---

## 6. Service account

Defaults to LocalSystem when the element is absent.

```xml
<serviceaccount>
  <username>DOMAIN\svc_myapp</username>
  <password>Pa55w0rd</password>
  <allowservicelogon>true</allowservicelogon>
</serviceaccount>
```

| Sub-element | Notes |
| --- | --- |
| `username` | `DOMAIN\User`, `User@DOMAIN`, or `.\User` for a local account. |
| `password` | Stored in clear text in the XML — protect the file with NTFS permissions. |
| `allowservicelogon` | `true` grants the account the *Log on as a service* right during install. |
| `prompt` | `dialog` or `console` — ask for the credentials at install time instead of storing them. |

Built-in accounts take no password:

```xml
<serviceaccount><username>LocalSystem</username></serviceaccount>
<serviceaccount><username>NT AUTHORITY\LocalService</username></serviceaccount>
<serviceaccount><username>NT AUTHORITY\NetworkService</username></serviceaccount>
```

Group Managed Service Account: append `$` to the name and omit `<password>`.

```xml
<serviceaccount>
  <username>DOMAIN\gmsa_myapp$</username>
  <allowservicelogon>true</allowservicelogon>
</serviceaccount>
```

---

## 7. Failure actions

```xml
<onfailure action="restart" delay="10 sec" />
<onfailure action="restart" delay="60 sec" />
<onfailure action="none" />
<resetfailure>1 hour</resetfailure>
```

- `action` is required: `restart`, `reboot` or `none`.
- `delay` is optional and defaults to `0`; it uses the duration format from section 3.
- The elements are consumed in order: first failure → first element, and so on.
- Once the list is exhausted, the **last** action repeats for every further failure. A
  single `<onfailure action="restart" delay="10 sec" />` therefore means "always restart".
- `reboot` reboots Windows with a bug-check screen. Use it only when you mean it.
- `resetfailure` is how long the service must stay up before the counter goes back to zero.

Note: these actions fire when the *service* is reported as failed, which for WinSW means
the wrapped process exited with a non-zero exit code.

---

## 8. Downloads

Fetched before the main executable is launched, on every start.

```xml
<download from="https://example.com/some.dat" to="%BASE%\some.dat" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat" failOnError="true" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat" auth="sspi" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat"
          auth="basic" user="aUser" password="aPassw0rd" />
<download from="http://example.com/some.dat" to="%BASE%\some.dat"
          auth="basic" unsecureAuth="true" user="aUser" password="aPassw0rd" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat"
          proxy="http://user:pass@192.168.1.5:8080/" />
```

| Attribute | Required | Default | Notes |
| --- | --- | --- | --- |
| `from` | yes | — | Source URL. |
| `to` | yes | — | Destination file path. |
| `failOnError` | no | `false` | `true` aborts service startup when the download fails. |
| `auth` | no | `none` | `none`, `sspi` (Kerberos/NTLM) or `basic`. |
| `user` | with `basic` | — | **The attribute is `user`, not `username`.** The old `complete.xml` sample is wrong here. |
| `password` | with `basic` | — | |
| `unsecureAuth` | no | `false` | Required to allow `basic` over plain HTTP; the wrapper refuses otherwise. |
| `proxy` | no | — | `http://HOST:PORT/` or `http://USER:PASS@HOST:PORT/`. |

If the destination already exists, the wrapper sends `If-Modified-Since` and skips the
transfer on `304 Not Modified`.

---

## 9. Checklist before shipping a file

1. Root element is `<service>`; the document is well-formed XML.
2. `<id>` is unique on the machine and contains no spaces.
3. `<executable>` is an absolute path (or `%BASE%`-based), and the file exists.
4. Every path that lives inside the installation folder is written as `%BASE%\…`.
5. `&`, `<`, `>` inside `<arguments>` are escaped.
6. Element names match section 4 **exactly**, including camelCase
   (`delayedAutoStart`, `preshutdownTimeout`, `securityDescriptor`, `autoRefresh`,
   `sharedDirectoryMapping`, `stdoutPath`, `stderrPath`, `sizeThreshold`, `keepFiles`,
   `autoRollAtTime`, `zipOlderThanNumDays`, `zipDateFormat`).
7. Booleans are `true`/`false`; durations are integer + a lowercase unit.
8. If `<stoparguments>` is present, `<startarguments>` is used instead of `<arguments>`.
9. If the log mode is `roll-by-time` or `roll-by-size-time`, `<pattern>` is present.
10. `<outfilepattern>` / `<errfilepattern>` are only used with a mode that honours them.
11. `<depend>` names service ids, not display names.
12. A `<serviceaccount>` with a password is only used on a file with restricted NTFS ACLs.
13. Install-time settings are applied with `winsw refresh` after an edit, not just a restart.

---

## 10. Worked examples

### Minimal

```xml
<service>
  <id>myapp</id>
  <executable>%BASE%\myapp.exe</executable>
</service>
```

### Java application, daily logs, restart on crash

```xml
<?xml version="1.0" encoding="UTF-8"?>
<service>
  <id>jenkins</id>
  <name>Jenkins</name>
  <description>Jenkins continuous integration server.</description>

  <env name="JENKINS_HOME" value="%BASE%" />
  <executable>java</executable>
  <arguments>-Xrs -Xmx1g -jar "%BASE%\jenkins.war" --httpPort=8080</arguments>

  <startmode>Automatic</startmode>
  <delayedAutoStart>true</delayedAutoStart>
  <stoptimeout>1 min</stoptimeout>

  <onfailure action="restart" delay="10 sec" />
  <onfailure action="restart" delay="60 sec" />
  <resetfailure>1 hour</resetfailure>

  <logpath>%BASE%\logs</logpath>
  <log mode="roll-by-time">
    <pattern>yyyyMMdd</pattern>
    <keepFiles>30</keepFiles>
  </log>
</service>
```

### Service account, dependency, pre-start hook, size-based logs

```xml
<?xml version="1.0" encoding="UTF-8"?>
<service>
  <id>svc-report</id>
  <name>Report Generator</name>
  <description>Generates nightly reports from the warehouse.</description>

  <serviceaccount>
    <username>CORP\svc_report</username>
    <password>Pa55w0rd</password>
    <allowservicelogon>true</allowservicelogon>
  </serviceaccount>

  <depend>MSSQLSERVER</depend>

  <workingdirectory>%BASE%</workingdirectory>
  <executable>%BASE%\python\python.exe</executable>
  <arguments>-u service.py --config %BASE%\conf\prod.ini</arguments>
  <hidewindow>true</hidewindow>
  <priority>BelowNormal</priority>

  <prestart>
    <executable>%BASE%\hooks\wait-for-db.cmd</executable>
    <stdoutPath>%BASE%\logs\prestart.log</stdoutPath>
    <stderrPath>%BASE%\logs\prestart.log</stderrPath>
  </prestart>

  <sharedDirectoryMapping>
    <map label="R:" uncpath="\\fileserver\reports" />
  </sharedDirectoryMapping>

  <logpath>%BASE%\logs</logpath>
  <logname>report</logname>
  <log mode="roll-by-size">
    <sizeThreshold>20480</sizeThreshold>
    <keepFiles>10</keepFiles>
  </log>

  <onfailure action="restart" delay="30 sec" />
  <resetfailure>2 hours</resetfailure>
</service>
```

### Graceful stop through a stop command

```xml
<service>
  <id>tomcat</id>
  <name>Apache Tomcat</name>
  <executable>%BASE%\bin\catalina.bat</executable>
  <startarguments>run</startarguments>
  <stopexecutable>%BASE%\bin\catalina.bat</stopexecutable>
  <stoparguments>stop</stoparguments>
  <stoptimeout>2 min</stoptimeout>
</service>
```

---

## 11. Related pages

- [XML configuration file](xml-config-file.md) — the full narrative reference.
- [Logging and error reporting](logging-and-error-reporting.md)
- [CLI commands](cli-commands.md)
- [Graphical console](gui.md)
