using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WinSW.Gui.Services
{
    /// <summary>One Windows event written by or about a service.</summary>
    public sealed class ServiceEvent
    {
        public ServiceEvent(DateTime time, EventLogEntryType type, long eventId, string source, string message)
        {
            this.Time = time;
            this.Type = type;
            this.EventId = eventId;
            this.Source = source;
            this.Message = message;
        }

        public DateTime Time { get; }

        public EventLogEntryType Type { get; }

        public long EventId { get; }

        public string Source { get; }

        public string Message { get; }

        public bool IsError => this.Type == EventLogEntryType.Error || this.Type == EventLogEntryType.FailureAudit;

        public bool IsWarning => this.Type == EventLogEntryType.Warning;

        public string TimeText => this.Time.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>The first line, for the list; the full text goes in the tooltip.</summary>
        public string Headline
        {
            get
            {
                int newline = this.Message.IndexOfAny(new[] { '\r', '\n' });
                return newline < 0 ? this.Message : this.Message.Substring(0, newline);
            }
        }
    }

    /// <summary>
    /// Reads the Application event log for what the wrapper reported about a service.
    /// </summary>
    /// <remarks>
    /// The wrapper registers the service ID as an event source at install time and writes
    /// its own start/stop/failure records under it, falling back to the shared source
    /// "Windows Service Wrapper" when that fails. The service control manager's own
    /// records ("The X service terminated unexpectedly") live in the System log under
    /// "Service Control Manager" and mention the display name, so both logs are searched.
    /// This is where the answer to "why did it not start" usually is.
    /// </remarks>
    public static class EventLogReader
    {
        private const string FallbackSource = "Windows Service Wrapper";
        private const string ScmSource = "Service Control Manager";

        /// <summary>Upper bound on records examined per log; each access is a native read.</summary>
        private const int ScanLimit = 4000;

        public static IReadOnlyList<ServiceEvent> Read(string serviceId, string displayName, int maximum = 200)
        {
            var results = new List<ServiceEvent>();

            Collect("Application", results, maximum, e =>
                string.Equals(e.Source, serviceId, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(e.Source, FallbackSource, StringComparison.OrdinalIgnoreCase)
                    && e.Message.Contains(serviceId, StringComparison.OrdinalIgnoreCase)));

            Collect("System", results, maximum, e =>
                string.Equals(e.Source, ScmSource, StringComparison.OrdinalIgnoreCase)
                && (e.Message.Contains(displayName, StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains(serviceId, StringComparison.OrdinalIgnoreCase)));

            results.Sort(static (x, y) => y.Time.CompareTo(x.Time));
            if (results.Count > maximum)
            {
                results.RemoveRange(maximum, results.Count - maximum);
            }

            return results;
        }

        private static void Collect(string logName, List<ServiceEvent> results, int maximum, Func<EventLogEntry, bool> matches)
        {
            try
            {
                using var log = new EventLog(logName);
                var entries = log.Entries;
                int count = entries.Count;
                int found = 0;

                for (int i = count - 1; i >= 0 && i >= count - ScanLimit && found < maximum; i--)
                {
                    EventLogEntry entry;
                    try
                    {
                        entry = entries[i];
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        // The log was cleared or rolled underneath the enumeration.
                        break;
                    }

                    if (!matches(entry))
                    {
                        continue;
                    }

                    results.Add(new ServiceEvent(entry.TimeGenerated, entry.EntryType, entry.InstanceId, entry.Source, entry.Message));
                    found++;
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The Security log needs elevation; Application and System normally do not,
                // but a hardened machine may say otherwise. Nothing to show is acceptable.
            }
        }
    }
}
