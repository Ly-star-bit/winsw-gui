using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using EventQueryReader = System.Diagnostics.Eventing.Reader.EventLogReader;

namespace WinSW.Gui.Services
{
    /// <summary>One Windows event written by or about a service.</summary>
    public sealed class ServiceEvent
    {
        public ServiceEvent(DateTime time, EventLogEntryType type, long eventId, string source, string message)
        {
            this.Time = time;
            this.Type = type;

            // Whoever reads the record, the ID is its low sixteen bits: an EventLogEntry's
            // InstanceId carries the qualifier above them, and an event ID is never wider.
            this.EventId = eventId & 0xFFFF;
            this.Source = source;
            this.Message = message;
        }

        public DateTime Time { get; }

        public EventLogEntryType Type { get; }

        /// <summary>The event ID as Event Viewer shows it: 7031, not the 3221232503 of the qualified ID.</summary>
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

    /// <summary>How far back a search of the event logs looked.</summary>
    /// <remarks>
    /// A search looks at a bounded number of records, newest first. When it stops at that bound
    /// having found nothing, "no events" is true only of the records it looked at, and is to be
    /// said that way: on a System log full of Schannel or DCOM warnings, a bound on every record
    /// was a few hours' worth, and "no events" was wrong about a service that crashed yesterday.
    /// </remarks>
    public readonly struct EventScan
    {
        public EventScan(int examined, DateTime? cutShortAt)
        {
            this.Examined = examined;
            this.CutShortAt = cutShortAt;
        }

        /// <summary>How many records were looked at.</summary>
        public int Examined { get; }

        /// <summary>
        /// When the search stopped before the first record there is: the time of the oldest one
        /// it looked at, from which on nothing was passed over. Null when it went all the way
        /// back, so that nothing found means there is nothing.
        /// </summary>
        public DateTime? CutShortAt { get; }

        /// <summary>
        /// Two logs' searches as one. Nothing was passed over in either from the later of the
        /// times they were cut short at; a log searched to its start covers any time, so it
        /// leaves the other's time as it is.
        /// </summary>
        public static EventScan Combine(EventScan first, EventScan second)
        {
            DateTime? cutShortAt = first.CutShortAt;
            if (second.CutShortAt is DateTime time && (cutShortAt is null || time > cutShortAt))
            {
                cutShortAt = time;
            }

            return new EventScan(first.Examined + second.Examined, cutShortAt);
        }
    }

    /// <summary>The events a search found about a service, newest first, and how far back it looked.</summary>
    public sealed class EventSearch
    {
        public EventSearch(IReadOnlyList<ServiceEvent> events, EventScan scan)
        {
            this.Events = events;
            this.Scan = scan;
        }

        public IReadOnlyList<ServiceEvent> Events { get; }

        public EventScan Scan { get; }
    }

    /// <summary>
    /// Reads the Application event log for what the wrapper reported about a service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wrapper registers the service ID as an event source at install time and writes
    /// its own start/stop/failure records under it, falling back to the shared source
    /// "Windows Service Wrapper" when that fails. The service control manager's own
    /// records ("The X service terminated unexpectedly") live in the System log under
    /// "Service Control Manager" and mention the display name, so both logs are searched.
    /// This is where the answer to "why did it not start" usually is.
    /// </para>
    /// <para>
    /// The logs are queried for those sources' records (EventLogQuery, which Server 2012 R2 has
    /// had since Vista) rather than read record by record. The bound on how many records a search
    /// looks at was spent on every record of the log, and a System log full of Schannel or DCOM
    /// noise used it up within hours; the event log service now leaves those out before any
    /// record reaches here, so the bound goes on the few sources that can be about the service.
    /// Which of their records are about it is still decided here: the query language has no
    /// string functions, so "the message names the service" cannot be asked in it. What the
    /// bound still cuts short is said as such (<see cref="EventScan"/>) rather than as "none".
    /// Should the query be refused, the logs are read record by record as before.
    /// </para>
    /// </remarks>
    public static class EventLogReader
    {
        private const string FallbackSource = "Windows Service Wrapper";
        private const string ScmSource = "Service Control Manager";

        /// <summary>Upper bound on records examined per log; each access is a native read.</summary>
        private const int ScanLimit = 4000;

