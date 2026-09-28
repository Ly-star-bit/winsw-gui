using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using WinSW.Native;
using Xunit;
using static WinSW.Native.ProcessApis;

namespace WinSW.Tests.Util
{
    internal static class ChildProcesses
    {
        /// <summary>
        /// Waits for <paramref name="parent"/> to have started a process called
        /// <paramref name="name"/>, and returns it. The handle keeps its process ID from being
        /// reused, so a test can ask about the process after it has exited; the caller disposes
        /// of both.
        /// </summary>
        internal static (Process Process, Handle Handle) WaitFor(Process parent, string name)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                if (Find(parent, name) is { } result)
                {
                    return result;
                }

                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Process {parent.Id} did not start '{name}'.");
                Thread.Sleep(100);
            }
        }

        // What ProcessExtensions.GetChildren does, for one name. Not GetChildren itself: its
        // tuple is System.ValueTuple.dll's type in the .NET Framework build of WinSW.Core and
        // mscorlib's in this one, so on net471 the call fails with MissingMethodException.
        private static (Process Process, Handle Handle)? Find(Process parent, string name)
        {
            var startTime = parent.StartTime;
            foreach (var other in Process.GetProcessesByName(name))
            {
                var handle = OpenProcess(ProcessAccess.QueryInformation, false, other.Id);
                try
                {
                    if (handle != IntPtr.Zero
                        && other.StartTime > startTime
                        && NtQueryInformationProcess(
                            handle,
                            PROCESSINFOCLASS.ProcessBasicInformation,
                            out var information,
                            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>()) == 0
                        && (int)information.InheritedFromUniqueProcessId == parent.Id)
                    {
                        return (other, handle);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException || e is Win32Exception)
                {
                    // It exited, or will not say when it started: not the one being waited for.
                }

                other.Dispose();
                handle.Dispose();
            }

            return null;
        }
    }
}
