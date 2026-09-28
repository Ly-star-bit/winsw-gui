using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>What the service control manager does after a failure; the values are SC_ACTION_TYPE's.</summary>
    public enum RecoveryActionKind
    {
        None = 0,
        Restart = 1,
        Reboot = 2,
        RunCommand = 3,
    }

    /// <summary>One step of a service's recovery: what is done after a failure, and how long after it.</summary>
    public readonly record struct RecoveryAction(RecoveryActionKind Kind, TimeSpan Delay)
    {
        /// <summary>
        /// Both do the same thing. The delay of a step that does nothing is not compared: nothing
        /// happens either way, and services.msc and the wrapper leave different delays on it.
        /// </summary>
        public bool SameEffectAs(RecoveryAction other) =>
            this.Kind == other.Kind && (this.Kind == RecoveryActionKind.None || this.Delay == other.Delay);
    }

    /// <summary>
    /// A service's recovery settings — the failure actions on the Recovery tab of services.msc — as
    /// the service control manager holds them, or as a configuration file declares them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first failure is answered with the first action, the second with the second, and every
    /// failure after the last action with the last action again: the last one repeats for as long
    /// as the service keeps failing. The count of failures goes back to zero once
    /// <see cref="ResetAfter"/> has passed without one.
    /// </para>
    /// <para>
    /// Immutable, and compared by content: a rescan reads a fresh copy every half minute, and an
    /// entry that took each one for a change would repaint its detail panel for nothing. It is
    /// read on the rescan's worker and handed to the UI thread as it is.
    /// </para>
    /// </remarks>
    public sealed class RecoverySettings : IEquatable<RecoverySettings>
    {
        /// <summary>
        /// ERROR_PROCESS_ABORTED: what the service control manager records for a service process
        /// that ended without reporting that it had stopped. That is a crash, and the one kind of
        /// stop the failure actions answer unless <see cref="OnNonCrashFailures"/> is set. The
        /// wrapper leaves this way whenever its program exits with a code other than 0.
        /// </summary>
        public const int ProcessAborted = 1067;

        /// <summary>
        /// Slack on top of a restart's delay before a service still stopped is taken to be staying
        /// stopped: the stop is seen up to a poll after it happened, and the start that answers it
        /// takes a moment to show as Starting.
        /// </summary>
        public static readonly TimeSpan RestartMargin = TimeSpan.FromSeconds(15);

        /// <summary>
        /// What the wrapper sets when a configuration declares failure actions and no
        /// <c>&lt;resetfailure&gt;</c>; see <c>ServiceConfig.ResetFailureAfter</c>.
        /// </summary>
        public static readonly TimeSpan WrapperDefaultReset = TimeSpan.FromDays(1);

        /// <summary>SERVICE_FAILURE_ACTIONS.dwResetPeriod's INFINITE: the count never goes back to zero.</summary>
        private const uint NeverReset = uint.MaxValue;

        /// <summary>More than any recovery tab holds; a larger count read back is not believed.</summary>
        private const int MostActions = 64;

        public RecoverySettings(ImmutableArray<RecoveryAction> actions, TimeSpan? resetAfter, bool onNonCrashFailures)
        {
            this.Actions = actions.IsDefault ? ImmutableArray<RecoveryAction>.Empty : actions;
            this.ResetAfter = resetAfter;
            this.OnNonCrashFailures = onNonCrashFailures;
        }

        /// <summary>The actions in the order failures are answered with; the last repeats.</summary>
        public ImmutableArray<RecoveryAction> Actions { get; }

        /// <summary>How long without a failure before the count starts over; null when it never does.</summary>
        public TimeSpan? ResetAfter { get; }

        /// <summary>
        /// The actions also answer a stop the service reported itself with an exit code other than
        /// 0, not only a crash: services.msc's "Enable actions for stops with errors". A
        /// configuration file cannot set it, so comparisons with one leave it out.
        /// </summary>
        public bool OnNonCrashFailures { get; }

        /// <summary>Some failure is answered with a restart of the service.</summary>
        public bool Restarts => this.Actions.Any(a => a.Kind == RecoveryActionKind.Restart);

        /// <summary>Some failure is answered with something other than nothing.</summary>
        public bool DoesAnything => this.Actions.Any(a => a.Kind != RecoveryActionKind.None);

        /// <summary>
        /// The actions with the repetition the service control manager adds taken back out: an
        /// action that only repeats the one before it at the end says nothing the last one does not
        /// say already, and none at all when nothing is ever done.
        /// </summary>
        private ImmutableArray<RecoveryAction> Effective
        {
            get
            {
                if (!this.DoesAnything)
                {
                    return ImmutableArray<RecoveryAction>.Empty;
                }

                int length = this.Actions.Length;
                while (length > 1 && this.Actions[length - 1].SameEffectAs(this.Actions[length - 2]))
                {
                    length--;
                }

                return length == this.Actions.Length ? this.Actions : ImmutableArray.Create(this.Actions, 0, length);
            }
        }

        /// <summary>
        /// Which failure is answered how makes a difference, so the reset period does too. With one
        /// action, or the same action throughout, every failure is answered alike and the count
        /// changes nothing.
        /// </summary>
        private bool CountMatters => this.Effective.Length > 1;

        /// <summary>
        /// The settings as <c>QueryServiceConfig2W</c> returns them: the reset period in seconds,
        /// INFINITE for never, and each action's type with its delay in milliseconds.
        /// </summary>
        public static RecoverySettings FromScm(uint resetPeriodSeconds, IReadOnlyList<(int Type, uint DelayMilliseconds)> actions, bool onNonCrashFailures)
        {
            var builder = ImmutableArray.CreateBuilder<RecoveryAction>(Math.Min(actions.Count, MostActions));
            foreach (var (type, delay) in actions.Take(MostActions))
            {
                var kind = type is >= (int)RecoveryActionKind.None and <= (int)RecoveryActionKind.RunCommand
                    ? (RecoveryActionKind)type
                    : RecoveryActionKind.None;
                builder.Add(new RecoveryAction(kind, TimeSpan.FromMilliseconds(delay)));
            }

            TimeSpan? reset = resetPeriodSeconds == NeverReset ? null : TimeSpan.FromSeconds(resetPeriodSeconds);
            return new RecoverySettings(builder.MoveToImmutable(), reset, onNonCrashFailures);
        }

        /// <summary>
        /// What <paramref name="model"/>'s <c>&lt;onfailure&gt;</c> and <c>&lt;resetfailure&gt;</c>
        /// would have the wrapper set, read the way the wrapper reads them. Null when the file
        /// declares no failure actions — the wrapper then leaves the service's own alone, so there
        /// is nothing to compare with — or declares some the wrapper would refuse.
        /// </summary>
        public static RecoverySettings? FromConfig(ServiceConfigModel model)
        {
            if (model.FailureActions.Count == 0)
            {
                return null;
            }

            var builder = ImmutableArray.CreateBuilder<RecoveryAction>(model.FailureActions.Count);
            foreach (var declared in model.FailureActions)
            {
                // The wrapper matches the names exactly and refuses anything else.
                RecoveryActionKind kind;
                switch (declared.Action)
                {
                    case "restart":
                        kind = RecoveryActionKind.Restart;
                        break;
                    case "reboot":
                        kind = RecoveryActionKind.Reboot;
                        break;
                    case "none":
                        kind = RecoveryActionKind.None;
                        break;
                    default:
                        return null;
                }

                // No delay is no wait, as in XmlServiceConfig.FailureActions.
                var delay = TimeSpan.Zero;
                if (!string.IsNullOrWhiteSpace(declared.Delay) && !ServiceConfigModel.TryParseTime(declared.Delay!, out delay))
                {
                    return null;
                }

                builder.Add(new RecoveryAction(kind, delay));
            }

            var reset = WrapperDefaultReset;
            if (!string.IsNullOrWhiteSpace(model.ResetFailureAfter) && !ServiceConfigModel.TryParseTime(model.ResetFailureAfter!, out reset))
            {
                return null;
            }

            return new RecoverySettings(builder.MoveToImmutable(), reset, false);
        }

        /// <summary>
        /// The action that answers failure number <paramref name="failure"/>, counted from 1: the
        /// last action for every failure past it. Null when there are no actions.
        /// </summary>
        public RecoveryAction? ActionFor(int failure) =>
            this.Actions.IsEmpty ? null : this.Actions[Math.Clamp(failure, 1, this.Actions.Length) - 1];

        /// <summary>
        /// The two answer every failure alike: the same action, after the same delay, for the
        /// first failure, the second and every one after, and the same reset period where the count
        /// makes a difference. <see cref="OnNonCrashFailures"/> is left out: a configuration file
        /// cannot say anything about it.
        /// </summary>
        public bool SameEffectAs(RecoverySettings other)
        {
            var mine = this.Effective;
            var theirs = other.Effective;
            if (mine.Length != theirs.Length)
            {
                return false;
            }

            for (int i = 0; i < mine.Length; i++)
            {
                if (!mine[i].SameEffectAs(theirs[i]))
                {
                    return false;
                }
            }

            // The wrapper hands the reset period over in whole seconds, and that is all the
            // service control manager keeps of it.
            return !this.CountMatters || WholeSeconds(this.ResetAfter) == WholeSeconds(other.ResetAfter);

            static long? WholeSeconds(TimeSpan? value) => value is { } span ? (long)span.TotalSeconds : null;
        }

        /// <summary>
        /// A stop with <paramref name="exitCode"/> is one the actions answer: a crash, or with
        /// <see cref="OnNonCrashFailures"/> set, any stop with an exit code other than 0.
        /// </summary>
        /// <remarks>
        /// A service that reports its own stop is not restarted by the actions otherwise, whatever
        /// its code. That is how a wrapper that could not start its program leaves, and it stays
        /// stopped.
        /// </remarks>
        public bool Answers(int exitCode) =>
            exitCode == ProcessAborted || (this.OnNonCrashFailures && exitCode != 0);

        /// <summary>
        /// The latest a restart answering a stop first seen at <paramref name="stoppedAt"/> can be
        /// due, margin included; null when the actions will not restart the service after it.
        /// </summary>
        /// <param name="exitCode">The exit code the service control manager holds for the stop.</param>
        /// <param name="stoppedAt">When the stop was first seen.</param>
        /// <param name="crashCount">
        /// The stops counted in the dashboard's crash window, this one included; see
        /// <see cref="CrashAnnouncer.CountFor"/>. It tells which action comes next, but only from
        /// below: the window starts its count again every five minutes, and the service control
        /// manager's own count goes on until <see cref="ResetAfter"/> — a day unless the file says
        /// otherwise — and includes failures from before the console was opened.
        /// </param>
        /// <remarks>
        /// So the longest restart delay from the counted action onwards is waited out, not the
        /// counted action's own. Waiting too long costs a label that says "restarting" a little
        /// longer: a restart that comes sooner shows as Starting and ends the wait. Waiting too
        /// short is what this is here to prevent — a row that says Stopped while Windows is about
        /// to start the service again. A reset period shorter than the window can bring the service
        /// control manager's count below the window's, and then every action is in play.
        /// </remarks>
        public DateTime? RestartDueBy(int exitCode, DateTime stoppedAt, int crashCount)
        {
            if (this.Actions.IsEmpty || !this.Answers(exitCode))
            {
                return null;
            }

            bool countFromBelow = crashCount >= 1 && (this.ResetAfter is null || this.ResetAfter >= CrashAnnouncer.Window);
            int from = countFromBelow ? Math.Min(crashCount, this.Actions.Length) - 1 : 0;

            TimeSpan? longest = null;
            for (int i = from; i < this.Actions.Length; i++)
            {
                var action = this.Actions[i];
                if (action.Kind == RecoveryActionKind.Restart && (longest is null || action.Delay > longest))
                {
                    longest = action.Delay;
                }
            }

            return longest is { } delay ? stoppedAt + delay + RestartMargin : null;
        }

        /// <summary>
        /// The settings in words, for the dashboard: "restart after 10 s, restart after 1 min, then
        /// restart after 5 min every time; the count resets after 1 h without a failure".
        /// </summary>
        /// <param name="format">Looks a phrase up by key and fills it in: <c>Localizer.Format</c>.</param>
        public string Describe(Func<string, object?[], string> format)
        {
            var steps = this.Effective;
            if (steps.IsEmpty)
            {
                return format("M.Dash.Recovery.Nothing", Array.Empty<object?>());
            }

            var last = steps[steps.Length - 1];
            string text;
            if (steps.Length == 1)
            {
                text = format("M.Dash.Recovery.Every", new object?[] { Step(last) });
            }
            else
            {
                text = Step(steps[0]);
                for (int i = 1; i < steps.Length - 1; i++)
                {
                    text = format("M.Dash.Recovery.Next", new object?[] { text, Step(steps[i]) });
                }

                string tail = last.Kind == RecoveryActionKind.None
                    ? format("M.Dash.Recovery.ThenNothing", Array.Empty<object?>())
                    : format("M.Dash.Recovery.ThenEvery", new object?[] { Step(last) });
                text = format("M.Dash.Recovery.Next", new object?[] { text, tail });
            }

            if (!this.CountMatters)
            {
                return text;
            }

            return this.ResetAfter is { } reset
                ? format("M.Dash.Recovery.Reset", new object?[] { text, Duration(reset) })
                : format("M.Dash.Recovery.NeverReset", new object?[] { text });

            string Step(RecoveryAction action) => action.Kind switch
            {
                RecoveryActionKind.Restart => format("M.Dash.Recovery.Restart", new object?[] { Duration(action.Delay) }),
                RecoveryActionKind.Reboot => format("M.Dash.Recovery.Reboot", new object?[] { Duration(action.Delay) }),
                RecoveryActionKind.RunCommand => format("M.Dash.Recovery.RunCommand", new object?[] { Duration(action.Delay) }),
                _ => format("M.Dash.Recovery.Nothing", Array.Empty<object?>()),
            };

            string Duration(TimeSpan span)
            {
                var (count, unit) = Unit(span);
                return format(unit, new object?[] { count });
            }
        }

        /// <summary>
        /// A span as a count of the largest unit it is a whole number of — "90 s" rather than
        /// "1.5 min", "1 h" rather than "60 min" — with the key of the phrase for that unit.
        /// </summary>
        internal static (long Count, string Key) Unit(TimeSpan span)
        {
            long milliseconds = (long)span.TotalMilliseconds;
            if (milliseconds == 0)
            {
                return (0, "M.Dash.Recovery.Seconds");
            }

            if (milliseconds % 86_400_000 == 0)
            {
                return (milliseconds / 86_400_000, "M.Dash.Recovery.Days");
            }

            if (milliseconds % 3_600_000 == 0)
            {
                return (milliseconds / 3_600_000, "M.Dash.Recovery.Hours");
            }

            if (milliseconds % 60_000 == 0)
            {
                return (milliseconds / 60_000, "M.Dash.Recovery.Minutes");
            }

            return milliseconds % 1_000 == 0
                ? (milliseconds / 1_000, "M.Dash.Recovery.Seconds")
                : (milliseconds, "M.Dash.Recovery.Milliseconds");
        }

        public bool Equals(RecoverySettings? other) =>
            other is not null
            && this.ResetAfter == other.ResetAfter
            && this.OnNonCrashFailures == other.OnNonCrashFailures
            && this.Actions.SequenceEqual(other.Actions);

        public override bool Equals(object? obj) => this.Equals(obj as RecoverySettings);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            hash.Add(this.ResetAfter);
            hash.Add(this.OnNonCrashFailures);
            foreach (var action in this.Actions)
            {
                hash.Add(action);
            }

            return hash.ToHashCode();
        }
    }
}
