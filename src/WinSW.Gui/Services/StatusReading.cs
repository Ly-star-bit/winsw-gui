using System;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// One pass over the machine: a connection to the service control manager and a
    /// snapshot of every process, each taken once and shared by every <see cref="Sample"/>
    /// made against it. Dispose closes the connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sampling used to be one self-contained call per service, and what each one cost was
    /// hidden inside <see cref="System.Diagnostics.Process"/>: <c>GetProcessById</c> enumerates
    /// every process ID on the machine, and the first counter read has the runtime take a
    /// <c>SystemProcessInformation</c> snapshot — every process, every thread — and keep the
    /// one entry it was asked about. That was one full snapshot per running service per
    /// tick, plus the connection to the service control manager made and dropped per service,
    /// plus one more snapshot through Toolhelp for the process tree. Ten wrapped services at
    /// the burst rate came to some twenty-five snapshots a second, of a machine that had not
    /// changed between them.
    /// </para>
    /// <para>
    /// Now one connection answers every status query and one snapshot answers every counter
    /// and the tree. The snapshot opens no process, so a LocalSystem service's start time is
    /// read by a standard user as readily as by an administrator; the old path had to leave
    /// it blank.
    /// </para>
    /// </remarks>
    public sealed class StatusReading : IDisposable
    {
        private IntPtr manager;

        public StatusReading()
        {
            // The processes first. A service that starts between the two readings shows its
            // state with no counters this tick, and its counters the next; the other order
            // could show counters for a process that had been read and then exited.
            this.Processes = ProcessSnapshot.Take();
            this.manager = NativeMethods.OpenServiceManager();
        }

        /// <summary>Null when the snapshot could not be taken; every sample then has no process.</summary>
        public ProcessSnapshot? Processes { get; }

        /// <summary>
        /// Reads the volatile state of one service and its hosting process.
        /// </summary>
        /// <remarks>
        /// Split from <see cref="ServiceDiscovery.Apply"/> so the reading can be done off the UI
        /// thread. What is left per service is one round trip to the service control manager;
        /// everything about the process is a dictionary lookup.
        /// </remarks>
        public ServiceSample Sample(string serviceName)
        {
            if (!NativeMethods.TryQueryServiceStatus(this.manager, serviceName, out var status))
            {
                // The service was uninstalled between the scan and this reading.
                return default;
            }

            var state = (ServiceControllerStatus)status.CurrentState;
            var sample = new ServiceSample
            {
                Queried = true,
                Status = state,
                ProcessId = state == ServiceControllerStatus.Running ? status.ProcessId : 0,
                LastExitCode = status.Win32ExitCode == NativeMethods.ERROR_SERVICE_SPECIFIC_ERROR
                    ? status.ServiceSpecificExitCode
                    : status.Win32ExitCode,
            };

            if (sample.ProcessId <= 0 || this.Processes is null || !this.Processes.TryGet(sample.ProcessId, out var process))
            {
                // Not running, or started after the snapshot was taken.
                return sample;
            }

            return sample with
            {
                HasProcess = true,
                ProcessorTime = process.ProcessorTime,
                WorkingSet = process.WorkingSetBytes,
                Handles = process.HandleCount,
                StartedAt = process.StartedAt,
            };
        }

        /// <summary>The tree under a process, out of the same snapshot the samples came from.</summary>
        public ProcessNode? Tree(int processId) =>
            this.Processes is { } processes ? ProcessTreeProvider.Build(processes, processId) : null;

        public void Dispose()
        {
            if (this.manager != IntPtr.Zero)
            {
                NativeMethods.CloseServiceHandle(this.manager);
                this.manager = IntPtr.Zero;
            }
        }
    }
}
