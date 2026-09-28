namespace WinSW.Gui.Services
{
    /// <summary>One line of a log as a list shows it, with its severity worked out once.</summary>
    /// <remarks>
    /// <para>
    /// A class, and on purpose without an <c>Equals</c> of its own: every line is its own item
    /// however often its text comes back. A list of strings finds an item by its text, so
    /// scrolling to the newest of a restart loop's identical lines — uvicorn's carry no
    /// timestamp, and every cycle writes them again — scrolled to the first cycle's copy, and
    /// Follow sat on the first failure while the loop went on below it. Selection had the same
    /// trouble. A record would bring the text comparison back.
    /// </para>
    /// <para>
    /// The severity is kept rather than asked for: the list's colouring asked for it every time
    /// a row came into view, and the error count and "next error" again for every line in the
    /// buffer.
    /// </para>
    /// </remarks>
    public sealed class LogLine
    {
        public LogLine(string text)
        {
            this.Text = text;
            this.IsError = LogSeverity.IsError(text);
            this.IsWarning = !this.IsError && LogSeverity.IsWarning(text);
        }

        public string Text { get; }

        /// <summary>The line looks like an error; see <see cref="LogSeverity.IsError"/>.</summary>
        public bool IsError { get; }

        /// <summary>
        /// The line looks like a warning and not like an error, so that at most one of the two
        /// holds and a line is coloured one way whatever order the colours are tried in.
        /// </summary>
        public bool IsWarning { get; }

        /// <summary>The text: what the list's keyboard search and screen readers go by.</summary>
        public override string ToString() => this.Text;
    }
}
