using System.Diagnostics;
using log4net.Appender;
using log4net.Core;

namespace WinSW.Logging
{
    /// <summary>
    /// Implementes service Event log appender for log4j.
    /// The implementation presumes that service gets initialized after the logging.
    /// </summary>
    internal sealed class ServiceEventLogAppender : AppenderSkeleton
    {
        private readonly WrapperServiceEventLogProvider provider;

        internal ServiceEventLogAppender(WrapperServiceEventLogProvider provider)
        {
            this.provider = provider;
        }

        protected override void Append(LoggingEvent loggingEvent)
        {
            // Both are nullable as of log4net 3: an event can be rendered from a null message,
            // and its level is unset until a repository assigns one.
            string message = loggingEvent.RenderedMessage ?? string.Empty;
            var type = ToEventLogEntryType(loggingEvent.Level);

            var eventLog = this.provider.Locate();

            if (eventLog is not null)
            {
                eventLog.WriteEntry(message, type);
                return;
            }

            try
            {
                using var backupLog = new EventLog("Application", ".", "Windows Service Wrapper");
                backupLog.WriteEntry(message, type);
            }
            catch
            {
            }
        }

        private static EventLogEntryType ToEventLogEntryType(Level? level)
        {
            if (level is null)
            {
                return EventLogEntryType.Information;
            }

            if (level.Value >= Level.Error.Value)
            {
                return EventLogEntryType.Error;
            }

            if (level.Value >= Level.Warn.Value)
            {
                return EventLogEntryType.Warning;
            }

            // All other events will be posted as information
            return EventLogEntryType.Information;
        }
    }
}
