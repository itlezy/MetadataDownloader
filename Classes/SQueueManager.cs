using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using MongoDB.Driver;

using MonoTorrent;
using MonoTorrent.Client;

using static Crayon.Output;

namespace MetadataDownloader
{
    class SQueueManager
    {
        private ApplcConfig ac = new ApplcConfig ();
        private DAO dao = new DAO ();
        private int timeoutCount = 0, downloadedCount = 0;

        public async Task DownloadAsync (
            String hash,
            ClientEngine engine,
            CancellationToken token)
        {
            if (MagnetLink.TryParse (ac.MAGNET_PREFIX + hash, out MagnetLink magnetLink)) {

                var manager = await engine.AddAsync (magnetLink, ac.TMP_SAVE_DIR);

                Console.WriteLine (
                    $"DownloadAsync()  Adding   torrent {Green (magnetLink.InfoHashes.V1.ToHex ().ToLower ())}");

                await manager.StartAsync ();
                await manager.WaitForMetadataAsync (token);

                if (manager.HasMetadata && manager.Files.Count > 0) {
                    Console.WriteLine (
                        $"DownloadAsync()  Metadata Received {Green (magnetLink.InfoHashes.V1.ToHex ().ToLower ())} - * [ {Magenta (manager.Torrent.Name)} ] * -");

                    //manager.Files.OrderByDescending (t => t.Length).First ().FullPath

                    dao.UpdateHashId (
                        new MTorr () {
                            HashId = magnetLink.InfoHashes.V1.ToHex ().ToLower (),
                            Name = manager.Torrent.Name,
                            Length = manager.Torrent.Size,
                            Comment = manager.Torrent.Comment,
                            Timeout = false
                        });

                    downloadedCount++;
                }

                try {
                    File.Copy (manager.MetadataPath, ac.TORRENT_OUTPUT_PATH + manager.Torrent.Name + ".torrent");
                } catch (Exception ex) {
                    Console.Error.WriteLine (ex.Message);
                }

                await manager.StopAsync (new TimeSpan (0, 0, ac.TORRENT_STOP_TIMEOUT));

                try {
                    await engine.RemoveAsync (manager.Torrent, RemoveMode.CacheDataAndDownloadedData);
                } catch (Exception ex) {
                    Console.Error.WriteLine (ex.Message);
                }
            }
        }

        public async Task MainLoop (CancellationToken token)
        {
            // Give an example of how settings can be modified for the engine.
            var settingBuilder = new EngineSettingsBuilder {
                // Allow the engine to automatically forward ports using upnp/nat-pmp (if a compatible router is available)
                AllowPortForwarding = true,

                // Automatically save a cache of the DHT table when all torrents are stopped.
                AutoSaveLoadDhtCache = true,

                // If a MagnetLink is used to download a torrent, the engine will try to load a copy of the metadata
                // it's cache directory. Otherwise the metadata will be downloaded and stored in the cache directory
                // so it can be reloaded later.
                AutoSaveLoadMagnetLinkMetadata = true,

                // Use a fixed port to accept incoming connections from other peers for testing purposes. Production usages should use a random port, 0, if possible.
                ListenEndPoint = new IPEndPoint (IPAddress.Any, 55123),

                // Use a fixed port for DHT communications for testing purposes. Production usages should use a random port, 0, if possible.
                DhtEndPoint = new IPEndPoint (IPAddress.Any, 55123),
            };

            using var engine = new ClientEngine (settingBuilder.ToSettings ());

            if (engine.Settings.AllowPortForwarding)
                Console.WriteLine ("MainLoop()       uPnP or NAT-PMP port mappings will be created for any ports needed by MonoTorrent");

            while (true) {
                await Task.Delay (ac.MAIN_LOOP_INTERVAL);

                Console.WriteLine ("MainLoop()       Checking for torrents count {0} / {1} - Dowloaded {2}, Timedout {3} - DHT nodes {4}",
                    engine.Torrents.Count,
                    ac.TORRENT_PARALLEL_LIMIT - 1,
                    downloadedCount,
                    timeoutCount,
                    engine.Dht.NodeCount
                    );

                if (engine.Torrents.Count < ac.TORRENT_PARALLEL_LIMIT) {
                    var hash = dao.GetNextHashId ();

                    DownloadAsync (hash, engine, token);
                } else {
                    // FIFO logic, just remove the oldest
                    var torrent = engine.Torrents.First ();
                    Console.WriteLine ($"MainLoop()       Removing torrent {Red (torrent.InfoHashes.V1.ToHex ().ToLower ())}");

                    dao.UpdateHashId (
                       new MTorr () {
                           HashId = torrent.InfoHashes.V1.ToHex ().ToLower (),
                           Name = null,
                           Length = 0,
                           Comment = null,
                           Timeout = true
                       });

                    timeoutCount++;

                    await torrent.StopAsync (new TimeSpan (0, 0, ac.TORRENT_STOP_TIMEOUT));

                    try {
                        await engine.RemoveAsync (torrent, RemoveMode.CacheDataAndDownloadedData);
                    } catch (Exception ex) {
                        Console.Error.WriteLine (ex.Message);
                    }
                }

                if (token.IsCancellationRequested) {
                    await engine.StopAllAsync ();

                    if (engine.Settings.AllowPortForwarding)
                        Console.WriteLine ("MainLoop()       uPnP and NAT-PMP port mappings have been removed");

                    Console.WriteLine ("MainLoop()       Cancellation request received, exiting..");

                    break;
                }
            }
        }
    }
}
