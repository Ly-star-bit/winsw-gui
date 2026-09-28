using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The elevated steps that turn the unattended alert on and off; see <see cref="UnattendedAlert"/>.
    /// Each runs in a process of its own, started as administrator by the console, and says how
    /// it went only through its exit code, with the reason in this account's error log.
    /// </summary>
    internal static class UnattendedAlertSetup
    {
        /// <summary>
        /// The waits between attempts to delete a folder that is in use: a minute and a half in
        /// all. A run of the task holds its copy for as long as it takes to post — at worst three
        /// attempts of <see cref="AlertWebhook.SendTimeout"/> with
        /// <see cref="UnattendedAlertRun.RetryDelays"/> between them — and the wait has to be
        /// longer than that, yet leave the rest of the step inside the console's
        /// <see cref="UnattendedAlert.ElevatedTimeout"/>.
        /// </summary>
        internal static readonly TimeSpan[] InUseDelays = Enumerable.Repeat(TimeSpan.FromSeconds(3), 30).ToArray();

        /// <summary>
        /// Deletes the task, prepares the folders, puts this executable and the webhook copy in
        /// the private one, writes the manifest and registers the task again.
        /// </summary>
        /// <remarks>
        /// The task goes first. While it is registered its runs work from the private folder,
        /// which is about to be emptied and filled again: a run started in between would find no
        /// webhook to post with, and one going on holds the executable, which cannot be deleted
        /// until it ends. Deleted, the task starts no more runs — the one going on finishes,
        /// and emptying the folder waits for it — and a console that finds no task posts itself.
        /// So whatever fails from there on leaves nothing registered that cannot post, and the
        /// settings page shows the alert off, with the reason.
        /// </remarks>
        /// <param name="sealedCopy">The webhook, sealed to the machine and in Base64, as the console hands it on.</param>
        public static int SetUp(string? sealedCopy)
        {
            byte[] copyBytes;
            try
            {
                copyBytes = Convert.FromBase64String(sealedCopy ?? string.Empty);
            }
            catch (FormatException e)
            {
                ErrorLog.Record("unattended alert, set up", e);
                return UnattendedAlert.ExitBadCopy;
            }

            if (UnattendedAlert.Unseal(copyBytes) is not { } copy)
            {
                ErrorLog.Record("unattended alert, set up", new InvalidDataException("The webhook copy handed over does not open on this machine."));
                return UnattendedAlert.ExitBadCopy;
            }

            if (ReadOwnTask(out bool exists) is { } refused)
            {
                return refused;
            }

            string? executable = Environment.ProcessPath;
            if (executable is null)
            {
                ErrorLog.Record("unattended alert, set up", new InvalidOperationException("The path of this executable is not known."));
                return UnattendedAlert.ExitFiles;
            }

            // Nothing has been touched yet if this fails: the task goes on as it was.
            if (exists && SchTasks($"/Delete /TN \"{UnattendedAlert.TaskPath}\" /F") != 0)
            {
                return UnattendedAlert.ExitScheduler;
            }

            try
            {
                PrepareMachineFolder();
                RecreatePrivateFolder();
                File.Copy(executable, UnattendedAlert.RunnerPath, overwrite: true);

                // A copy keeps the original's attributes, and a read-only one could never be
                // deleted again: turning the alert off, or on once more, would fail on it.
                File.SetAttributes(UnattendedAlert.RunnerPath, File.GetAttributes(UnattendedAlert.RunnerPath) & ~FileAttributes.ReadOnly);
                File.WriteAllBytes(UnattendedAlert.CopyPath, copyBytes);
                File.WriteAllText(UnattendedAlert.ManifestPath, UnattendedAlert.ManifestToJson(new AlertManifest
                {
                    Version = UpdateChecker.CurrentGuiVersion,
                    Fingerprint = UnattendedAlert.Fingerprint(copy.Url, copy.Secret),
                    Language = copy.Language,
                    InstalledAt = DateTimeOffset.Now,
                    InstalledBy = Environment.UserDomainName + "\\" + Environment.UserName,
                }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
            {
                ErrorLog.Record("unattended alert, set up", e);
                return UnattendedAlert.ExitFiles;
            }

            // Written where only administrators and SYSTEM can reach it, so that nobody can
            // change the definition between writing it and schtasks reading it.
            string definition = Path.Combine(UnattendedAlert.PrivateFolder, "task.xml");
            try
            {
                // schtasks reads a definition as UTF-16, which is also what it writes one as.
                File.WriteAllText(definition, UnattendedAlert.BuildXml(UnattendedAlert.RunnerPath, UnattendedAlert.ExtractionBase), Encoding.Unicode);
                int result = SchTasks($"/Create /TN \"{UnattendedAlert.TaskPath}\" /XML \"{definition}\" /F");
                return result == 0 ? UnattendedAlert.ExitDone : UnattendedAlert.ExitScheduler;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Record("unattended alert, set up", e);
                return UnattendedAlert.ExitFiles;
            }
            finally
            {
                TryDelete(definition);
            }
        }

        /// <summary>
        /// Deletes the task and the private folder with the copies in it, and the manifest.
        /// The state and the log stay: they say what was sent, which is still worth reading.
        /// A run of the task that is going on is waited for, as in <see cref="SetUp"/>.
        /// </summary>
        public static int Remove()
        {
            if (ReadOwnTask(out bool exists) is { } refused)
            {
                return refused;
            }

            if (exists && SchTasks($"/Delete /TN \"{UnattendedAlert.TaskPath}\" /F") != 0)
            {
                return UnattendedAlert.ExitScheduler;
            }

            try
            {
                DeleteFolder(UnattendedAlert.PrivateFolder);
                if (File.Exists(UnattendedAlert.ManifestPath))
                {
                    File.Delete(UnattendedAlert.ManifestPath);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Record("unattended alert, remove", e);
                return UnattendedAlert.ExitFiles;
            }

            return UnattendedAlert.ExitDone;
        }

        /// <summary>
        /// Looks for a task by that name. Null when there is none or it is this console's own
        /// (<paramref name="exists"/> says which); otherwise the exit code to stop with.
        /// </summary>
        private static int? ReadOwnTask(out bool exists)
        {
            exists = false;
            try
            {
                var task = UnattendedAlert.ReadTask();
                exists = task is not null;
                return task is { Ours: false } ? UnattendedAlert.ExitNameTaken : null;
            }
            catch (Exception e) when (UnattendedAlert.IsSchedulerFailure(e))
            {
                ErrorLog.Record("unattended alert, task scheduler", e);
                return UnattendedAlert.ExitScheduler;
            }
        }

        /// <summary>
        /// Makes <c>%ProgramData%\WinSW.Gui</c> one that only administrators and SYSTEM can
        /// change, and everyone signed in can read.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ProgramData lets any user create a folder in it, and the one who does owns it and
        /// can change its permissions back whenever they like. A folder of this name that
        /// somebody other than an administrator or SYSTEM owns is therefore not trusted with
        /// the task's executable: it is deleted, with whatever was put in it, and made anew.
        /// A link is refused rather than followed.
        /// </para>
        /// <para>
        /// Its permissions are set whole and not inherited, with administrators as the owner.
        /// </para>
        /// </remarks>
        private static void PrepareMachineFolder()
        {
            var folder = new DirectoryInfo(UnattendedAlert.MachineFolder);
            if (folder.Exists)
            {
                RefuseLink(folder);
                if (!OwnedByAdministrators(folder))
                {
                    folder.Delete(recursive: true);
                }
            }

            CreateOwnFolder(UnattendedAlert.MachineFolder, FolderSecurity(othersMayRead: true));
        }

        /// <summary>
        /// Makes the private folder anew, for administrators and SYSTEM alone: nothing from an
        /// earlier copy — an older executable, what it unpacked — is kept.
        /// </summary>
        private static void RecreatePrivateFolder()
        {
            DeleteFolder(UnattendedAlert.PrivateFolder);
            CreateOwnFolder(UnattendedAlert.PrivateFolder, FolderSecurity(othersMayRead: false));
        }

        /// <summary>
        /// Makes a folder with <paramref name="security"/>, then checks it however it came to be
        /// there. Creating does nothing when the folder exists, and a standard user can make one
        /// in ProgramData in the moment between deleting or looking and creating: a folder they
        /// made would stay theirs, and whoever owns the folder the task's executable is in can
        /// swap that executable for their own. So it is read again, a link or any owner but
        /// administrators or SYSTEM is refused, and the permissions are set whole, whoever
        /// created it.
        /// </summary>
        private static void CreateOwnFolder(string path, DirectorySecurity security)
        {
            new DirectoryInfo(path).Create(security);

            var folder = new DirectoryInfo(path);
            RefuseLink(folder);
            if (!OwnedByAdministrators(folder))
            {
                throw new IOException($"'{folder.FullName}' was made by another account while it was being prepared. Turn the unattended alert on again.");
            }

            folder.SetAccessControl(security);
        }

        private static void RefuseLink(DirectoryInfo folder)
        {
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"'{folder.FullName}' is a link, not a folder. Remove it, then turn the unattended alert on again.");
            }
        }

        /// <summary>Whether administrators or SYSTEM own the folder, by SID: the groups' names are translated.</summary>
        private static bool OwnedByAdministrators(DirectoryInfo folder)
        {
            var owner = folder.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
        }

        /// <summary>
        /// Administrators and SYSTEM in full, and with <paramref name="othersMayRead"/> any
        /// signed-in account may read; nothing inherited from above. By SID: the groups' names
        /// are translated on a Chinese or Japanese Windows.
        /// </summary>
        private static DirectorySecurity FolderSecurity(bool othersMayRead)
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            const InheritanceFlags everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, everything, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, everything, PropagationFlags.None, AccessControlType.Allow));
            if (othersMayRead)
            {
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, everything, PropagationFlags.None, AccessControlType.Allow));
            }

            return security;
        }

        /// <summary>
        /// Deletes a folder and everything in it, if it is there. A run of the task may still be
        /// using the copy, so a folder in use is tried again until <see cref="InUseDelays"/>
        /// run out. Each attempt deletes what it can: what is left is only what is still in use.
        /// </summary>
        internal static void DeleteFolder(string path) => RetryWhileInUse(
            () =>
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            },
            InUseDelays,
            Thread.Sleep);

        /// <summary>
        /// Runs <paramref name="action"/>, and again after each of <paramref name="delays"/> in
        /// turn while it fails for a file in use; then lets the failure through. A file another
        /// process has open fails to delete with an <see cref="IOException"/>; a running
        /// executable, or a library loaded from the folder, with access denied, which .NET
        /// throws as an <see cref="UnauthorizedAccessException"/> — the case this is for.
        /// </summary>
        internal static void RetryWhileInUse(Action action, IReadOnlyList<TimeSpan> delays, Action<TimeSpan> wait)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < delays.Count)
                {
                    wait(delays[attempt]);
                }
            }
        }

        /// <summary>
        /// Runs <c>schtasks</c> from the system directory — this process is elevated, and a bare
        /// name would run whatever the PATH finds first — and returns its exit code. What it
        /// printed goes to the error log when it fails.
        /// </summary>
        private static int SchTasks(string arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,

                // Both drained as they arrive, so that neither pipe can fill and stall it.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = Process.Start(start) ?? throw new InvalidOperationException("schtasks did not start.");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60_000))
                {
                    process.Kill();
                    ErrorLog.Record("unattended alert, schtasks " + arguments, new TimeoutException("schtasks did not finish within a minute."));
                    return -1;
                }

                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string said = (error.Result + " " + output.Result).Trim();
                    ErrorLog.Record(
                        "unattended alert, schtasks " + arguments,
                        new InvalidOperationException("schtasks exited with " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + said));
                }

                return process.ExitCode;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ErrorLog.Record("unattended alert, schtasks " + arguments, e);
                return -1;
            }
        }

        private static void TryDelete(string path)
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
