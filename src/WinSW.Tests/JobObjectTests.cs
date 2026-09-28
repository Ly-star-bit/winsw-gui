using System.Diagnostics;
using WinSW.Native;
using WinSW.Tests.Util;
using Xunit;

namespace WinSW.Tests
{
    /// <summary>
    /// The job behind <c>&lt;endProcessesWithWrapper&gt;</c>. The wrapper puts its own process
    /// in it; here a child stands in for the wrapper, since a test host in the job would be
    /// ended along with everything else when the test closes it.
    /// </summary>
    public class JobObjectTests
    {
        [Fact]
        public void Closing_The_Job_Ends_A_Child_And_A_Grandchild_Whose_Parent_Has_Exited()
        {
            var job = JobObject.CreateKillOnClose();
            bool closed = false;
            try
            {
                // cmd takes its commands from the pipe, so it starts nothing before it is in the job.
                using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/d /q")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                })!;
                JobObject.Assign(job, child.Handle);

                child.StandardInput.WriteLine("start \"\" /b ping -n 3600 127.0.0.1 >nul");
                var grandchild = ChildProcesses.WaitFor(child, "ping");
                using (grandchild.Process)
                using (grandchild.Handle)
                {
                    // With its parent gone, nothing that follows parent links can find the
                    // grandchild any more, and it does not end on its own.
                    child.StandardInput.WriteLine("exit");
                    Assert.True(child.WaitForExit(10_000));
                    Assert.False(grandchild.Process.HasExited);

                    closed = true;
                    job.Dispose();

                    Assert.True(grandchild.Process.WaitForExit(10_000));
                }
            }
            finally
            {
                // Ends whatever a failed assertion left running.
                if (!closed)
                {
                    job.Dispose();
                }
            }
        }
    }
}
