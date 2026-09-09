using System;
using System.IO;
using System.Threading.Tasks;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Writes a configuration to a place the current user cannot write to, by way of a copy
    /// the elevated wrapper moves into position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A configuration usually lives beside the service it configures, under Program Files or
    /// the install root, where a standard user has no write access. Saving it means writing it
    /// somewhere they can and copying it across with administrator rights, which is one
    /// elevation prompt rather than running the whole console elevated.
    /// </para>
    /// <para>
    /// The staged copy is short-lived and unpredictable by name. It was neither: a fixed
    /// <c>%TEMP%\WinSW.Gui\&lt;the configuration's own name&gt;</c> that nothing ever deleted,
    /// so every elevated save left the file — including whatever <c>&lt;serviceaccount&gt;</c>
    /// password it carries — sitting in the temporary directory indefinitely, and two
    /// configurations with the same file name staged over one another.
    /// </para>
    /// </remarks>
    public static class StagedWrite
    {
        /// <summary>The staging directory, shared with nothing else.</summary>
        private static string Directory => Path.Combine(Path.GetTempPath(), "WinSW.Gui");

        /// <summary>
        /// Saves <paramref name="model"/> and has it copied to <paramref name="destination"/>
        /// with administrator rights.
        /// </summary>
        /// <param name="copy">
        /// How the copy is performed. Defaults to the elevated one; a test supplies its own so
        /// the staging and its cleanup can be exercised without an elevation prompt.
        /// </param>
        /// <exception cref="IOException">The staged copy could not be written.</exception>
        /// <exception cref="UnauthorizedAccessException">The staging directory is not writable.</exception>
        public static async Task<CommandResult> ElevatedAsync(
            ServiceConfigModel model,
            string destination,
            Func<string, string, Task<CommandResult>>? copy = null)
        {
            string staging = Path.Combine(Directory, Path.GetRandomFileName() + ".xml");

            System.IO.Directory.CreateDirectory(Directory);

            // Save points the model at what it just wrote, which here is a temporary file about
            // to be deleted. Left that way, a save whose elevated copy failed or was declined
            // would leave the editor resolving its relative paths against the temp directory.
            string? destinationOfRecord = model.FilePath;
            model.Save(staging);
            model.FilePath = destinationOfRecord;

            try
            {
                return await (copy ?? WinSwCli.CopyElevatedAsync)(staging, destination).ConfigureAwait(false);
            }
            finally
            {
                Delete(staging);
            }
        }

        /// <summary>
        /// Removes the staged copy. A file that is missing is the wanted outcome, and one an
        /// anti-virus scanner still has open is not worth failing a completed save over — the
        /// next write uses a different name regardless.
        /// </summary>
        private static void Delete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
