using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The handful of Win32 calls the GUI needs. All of them are query-only and succeed for
    /// a standard user; anything that mutates a service goes through the elevated wrapper.
    /// </summary>
    internal static class NativeMethods
    {
        internal const int SC_MANAGER_CONNECT = 0x0001;
        internal const int SERVICE_QUERY_STATUS = 0x0004;
        internal const int SC_STATUS_PROCESS_INFO = 0;
        internal const int ERROR_INSUFFICIENT_BUFFER = 122;
        internal const int ERROR_CANCELLED = 1223;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SERVICE_STATUS_PROCESS
        {
            public int ServiceType;
            public int CurrentState;
            public int ControlsAccepted;
            public int Win32ExitCode;
            public int ServiceSpecificExitCode;
            public int CheckPoint;
            public int WaitHint;
            public int ProcessId;
            public int ServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, int access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, int access);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>
        /// Connects to the service control manager for querying. Zero when it refused, which
        /// the caller treats as every service being unanswerable rather than as an error.
        /// </summary>
        internal static IntPtr OpenServiceManager() => OpenSCManagerW(null, null, SC_MANAGER_CONNECT);

        /// <summary>
        /// Reads the full SERVICE_STATUS_PROCESS: state, hosting process and the exit codes
        /// the service left behind the last time it stopped.
        /// </summary>
        /// <param name="manager">
        /// A connection from <see cref="OpenServiceManager"/>. Taken rather than made here so
        /// that a pass over every service costs one connection, not one per service: the
        /// connection is a round trip to services.exe of its own, before the query.
        /// </param>
        internal static bool TryQueryServiceStatus(IntPtr manager, string serviceName, out SERVICE_STATUS_PROCESS status)
        {
            status = default;

            if (manager == IntPtr.Zero)
            {
                return false;
            }

            IntPtr service = OpenServiceW(manager, serviceName, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                int size = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, buffer, size, out _))
                    {
                        return false;
                    }

                    status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buffer);
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }

        /// <summary>ERROR_SERVICE_SPECIFIC_ERROR: the real code is in ServiceSpecificExitCode.</summary>
        internal const int ERROR_SERVICE_SPECIFIC_ERROR = 1066;

        // Process snapshot ------------------------------------------------------
        //
        // One NtQuerySystemInformation call describes every process on the machine: its
        // parent, its name, its times and its counters. It is what Task Manager reads, what
        // Toolhelp copies into its own snapshot, and what the runtime takes — once per
        // Process object — to answer WorkingSet64 or HandleCount. Asking for it directly
        // means asking once per poll instead of once per service, and needs no handle to
        // any process, so a standard user reads a LocalSystem service's process as fully as
        // an administrator does.

        /// <summary>SystemProcessInformation, from SYSTEM_INFORMATION_CLASS.</summary>
        internal const int SystemProcessInformation = 5;

        /// <summary>The buffer was too small; the returned length says what would have fitted.</summary>
        internal const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        [StructLayout(LayoutKind.Sequential)]
        internal struct UNICODE_STRING
        {
            /// <summary>In bytes, without a terminator.</summary>
            public ushort Length;

            public ushort MaximumLength;

            /// <summary>Points into the same buffer the entry was read from.</summary>
            public IntPtr Buffer;
        }

        /// <summary>
        /// The head of one entry in a SystemProcessInformation buffer, up to the last field
        /// that is read. The pool, page-file and I/O counters that follow it, and then the
        /// entry's SYSTEM_THREAD_INFORMATION array, are stepped over with NextEntryOffset.
        /// </summary>
        /// <remarks>
        /// Field for field the layout the runtime's own System.Diagnostics.Process declares,
        /// which is what keeps it right on x86, x64 and ARM64 alike: the handles and sizes
        /// are pointer-sized, everything else is fixed. The runtime leaves the 48 bytes
        /// between NumberOfThreads and ImageName as reserved; they are the process's times,
        /// as every process viewer since Windows 2000 has read them, and CreateTime is the
        /// reason to read them here — the start time of a process this user may not open.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_PROCESS_INFORMATION
        {
            public uint NextEntryOffset;
            public uint NumberOfThreads;
            public long WorkingSetPrivateSize;
            public uint HardFaultCount;
            public uint NumberOfThreadsHighWatermark;
            public ulong CycleTime;

            /// <summary>A FILETIME: 100-nanosecond intervals since 1601, UTC. Zero for Idle and System.</summary>
            public long CreateTime;

            /// <summary>In 100-nanosecond units, like a TimeSpan's ticks.</summary>
            public long UserTime;

            public long KernelTime;

            /// <summary>The image file's name alone, "WinSW.exe"; empty for Idle and System.</summary>
            public UNICODE_STRING ImageName;

            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
            public uint HandleCount;
            public uint SessionId;
            public UIntPtr UniqueProcessKey;
            public UIntPtr PeakVirtualSize;
            public UIntPtr VirtualSize;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
        }

        /// <summary>
        /// Returns an NTSTATUS: negative on failure, <see cref="STATUS_INFO_LENGTH_MISMATCH"/>
        /// with <paramref name="returnLength"/> set when the buffer has to grow.
        /// </summary>
        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int bufferLength, out int returnLength);

        // Window resize border ------------------------------------------------
        //
        // What WindowResizeBorder answers WM_NCHITTEST with. The codes are the window
        // manager's own: it reads the answer and starts the matching resize drag.

        internal const int WM_NCHITTEST = 0x0084;

        internal const int HTNOWHERE = 0;
        internal const int HTLEFT = 10;
        internal const int HTRIGHT = 11;
        internal const int HTTOP = 12;
        internal const int HTTOPLEFT = 13;
        internal const int HTTOPRIGHT = 14;
        internal const int HTBOTTOM = 15;
        internal const int HTBOTTOMLEFT = 16;
        internal const int HTBOTTOMRIGHT = 17;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>The window's outer rectangle in screen pixels.</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out RECT rect);

        // Notification area ---------------------------------------------------
        //
        // WPF has no tray control. Windows Forms has one, and using it cost the whole
        // Windows Forms framework — about 15 MB of the self-contained build — for a single
        // class. Shell_NotifyIcon is what that class calls.

        internal const int NIM_ADD = 0x0;
        internal const int NIM_MODIFY = 0x1;
        internal const int NIM_DELETE = 0x2;
        internal const int NIM_SETVERSION = 0x4;

        internal const int NIF_MESSAGE = 0x1;
        internal const int NIF_ICON = 0x2;
        internal const int NIF_TIP = 0x4;
        internal const int NIF_INFO = 0x10;

        internal const int NIIF_INFO = 0x1;
        internal const int NIIF_ERROR = 0x3;

        /// <summary>
        /// Version 4 reports the cursor position with the message and sends NIN_SELECT and
        /// NIN_BALLOONUSERCLICK rather than raw mouse messages, which is the difference
        /// between reading the notification and guessing at it.
        /// </summary>
        internal const int NOTIFYICON_VERSION_4 = 4;

        internal const int WM_APP = 0x8000;

        /// <summary>The private message the icon reports through. Any WM_APP + n will do.</summary>
        internal const int WM_TRAYICON = WM_APP + 1;

        // The notifications that arrive in lParam are WM_USER-based, not WM_APP-based, and
        // the two bases are nowhere near each other. They share the message space with
        // WM_TRAYICON above but never the same position: WM_TRAYICON is the message, these
        // are what it carries.
        internal const int WM_USER = 0x0400;

        internal const int NIN_SELECT = WM_USER + 0;
        internal const int NIN_KEYSELECT = WM_USER + 1;
        internal const int NIN_BALLOONTIMEOUT = WM_USER + 4;
        internal const int NIN_BALLOONUSERCLICK = WM_USER + 5;

        internal const int WM_LBUTTONDBLCLK = 0x203;
        internal const int WM_RBUTTONUP = 0x205;
        internal const int WM_CONTEXTMENU = 0x7B;

        internal const int SM_CXSMICON = 49;

        /// <summary>WS_CHILD. A message-only window is parented and never shown.</summary>
        internal const int WS_CHILD = unchecked((int)0x40000000);

        /// <summary>A message-only window: it has a queue but is never shown or enumerated.</summary>
        internal static readonly IntPtr HWND_MESSAGE = new(-3);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NOTIFYICONDATA
        {
            public int Size;
            public IntPtr Window;
            public int Id;
            public int Flags;
            public int CallbackMessage;
            public IntPtr Icon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Tip;

            public int State;
            public int StateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Info;

            /// <summary>Overlaps uTimeout, which is ignored from Vista on.</summary>
            public int Version;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string InfoTitle;

            public int InfoFlags;
            public Guid ItemGuid;
            public IntPtr BalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATA data);

        /// <summary>
        /// Explorer broadcasts this when the taskbar is created, which includes every time it
        /// restarts. An icon added before that is gone and has to be added again.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegisterWindowMessageW(string message);

        /// <summary>
        /// A menu opened from a tray icon stays up after a click elsewhere unless its owner
        /// is the foreground window first.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        /// <summary>Any process may take the foreground next; see <see cref="AllowSetForegroundWindow"/>.</summary>
        internal const int ASFW_ANY = -1;

        /// <summary>
        /// Lets another process bring its window to the front. Windows refuses that to a process
        /// the user is not interacting with, so a console woken by a second launch would only
        /// flash in the taskbar; the second launch is the one the user just started, and it can
        /// pass its turn on.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int processId);
    }
}
