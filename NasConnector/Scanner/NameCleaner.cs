using System.Text.RegularExpressions;

namespace NasConnector
{
    // Turns a raw folder name into a clean, human-readable game title so the
    // Playnite grid looks good AND Playnite's metadata search can find a cover.
    // Example: "Cyberpunk.2077.v2.1" -> "Cyberpunk 2077".
    //
    // Deliberately conservative: if cleaning would leave nothing meaningful, the
    // original folder name is returned so a game is never shown with an empty title.
    public static class NameCleaner
    {
        // Bracketed groups: [DX11], (2024), {v1.2}, etc.
        private static readonly Regex BracketGroups = new Regex(
            @"[\[\(\{][^\]\)\}]*[\]\)\}]", RegexOptions.Compiled);

        // Version / build / update tokens and everything after them: v1.2.3 (now "v1 2 3"
        // after separator normalization), Build 1234, Update 5, r1234.
        private static readonly Regex VersionTail = new Regex(
            @"\b(v\d+|build\s*\d+|update\s*\d+|r\d{3,})\b.*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MultiSpace = new Regex(@"\s{2,}", RegexOptions.Compiled);

        public static string Clean(string rawFolderName)
        {
            if (string.IsNullOrWhiteSpace(rawFolderName))
                return rawFolderName;

            // Drop bracketed groups first (they often contain the noisiest tokens),
            // then normalize separators so the word-boundary matching below is reliable.
            var name = BracketGroups.Replace(rawFolderName.Trim(), " ");
            name = name.Replace('_', ' ').Replace('.', ' ');

            // Cut any version/build/update tail.
            name = VersionTail.Replace(name, string.Empty);

            name = MultiSpace.Replace(name, " ").Trim(' ', '-', '_', '.');

            // If we stripped everything, fall back to the original so the title is never blank.
            return string.IsNullOrWhiteSpace(name) ? rawFolderName.Trim() : name;
        }
    }
}
