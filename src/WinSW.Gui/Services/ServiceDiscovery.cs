using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Xml;
using Microsoft.Win32;
using WinSW.Gui.Model;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// One point-in-time reading of a service and the process hosting it.
    /// </summary>
    /// <remarks>
    /// Plain values, and deliberately not a <see cref="ServiceEntry"/>: this is what crosses
    /// back from the sampling thread, and an entry carries bindings that may only be touched
    /// on the UI thread. <see cref="StatusReading.Sample"/> produces one and
    /// <see cref="ServiceDiscovery.Apply"/> is where the two meet.
    /// </remarks>
    public readonly struct ServiceSample
    {
        /// <summary>False when the service could not be queried at all — usually uninstalled.</summary>
        public bool Queried { get; init; }

        public ServiceControllerStatus Status { get; init; }

        /// <summary>The hosting process, or 0 when the service is not running.</summary>
        public int ProcessId { get; init; }

        public int LastExitCode { get; init; }

        /// <summary>False when the process could not be opened, so the metrics below are unset.</summary>
        public bool HasProcess { get; init; }

        public TimeSpan ProcessorTime { get; init; }

        public long WorkingSet { get; init; }

        public int Handles { get; init; }

        /// <summary>Local time; null when the process was not in the snapshot.</summary>
        public DateTime? StartedAt { get; init; }

        /// <summary>
        /// While running: everything under the wrapper, to know later what outlived it. Default
        /// (not merely empty) when nothing was read. Immutable, and a struct: a reading crosses
        /// from the worker to the UI thread, and carries nothing either side could change.
        /// </summary>
        public ImmutableArray<ProcessMark> Descendants { get; init; }

        /// <summary>While stopped: the service's program, still running outside it; see <see cref="StrayProcesses"/>.</summary>
        public StrayFinding? Stray { get; init; }
    }

    /// <summary>
    /// Finds the installed services that are hosted by a WinSW wrapper.
    /// </summary>
    /// <remarks>
    /// The wrapper installs a service whose image path is
    /// <c>"&lt;wrapper.exe&gt;"</c> optionally followed by <c>"&lt;config.xml&gt;"</c>
    /// (see the <c>install</c> command in <c>WinSW/Program.cs</c>). Reading it back from the
    /// registry gives both halves without needing the wrapper to be runnable, and works for
    /// a standard user. <c>winsw dev list</c> answers the same question, but only for one
    /// wrapper executable and only when that executable is at hand.
    /// </remarks>
    public static class ServiceDiscovery
    {
        private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

        /// <summary>Version-info product name stamped on every WinSW build.</summary>
        private const string WrapperProduct = "Windows Service Wrapper";

        public static IReadOnlyList<ServiceEntry> Discover()
        {
            var results = new List<ServiceEntry>();

            using var servicesKey = Registry.LocalMachine.OpenSubKey(ServicesKey);
            if (servicesKey is null)
            {
                return results;
            }

            // Services installed under one install root share a single wrapper executable by
            // design, so the same file would otherwise be opened once per service on a sweep
            // that already runs every thirty seconds. Scoped to this sweep, not static: the
            // point of the next one is to notice that the file has changed.
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var controllers = new Dictionary<string, ServiceController>(StringComparer.OrdinalIgnoreCase);
            foreach (var controller in ServiceController.GetServices())
            {
                controllers[controller.ServiceName] = controller;
            }

            try
            {
                foreach (string name in servicesKey.GetSubKeyNames())
                {
                    if (!controllers.TryGetValue(name, out var controller))
                    {
                        // Drivers and other non-Win32 services never host a wrapper.
                        continue;
                    }

                    using var key = servicesKey.OpenSubKey(name);
                    if (key?.GetValue("ImagePath") is not string imagePath || imagePath.Length == 0)
                    {
                        continue;
                    }

                    if (!TryDescribe(name, imagePath, out string wrapperPath, out string? configPath, out string? problem))
                    {
                        continue;
                    }

                    var entry = new ServiceEntry(name, SafeDisplayName(controller, name), wrapperPath, configPath)
                    {
                        Description = key.GetValue("Description") as string ?? string.Empty,
                        StartMode = DescribeStartMode(key),
                        Account = key.GetValue("ObjectName") as string ?? "LocalSystem",
                        Problem = problem,
                        WrapperVersion = VersionOf(versions, wrapperPath),
                        ConfigWrittenAt = WrittenAt(configPath),
                        ExecutablePath = ExecutableOf(configPath),
                        DependsOn = Names(() => controller.ServicesDependedOn),
                        DependedBy = Names(() => controller.DependentServices),
                    };

                    results.Add(entry);
                }
            }
            finally
            {
                foreach (var controller in controllers.Values)
                {
                    controller.Dispose();
                }
            }

            results.Sort(static (x, y) => string.Compare(x.ServiceName, y.ServiceName, StringComparison.OrdinalIgnoreCase));
            return results;
        }

        /// <summary>
        /// Writes a reading onto its entry. Must run on the UI thread: every property here
        /// raises PropertyChanged, and <see cref="ServiceEntry.Sample"/> keeps the running CPU
        /// history that the sparkline is bound to. The reading itself comes from
        /// <see cref="StatusReading.Sample"/>, on whatever thread took it.
        /// </summary>
        public static void Apply(ServiceEntry entry, in ServiceSample sample)
        {
            if (!sample.Queried)
            {
                entry.Status = null;
                entry.ProcessId = 0;
                entry.NoteStray(null, DateTime.UtcNow);
                entry.ClearSample();
                return;
            }

            // A new wrapper is a new tree: what was noted under the last one describes a run
            // that has ended, and is dropped before anything is noted under this one.
            if (sample.ProcessId != 0 && sample.ProcessId != entry.ProcessId)
            {
                entry.Descendants = Array.Empty<ProcessMark>();
            }

            entry.Status = sample.Status;
            entry.ProcessId = sample.ProcessId;
            entry.LastExitCode = sample.LastExitCode;
            entry.NoteStray(sample.Stray, DateTime.UtcNow);
            if (!sample.Descendants.IsDefault)
            {
                entry.Descendants = sample.Descendants;
            }

            if (!sample.HasProcess)
            {
                entry.ClearSample();
                return;
            }

            entry.Sample(sample.ProcessorTime, sample.WorkingSet, sample.Handles, sample.StartedAt);
        }

        /// <summary>
        /// Re-reads the wrapper's file version. Nothing else does: the periodic poll refreshes
        /// status and metrics, and re-reading a version resource for every service on every
        /// tick would be work for a value that changes only when the file is replaced.
        /// </summary>
        public static void RefreshWrapperVersion(ServiceEntry entry) => entry.WrapperVersion = ReadVersion(entry.WrapperPath);

        /// <summary>The wrapper's file version, read once per distinct path per sweep.</summary>
        /// <summary>
        /// The program the configuration runs, as a full path, for finding it running outside the
        /// service once the service has stopped. Null when it is named bare — <c>java</c>, found
        /// on the PATH — because a bare name matches every copy on the machine, and when the
        /// configuration cannot be read.
        /// </summary>
        private static string? ExecutableOf(string? configPath)
        {
            if (configPath is null)
            {
                return null;
            }

            try
            {
                var model = ServiceConfigModel.Load(configPath);
                if (string.IsNullOrWhiteSpace(model.Executable))
                {
                    return null;
                }

                string expanded = ConfigPaths.Expand(model.Executable!, configPath);
                if (!Path.IsPathRooted(expanded))
                {
                    return null;
                }

                string full = Path.GetFullPath(expanded);
                return Path.HasExtension(full) ? full : full + ".exe";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// When the configuration was last written, for telling whether the running process
        /// predates it. Read here, once a sweep, rather than on every status poll: the answer
        /// only matters at the scale of an edit, and the poll is kept to the one snapshot.
        /// </summary>
        private static DateTime? WrittenAt(string? configPath)
        {
            if (configPath is null)
            {
                return null;
            }

            try
            {
                return File.GetLastWriteTime(configPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string VersionOf(Dictionary<string, string> cache, string path)
        {
            if (!cache.TryGetValue(path, out string? version))
            {
                version = ReadVersion(path);
                cache[path] = version;
            }

            return version;
        }

        private static string ReadVersion(string path)
        {
            try
            {
                return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty : string.Empty;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static string[] Names(Func<ServiceController[]> query)
        {
            try
            {
                var related = query();
                var names = new string[related.Length];
                for (int i = 0; i < related.Length; i++)
                {
                    names[i] = related[i].ServiceName;
                    related[i].Dispose();
                }

                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                return names;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Decides whether a registered service image belongs to WinSW, and resolves the
        /// configuration file it was installed with.
        /// </summary>
        private static bool TryDescribe(
            string serviceName,
            string imagePath,
            out string wrapperPath,
            out string? configPath,
            out string? problem)
        {
            wrapperPath = string.Empty;
            configPath = null;
            problem = null;

            var tokens = SplitCommandLine(imagePath);
            if (tokens.Count == 0)
            {
                return false;
            }

            wrapperPath = tokens[0];
            if (!wrapperPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Global-tool form: the configuration is passed as the single argument.
            // Bundled form: no argument, and the configuration sits next to the executable
            // under the same base name.
            string? candidate = tokens.Count > 1 && tokens[1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? tokens[1]
                : Path.ChangeExtension(wrapperPath, ".xml");

            if (candidate != null && File.Exists(candidate) && DeclaresService(candidate))
            {
                configPath = Path.GetFullPath(candidate);
                return true;
            }

            // No usable configuration. Only claim the service if the image really is a
            // wrapper, and then surface the missing file rather than hiding the service.
            // A wrapper is registered with no arguments or with just its configuration, so
            // anything else is ruled out before paying for a version-info read: that check
            // would otherwise run once per ordinary service on the machine.
            bool shapedLikeWrapper = tokens.Count == 1
                || (tokens.Count == 2 && tokens[1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

            if (!shapedLikeWrapper || !IsWrapperExecutable(wrapperPath))
            {
                return false;
            }

            problem = candidate is null
                ? Localizer.Format("M.Discovery.NoConfig", serviceName)
                : Localizer.Format("M.Discovery.ConfigMissing", candidate);
            return true;
        }

        private static bool DeclaresService(string path)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                };

                using var reader = XmlReader.Create(path, settings);
                return reader.MoveToContent() == XmlNodeType.Element
                    && reader.Name.Equals("service", StringComparison.Ordinal);
            }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>True when the file's version information identifies it as a WinSW build.</summary>
        public static bool IsWrapperExecutable(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                // 'winsw customize' rewrites the company name but leaves the product alone,
                // so re-branded wrappers are still recognised here.
                return FileVersionInfo.GetVersionInfo(path).ProductName?.Contains(WrapperProduct, StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string SafeDisplayName(ServiceController controller, string fallback)
        {
            try
            {
                string name = controller.DisplayName;
                return string.IsNullOrEmpty(name) ? fallback : name;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }

        private static string DescribeStartMode(RegistryKey key)
        {
            int start = key.GetValue("Start") is int value ? value : -1;
            string mode = start switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown",
            };

            if (start == 2 && key.GetValue("DelayedAutostart") is int delayed && delayed != 0)
            {
                mode = "Automatic (delayed)";
            }

            return mode;
        }

        /// <summary>
        /// Splits a registry image path the way the service control manager does: quoted
        /// segments are taken whole, everything else is separated by whitespace.
        /// </summary>
        internal static List<string> SplitCommandLine(string value)
        {
            var tokens = new List<string>();
            int index = 0;

            while (index < value.Length)
            {
                while (index < value.Length && char.IsWhiteSpace(value[index]))
                {
                    index++;
                }

                if (index >= value.Length)
                {
                    break;
                }

                int start;
                if (value[index] == '"')
                {
                    start = ++index;
                    while (index < value.Length && value[index] != '"')
                    {
                        index++;
                    }

                    tokens.Add(value.Substring(start, index - start));
                    if (index < value.Length)
                    {
                        index++;
                    }
                }
                else
                {
                    start = index;
                    while (index < value.Length && !char.IsWhiteSpace(value[index]))
                    {
                        index++;
                    }

                    tokens.Add(value.Substring(start, index - start));
                }
            }

            return tokens;
        }
    }
}
