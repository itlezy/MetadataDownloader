using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MonoTorrent;
using MonoTorrent.Client;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Attributes;
using System.Linq.Expressions;

using static Crayon.Output;

namespace MetadataDownloader
{
    class Program
    {
        static void Main (string[] args)
        {
            CancellationTokenSource cancellation = new CancellationTokenSource ();

            var task = new QueueManager ().MainLoop (cancellation.Token);

            // We need to cleanup correctly when the user closes the window by using ctrl-c
            // or an unhandled exception happens

            Console.CancelKeyPress += delegate { cancellation.Cancel (); task.Wait (); };
            AppDomain.CurrentDomain.ProcessExit += delegate { cancellation.Cancel (); task.Wait (); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine (e.ExceptionObject); cancellation.Cancel (); task.Wait (); };
            Thread.GetDomain ().UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine (e.ExceptionObject); cancellation.Cancel (); task.Wait (); };

            task.Wait ();
        }

        public class MTorrent
        {
            [BsonElement ("_id")]
            public String Id { get; set; }

            [BsonElement ("name")]
            public string Name { get; set; }

            [BsonElement ("comment")]
            public string Comment { get; set; }

            [BsonElement ("length")]
            public long Length { get; set; }

            [BsonElement ("processed")]
            [BsonRepresentation (BsonType.Boolean)]
            public bool Processed { get; set; }

            [BsonElement ("processedTime")]
            [BsonRepresentation (BsonType.DateTime)]
            public DateTime ProcessedTime { get; set; }

            [BsonElement ("timeout")]
            [BsonRepresentation (BsonType.Boolean)]
            public bool Timeout { get; set; }

            [BsonElement ("downloaded")]
            [BsonRepresentation (BsonType.Boolean)]
            public bool Downloaded { get; set; }

            [BsonElement ("downloadedTime")]
            [BsonRepresentation (BsonType.DateTime)]
            public DateTime DownloadedTime { get; set; }

        }

        class QueueManager
        {
            // The timeout will be determined by TORRENT_PARALLEL_LIMIT * MAIN_LOOP_INTERVAL as torrents get removed on a FIFO logic basis
            private const int MAIN_LOOP_INTERVAL = 1333;
            private const int TORRENT_PARALLEL_LIMIT = 123;
            private const int TORRENT_STOP_TIMEOUT = 3; // seconds
            private const string TORRENT_OUTPUT_PATH = @"c:\tmp_out\";
            private const string DB_URL = "mongodb://127.0.0.1:27017/tor";
            private const string DB_NAME = "tor";
            private const string DB_COLLECTION_NAME = "torrsv1";
            private const string TMP_SAVE_DIR = @".\tmp_dld\";
            private const string MAGNET_PREFIX = "magnet:?xt=urn:btih:";

            private int timeoutCount = 0, downloadedCount = 0;

            private String GetNextHashId ()
            {
                var client = new MongoClient (DB_URL);
                var database = client.GetDatabase (DB_NAME);
                var collection = database.GetCollection<MTorrent> (DB_COLLECTION_NAME);

                Expression<Func<MTorrent, bool>> filter = m => (m.Processed == false);

                var update = Builders<MTorrent>.Update
                .Set (m => m.Processed, true)
                .Set (m => m.ProcessedTime, DateTime.UtcNow);

                var options = new FindOneAndUpdateOptions<MTorrent, MTorrent> {
                    IsUpsert = false,
                    ReturnDocument = ReturnDocument.After
                };

                var mTorrent = collection.FindOneAndUpdate (filter, update, options);

                //Console.WriteLine ("GetNextHashId()  Found Torrent {0}, processedTime {1}",
                //    mTorrent.Id,
                //    mTorrent.ProcessedTime);

                return mTorrent.Id;
            }

            private void UpdateHashId (MTorrent mTorrentU)
            {
                var client = new MongoClient (DB_URL);
                var database = client.GetDatabase (DB_NAME);
                var collection = database.GetCollection<MTorrent> (DB_COLLECTION_NAME);

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
                if (MagnetLink.TryParse (MAGNET_PREFIX + hash, out MagnetLink magnetLink)) {

                    var manager = await engine.AddAsync (magnetLink, TMP_SAVE_DIR);

                    Console.WriteLine (
                        $"DownloadAsync()  Adding   torrent {Green (magnetLink.InfoHashes.V1.ToHex ().ToLower ())}");

                    await manager.StartAsync ();
                    await manager.WaitForMetadataAsync (token);

                    if (manager.HasMetadata && manager.Files.Count > 0) {
                        Console.WriteLine (
                            $"DownloadAsync()  Metadata Received {Green (magnetLink.InfoHashes.V1.ToHex ().ToLower ())} * - [ {Magenta (manager.Torrent.Name)} ] - *");

                        //manager.Files.OrderByDescending (t => t.Length).First ().FullPath);

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
                        File.Copy (manager.MetadataPath, TORRENT_OUTPUT_PATH + manager.Torrent.Name + ".torrent");
                    } catch (Exception ex) {
                        Console.Error.WriteLine (ex.Message);
                    }

                    await manager.StopAsync (new TimeSpan (0, 0, TORRENT_STOP_TIMEOUT));

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
                    await Task.Delay (MAIN_LOOP_INTERVAL);

                    Console.WriteLine ("MainLoop()       Checking for torrents count {0} / {1} - Dowloaded {2}, Timedout {3} - DHT nodes {4}",
                        engine.Torrents.Count,
                        TORRENT_PARALLEL_LIMIT - 1,
                        downloadedCount,
                        timeoutCount,
                        engine.Dht.NodeCount
                        );

                    if (engine.Torrents.Count < TORRENT_PARALLEL_LIMIT) {
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

                        await torrent.StopAsync (new TimeSpan (0, 0, TORRENT_STOP_TIMEOUT));

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
}
