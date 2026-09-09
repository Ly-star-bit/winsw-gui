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
        internal const int TH32CS_SNAPPROCESS = 0x0002;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PROCESSENTRY32
        {
            public int Size;
            public int Usage;
            public int ProcessId;
            public IntPtr DefaultHeapId;
            public int ModuleId;
            public int Threads;
            public int ParentProcessId;
            public int PriorityClassBase;
            public int Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// Returns the process ID hosting <paramref name="serviceName"/>, or 0 when the
        /// service is not running or cannot be queried.
        /// </summary>
        internal static int GetServiceProcessId(string serviceName) =>
            TryQueryServiceStatus(serviceName, out var status) ? status.ProcessId : 0;

        /// <summary>
        /// Reads the full SERVICE_STATUS_PROCESS: state, hosting process and the exit codes
        /// the service left behind the last time it stopped.
        /// </summary>
        internal static bool TryQueryServiceStatus(string serviceName, out SERVICE_STATUS_PROCESS status)
        {
            status = default;

            IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero)
            {
                return false;
            }

            try
            {
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
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        /// <summary>ERROR_SERVICE_SPECIFIC_ERROR: the real code is in ServiceSpecificExitCode.</summary>
        internal const int ERROR_SERVICE_SPECIFIC_ERROR = 1066;

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
    }
}
