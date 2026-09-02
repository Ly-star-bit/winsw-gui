using System;

namespace WinSW.Gui.Services
{
    /// <summary>Cheap classification of log lines, shared by the viewer's colouring, counting and navigation.</summary>
    public static class LogSeverity
    {
        public static bool IsError(string line) =>
            Contains(line, "error") || Contains(line, "exception") || Contains(line, "fatal") || Contains(line, "severe");

        public static bool IsWarning(string line) => Contains(line, "warn");

        private static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
