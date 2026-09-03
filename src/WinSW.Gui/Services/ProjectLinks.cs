namespace WinSW.Gui.Services
{
    /// <summary>
    /// Where this application sends people for documentation and code.
    /// </summary>
    /// <remarks>
    /// This repository, not <c>winsw/winsw</c>. It is the branch the bundled wrapper is built
    /// from and is ahead of the last upstream release; it carries documents that do not exist
    /// upstream at all — this console's own page and the XML cheat sheet — so links into the
    /// upstream repository for those return 404.
    /// </remarks>
    public static class ProjectLinks
    {
        public const string Repository = "https://github.com/Ly-star-bit/winsw-gui";

        public const string Issues = Repository + "/issues";

        public const string DocsBase = Repository + "/blob/main/docs/";

        public const string XmlConfig = DocsBase + "xml-config-file.md";

        public static string Doc(string fileName, string anchor = "") =>
            DocsBase + fileName + (anchor.Length > 0 ? "#" + anchor : string.Empty);
    }
}
