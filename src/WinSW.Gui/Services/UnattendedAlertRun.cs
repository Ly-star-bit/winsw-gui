using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinSW.Gui.Localization;
using Eventing = System.Diagnostics.Eventing.Reader;

namespace WinSW.Gui.Services
{
    /// <summary>The kinds of failure the service control manager records.</summary>
    public enum ScmFailureKind
    {
        /// <summary>7031, 7034: the service's process ended without the service saying it had stopped.</summary>
        Crashed,

        /// <summary>7000: the service could not be started.</summary>
        StartFailed,

        /// <summary>7009: the service did not report that it had started in time.</summary>
        StartTimedOut,

        /// <summary>7023: the service stopped with a Windows error code.</summary>
        EndedWithError,

        /// <summary>7024: the service stopped with a code of its own.</summary>
        EndedWithCode,
    }

    /// <summary>
    /// One failure as the service control manager recorded it: what kind, the name it logged
    /// for the service, and the number that goes with it.
    /// </summary>
    /// <param name="Number">
    /// How many times it has failed (<see cref="ScmFailureKind.Crashed"/>), the time allowed in
    /// milliseconds (<see cref="ScmFailureKind.StartTimedOut"/>), or the error or exit code;
    /// null when the value recorded is not a number.
    /// </param>
    /// <param name="Recorded">That value as the event holds it.</param>
    public sealed record ScmFailure(ScmFailureKind Kind, string LoggedName, int? Number, string Recorded)
    {
        /// <summary>
        /// Reads a service control manager event's values; null for any other event, or one
        /// without a service name.
        /// </summary>
        /// <remarks>
        /// The service is the first value in every one of them but 7009 — "A timeout was
        /// reached (%1 milliseconds) while waiting for the %2 service to connect" — where it is
        /// the second. Error codes are recorded as message references, <c>%%1053</c>, which the
        /// event viewer replaces with the system's text for that code.
        /// </remarks>
        public static ScmFailure? From(int eventId, IReadOnlyList<string> values)
        {
            string Value(int index) => values.Count > index ? values[index].Trim() : string.Empty;

            var failure = eventId switch
            {
                7031 or 7034 => new ScmFailure(ScmFailureKind.Crashed, Value(0), NumberIn(Value(1)), Value(1)),
                7000 => new ScmFailure(ScmFailureKind.StartFailed, Value(0), NumberIn(Value(1)), Value(1)),
                7009 => new ScmFailure(ScmFailureKind.StartTimedOut, Value(1), NumberIn(Value(0)), Value(0)),
                7023 => new ScmFailure(ScmFailureKind.EndedWithError, Value(0), NumberIn(Value(1)), Value(1)),
                7024 => new ScmFailure(ScmFailureKind.EndedWithCode, Value(0), NumberIn(Value(1)), Value(1)),
                _ => null,
            };

            return failure is { LoggedName.Length: > 0 } ? failure : null;
        }

        /// <summary>
        /// The message: which dictionary key, with what in it. <paramref name="errorText"/>
        /// describes a Windows error code; a parameter so that the choice can be tested away
        /// from Windows.
        /// </summary>
        public (string Key, object[] Args) Describe(string machine, string service, Func<int, string> errorText) => this.Kind switch
        {
            ScmFailureKind.Crashed => ("M.Alert.Crashed", new object[] { machine, service, this.NumberOrRecorded() }),
            ScmFailureKind.StartFailed => ("M.Alert.StartFailed", new object[] { machine, service, this.Number is int code ? errorText(code) : this.Recorded }),
            ScmFailureKind.StartTimedOut => ("M.Alert.StartTimedOut", new object[] { machine, service, this.Number is int ms ? ((int)Math.Round(ms / 1000.0)).ToString(CultureInfo.InvariantCulture) : this.Recorded }),
            ScmFailureKind.EndedWithError => ("M.Alert.EndedWithError", new object[] { machine, service, this.Number is int code ? errorText(code) : this.Recorded }),
            _ => ("M.Alert.EndedWithCode", new object[] { machine, service, this.NumberOrRecorded() }),
        };

