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
        };

        /// <summary>A language code from <see cref="Localizer.Languages"/>, or null to follow the OS.</summary>
        public string? Language { get; set; }

        public static AppSettings Load()
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
