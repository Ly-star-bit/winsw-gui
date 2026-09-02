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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);

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
    }
}
