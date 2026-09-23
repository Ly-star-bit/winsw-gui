using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WinSW.Gui.Services
{
    /// <summary>One earlier version of a configuration file, as set aside by <see cref="ConfigHistory"/>.</summary>
    public sealed class ConfigVersion
    {
        public ConfigVersion(string path, DateTime savedAt, long size)
        {
            this.Path = path;
            this.SavedAt = savedAt;
            this.Size = size;
        }

        /// <summary>The copy inside the history folder.</summary>
        public string Path { get; }

        /// <summary>When this version was written to the configuration, local time.</summary>
        public DateTime SavedAt { get; }

        public long Size { get; }

        public string Label => this.SavedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + "  ·  " + Math.Max(1, (this.Size + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
    }

    /// <summary>
    /// The versions of a configuration file that saving from this console has replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every save overwrites the file, and "Save &amp; apply" pushes it to the running service,
    /// so a wrong edit went straight from the form into production with nothing to go back to.
    /// Before each save the file on disk is copied aside, and the editor can load any of the
    /// copies back into the form, where saving it restores it.
    /// </para>
    /// <para>
    /// The copies live under <c>%LOCALAPPDATA%\WinSW.Gui\history</c>, one folder per
    /// configuration, because that is always writable: a configuration under the install root
    /// or Program Files needs elevation to write beside, and a backup that asks for its own
    /// prompt is one that gets declined. The price is that they belong to the Windows user
    /// who saved, like the settings do. A copy carries whatever the configuration carries,
    /// a service account's password included, which is why only the last
    /// <see cref="Keep"/> are kept and why they stay in the user's own profile.
    /// </para>
    /// </remarks>
    public static class ConfigHistory
    {
        /// <summary>Versions kept per configuration; older ones are deleted as new ones arrive.</summary>
        internal const int Keep = 20;

        private const string StampFormat = "yyyyMMdd-HHmmss-fff";

        private static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "history");

        /// <summary>
        /// Copies the file at <paramref name="configPath"/> aside before it is overwritten.
        /// Never throws: a save is not refused because its backup could not be made.
        /// </summary>
        public static void Preserve(string configPath) => Preserve(configPath, Root);

        /// <summary>Earlier versions of <paramref name="configPath"/>, newest first.</summary>
        public static IReadOnlyList<ConfigVersion> List(string configPath) => List(configPath, Root);

        internal static void Preserve(string configPath, string root)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    return;
                }

                byte[] current = File.ReadAllBytes(configPath);

                // Saving twice without a change in between is not a new version; neither is
                // a save that was declined at the elevation prompt and then tried again.
                var newest = List(configPath, root).FirstOrDefault();
                if (newest != null && File.ReadAllBytes(newest.Path).AsSpan().SequenceEqual(current))
                {
                    return;
                }

                string folder = FolderFor(configPath, root);
                Directory.CreateDirectory(folder);

                // Named for when the version was written, which is how anyone looks for it
                // ("the one from yesterday afternoon"), not for when it was set aside.
                string stamp = File.GetLastWriteTime(configPath).ToString(StampFormat, CultureInfo.InvariantCulture);
                string copy = Path.Combine(folder, stamp + ".xml");
                for (int n = 2; File.Exists(copy); n++)
                {
                    copy = Path.Combine(folder, stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ".xml");
                }

                File.WriteAllBytes(copy, current);

                // For whoever opens the folder: which file these are versions of.
                File.WriteAllText(Path.Combine(folder, "source.txt"), Path.GetFullPath(configPath));

                foreach (var old in List(configPath, root).Skip(Keep))
                {
                    File.Delete(old.Path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        internal static IReadOnlyList<ConfigVersion> List(string configPath, string root)
        {
            string folder = FolderFor(configPath, root);
            if (!Directory.Exists(folder))
            {
                return Array.Empty<ConfigVersion>();
            }

            var versions = new List<ConfigVersion>();
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.xml"))
                {
                    string name = Path.GetFileNameWithoutExtension(file.Name);
                    string stamp = name.Length > StampFormat.Length ? name.Substring(0, StampFormat.Length) : name;
                    if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var savedAt))
                    {
                        versions.Add(new ConfigVersion(file.FullName, savedAt, file.Length));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            return versions
                .OrderByDescending(v => v.SavedAt)
                .ThenByDescending(v => v.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// One folder per configuration: its file name, for whoever browses the folder, and a
        /// hash of its full path, because two services' configurations are often both called
        /// something like <c>app.xml</c>.
        /// </summary>
        internal static string FolderFor(string configPath, string root)
        {
            string full = Path.GetFullPath(configPath);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
            return Path.Combine(root, Path.GetFileNameWithoutExtension(full) + "-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant());
        }
    }
}
