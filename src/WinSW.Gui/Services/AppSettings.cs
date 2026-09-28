using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Per-user preferences, stored as JSON under <c>%LOCALAPPDATA%\WinSW.Gui</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is written whole on every change, so it is written beside itself first and then
    /// swapped in: a write cut short — the machine losing power, the disk filling — leaves the
    /// previous settings in place rather than half of the new ones.
    /// </para>
    /// <para>
    /// A file that is there but cannot be used is never quietly replaced. The console starts
    /// with the defaults, and the file is moved aside to <c>settings.bad.json</c>, which is
    /// what the settings page then says. It used to be written over with the defaults at the
    /// first change, taking the groups, the webhook and the roots with it.
    /// </para>
    /// </remarks>
    public sealed class AppSettings
    {
        /// <summary>
        /// How many times a read that another process got in the way of is tried, <see cref="ReadRetryDelay"/>
        /// apart, before the file is taken to be unreadable.
        /// </summary>
        private const int ReadAttempts = 3;

        private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

        /// <summary>One save at a time from this process; each process writes through a file of its own.</summary>
        private static readonly object SaveGate = new();

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        private static AppSettings? current;

        /// <summary>The file this instance was read from and is written back to.</summary>
        private string location = FilePath;

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "settings.json");

        /// <summary>The one instance every subsystem reads and writes, loaded on first use.</summary>
        public static AppSettings Current => current ??= Load(FilePath, e => ErrorLog.Record("settings file", e));

        /// <summary>
        /// Where a settings file that could not be used was moved to; null when there was none.
        /// These settings are then the defaults.
        /// </summary>
        [JsonIgnore]
        public string? SetAsidePath { get; private set; }

        /// <summary>
        /// The settings file could be neither used nor moved aside. It is left as it is, and not
        /// written over for the life of this process: these settings are the defaults, and a
        /// change to them lasts until the console closes.
        /// </summary>
        [JsonIgnore]
        public bool IsFileUnreadable { get; private set; }

        /// <summary>A language code from <c>Localizer.Languages</c>, or null to follow the OS.</summary>
        public string? Language { get; set; }

        /// <summary>"system", "light" or "dark"; null follows the OS.</summary>
        public string? Theme { get; set; }

        public LogEncodingChoice LogEncoding { get; set; } = LogEncodingChoice.Auto;

        /// <summary>Seconds between background rescans of installed services; 0 disables.</summary>
        public int AutoRescanSeconds { get; set; } = 30;

        public bool MinimizeToTray { get; set; }

        /// <summary>Show a notification when a service stops without the GUI having asked it to.</summary>
        public bool NotifyOnUnexpectedStop { get; set; } = true;

        /// <summary>Where unexpected stops are also posted, encrypted to this user; see <see cref="AlertWebhook"/>.</summary>
        public string? AlertWebhook { get; set; }

        /// <summary>The webhook's signing secret, encrypted to this user.</summary>
        public string? AlertSecret { get; set; }

        /// <summary>When this console last posted an alert; null when it never has. Shown on the settings page.</summary>
        public DateTimeOffset? LastAlertAt { get; set; }

        /// <summary>The service that alert was about.</summary>
        public string? LastAlertService { get; set; }

        /// <summary>Why that alert was not sent; null when it was.</summary>
        public string? LastAlertError { get; set; }

        /// <summary>
        /// Desktop tasks whose unexpected stops are not told, in the tray or the group chat, by
        /// name; null or empty when every task's are. Each task's own checkbox on the Desktop tasks
        /// page sets it: a robot that is expected to end now and then should not page anyone.
        /// </summary>
        public List<string>? QuietDesktopTasks { get; set; }

        public double? WindowLeft { get; set; }

        public double? WindowTop { get; set; }

        public double? WindowWidth { get; set; }

        public double? WindowHeight { get; set; }

        public bool WindowMaximized { get; set; }

        /// <summary>Icon-only navigation rail.</summary>
        public bool RailCollapsed { get; set; }

        public bool LogWrapLines { get; set; }

        public double LogFontSize { get; set; } = 12;

        public bool SortServicesByStatus { get; set; }

        /// <summary>Show the service list under group headings.</summary>
        public bool GroupServices { get; set; }

        /// <summary>
        /// The group each service is filed under, by service name. The console's own
        /// bookkeeping, not the service's: nothing is written to its configuration.
        /// </summary>
        public Dictionary<string, string> ServiceGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Computers the Remote page has connected to, most recent first. Names only: the page
        /// connects with the signed-in user's own Windows credentials, and keeps none.
        /// </summary>
        public List<string>? RemoteMachines { get; set; }

        /// <summary>The Remote page lists only the services a WinSW wrapper hosts.</summary>
        public bool RemoteWrappersOnly { get; set; } = true;

        /// <summary>
        /// Where new services are installed: one folder per service under this root, with a
        /// single wrapper shared from <c>bin</c>. Null means the default, which is
        /// <c>%ProgramData%\WinSW</c> — the place Windows sets aside for machine-wide
        /// application data, which is what a service's configuration and logs are.
        /// </summary>
        public string? InstallRoot { get; set; }

        /// <summary>The configured install root, or the default when none is set.</summary>
        [JsonIgnore]
        public string EffectiveInstallRoot =>
            string.IsNullOrWhiteSpace(this.InstallRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSW")
                : this.InstallRoot!.Trim();

        /// <summary>
        /// Where desktop tasks are installed. Null means the default,
        /// <c>%LOCALAPPDATA%\WinSW</c> — a per-user location, because a desktop task is a
        /// per-user thing: it runs as one account, in that account's session, and everything
        /// it writes has to be writable by that account without administrator rights.
        /// </summary>
        public string? TaskRoot { get; set; }

        /// <summary>The configured desktop-task root, or the default when none is set.</summary>
        [JsonIgnore]
        public string EffectiveTaskRoot =>
            string.IsNullOrWhiteSpace(this.TaskRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSW")
                : this.TaskRoot!.Trim();

        /// <summary>
        /// Reads the settings at <paramref name="path"/>; the defaults when there is no file.
        /// </summary>
        /// <remarks>
        /// A file that is there but cannot be used is not worth failing startup over, and not
        /// worth losing either. One that does not parse is moved aside to
        /// <c>settings.bad.json</c>, replacing an older one, and the defaults are used. One that
        /// cannot be read at all is tried again briefly — another process may have it open —
        /// and then moved aside the same way; when even that fails it is left alone, and the
        /// defaults returned are never written over it (<see cref="IsFileUnreadable"/>).
        /// </remarks>
        /// <param name="failed">Told why a file that is there could not be used.</param>
        internal static AppSettings Load(string path, Action<Exception>? failed = null)
        {
            Exception problem;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return new AppSettings { location = path };
                    }

                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();

                    // Service names are not case-sensitive; the dictionary the reader builds is.
                    loaded.ServiceGroups = new Dictionary<string, string>(loaded.ServiceGroups ?? new(), StringComparer.OrdinalIgnoreCase);
                    loaded.location = path;
                    return loaded;
                }
                catch (JsonException e)
                {
                    problem = e;
                    break;
                }
                catch (IOException e) when (attempt < ReadAttempts)
                {
                    problem = e;
                    Thread.Sleep(ReadRetryDelay);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problem = e;
                    break;
                }
            }

            failed?.Invoke(problem);

            var defaults = new AppSettings { location = path };
            string setAside = Path.ChangeExtension(path, ".bad.json");
            try
            {
                File.Move(path, setAside, overwrite: true);
                defaults.SetAsidePath = setAside;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && !File.Exists(path))
            {
                // A second console starting at the same moment moved it first.
                defaults.SetAsidePath = setAside;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                defaults.IsFileUnreadable = true;
            }

            return defaults;
        }

        /// <summary>
        /// Writes the settings, through a file beside them that is then swapped in.
        /// </summary>
        /// <remarks>
        /// The temporary file is named after the process: a second console — an elevated one
        /// beside a standard one — saving at the same moment must not write into the same file,
        /// or one of them would swap in the other's half-written copy.
        /// </remarks>
        public void Save()
        {
            if (this.IsFileUnreadable)
            {
                return;
            }

            lock (SaveGate)
            {
                string temporary = this.location + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(this.location))!);
                    File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));

                    if (File.Exists(this.location))
                    {
                        File.Replace(temporary, this.location, null);
                    }
                    else
                    {
                        File.Move(temporary, this.location);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Losing a preference is preferable to an error dialog for a preference. The
                    // file it would have been is not left lying about.
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }
}
