using System;
using System.Collections.Generic;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Decides which of the console's own failures get a dialog: one at a time, the others
    /// counted, and a failure that keeps coming back on its own shown now and then rather than
    /// every time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A message box runs a message loop of its own, so the two-second status poll carries on
    /// behind it. A failure in the poll used to open a dialog on top of the dialog every two
    /// seconds — in a disconnected tray session for as long as nobody came back — each one a
    /// message loop nested inside the last. Now, while a dialog is open, other failures are
    /// only counted (every one is in <see cref="ErrorLog"/> regardless), and the count is given
    /// once that dialog is dismissed.
    /// </para>
    /// <para>
    /// One at a time is not enough for a failure that returns every two seconds: dismissing it
    /// would only make room for the next. A failure that arrives while a dialog is open got
    /// there without anybody's help — the dialog is modal, so as a rule nobody could have
    /// clicked anything — so it is taken to come from the console's own background work, and for
    /// <see cref="QuietPeriod"/> after the dialog is dismissed its repeats are only counted.
    /// The first time it is shown after that, the count is in its text.
    /// </para>
    /// <para>
    /// The exception is a command's failure. A click can start an operation that fails only
    /// later, while a dialog about something else is open; that failure is counted like the
    /// others, but never kept quiet, so the next click that fails the same way is answered.
    /// </para>
    /// <para>
    /// Failures are reported from whatever thread they happen on, so every member takes a lock.
    /// Times are passed in, so that tests can move them.
    /// </para>
    /// </remarks>
    internal sealed class ErrorDialogGate
    {
        internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(10);

        private readonly object gate = new();

        /// <summary>
        /// Failures being kept quiet, by <see cref="SignatureOf"/>: until when, and how many
        /// times they came back meanwhile. Only failures that once arrived under a dialog are
        /// ever put here, which keeps it small.
        /// </summary>
        private readonly Dictionary<string, (DateTime Until, int Repeats)> quiet = new(StringComparer.Ordinal);

        /// <summary>The failures that arrived while the dialog on screen was open.</summary>
        private readonly HashSet<string> arrived = new(StringComparer.Ordinal);

        private bool open;
        private int folded;

        /// <summary>
        /// Asks to show a dialog for a failure. Null means no: a dialog is already on screen,
        /// or this failure is being kept quiet; it has been counted either way. Otherwise the
        /// dialog is now open, and the number is how many repeats of this failure went unshown
        /// since it was last on screen.
        /// </summary>
        /// <param name="command">
        /// True for the failure of something somebody asked for: counted while another dialog
        /// is open, like any other, but never kept quiet.
        /// </param>
        public int? TryOpen(string signature, DateTime now, bool command = false)
        {
            lock (this.gate)
            {
                if (!command && this.quiet.TryGetValue(signature, out var hush) && now < hush.Until)
                {
                    this.quiet[signature] = (hush.Until, hush.Repeats + 1);
                    return null;
                }

                if (this.open)
                {
                    this.folded++;
                    if (!command)
                    {
                        this.arrived.Add(signature);
                    }

                    return null;
                }

                // Only reached once any quiet has run out: the repeats counted during it are
                // what the dialog now shown has to report.
                int repeats = this.quiet.Remove(signature, out var ended) ? ended.Repeats : 0;
                this.open = true;
                return repeats;
            }
        }

        /// <summary>
        /// Asks to show the notice that follows a dismissed dialog. False when something else
        /// took the screen first; the notice is then simply not shown.
        /// </summary>
        public bool TryOpenNotice()
        {
            lock (this.gate)
            {
                if (this.open)
                {
                    return false;
                }

                this.open = true;
                return true;
            }
        }

        /// <summary>
        /// The dialog on screen was dismissed. Returns how many failures arrived while it was
        /// open, and keeps each of them quiet from now for <see cref="QuietPeriod"/>.
        /// </summary>
        public int Close(DateTime now)
        {
            lock (this.gate)
            {
                foreach (string signature in this.arrived)
                {
                    // A failure that ran out of quiet and came straight back keeps the repeats
                    // it had not reported yet.
                    int repeats = this.quiet.TryGetValue(signature, out var earlier) ? earlier.Repeats : 0;
                    this.quiet[signature] = (now + QuietPeriod, repeats);
                }

                int count = this.folded;
                this.arrived.Clear();
                this.folded = 0;
                this.open = false;
                return count;
            }
        }

        /// <summary>
        /// What makes two failures the same one: the type of the exception at the bottom of the
        /// chain and where it was thrown. Not the message, which often carries a path or a
        /// process ID that differs each time while the fault behind it does not.
        /// </summary>
        internal static string SignatureOf(Exception? exception)
        {
            if (exception is null)
            {
                return string.Empty;
            }

            var root = exception.GetBaseException();
            return root.GetType().FullName + Environment.NewLine + (root.StackTrace ?? root.Message);
        }
    }
}
