using System;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// What the dashboard's "upgrade wrapper" question says about the .NET Framework the bundled
    /// wrapper needs; see <see cref="NetFramework"/>.
    /// </summary>
    internal static class UpgradeFramework
    {
        /// <summary>Upstream's name for the .NET Framework build, as <see cref="WrapperKind.ReleaseAssetFor"/> reports it.</summary>
        internal const string FrameworkAsset = "WinSW-net461.exe";

        /// <summary>
        /// The note the question carries, or null for none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On a machine known to be older than 4.6.2 the finding itself, whatever the service runs
        /// now: the bundled wrapper is the net462 build, which fails there at the first start, and
        /// a service on upstream's net461 build is no better off after the swap than one on a
        /// self-contained build. It says which version the machine has and which installer puts
        /// it right, in place of a note that only said the dependency exists.
        /// </para>
        /// <para>
        /// A machine known to have 4.6.2 or later runs it, and is told nothing. One whose version
        /// could not be read keeps the general note for a service moving off a self-contained
        /// build, the only one that gains the dependency: nothing is known either way, and the
        /// check must never be what stops an upgrade.
        /// </para>
        /// </remarks>
        /// <param name="currentAsset">What the service runs now, as <see cref="WrapperKind.ReleaseAssetFor"/> names it; null when that could not be told.</param>
        /// <param name="framework">This machine's .NET Framework; <see cref="NetFramework.Installed"/>.</param>
        /// <param name="format">Looks a phrase up by key and fills it in: <c>Localizer.Format</c>.</param>
        internal static string? Note(string? currentAsset, NetFrameworkInfo framework, Func<string, object?[], string> format)
        {
            if (framework.TooOldForWrapper)
            {
                return format("M.Dash.UpgradeFrameworkTooOld", new object?[] { framework.Version, NetFramework.OfflineInstaller });
            }

            if (!framework.IsKnown && currentAsset is { } asset && asset != FrameworkAsset)
            {
                return format("M.Dash.UpgradeFrameworkBuild", Array.Empty<object?>());
            }

            return null;
        }
    }
}
