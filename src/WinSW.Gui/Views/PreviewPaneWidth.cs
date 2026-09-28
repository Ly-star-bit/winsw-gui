using System;

namespace WinSW.Gui.Views
{
    /// <summary>
    /// How wide the editor's XML preview is: nothing while it is hidden, and the width it was
    /// dragged to when it comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preview's column has a width of its own, and collapsing what is in a column does not
    /// shrink it: unticking the preview left the form exactly as narrow as before, beside 420 px
    /// of nothing. The column now goes to zero while the preview is hidden, and this remembers
    /// the width it had so that ticking it again gives back the width the splitter left.
    /// </para>
    /// <para>
    /// Plain numbers rather than <c>GridLength</c>, so the rules can be tested without WPF.
    /// </para>
    /// </remarks>
    internal sealed class PreviewPaneWidth
    {
        /// <summary>The preview's width until the splitter moves it; the column's width in the XAML.</summary>
        public const double DefaultWidth = 420;

        /// <summary>
        /// Narrower than this, the preview is not worth bringing back at that width. The splitter
        /// can drag the column to nothing, and restoring nothing would make ticking the box look
        /// as if it did not work.
        /// </summary>
        public const double MinimumUsefulWidth = 160;

        private double kept = DefaultWidth;

        /// <summary>
        /// The most the preview can take and still leave the form its minimum width.
        /// </summary>
        /// <remarks>
        /// A fixed-width column does not give way when the window narrows: at 1024 px with the
        /// navigation rail open, the page has about 736 px, and a 420 px form beside a 420 px
        /// preview ran 120 px past the edge, taking the preview's buttons with it. Applied as
        /// the column's maximum rather than as its width, so the dragged width comes back when
        /// the window is wide again.
        /// </remarks>
        /// <param name="bodyWidth">The width the form, the splitter and the preview share.</param>
        /// <param name="formMinimum">The form column's minimum width.</param>
        /// <param name="splitterWidth">The splitter's width with its margins.</param>
        public static double MaximumBeside(double bodyWidth, double formMinimum, double splitterWidth) =>
            Math.Max(0, bodyWidth - formMinimum - splitterWidth);

        /// <summary>
        /// Called as the preview is hidden, with the width its column has; returns the width
        /// the column takes while it is hidden.
        /// </summary>
        /// <remarks>
        /// A column that is already at nothing (hidden twice over, or dragged shut) leaves the
        /// width kept before in place: remembering zero would lose the one worth going back to.
        /// </remarks>
        public double Hide(double current)
        {
            if (current > 0)
            {
                this.kept = current;
            }

            return 0;
        }

        /// <summary>
        /// Called as the preview is shown, with the width its column has; returns the width to
        /// give it. A column that is showing already keeps the width it has.
        /// </summary>
        public double Show(double current)
        {
            if (current > 0)
            {
                return current;
            }

            return this.kept >= MinimumUsefulWidth ? this.kept : DefaultWidth;
        }
    }
}
