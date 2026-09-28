using System;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>What a reading of a service just started from the console says about the start.</summary>
    public enum StartWatchOutcome
    {
        /// <summary>Nothing yet: it is running, or on its way, and the watch goes on.</summary>
        Watching,

        /// <summary>It stopped again within the watch: the start did not hold.</summary>
        StoppedAgain,

        /// <summary>The watch is over; a stop from now on is an ordinary one.</summary>
        Expired,
    }

    /// <summary>
    /// Watches a service for a few seconds after a start from the console succeeded, for the stop
    /// that says it did not. 'winsw start' succeeds once Windows reports the service running, which
    /// the wrapper does as soon as it has started the program: a program that fails two seconds in —
    /// a port already taken, a module that will not import — is a start that "completed" and a row
    /// that turns Stopped straight after, and a green notice about it is the wrong one.
    /// </summary>
    public sealed class StartWatch
    {
        public StartWatch(string serviceName, DateTime until)
        {
            this.ServiceName = serviceName;
            this.Until = until;
        }

        public string ServiceName { get; }

        /// <summary>When the watch ends, in the clock <see cref="Observe"/> is given.</summary>
        public DateTime Until { get; }

        /// <summary>Takes one reading of the service's state, at <paramref name="now"/>.</summary>
        public StartWatchOutcome Observe(ServiceControllerStatus? status, DateTime now) =>
            now > this.Until ? StartWatchOutcome.Expired
            : status == ServiceControllerStatus.Stopped ? StartWatchOutcome.StoppedAgain
            : StartWatchOutcome.Watching;
    }
}
