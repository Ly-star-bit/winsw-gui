using System;
using System.Collections.Generic;
using System.IO;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// What the console was started with, read in one place.
    /// </summary>
    /// <remarks>
    /// Two kinds of launch give it a configuration, and not in the same place: the Explorer verb
    /// and the command line put the file first, and "Restart as administrator" puts it after its
    /// own switches. Only the first argument used to be looked at, so the configuration a
    /// restart handed on would have been dropped.
    /// </remarks>
    public sealed class StartupArguments
    {
        /// <summary>
        /// Tells the elevated copy that the one starting it is on its way out: it waits for that
        /// copy to close instead of deferring to it as a second launch would.
        /// </summary>
        public const string ReplaceArgument = "--replace";

        /// <summary>
        /// Keeps the tray watch of the copy that restarted as administrator. Not
        /// <see cref="Autostart.TrayArgument"/>, which starts hidden: the user who asked for the
        /// restart is looking for the window. It is shown, and closing it still puts the console
        /// in the tray rather than ending the watching.
        /// </summary>
        public const string KeepTrayArgument = "--keep-tray";

        private StartupArguments(string? configPath, bool tray, bool keepTray, bool replacing)
        {
            this.ConfigPath = configPath;
            this.Tray = tray;
            this.KeepTray = keepTray;
            this.Replacing = replacing;
        }

        /// <summary>
        /// The first .xml among the arguments that exists, as a full path: "WinSW.Gui.exe
        /// myapp.xml", the Explorer verb, or the configuration a copy restarted as administrator
        /// had open.
        /// </summary>
        public string? ConfigPath { get; }

        /// <summary>Started at sign-in: only the tray icon shows; see <see cref="Autostart"/>.</summary>
        public bool Tray { get; }

        /// <summary>See <see cref="KeepTrayArgument"/>.</summary>
        public bool KeepTray { get; }

        /// <summary>See <see cref="ReplaceArgument"/>.</summary>
        public bool Replacing { get; }

        /// <summary>Reads the command line; switches and the configuration may come in any order.</summary>
        /// <param name="args">The command line, as the application was given it.</param>
        /// <param name="fileExists">
        /// <see cref="File.Exists(string)"/>; a parameter so that tests need no files. A path that
        /// does not exist is not a configuration to open, which is how an argument that merely
        /// ends in ".xml" has always been treated.
        /// </param>
        public static StartupArguments Parse(IReadOnlyList<string> args, Func<string, bool> fileExists)
        {
            string? configPath = null;
            bool tray = false;
            bool keepTray = false;
            bool replacing = false;

            foreach (string argument in args)
            {
                if (IsSwitch(argument, Autostart.TrayArgument))
                {
                    tray = true;
                }
                else if (IsSwitch(argument, KeepTrayArgument))
                {
                    keepTray = true;
                }
                else if (IsSwitch(argument, ReplaceArgument))
                {
                    replacing = true;
                }
                else if (configPath is null && argument.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && fileExists(argument))
                {
                    configPath = Path.GetFullPath(argument);
                }
            }

            return new StartupArguments(configPath, tray, keepTray, replacing);
        }

        /// <summary>
        /// The command line for the copy that replaces this one as administrator, one element per
        /// argument: quoting a path with spaces in it is left to the process start.
        /// </summary>
        /// <param name="configPath">The configuration it is to open, if any.</param>
        /// <param name="keepTray">This copy watches from the tray; the new one is to do the same.</param>
        public static string[] ForElevatedRestart(string? configPath, bool keepTray)
        {
            var arguments = new List<string> { ReplaceArgument };
            if (keepTray)
            {
                arguments.Add(KeepTrayArgument);
            }

            if (configPath != null)
            {
                arguments.Add(configPath);
            }

            return arguments.ToArray();
        }

        private static bool IsSwitch(string argument, string name) => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase);
    }
}
