using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WinSW.Gui.Model
{
    /// <summary>What the service control manager can do when a service fails.</summary>
    public enum RecoveryKind
    {
        /// <summary>Nothing: the service stays stopped.</summary>
        None,

        /// <summary>Start the service again.</summary>
        Restart,

        /// <summary>Restart the computer.</summary>
        Reboot,
    }

    /// <summary>One failure action: what Windows does, and how long after the failure.</summary>
    public sealed class RecoveryStep
    {
        public RecoveryStep(RecoveryKind kind, TimeSpan delay)
        {
            this.Kind = kind;
            this.Delay = delay;
        }

        public RecoveryKind Kind { get; }

        public TimeSpan Delay { get; }
    }

    /// <summary>
    /// What the service control manager does when a service fails: the failure actions in order,
    /// and how long the service has to go without a failure before the count starts over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first failure gets the first action, the second the second, and every failure after
    /// the list runs out gets the last one again. The rows in the editor never said that last
    /// part, and a single "restart after 10 sec" is exactly what restarts a service that cannot
    /// start some 360 times an hour, for as long as nobody looks.
    /// </para>
    /// <para>
    /// The wording takes its templates through <c>text</c> rather than from the localizer
    /// directly: <c>Localizer.Get</c> in production, the dictionaries read from source in the
    /// tests, which have no WPF application to resolve resources in.
    /// </para>
    /// </remarks>
    public sealed class RecoveryPlan
    {
        /// <summary>The wrapper's default for <c>&lt;resetfailure&gt;</c>.</summary>
        public static readonly TimeSpan DefaultResetAfter = TimeSpan.FromDays(1);

        /// <summary>A last action that restarts sooner than this is warned about.</summary>
        public static readonly TimeSpan QuickRestart = TimeSpan.FromMinutes(1);

        public RecoveryPlan(IReadOnlyList<RecoveryStep> steps, TimeSpan resetAfter, bool writesNone)
        {
            this.Steps = steps;
            this.ResetAfter = resetAfter;
            this.WritesNone = writesNone;
        }

        /// <summary>The failure actions in order; the last one repeats for every further failure.</summary>
        public IReadOnlyList<RecoveryStep> Steps { get; }

        /// <summary>How long without a failure before the count goes back to zero.</summary>
        public TimeSpan ResetAfter { get; }

        /// <summary>
        /// There are no rows, and saving writes <c>&lt;onfailure action="none"/&gt;</c> for them,
        /// which clears the recovery the service has. Without it, no rows is a file that says
        /// nothing, and the wrapper leaves the service's recovery as it finds it.
        /// </summary>
        public bool WritesNone { get; }

        /// <summary>Whether a failure leads to nothing at all: no rows, or <c>none</c> in every one.</summary>
        public bool DoesNothing => this.Steps.All(s => s.Kind == RecoveryKind.None);

        /// <summary>
        /// Whether the action that repeats is a restart sooner than <see cref="QuickRestart"/>:
        /// a service that cannot start is then started again and again, all day.
        /// </summary>
        public bool RestartsInALoop =>
            this.Steps.Count > 0
            && this.Steps[this.Steps.Count - 1] is { Kind: RecoveryKind.Restart } last
            && last.Delay < QuickRestart;

        /// <summary>
        /// About how often, at most, the repeating restart starts a service that fails at once.
        /// An upper bound: the time the program takes to start and fail comes on top of the delay.
        /// </summary>
        public int RestartsPerHour
        {
            get
            {
                if (this.Steps.Count == 0 || this.Steps[this.Steps.Count - 1].Kind != RecoveryKind.Restart)
                {
                    return 0;
                }

                // An immediate restart is still bounded by the program's own start; a second is
                // as short a cycle as it is honest to count.
                double seconds = Math.Max(1, this.Steps[this.Steps.Count - 1].Delay.TotalSeconds);
                return (int)Math.Round(3600 / seconds);
            }
        }

        /// <summary>
        /// Whether the count makes any difference: only when not every failure gets the same.
        /// </summary>
        public bool ResetMatters =>
            this.Steps.Any(s => s.Kind != this.Steps[0].Kind || s.Delay != this.Steps[0].Delay);

        /// <summary>
        /// The plan in one sentence: which failure gets which action, that the last one repeats,
        /// and when the count starts over.
        /// </summary>
        public string Describe(Func<string, string> text)
        {
            if (this.Steps.Count == 0)
            {
                return text(this.WritesNone ? "M.Recovery.Cleared" : "M.Recovery.NotSet");
            }

            if (this.DoesNothing)
            {
                return text("M.Recovery.Nothing");
            }

            string sentence;
            if (this.Steps.Count == 1)
            {
                sentence = Format(text, "M.Recovery.Every", Action(this.Steps[0], text));
            }
            else
            {
                sentence = Format(text, "M.Recovery.First", Action(this.Steps[0], text));
                for (int i = 1; i < this.Steps.Count - 1; i++)
                {
                    sentence = Format(text, "M.Recovery.Join", sentence, Format(text, "M.Recovery.Nth", i + 1, Action(this.Steps[i], text)));
                }

                sentence = Format(text, "M.Recovery.Join", sentence, Format(text, "M.Recovery.Then", Action(this.Steps[this.Steps.Count - 1], text)));
            }

            // Where every failure gets the same, a count going back to zero changes nothing, and
            // saying it would only raise the question of what it is for.
            if (this.ResetMatters)
            {
                sentence = Format(text, "M.Recovery.Join", sentence, Format(text, "M.Recovery.Reset", Duration(this.ResetAfter, text)));
            }

            return Format(text, "M.Recovery.End", sentence);
        }

        /// <summary>
        /// The warning for a repeating restart sooner than a minute, with how often it would come
        /// round; empty when there is nothing to warn about.
        /// </summary>
        public string DescribeWarning(Func<string, string> text) =>
            this.RestartsInALoop ? Format(text, "M.Recovery.QuickLoop", this.RestartsPerHour) : string.Empty;

        /// <summary>
        /// A duration in the largest unit that divides it exactly, which is how it was typed:
        /// "90 sec" stays 90 seconds rather than becoming a minute and a half.
        /// </summary>
        public static string Duration(TimeSpan value, Func<string, string> text)
        {
            long milliseconds = (long)value.TotalMilliseconds;
            if (milliseconds == 0)
            {
                return Format(text, "M.Recovery.Seconds", 0);
            }

            foreach (var (size, one, many) in Units)
            {
                if (milliseconds % size == 0)
                {
                    long count = milliseconds / size;
                    return count == 1 ? text(one) : Format(text, many, count);
                }
            }

            return Format(text, "M.Recovery.Millis", milliseconds);
        }

        private static readonly (long Size, string One, string Many)[] Units =
        {
            (86_400_000L, "M.Recovery.Day", "M.Recovery.Days"),
            (3_600_000L, "M.Recovery.Hour", "M.Recovery.Hours"),
            (60_000L, "M.Recovery.Minute", "M.Recovery.Minutes"),
            (1_000L, "M.Recovery.Second", "M.Recovery.Seconds"),
        };

        private static string Action(RecoveryStep step, Func<string, string> text) => step.Kind switch
        {
            RecoveryKind.Restart => step.Delay > TimeSpan.Zero
                ? Format(text, "M.Recovery.Restart", Duration(step.Delay, text))
                : text("M.Recovery.RestartNow"),
            RecoveryKind.Reboot => step.Delay > TimeSpan.Zero
                ? Format(text, "M.Recovery.Reboot", Duration(step.Delay, text))
                : text("M.Recovery.RebootNow"),
            _ => text("M.Recovery.None"),
        };

        private static string Format(Func<string, string> text, string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, text(key), args);
    }
}
