using System;

namespace WinSW.Gui.Views
{
    /// <summary>
    /// How tall the editor's try-run panel is: its full height where the page has room, less
    /// where it does not, so the form above it keeps a share of the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel was a fixed 252 px, about 344 px with its header, and the form and the panel
    /// share one column with no scrolling between them. At 1024×768 the page body is about
    /// 530 px, so a running try run left the form under 200 px, and at 1280×800 with 125 %
    /// scaling next to nothing. The panel now takes at most a share of the body.
    /// </para>
    /// <para>
    /// Plain numbers, like <see cref="PreviewPaneWidth"/>, so the rule can be tested without WPF.
    /// </para>
    /// </remarks>
    internal static class TrialPanelHeight
    {
        /// <summary>The panel's height where the page has room for it; what it always was.</summary>
        public const double Full = 252;

        /// <summary>Below this the buttons and a few lines of output no longer fit.</summary>
        public const double Minimum = 150;

        /// <summary>The share of the page body the panel may take.</summary>
        public const double ShareOfBody = 0.4;

        /// <summary>The panel's height for a page body of <paramref name="bodyHeight"/>.</summary>
        public static double For(double bodyHeight) =>
            double.IsNaN(bodyHeight) || bodyHeight <= 0
                ? Full
                : Math.Min(Full, Math.Max(Minimum, bodyHeight * ShareOfBody));
    }
}
