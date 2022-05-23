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
    class QueueManager
    {
        private ApplcConfig ac = new ApplcConfig ();

        private int timeoutCount = 0, downloadedCount = 0;

        private String GetNextHashId ()
        {
            var client = new MongoClient (ac.DB_URL);
            var database = client.GetDatabase (ac.DB_NAME);
            var collection = database.GetCollection<MTorrent> (ac.DB_COLLECTION_NAME);

            Expression<Func<MTorrent, bool>> filter = m => (
                m.Processed == false &&
                m.CountSeen > 1
            );

            var update = Builders<MTorrent>.Update
            .Set (m => m.Processed, true)
            .Set (m => m.ProcessedTime, DateTime.UtcNow);

            var options = new FindOneAndUpdateOptions<MTorrent, MTorrent> {
                IsUpsert = false,
                ReturnDocument = ReturnDocument.After
            };

            var mTorrent = collection.FindOneAndUpdate (filter, update, options);

            Console.WriteLine ("GetNextHashId()  Found Torrent {0}, countSeen {2}, processedTime {1}",
                mTorrent.Id,
                mTorrent.ProcessedTime,
                mTorrent.CountSeen
                );

            return mTorrent.Id;
        }

        private void UpdateHashId (MTorrent mTorrentU)
        {
            var client = new MongoClient (ac.DB_URL);
            var database = client.GetDatabase (ac.DB_NAME);
            var collection = database.GetCollection<MTorrent> (ac.DB_COLLECTION_NAME);

            Expression<Func<MTorrent, bool>> filter = m => (m.Id == mTorrentU.Id);

            var update = Builders<MTorrent>.Update
                .Set (m => m.Processed, true)
                .Set (m => m.Downloaded, !mTorrentU.Timeout)
                .Set (m => m.DownloadedTime, DateTime.UtcNow)
                .Set (m => m.Name, mTorrentU.Name)
                .Set (m => m.Comment, mTorrentU.Comment)
                .Set (m => m.Length, mTorrentU.Length)
                .Set (m => m.Timeout, mTorrentU.Timeout);

            var options = new FindOneAndUpdateOptions<MTorrent, MTorrent> {
                IsUpsert = false,
                ReturnDocument = ReturnDocument.After
            };

            var mTorrentR = collection.FindOneAndUpdate (filter, update, options);

            //Console.WriteLine ("UpdateHashId()   Torrent {0}, processedTime {1}, downloadedTime {2}, timeout {3}",
            //    mTorrentR.Id,
            //    mTorrentR.ProcessedTime,
            //    mTorrentR.DownloadedTime,
            //    mTorrentR.Timeout);
        }

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

                    UpdateHashId (
                        new MTorrent () {
                            Id = magnetLink.InfoHashes.V1.ToHex ().ToLower (),
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
                    var hash = GetNextHashId ();

                    DownloadAsync (hash, engine, token);
                } else {
                    // FIFO logic, just remove the oldest
                    var torrent = engine.Torrents.First ();
                    Console.WriteLine ($"MainLoop()       Removing torrent {Red (torrent.InfoHashes.V1.ToHex ().ToLower ())}");

                    UpdateHashId (
                        new MTorrent () {
                            Id = torrent.InfoHashes.V1.ToHex ().ToLower (),
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
