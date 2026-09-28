using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSW.Gui.Services
{
    /// <summary>How one alert went: when, about which service, and why it was not sent, if it was not.</summary>
    public sealed class AlertOutcome
    {
        public DateTimeOffset At { get; set; }

        /// <summary>The service's name (its ID), as the messages give it.</summary>
        public string Service { get; set; } = string.Empty;

        /// <summary>Null when the robot took the message; otherwise why it did not.</summary>
        public string? Error { get; set; }

        /// <summary>
        /// The later of the console's own last alert and the unattended alert's, and whether it
        /// is the unattended alert's. Either may be missing.
        /// </summary>
        public static (AlertOutcome? Last, bool ByTask) Latest(AlertOutcome? console, AlertOutcome? unattended) =>
            unattended != null && (console is null || unattended.At > console.At) ? (unattended, true) : (console, false);
    }

    /// <summary>What the throttle knows about one service.</summary>
    public sealed class AlertThrottleEntry
    {
        /// <summary>When a message about the service was last attempted, sent or not.</summary>
        public DateTimeOffset LastAttempt { get; set; }

        /// <summary>Failures of the service that no message has announced since the last one that was sent.</summary>
        public int Held { get; set; }
    }

    /// <summary>
    /// What the unattended alert keeps between runs: the throttle, and how the last alert went.
    /// </summary>
    /// <remarks>
    /// Each failure Windows records is a run of its own, so anything a run has to know about
    /// the one before it lives in a file. It is <c>alert-state.json</c> beside the unattended
    /// alert's other files (see <see cref="UnattendedAlert"/>): written only by SYSTEM, readable
    /// by any signed-in account, which is how Settings shows the last alert's result.
    /// </remarks>
    public sealed class AlertState
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public Dictionary<string, AlertThrottleEntry> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last message attempted; a failure held back by the throttle does not count.</summary>
        public AlertOutcome? Last { get; set; }

        /// <summary>
        /// Reads the state at <paramref name="path"/>. A file that is missing, unreadable or
        /// broken gives a fresh state: the worst that costs is one message the throttle would
        /// have held back.
        /// </summary>
        public static AlertState Load(string path)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AlertState>(File.ReadAllText(path), Options) ?? new AlertState();

                // Service names are not case-sensitive; the dictionary the reader builds is.
                loaded.Services = new Dictionary<string, AlertThrottleEntry>(loaded.Services ?? new(), StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return new AlertState();
            }
        }

        /// <summary>
        /// Writes the state through a file beside it that is then swapped in, so that a reader
        /// — the console showing the last alert — never sees half of it.
        /// </summary>
        /// <exception cref="IOException">The file could not be written.</exception>
        /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
        public void Save(string path)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
    }

    /// <summary>
    /// Keeps a service that fails over and over from filling the group chat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same rule as the console's own notices: the first failure is posted at once, and
    /// further failures of the same service within <see cref="Window"/> are counted instead.
    /// The next message after that says how many there were. A service that the service
    /// control manager restarts every few seconds is then one message every five minutes,
    /// each carrying the count, rather than one per restart.
    /// </para>
    /// <para>
    /// A message that could not be sent starts the window just the same. The run has already
    /// tried for a minute; a crash loop against a network that is down would otherwise queue
    /// one such minute per restart. What it failed to announce is counted with the others.
    /// </para>
    /// </remarks>
    public static class AlertThrottle
    {
        /// <summary>One message per service per this long.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

        /// <summary>Services not heard from for this long are forgotten, with whatever they had held.</summary>
        public static readonly TimeSpan Memory = TimeSpan.FromDays(1);

        /// <summary>
        /// Whether a failure of <paramref name="service"/> at <paramref name="now"/> is posted:
        /// null when it is held back (and counted), otherwise how many earlier failures the
        /// message should say were held back.
        /// </summary>
        public static int? Admit(AlertState state, string service, DateTimeOffset now)
        {
            if (!state.Services.TryGetValue(service, out var entry))
            {
                return 0;
            }

            // A clock set back is not a reason to stay quiet until it catches up.
            if (now >= entry.LastAttempt && now - entry.LastAttempt < Window)
            {
                entry.Held++;
                return null;
            }

            return entry.Held;
        }

        /// <summary>Notes a message attempted for <paramref name="service"/>, and whether it was sent.</summary>
        public static void Record(AlertState state, string service, DateTimeOffset now, bool sent)
        {
            int held = state.Services.TryGetValue(service, out var entry) ? entry.Held : 0;
            state.Services[service] = new AlertThrottleEntry
            {
                LastAttempt = now,
                Held = sent ? 0 : held + 1,
            };
        }

        /// <summary>Drops the services not heard from within <see cref="Memory"/>.</summary>
        public static void Prune(AlertState state, DateTimeOffset now)
        {
            foreach (string service in state.Services.Where(s => now - s.Value.LastAttempt > Memory).Select(s => s.Key).ToList())
            {
                state.Services.Remove(service);
            }
        }
    }
}
