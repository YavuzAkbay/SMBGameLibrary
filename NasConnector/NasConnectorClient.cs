using Playnite.SDK;

namespace NasConnector
{
    public class NasConnectorClient : LibraryClient
    {
        public override bool IsInstalled => true;

        public override void Open()
        {
            // No client application to open for a NAS
        }
    }
}
