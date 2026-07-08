using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace NasConnector
{
    public class NasConnectorPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("7f3a9d12-4b8e-4c21-a5f6-9e0b1c2d3e4f");
        public override string Name => "SMB Game Library";

        private readonly NasConnectorSettingsViewModel settingsVm;

        // Cache last scan so GetInstallActions can resolve entries without re-scanning
        private readonly Dictionary<string, NasGameEntry> scanCache =
            new Dictionary<string, NasGameEntry>(StringComparer.OrdinalIgnoreCase);

        public NasConnectorPlugin(IPlayniteAPI api) : base(api)
        {
            settingsVm = new NasConnectorSettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true };
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var s = settingsVm.Settings;
            var scanner = new NasLibraryScanner(s);
            List<NasGameEntry> entries;

            try
            {
                entries = scanner.ScanGames(args.CancelToken);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "NAS scan failed");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "nas-scan-error",
                    $"SMB Game Library: Could not reach {s.NasBasePath} — {ex.Message}",
                    NotificationType.Error));
                yield break;
            }

            lock (scanCache)
            {
                scanCache.Clear();
                foreach (var e in entries)
                    scanCache[e.GameId] = e;
            }

            // Games already installed via this plugin — skip them so Playnite doesn't reset
            // their IsInstalled flag to false on the next library update.
            var installedByUs = new HashSet<string>(
                PlayniteApi.Database.Games
                    .Where(g => g.PluginId == Id && g.IsInstalled)
                    .Select(g => g.GameId),
                StringComparer.OrdinalIgnoreCase);

            // Build a set of game names already installed from OTHER libraries so we don't
            // show a duplicate "not installed" NAS entry alongside an already-installed game.
            var installedElsewhere = new HashSet<string>(
                PlayniteApi.Database.Games
                    .Where(g => g.IsInstalled && g.PluginId != Id)
                    .Select(g => g.Name),
                StringComparer.OrdinalIgnoreCase);

            var excluded = new HashSet<string>(
                (s.ExcludedFolders ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            // Remove stale NAS entries: anything in the DB from this plugin that is
            // (a) no longer found on the NAS at all, OR (b) now a duplicate of an installed game,
            // OR (c) explicitly excluded by the user.
            // Never touch installed NAS games (user already installed them locally).
            var currentScanIds = new HashSet<string>(entries.Select(e => e.GameId));
            var toRemove = PlayniteApi.Database.Games
                .Where(g => g.PluginId == Id && !g.IsInstalled
                            && (!currentScanIds.Contains(g.GameId)
                                || installedElsewhere.Contains(g.Name)
                                || excluded.Contains(g.Name)))
                .ToList();
            foreach (var stale in toRemove)
                PlayniteApi.Database.Games.Remove(stale);

            foreach (var entry in entries)
            {
                // Scanner only returns recognized types (it yields null for anything it can't
                // classify), but guard against Unknown defensively.
                if (entry.GameType == NasGameType.Unknown)
                    continue;

                // Already installed by us — don't re-yield or Playnite resets IsInstalled to false
                if (installedByUs.Contains(entry.GameId))
                    continue;

                // Skip if an installed copy already exists in another library
                if (installedElsewhere.Contains(entry.DisplayName))
                    continue;

                if (excluded.Contains(entry.DisplayName))
                    continue;

                var meta = new GameMetadata
                {
                    GameId = entry.GameId,
                    Name = entry.DisplayName,
                    IsInstalled = false,
                    Tags = new HashSet<MetadataProperty>
                    {
                        new MetadataNameProperty("NAS")
                    }
                };

                yield return meta;
            }
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
                yield break;

            // Return instantly. Don't re-scan here — a cache miss (e.g. Playnite just
            // started) would block the UI thread on the SMB connect with no feedback.
            // Resolve the entry lazily via this delegate, which the controller calls
            // INSIDE its own cancellable progress dialog. See NasInstallController.
            NasGameEntry cached;
            lock (scanCache)
                scanCache.TryGetValue(args.Game.GameId, out cached);

            var gameId = args.Game.GameId;
            Func<System.Threading.CancellationToken, NasGameEntry> resolve =
                ct => ResolveEntry(gameId, ct);

            yield return new NasInstallController(
                args.Game, cached, resolve, settingsVm.Settings, PlayniteApi);
        }

        // Looks up a scan entry by game id, re-scanning the share (populating the cache)
        // only on a miss. Runs on the install worker thread inside a progress dialog, so
        // the SMB connect + walk it may trigger are cancellable and show status.
        private NasGameEntry ResolveEntry(string gameId, System.Threading.CancellationToken cancelToken)
        {
            NasGameEntry entry;
            lock (scanCache)
            {
                if (scanCache.TryGetValue(gameId, out entry))
                    return entry;
            }

            var scanner = new NasLibraryScanner(settingsVm.Settings);
            var entries = scanner.ScanGames(cancelToken);
            lock (scanCache)
            {
                foreach (var e in entries)
                    scanCache[e.GameId] = e;
                scanCache.TryGetValue(gameId, out entry);
            }
            return entry;
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
                yield break;

            yield return new NasUninstallController(args.Game, PlayniteApi);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsVm;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new NasConnectorSettingsView { DataContext = settingsVm };
        }
    }
}
