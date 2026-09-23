using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinSW.Gui.Services
{
    /// <summary>How a cleanup went: what is gone, how much that freed, and what is still there.</summary>
    public sealed record LogCleanupOutcome(int Deleted, long Freed, int Left, bool Declined);

    /// <summary>
    /// Deletes a service's old log files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rolling modes keep a bounded number of files, but "roll by time" and "append" do not, and
    /// a service left alone for a year fills the disk quietly. The files chosen are by age
    /// alone — last written more than a number of days ago — rather than by working out from
    /// their names which one is live: the file being written now has today's time, so it never
    /// qualifies, and no naming scheme has to be understood to know that.
    /// </para>
    /// <para>
    /// Logs under the install root were written by the service's account, often where a
    /// standard user may read but not delete. What this account may delete goes at once; what
    /// it is refused goes in one elevated batch. A file another process holds open is left
    /// where it is: that is not a question of rights, and elevation would not change it.
    /// </para>
    /// </remarks>
    public static class LogCleanup
    {
        public static async Task<LogCleanupOutcome> DeleteAsync(IReadOnlyList<(string Path, long Length)> files)
        {
            var refused = await Task.Run(() => DeleteDirectly(files.Select(f => f.Path))).ConfigureAwait(false);

            bool declined = false;
            if (refused.Count > 0)
            {
                var result = await WinSwCli.DeleteElevatedAsync(refused).ConfigureAwait(false);
                declined = result.Cancelled;
            }

            // Counted from what is gone rather than from exit codes: del reports little, and a
            // file deleted by the batch is as deleted as one removed here.
            return await Task.Run(() => Tally(files, declined)).ConfigureAwait(false);
        }

        /// <summary>Deletes what this account may; returns the paths refused for want of rights.</summary>
        internal static List<string> DeleteDirectly(IEnumerable<string> paths)
        {
            var refused = new List<string>();
            foreach (string path in paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (UnauthorizedAccessException)
                {
                    refused.Add(path);
                }
                catch (IOException)
                {
                    // In use.
                }
            }

            return refused;
        }

        internal static LogCleanupOutcome Tally(IReadOnlyList<(string Path, long Length)> files, bool declined)
        {
            int deleted = 0;
            long freed = 0;
            foreach (var (path, length) in files)
            {
                if (!File.Exists(path))
                {
                    deleted++;
                    freed += length;
                }
            }

            return new LogCleanupOutcome(deleted, freed, files.Count - deleted, declined);
        }
    }
}