        /// <summary>A number, as recorded or as a <c>%%</c> message reference.</summary>
        internal static int? NumberIn(string value)
        {
            string digits = value.StartsWith("%%", StringComparison.Ordinal) ? value.Substring(2) : value;
            return int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) ? number : null;
        }

        private string NumberOrRecorded() => this.Number?.ToString(CultureInfo.InvariantCulture) ?? this.Recorded;
    }

    /// <summary>
    /// The task scheduler's run: one failure Windows recorded, posted if it is a WinSW service's
    /// and the throttle lets it through. See <see cref="UnattendedAlert"/>.
    /// </summary>
    internal static class UnattendedAlertRun
    {
        private const string Provider = "Service Control Manager";

        /// <summary>
        /// Waits between attempts. The case this exists for is a machine that has just booted,
        /// where a service fails before the network is up; a minute is enough for that, and
        /// short enough that a queue of failures does not back up behind it.
        /// </summary>
        internal static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Runs one after another even if the task scheduler were to start two at once: the
        /// state file is read, changed and written back, and two runs doing that together would
        /// lose one's change.
        /// </summary>
        private const string GateName = "Global\\WinSW.Gui.UnattendedAlert";

        /// <summary>See <see cref="MayUse"/>. Asked at the first read or write, and not again: a run takes seconds.</summary>
        private static readonly Lazy<bool> MachineFolderUsable = new(CheckMachineFolder);

        /// <summary>
        /// Posts the failure recorded as <paramref name="recordNumber"/> in the System log.
        /// Runs on the application's thread; the parts that wait do not come back to it.
        /// </summary>
        public static int Run(string? recordNumber)
        {
            if (!long.TryParse(recordNumber, NumberStyles.None, CultureInfo.InvariantCulture, out long record))
            {
                Log("-", "failed: no event record number was given");
                return UnattendedAlert.ExitFailed;
            }

            (int Id, string Provider, IReadOnlyList<string> Values)? recorded;
            try
            {
                recorded = ReadEvent(record);
            }
            catch (Exception e) when (e is Eventing.EventLogException or UnauthorizedAccessException)
            {
                Log("-", "failed: event " + recordNumber + " could not be read: " + AlertWebhook.DescribeFailure(e));
                return UnattendedAlert.ExitFailed;
            }

            if (recorded is not { } logged || !string.Equals(logged.Provider, Provider, StringComparison.OrdinalIgnoreCase)
                || ScmFailure.From(logged.Id, logged.Values) is not { } failure)
            {
                return UnattendedAlert.ExitDone;
            }

            var services = ServiceDiscovery.Discover();
            string? service = UnattendedAlert.FindService(services.Select(s => (s.ServiceName, s.DisplayName)), failure.LoggedName);
            if (service is null)
            {
                // Not a WinSW service. Every service on the machine starts this run, and the
                // others are none of this console's business.
                return UnattendedAlert.ExitDone;
            }

            // Where the service's program writes its error output, for the line saying why.
            string? configPath = services.FirstOrDefault(s => string.Equals(s.ServiceName, service, StringComparison.OrdinalIgnoreCase))?.ConfigPath;

            if (UnattendedAlert.ReadCopy() is not { } copy)
            {
                Log(service, "failed: the copy of the webhook is missing or cannot be opened; turn the unattended alert on again");
                return UnattendedAlert.ExitFailed;
            }

            // SYSTEM has no preferences of its own. The language is the one the console was
            // in when the alert was turned on, set for this process only: nothing here saves.
            AppSettings.Current.Language = copy.Language;
            Localizer.Initialize();

            // As the console does at start, which this run never reaches: a program's error
            // output on a Chinese Windows is GBK, which would otherwise be quoted as Latin-1.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            using var gate = new Mutex(false, GateName);
            bool owned;
            try
            {
                // Longer than any one run takes. Past it, going ahead unguarded risks a count;
                // giving up would lose the message.
                owned = gate.WaitOne(TimeSpan.FromMinutes(2));
            }
            catch (AbandonedMutexException)
            {
                // A run that died holding it; the state it left is still a whole file.
                owned = true;
            }

            try
            {
                return Announce(failure, service, configPath, copy);
            }
            finally
            {
                if (owned)
                {
                    gate.ReleaseMutex();
                }
            }
        }

        /// <summary>
        /// Tries <paramref name="send"/> until it succeeds, at most once more than there are
        /// <see cref="RetryDelays"/>; the last failure, or null.
        /// </summary>
        internal static async Task<string?> SendWithRetriesAsync(Func<Task<string?>> send, Func<TimeSpan, Task> wait)
        {
            string? error = await send().ConfigureAwait(false);
            foreach (var delay in RetryDelays)
            {
                if (error is null)
                {
                    break;
                }

                await wait(delay).ConfigureAwait(false);
                error = await send().ConfigureAwait(false);
            }

            return error;
        }

        /// <summary>
        /// The message for one failure: what Windows recorded, how many failures before it were
        /// held back, and under it the line saying why, as the console's own alert gives it.
        /// </summary>
        /// <param name="causeFormat"><c>M.Alert.Cause</c>: {0} the file, {1} the line.</param>
        internal static string Message(ScmFailure failure, string machine, string service, int held, ErrorLine? cause, Func<string, object?[], string> format, string causeFormat, Func<int, string> errorText)
        {
            var (key, args) = failure.Describe(machine, service, errorText);
            string text = format(key, args);
            if (held > 0)
            {
                text += " " + format("M.Alert.Held", new object?[] { held });
            }

            return StopCause.Append(text, cause, causeFormat, null);
        }

        /// <summary>
        /// Whether a failure of this kind is given the last line of the program's error output.
        /// While this run posts to the console's own webhook the console leaves such failures to
        /// it, and they went without the line that says why, which the console's alert carries.
        /// </summary>
        /// <remarks>
        /// Only for a service that ran and then ended. The line is taken to be this run's when
        /// the file was written after the wrapper's last start in its log (see
        /// <see cref="StopCause"/>). A service that could not be started (7000), or whose process
        /// did not get as far as the service control manager in time (7009), may never have
        /// written a start: the last one in the log is then an earlier run's, and that run's
        /// last line would be quoted as the cause of this failure.
        /// </remarks>
        internal static bool QuotesCause(ScmFailureKind kind) =>
            kind is ScmFailureKind.Crashed or ScmFailureKind.EndedWithError or ScmFailureKind.EndedWithCode;

        /// <summary>
        /// Whether this run may read and write in <see cref="UnattendedAlert.MachineFolder"/>: it
        /// is there, is a folder rather than a link, and administrators or SYSTEM own it, as
        /// turning the alert on leaves it. Never throws; anything that cannot be told is a no.
        /// </summary>
        /// <remarks>
        /// The run never makes the folder. Made by SYSTEM, or by an administrator starting a
        /// run by hand, it would take ProgramData's permissions, under which every user may
        /// create files in it — among them a link named like the log or the state, for SYSTEM
        /// to write through — and turning the alert on keeps a folder administrators or SYSTEM
        /// own, with whatever was put in it. A folder that is missing, or that another account
        /// made, is left alone: the message still goes, without its line in the log or the
        /// throttle's count, and the throttle starts from nothing rather than from a state file
        /// another account could have written.
        /// </remarks>
        internal static bool MayUse(string folder)
        {
            try
            {
                var info = new DirectoryInfo(folder);
                return info.Exists
                    && (info.Attributes & FileAttributes.ReparsePoint) == 0
                    && UnattendedAlertSetup.OwnedByAdministrators(info);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or IdentityNotMappedException)
            {
                return false;
            }
        }

        private static int Announce(ScmFailure failure, string service, string? configPath, WebhookCopy copy)
        {
            var now = DateTimeOffset.Now;
            var state = MachineFolderUsable.Value ? AlertState.Load(UnattendedAlert.StatePath) : new AlertState();
            AlertThrottle.Prune(state, now);

            int? held = AlertThrottle.Admit(state, service, now);
            if (held is null)
            {
                Save(state);
                Log(service, "held back");
                return UnattendedAlert.ExitDone;
            }

            // Read only for a message that goes. Given at most StopCause.ReadTimeout, and read
            // off this thread, so waiting on it here cannot deadlock.
            var cause = QuotesCause(failure.Kind)
                ? StopCause.ReadAsync(new StopCauseSource(configPath, LogEncodingChoice.Auto, null, null, null)).GetAwaiter().GetResult()
                : null;

            // Worded here, on the application's thread, where the dictionaries are.
            string text = Message(failure, Environment.MachineName, service, held.Value, cause, Localizer.Format, Localizer.Get("M.Alert.Cause"), ErrorText);

            // Nothing below comes back to this thread, so waiting on it here cannot deadlock.
            string? error = SendWithRetriesAsync(() => AlertWebhook.SendAsync(copy.Url, copy.Secret, text), delay => Task.Delay(delay))
                .GetAwaiter().GetResult();

            AlertThrottle.Record(state, service, now, sent: error is null);
            state.Last = new AlertOutcome { At = now, Service = service, Error = error };
            Save(state);
            Log(service, error is null ? "ok" : "failed: " + error);
            return error is null ? UnattendedAlert.ExitDone : UnattendedAlert.ExitFailed;
        }

        /// <summary>The event with that record number in the System log; null when it has gone.</summary>
        private static (int Id, string Provider, IReadOnlyList<string> Values)? ReadEvent(long record)
        {
            var query = new Eventing.EventLogQuery(
                "System",
                Eventing.PathType.LogName,
                "*[System[EventRecordID=" + record.ToString(CultureInfo.InvariantCulture) + "]]");

            using var reader = new Eventing.EventLogReader(query);
            using var found = reader.ReadEvent();
            if (found is null)
            {
                return null;
            }

            var values = found.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty).ToList();
            return (found.Id, found.ProviderName ?? string.Empty, values);
        }

        /// <summary>The system's text for a Windows error code, with the code, as the event viewer shows it.</summary>
        private static string ErrorText(int code) =>
            new Win32Exception(code).Message.TrimEnd('.', ' ', '。') + " (" + code.ToString(CultureInfo.InvariantCulture) + ")";

        private static void Save(AlertState state)
        {
            if (!MachineFolderUsable.Value)
            {
                return;
            }

            try
            {
                state.Save(UnattendedAlert.StatePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The message has gone or been held either way; what is lost is the count the
                // next one would have carried.
                ErrorLog.Record("unattended alert state", e);
            }
        }

        /// <summary>
        /// One line in <see cref="UnattendedAlert.LogPath"/>, in the action log's shape, when the
        /// folder may be written to (<see cref="MayUse"/>). Never throws.
        /// </summary>
        internal static void Log(string service, string outcome)
        {
            if (MachineFolderUsable.Value)
            {
                ActionLog.Append(UnattendedAlert.LogPath, ActionLog.Format(DateTime.Now, "SYSTEM", "unattended alert", service, outcome));
            }
        }

        /// <summary><see cref="MayUse"/> for the machine folder, asked once per run; a no is recorded in the error log, once.</summary>
        private static bool CheckMachineFolder()
        {
            bool usable = MayUse(UnattendedAlert.MachineFolder);
            if (!usable)
            {
                ErrorLog.Record(
                    "unattended alert",
                    new IOException($"'{UnattendedAlert.MachineFolder}' is missing, is a link, or is not owned by administrators or SYSTEM. The message is posted without the log and the throttle's state. Turn the unattended alert on again."));
            }

            return usable;
        }
    }
}
