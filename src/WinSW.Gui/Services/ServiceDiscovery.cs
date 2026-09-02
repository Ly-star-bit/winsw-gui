using System;
using System.Collections.Generic;
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
        /// Refreshes the volatile parts of an entry: its state and hosting process.
        /// </summary>
        public static void RefreshStatus(ServiceEntry entry)
        {
            try
            {
                using var controller = new ServiceController(entry.ServiceName);
                entry.Status = controller.Status;
                entry.ProcessId = entry.Status == ServiceControllerStatus.Running
                    ? NativeMethods.GetServiceProcessId(entry.ServiceName)
                    : 0;
            }
            catch (InvalidOperationException)
            {
                // The service was uninstalled between the scan and this refresh.
                entry.Status = null;
                entry.ProcessId = 0;
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
