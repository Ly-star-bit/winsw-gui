using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The start type each service had before "Stop restarting" set it to Disabled, so that it can
    /// be put back: kept per user as JSON under <c>%LOCALAPPDATA%\WinSW.Gui</c>, beside the settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept as the word sc.exe takes — <c>auto</c>, <c>delayed-auto</c> or <c>demand</c> — because
    /// that is what restoring it hands to sc.exe. Only those three are ever written, and only those
    /// three are read back: the word goes onto a command line that runs elevated, and a file anyone
    /// logged on as this user can edit is not where to take one from unchecked.
    /// </para>
    /// <para>
    /// The console's own bookkeeping, like the groups: nothing is written to the service or its
    /// configuration, and another administrator's console does not know about it. The service's
    /// configuration file keeps its own start mode, which "Apply config" sets again.
    /// </para>
    /// <para>
    /// Used on the UI thread only; the file is a few lines, read on first use and again before
    /// every change, which goes into the file as it is then rather than over it with what this
    /// console first read: a second console, an elevated one beside a standard one, may have
    /// remembered or forgotten a service meanwhile, and writing the whole of an older copy back
    /// would undo that. What is offered is still what was read last; another console's change is
    /// seen here from this console's next change on, or its next start.
    /// </para>
    /// </remarks>
    public sealed class RememberedStartTypes
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static RememberedStartTypes? current;

        private readonly string filePath;

        /// <summary>
        /// Changes that could not be written, by service, null for one forgotten: made again on the
        /// file as it is read before the next change, so that a failed write costs no more than it
        /// did when the file was read only once — the change is kept for this session.
        /// </summary>
        private readonly Dictionary<string, string?> unsaved = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string>? types;

        public RememberedStartTypes(string filePath) => this.filePath = filePath;

        /// <summary>The one instance the dashboard uses, over the file beside the settings.</summary>
        public static RememberedStartTypes Current => current ??= new RememberedStartTypes(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "start-types.json"));

        /// <summary>
        /// The word sc.exe takes for a start type: null for one that "Stop restarting" does not
        /// remember — a service that is disabled already has nothing to go back to, and a boot or
        /// system start belongs to drivers.
        /// </summary>
        public static string? TokenFor(ServiceStartMode? startType, bool delayed) => startType switch
        {
            ServiceStartMode.Automatic => delayed ? "delayed-auto" : "auto",
            ServiceStartMode.Manual => "demand",
            _ => null,
        };

        /// <summary>The start type <paramref name="token"/> stands for, for saying it in the words the panel uses.</summary>
        public static (ServiceStartMode StartType, bool Delayed)? StartTypeOf(string token) => token switch
        {
            "auto" => (ServiceStartMode.Automatic, false),
            "delayed-auto" => (ServiceStartMode.Automatic, true),
            "demand" => (ServiceStartMode.Manual, false),
            _ => null,
        };

        /// <summary>The start type remembered for <paramref name="serviceName"/>, or null.</summary>
        public string? For(string serviceName) =>
            this.Load().TryGetValue(serviceName, out string? token) ? token : null;

        public void Remember(string serviceName, string token)
        {
            if (StartTypeOf(token) is null)
            {
                throw new ArgumentException($"'{token}' is not a start type to remember.", nameof(token));
            }

            this.Change(serviceName, token);
        }

        public void Forget(string serviceName) => this.Change(serviceName, null);

        /// <summary>Makes one change to <paramref name="types"/>; true when it changed anything.</summary>
        private static bool Apply(Dictionary<string, string> types, string serviceName, string? token)
        {
            if (token is null)
            {
                return types.Remove(serviceName);
            }

            if (types.TryGetValue(serviceName, out string? had) && had == token)
            {
                return false;
            }

            types[serviceName] = token;
            return true;
        }

        /// <summary>
        /// Remembers <paramref name="token"/> for <paramref name="serviceName"/>, or forgets the
        /// service when it is null, in the file as it is now; see the remarks. Written only when
        /// that changes the file.
        /// </summary>
        private void Change(string serviceName, string? token)
        {
            // What this console already holds stands in for a file that cannot be read now: the
            // change is not to wipe what another console wrote because of a moment's lock.
            var current = this.Read() ?? new Dictionary<string, string>(this.Load(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, pending) in this.unsaved)
            {
                Apply(current, name, pending);
            }

            bool changed = Apply(current, serviceName, token) || this.unsaved.Count > 0;
            this.types = current;
            if (!changed)
            {
                return;
            }

            if (this.Save(current))
            {
                this.unsaved.Clear();
            }
            else
            {
                // Kept for this session all the same; only a restart of the console forgets it.
                this.unsaved[serviceName] = token;
            }
        }

        private Dictionary<string, string> Load() =>
            this.types ??= this.Read() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The file as it is now: empty when there is none, null when it cannot be read. Unreadable
        /// is the same as empty for what is offered — the worst it costs is a restore not offered —
        /// but not for what is written over it.
        /// </summary>
        private Dictionary<string, string>? Read()
        {
            // Service names are not case-sensitive; what the reader builds is.
            var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(this.filePath)
                    && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(this.filePath)) is { } read)
                {
                    foreach (var (name, token) in read)
                    {
                        if (!string.IsNullOrEmpty(name) && token != null && StartTypeOf(token) != null)
                        {
                            loaded[name] = token;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return null;
            }

            return loaded;
        }

        /// <summary>
        /// Written beside the file and moved over it, as <see cref="RememberedRuns"/> writes its
        /// own: a console ending halfway through a write leaves the last whole file rather than half
        /// of one. The file written beside it is named after the process, so that two consoles
        /// writing at once do not swap in each other's half-written copy.
        /// </summary>
        private bool Save(Dictionary<string, string> types)
        {
            string staging = this.filePath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.filePath)!);
                File.WriteAllText(staging, JsonSerializer.Serialize(types, Options));
                File.Move(staging, this.filePath, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(staging);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                }

                return false;
            }
        }
    }
}
