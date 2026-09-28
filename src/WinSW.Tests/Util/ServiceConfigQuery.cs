using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using WinSW.Native;

namespace WinSW.Tests.Util
{
    /// <summary>
    /// Reads back what the service control manager holds for a service, for the settings that
    /// <see cref="ServiceController"/> does not expose. A test that asserts on these reads the
    /// manager's answer, not the configuration file the wrapper was given.
    /// </summary>
    internal static class ServiceConfigQuery
    {
        /// <exception cref="Win32Exception" />
        internal static bool DelayedAutoStart(string serviceName)
        {
            // SERVICE_DELAYED_AUTO_START_INFO is a single BOOL.
            return Query(serviceName, ServiceApis.ServiceConfigInfoLevels.DELAYED_AUTO_START_INFO, buffer => Marshal.ReadInt32(buffer) != 0);
        }

        /// <exception cref="Win32Exception" />
        internal static SC_ACTION[] FailureActions(string serviceName, out TimeSpan resetPeriod)
        {
            uint resetSeconds = 0;
            var actions = Query(serviceName, ServiceApis.ServiceConfigInfoLevels.FAILURE_ACTIONS, buffer =>
            {
                // The action array is in the same buffer, so it is copied out before the buffer
                // is freed.
                var info = Marshal.PtrToStructure<SERVICE_FAILURE_ACTIONS>(buffer);
                resetSeconds = info.ResetPeriod;

                var result = new SC_ACTION[info.ActionsCount];
                int size = Marshal.SizeOf<SC_ACTION>();
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = Marshal.PtrToStructure<SC_ACTION>(info.Actions + (i * size));
                }

                return result;
            });

            resetPeriod = TimeSpan.FromSeconds(resetSeconds);
            return actions;
        }

        private static T Query<T>(string serviceName, ServiceApis.ServiceConfigInfoLevels infoLevel, Func<IntPtr, T> read)
        {
            using var controller = new ServiceController(serviceName);
            using var handle = controller.ServiceHandle;

            _ = QueryServiceConfig2(handle, infoLevel, IntPtr.Zero, 0, out int bytesNeeded);

            var buffer = Marshal.AllocHGlobal(bytesNeeded);
            try
            {
                if (!QueryServiceConfig2(handle, infoLevel, buffer, bytesNeeded, out _))
                {
                    throw new Win32Exception();
                }

                return read(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport(Libraries.Advapi32, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryServiceConfig2W")]
        private static extern bool QueryServiceConfig2(SafeHandle serviceHandle, ServiceApis.ServiceConfigInfoLevels infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

        // SERVICE_FAILURE_ACTIONSW, with the two strings left as pointers since nothing here
        // reads them.
        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_FAILURE_ACTIONS
        {
            public uint ResetPeriod;
            public IntPtr RebootMessage;
            public IntPtr Command;
            public int ActionsCount;
            public IntPtr Actions;
        }
    }
}
