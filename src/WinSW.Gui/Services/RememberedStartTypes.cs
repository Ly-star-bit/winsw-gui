using System;
using System.Collections.Generic;
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
    /// Used on the UI thread only; the file is a few lines, read once on first use.
    /// </para>
    /// </remarks>
    public sealed class RememberedStartTypes
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static RememberedStartTypes? current;

        private readonly string filePath;
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

            this.Load()[serviceName] = token;
            this.Save();
        }

        public void Forget(string serviceName)
        {
            if (this.Load().Remove(serviceName))
            {
                this.Save();
            }
        }

        private Dictionary<string, string> Load()
        {
            if (this.types != null)
            {
                return this.types;
            }

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
                // Unreadable is the same as empty: the worst it costs is a restore not offered.
            }

            return this.types = loaded;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.filePath)!);
                File.WriteAllText(this.filePath, JsonSerializer.Serialize(this.types, Options));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Kept for this session all the same; only a restart of the console forgets it.
            }
        }
    }
}
