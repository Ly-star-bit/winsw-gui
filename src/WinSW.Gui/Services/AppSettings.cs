using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Per-user preferences, stored as JSON under <c>%LOCALAPPDATA%\WinSW.Gui</c>.
    /// </summary>
    public sealed class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "settings.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        private static AppSettings? current;

        /// <summary>The one instance every subsystem reads and writes, loaded on first use.</summary>
        public static AppSettings Current => current ??= Load();

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

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // A corrupt or unreadable settings file is not worth failing startup over.
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Losing a preference is preferable to an error dialog for a preference.
            }
        }
    }
}
