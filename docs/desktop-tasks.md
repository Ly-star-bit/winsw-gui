# Desktop tasks

A Windows service cannot show anything.

Since Windows Vista, every service runs in **session 0**, which is isolated from the desktop.
Session 0 has a window station and a desktop object, but no display renders them: a screenshot of it comes back blank, its resolution is a fiction, and no keystroke or mouse event ever reaches it.
A program that opens a window, reads the screen or drives the mouse — an automation robot above all — cannot do its job there.

`<interactive>true</interactive>` does not change this, and in WinSW 3 it does nothing at all:
the element is parsed and never read, and the service is always created as `SERVICE_WIN32_OWN_PROCESS`.
Even when it did something, all it did was set `SERVICE_INTERACTIVE_PROCESS`, whose only visible effect came from the *Interactive Services Detection* service — disabled by default since Windows 8, and removed outright in Windows 10 version 1803 and Windows Server 2019.

The way to run such a program unattended is a **scheduled task with a logon trigger**.
That starts it in the session the user actually logged on to, on a real desktop.
This is what the graphical console calls a *desktop task*.

## What it is made of

```
Task Scheduler  ──at logon──▶  WinSW.exe console app.xml  ──▶  the program
   (session 1)                     (session 1)                  (session 1)
```

The task does not start the program directly. It starts the wrapper in [console mode](cli-commands.md#console-command), which then starts the program.
Everything the wrapper does for a service it also does here:

| | |
| --- | --- |
| Configuration | the same `*.xml`, read the same way |
| Logs | the same `<log>` modes, the same rotation, the same `%BASE%` |
| Environment | `<env>`, `<workingdirectory>`, `SERVICE_ID`, `BASE` |
| Hooks | `<prestart>`, `<poststart>`, `<prestop>`, `<poststop>` |
| Stop | `<stoptimeout>`, `<stopexecutable>`, Ctrl+C to the child, then the process tree |

What the task scheduler contributes is the half a service would have got from the service control manager: starting at logon, and starting the program again if it dies.

## What is different from a service

**Someone has to be logged on.** The trigger fires at logon and the task runs in that session; log off and it stops.
For unattended use the machine has to log on by itself — the usual arrangement is a dedicated account with automatic logon, no screen saver and no lock screen.
An automation robot that works by recognising what is on screen also needs a display: a headless machine with no monitor attached falls back to a minimal resolution, and a virtual display driver is the usual fix.

**Recovery is a repetition, not a recovery action.** `<onfailure>` is a list of actions the service control manager takes, and it never sees a scheduled task, so it is ignored.
The console registers a repeating trigger instead, with the multiple-instances policy set to *ignore new*: every tick tries to start the task, a tick that finds it already running does nothing, and a tick that finds it gone starts it.
This is more robust than the task scheduler's own *restart on failure*, which depends on how the exit is classified — but it also brings back a program that exited on purpose. Turn it off for a program that is meant to finish.

**No elevation anywhere.** The task runs as the account that registered it, using its own token, and nothing in registering it needs administrator rights.
That is why desktop tasks install under `%LOCALAPPDATA%\WinSW` rather than `%ProgramData%\WinSW`: the account has to be able to write the logs, and a folder an administrator created is not one it can necessarily write to.

**Elevation is a checkbox, not a prompt.** *Run with the account's full token* sets the task's run level to `HighestAvailable`.
It only does anything if the account is an administrator, and its point is that the program then starts elevated **without** a UAC prompt at logon.

## The console window

The wrapper is a console application, and it allocates a console so that a stop can send the child a Ctrl+C.
In session 0 nobody sees it; in a logged-on session it would be a black window in front of the user for as long as the program runs.

`<hidewindow>true</hidewindow>` hides it, and the console sets it for every desktop task it registers.
Turning it off is the quickest way to see why a program will not start.

## Stopping one

A service is stopped through the service control manager. A scheduled task has no such channel — the task scheduler's *End* terminates the process tree, skipping `<stopexecutable>` and every stop hook.

So a console-mode wrapper publishes a named event derived from its service ID, and waits on it.
The console sets that event, gives the wrapper its configured `<stoptimeout>` to shut the program down cleanly, and only terminates the task if that runs out.
`winsw console --stop <config>` does the same thing from a command line.

The event is scoped to the logon session, which is where both halves live.
One case it does not cover: a task registered with the elevated run level cannot be signalled from a console that is not itself elevated, and the stop falls back to termination.

## Where things end up

```
%LOCALAPPDATA%\WinSW\
├── bin\
│   └── WinSW.exe                 one wrapper, shared by every desktop task
└── <task id>\
    ├── <task id>.xml             the configuration
    └── logs\                     %BASE%\logs
```

The task itself is registered in the task scheduler library under `\WinSW\<task id>`.
Deleting it from the console removes the task and leaves the configuration and logs alone.

## Choosing between the two

| | Windows service | Desktop task |
| --- | --- | --- |
| Starts | at boot | at logon |
| Needs a logged-on user | no | yes |
| Can show a window | no | yes |
| Can read the screen or move the mouse | no | yes |
| Survives a log off | yes | no |
| Installing it needs administrator rights | yes | no |
| Restart after a crash | `<onfailure>`, via the SCM | repeating trigger |

A server-side process with no user interface belongs in a service. An automation robot belongs in a desktop task. A program that has a tray icon but does its real work in the background is usually two things: a service, and a small companion started at logon.
