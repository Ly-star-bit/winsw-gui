using System;
using System.Diagnostics;
using System.Threading;
using WinSW.Native;
using WinSW.Util;
using Xunit;

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
                (Process Process, Handle Handle)? found = null;
                foreach (var child in parent.GetChildren())
                {
                    if (found is null && string.Equals(child.Process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = child;
                        continue;
                    }

                    child.Process.Dispose();
                    child.Handle.Dispose();
                }

                if (found is { } result)
                {
                    return result;
                }

                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Process {parent.Id} did not start '{name}'.");
                Thread.Sleep(100);
            }
        }
    }
}
