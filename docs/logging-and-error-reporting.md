# Logging and error reporting

## Logging

Winsw supports several different ways to capture stdout and stderr from the process you launch.

## Log directory

The `<logpath>` element specifies the directory in which the log files are created. If this element is absent, it'll default to the same directory where the configuration file resides.

## Append mode (default)

In this mode, *myapp.out.log* and *myapp.err.log* (where *myapp* is the base name of the executable and the configuration file) are created and outputs are simply appended to these files. Note that the file can get quite big.

```xml
<log mode="append"/>
```

## Reset mode

Works like the append mode, except that every time the service starts, the old log files are truncated.

```xml
<log mode="reset"/>
```

## Ignore mode

Throw away stdout and stderr, and do not produce any log files at all.

```xml
<log mode="none"/>
```

## Roll mode

Works like the append mode, but in addition, if the log file gets bigger than a set size, it gets rolled to *myapp.0.out.log*, the one before that to *myapp.1.out.log*, and so on, up to `keepFiles` rolled files. The nested `<sizeThreshold>` element specifies the rotation threshold in KB (defaults to 10MB), and the nested `<keepFiles>` element specifies the number of rolled files to keep (defaults to 8.)

```xml
<log mode="roll-by-size">
  <sizeThreshold>10240</sizeThreshold>
  <keepFiles>8</keepFiles>
</log>
```

If one of the rolled files cannot be moved or deleted, for example because another program holds it open, WinSW writes the reason to the Windows event log and rolls the others; the file that would have moved onto its name is deleted instead.
If *myapp.out.log* itself cannot be rolled, WinSW writes the reason to the Windows event log and keeps appending to it, and tries again once another `<sizeThreshold>` of output has been written.

## Roll by time mode

Works like the roll mode, except that instead of using the size as a threshold, use the time period as the threshold.

This configuration must accompany a nested `<pattern>` element, which specifies the timestamp pattern used as the log file name.
The optional `<keepFiles>` element specifies the number of rolled log files to keep. When the limit is exceeded, the oldest files are deleted automatically. If omitted, no files are deleted.

```xml
<log mode="roll-by-time">
  <pattern>yyyyMMdd</pattern>
  <keepFiles>30</keepFiles>
</log>
```

The syntax of the pattern string is specified by [DateTime.ToString(String)](https://docs.microsoft.com/dotnet/api/system.datetime.tostring#System_DateTime_ToString_System_String_).
For example, in the above example, the log of Jan 1, 2013 gets written to `myapp.20130101.out.log` and `myapp.20130101.err.log`.

## Roll by size and time mode

Works in a combination of roll size mode and roll time mode. Outputs are appended to *myapp.out.log* and *myapp.err.log*, and if one of them gets bigger than a set size, it gets rolled to a name made from the `<pattern>` provided and a number, and a new file is started.
The nested `<sizeThreshold>` element specifies the rotation threshold in KB (defaults to 10MB), and the nested `<pattern>` element is required.

```xml
<log mode="roll-by-size-time">
  <sizeThreshold>10240</sizeThreshold>
  <pattern>yyyyMMdd</pattern>
  <autoRollAtTime>00:00:00</autoRollAtTime>
</log>
```

The syntax of the pattern string is specified by [DateTime.ToString(String)](https://docs.microsoft.com/dotnet/api/system.datetime.tostring#System_DateTime_ToString_System_String_).
For example, in the above example, the log of Jan 1, 2013 gets rolled to `myapp.20130101.#0001.out.log`, `myapp.20130101.#0002.out.log` and so on.

The syntax of the autoRollAtTime is specified by [TimeSpan.ToString(String)](https://docs.microsoft.com/dotnet/api/system.timespan.tostring#System_TimeSpan_ToString_System_String_).
For example, in the above example, at the start of the day it will roll the file over, whatever its size.
A file rolled at that time is named after the day before.

If a file cannot be rolled, for example because another program holds it open without allowing it to be deleted, WinSW writes the reason to the Windows event log and keeps appending to the same file.
A roll on size is tried again once another `<sizeThreshold>` of output has been written.

### Automatic archiving of logs

:warning: This feature is reported to be broken in recent WinSW versions.
It is a potential subject for removal.

Archiving runs only right after the roll at `<autoRollAtTime>`; a roll on size never archives anything.
Without `<autoRollAtTime>`, `<zipOlderThanNumDays>` and `<zipDateFormat>` have no effect.

```xml
<log mode="roll-by-size-time">
  <pattern>yyyyMMdd</pattern>
  <autoRollAtTime>00:00:00</autoRollAtTime>
  <zipOlderThanNumDays>5</zipOlderThanNumDays>
  <zipDateFormat>yyyyMM</zipDateFormat>
</log>
```

The `zipOlderThanNumDays` element provides the number of days of files to keep as they are: every file in the log directory whose name ends like the log files (*.out.log* and *.err.log* by default) and that has not been written to for longer is added to a zip file and deleted.
This includes the logs of other programs that share the directory.

The `zipDateFormat` element names the zip file from the date the log file was last accessed, for example *myapp.201301.zip* for January 2013 in the above example. It defaults to `yyyyMM`, and its syntax is specified by [DateTime.ToString(String)](https://docs.microsoft.com/dotnet/api/system.datetime.tostring#System_DateTime_ToString_System_String_).

## Error reporting

WinSW exits with code 0 on success. Any positive exit code is a [system error code](https://docs.microsoft.com/windows/win32/debug/system-error-codes).

When winsw is running as a service, more detailed error information is reported to the Windows event log.
