namespace NasConnector
{
    public class NasGameEntry
    {
        public string GameId { get; set; }
        public string DisplayName { get; set; }
        public NasGameType GameType { get; set; }
        public string NasFolderPath { get; set; }

        // For SingleArchive: full path to the archive file inside the game subfolder
        public string NasArchivePath { get; set; }
    }
}