        public static IReadOnlyList<ServiceEvent> Read(string serviceId, string displayName, int maximum = 200) =>
            Search(serviceId, displayName, maximum).Events;

        /// <summary>
        /// The events about a service, newest first and at most <paramref name="maximum"/> of
        /// them, and how far back the logs were searched for them.
        /// </summary>
        public static EventSearch Search(string serviceId, string displayName, int maximum = 200)
        {
            var results = new List<ServiceEvent>();

            var application = Collect(
                "Application",
                new[] { serviceId, FallbackSource },
                results,
                maximum,
                (source, inserts) => IsFromWrapper(source, inserts, serviceId));

            var system = Collect(
                "System",
                new[] { ScmSource },
                results,
                maximum,
                (source, inserts) => IsFromScm(source, inserts, serviceId, displayName));

            results.Sort(static (x, y) => y.Time.CompareTo(x.Time));
            if (results.Count > maximum)
            {
                results.RemoveRange(maximum, results.Count - maximum);
            }

            return new EventSearch(results.ToArray(), EventScan.Combine(application, system));
        }

        /// <summary>
        /// An Application log record is about the service when the wrapper wrote it under the
        /// service's own source, or under the shared fallback source with the service in its text.
        /// </summary>
        internal static bool IsFromWrapper(string source, IReadOnlyList<string?> inserts, string serviceId) =>
            string.Equals(source, serviceId, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(source, FallbackSource, StringComparison.OrdinalIgnoreCase)
                && inserts.Any(text => text != null && text.Contains(serviceId, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// A System log record is about the service when the service control manager wrote it
        /// with the service's name or display name as one of its insertion strings.
        /// </summary>
        /// <remarks>
        /// The manager puts the name in whole — "The {0} service entered the {1} state." — so it
        /// is compared whole. Looked for anywhere in the message, as it was, a service named
        /// "api" was also every service with "api" in its name, and an empty display name
        /// matched every record the manager wrote.
        /// </remarks>
        internal static bool IsFromScm(string source, IReadOnlyList<string?> inserts, string serviceId, string displayName) =>
            string.Equals(source, ScmSource, StringComparison.OrdinalIgnoreCase)
            && inserts.Any(text => IsName(text, serviceId) || IsName(text, displayName));

        /// <summary>
        /// The query for the records of <paramref name="sources"/>, in the form Event Viewer's
        /// own filter writes it. Null when a name cannot be put in an XPath string, which has no
        /// escapes: one holding both kinds of quote.
        /// </summary>
        internal static string? SourceQuery(IReadOnlyList<string> sources)
        {
            var names = new List<string>(sources.Count);
            foreach (string source in sources)
            {
                string? literal = !source.Contains('\'') ? "'" + source + "'"
                    : !source.Contains('"') ? "\"" + source + "\""
                    : null;
                if (literal is null)
                {
                    return null;
                }

                names.Add("@Name=" + literal);
            }

            return "*[System[Provider[" + string.Join(" or ", names) + "]]]";
        }

        private static bool IsName(string? text, string name)
        {
            string trimmed = name.Trim();
            return trimmed.Length > 0 && text != null && string.Equals(text.Trim(), trimmed, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds to <paramref name="results"/> the newest records of <paramref name="sources"/>
        /// in <paramref name="logName"/> that <paramref name="matches"/> takes, given their
        /// source and insertion strings, and says how far back it looked.
        /// </summary>
        private static EventScan Collect(string logName, string[] sources, List<ServiceEvent> results, int maximum, Func<string, IReadOnlyList<string?>, bool> matches)
        {
            string? query = SourceQuery(sources);
            if (query != null)
            {
                var found = new List<ServiceEvent>();
                try
                {
                    var scan = Query(logName, query, found, maximum, matches);
                    results.AddRange(found);
                    return scan;
                }
                catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
                {
                    // The query was refused, or failed part way; what it found is let go of,
                    // and the log is read the way it always was.
                }
            }

            return Scan(logName, results, maximum, matches);
        }

        private static EventScan Query(string logName, string query, List<ServiceEvent> results, int maximum, Func<string, IReadOnlyList<string?>, bool> matches)
        {
            using var reader = new EventQueryReader(new EventLogQuery(logName, PathType.LogName, query) { ReverseDirection = true });
            int examined = 0;
            int found = 0;
            DateTime? oldest = null;

            while (true)
            {
                if (examined >= ScanLimit || found >= maximum)
                {
                    // Stopped at the bound, or with all that is wanted. The time can only be
                    // missing if no record looked at had one, which the event log does not
                    // write; now is then the only time nothing was passed over from.
                    return new EventScan(examined, oldest ?? DateTime.Now);
                }

                using var record = reader.ReadEvent();
                if (record is null)
                {
                    return new EventScan(examined, null);
                }

                examined++;
                oldest = record.TimeCreated ?? oldest;

                string source = record.ProviderName ?? string.Empty;
                var inserts = InsertsOf(record);
                if (!matches(source, inserts))
                {
                    continue;
                }

                results.Add(new ServiceEvent(record.TimeCreated ?? default, TypeOf(record), record.Id, source, Describe(record, inserts)));
                found++;
            }
        }

        /// <summary>
        /// The log read record by record from the newest, as it was before it was queried. Each
        /// record is a native read, and <see cref="ScanLimit"/> of them are every source's.
        /// </summary>
        private static EventScan Scan(string logName, List<ServiceEvent> results, int maximum, Func<string, IReadOnlyList<string?>, bool> matches)
        {
            int examined = 0;
            DateTime? oldest = null;
            try
            {
                using var log = new EventLog(logName);
                var entries = log.Entries;
                int count = entries.Count;
                int found = 0;

                for (int i = count - 1; i >= 0; i--)
                {
                    if (examined >= ScanLimit || found >= maximum)
                    {
                        return new EventScan(examined, oldest);
                    }

                    EventLogEntry entry;
                    try
                    {
                        entry = entries[i];
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        // The log was cleared or rolled underneath the enumeration. Records older
                        // than the last one read may still be there, so the search did not reach
                        // the start.
                        return new EventScan(examined, oldest);
                    }

                    examined++;
                    oldest = entry.TimeGenerated;

                    // The insertion strings are the record itself; the message is them formatted
                    // through the source's message table, which is the costly part, and is done
                    // only for a record that is kept.
                    if (!matches(entry.Source, entry.ReplacementStrings))
                    {
                        continue;
                    }

                    // InstanceId carries the qualifier bits: 3221232503 is 7031.
                    results.Add(new ServiceEvent(entry.TimeGenerated, entry.EntryType, entry.InstanceId & 0xFFFF, entry.Source, entry.Message));
                    found++;
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The Security log needs elevation; Application and System normally do not,
                // but a hardened machine may say otherwise. Nothing to show is acceptable.
            }

            return new EventScan(examined, null);
        }

        private static IReadOnlyList<string?> InsertsOf(EventRecord record)
        {
            try
            {
                return record.Properties.Select(p => p.Value?.ToString()).ToArray();
            }
            catch (EventLogException)
            {
                // A record whose values cannot be rendered names nothing.
                return Array.Empty<string?>();
            }
        }

        private static string Describe(EventRecord record, IReadOnlyList<string?> inserts)
        {
            try
            {
                string? message = record.FormatDescription();
                if (!string.IsNullOrEmpty(message))
                {
                    return message;
                }
            }
            catch (EventLogException)
            {
                // Falls through to the insertion strings below.
            }

            // No message table for the source on this machine: it went with a service that was
            // removed, say. The record's own words are its insertion strings.
            return string.Join(" ", inserts.Where(text => !string.IsNullOrEmpty(text)));
        }

        private static EventLogEntryType TypeOf(EventRecord record)
        {
            long keywords = record.Keywords ?? 0;
            if ((keywords & (long)StandardEventKeywords.AuditFailure) != 0)
            {
                return EventLogEntryType.FailureAudit;
            }

            if ((keywords & (long)StandardEventKeywords.AuditSuccess) != 0)
            {
                return EventLogEntryType.SuccessAudit;
            }

            return record.Level switch
            {
                (byte)StandardEventLevel.Critical or (byte)StandardEventLevel.Error => EventLogEntryType.Error,
                (byte)StandardEventLevel.Warning => EventLogEntryType.Warning,
                _ => EventLogEntryType.Information,
            };
        }
    }
}
