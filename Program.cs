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
            private const int MAIN_LOOP_INTERVAL = 3000;
            private const int TORRENT_PARALLEL_LIMIT = 33;

            private String GetNextHashId ()
            {
                var client = new MongoClient ("mongodb://127.0.0.1:27017/tor");
                var database = client.GetDatabase ("tor");
                var collection = database.GetCollection<MTorrent> ("torrsv1");
                //var mTorrent = collection.Find (x => x.Processed == false).FirstOrDefault ();

                Expression<Func<MTorrent, bool>> filter = m => (m.Processed == false);

                var update = Builders<MTorrent>.Update
                .Set (m => m.Processed, true)
                .Set (m => m.ProcessedTime, DateTime.UtcNow);

                var options = new FindOneAndUpdateOptions<MTorrent, MTorrent> {
                    IsUpsert = false,
                    ReturnDocument = ReturnDocument.After
                };

                var mTorrent = collection.FindOneAndUpdate (filter, update, options);

                Console.WriteLine ("GetNextHashId() Found torr {0}, processedTime {1}",
                    mTorrent.Id,
                    mTorrent.ProcessedTime);

                return mTorrent.Id;
            }

            private String UpdateHashId (
                String hashId,
                String torrentName,
                long torrentLength,
                String torrentComment,
                bool timeout
                )
            {
                var client = new MongoClient ("mongodb://127.0.0.1:27017/tor");
                var database = client.GetDatabase ("tor");
                var collection = database.GetCollection<MTorrent> ("torrsv1");
                //var mTorrent = collection.Find (x => x.Processed == false).FirstOrDefault ();

                Expression<Func<MTorrent, bool>> filter = m => (m.Id == hashId);

                var update = Builders<MTorrent>.Update
                    .Set (m => m.Processed, true)
                    .Set (m => m.Downloaded, !timeout)
                    .Set (m => m.DownloadedTime, DateTime.UtcNow)
                    .Set (m => m.Name, torrentName)
                    .Set (m => m.Comment, torrentComment)
                    .Set (m => m.Length, torrentLength)
                    .Set (m => m.Timeout, timeout);

                var options = new FindOneAndUpdateOptions<MTorrent, MTorrent> {
                    IsUpsert = false,
                    ReturnDocument = ReturnDocument.After
                };

                var mTorrent = collection.FindOneAndUpdate (filter, update, options);

                Console.WriteLine ("UpdateHashId() Torrent {0}, processedTime {1}, downloadedTime {2}, timeout {3}",
                    mTorrent.Id,
                    mTorrent.ProcessedTime,
                    mTorrent.DownloadedTime,
                    timeout);

                return mTorrent.Id;
            }

            public async Task DownloadAsync (
                String hash,
                ClientEngine engine,
                CancellationToken token)
            {
                if (MagnetLink.TryParse ("magnet:?xt=urn:btih:" + hash, out MagnetLink link)) {

                    var manager = await engine.AddAsync (link, ".");

                    Console.WriteLine ("DownloadAsync() Adding torrent {0}", link.InfoHashes.V1.ToHex ().ToLower ());

                    await manager.StartAsync ();
                    await manager.WaitForMetadataAsync (token);

                    if (manager.HasMetadata && manager.Files.Count > 0) {
                        Console.WriteLine ("DownloadAsync() Metadata Received {0}\n\t{1}\n\t{2}\n\t{3}\n",
                            link.InfoHashes.V1.ToHex ().ToLower (),
                            manager.MetadataPath,
                            manager.Torrent.Name,
                            manager.Files.OrderByDescending (t => t.Length).First ().FullPath);

                        UpdateHashId (
                            link.InfoHashes.V1.ToHex ().ToLower (),
                            manager.Torrent.Name,
                            manager.Torrent.Size,
                            manager.Torrent.Comment,
                            false);
                    }

                    try {
                        File.Copy (manager.MetadataPath, @"c:\tmp_out\" + manager.Torrent.Name + ".torrent");
                    } catch (Exception ex) {
                        Console.Error.WriteLine (ex.Message);
                    }

                    await engine.RemoveAsync (link);
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
                    Console.WriteLine ("MainLoop() uPnP or NAT-PMP port mappings will be created for any ports needed by MonoTorrent");

                while (true) {
                    Console.WriteLine ("MainLoop() Checking for torrents count {0} / {1}", engine.Torrents.Count, TORRENT_PARALLEL_LIMIT);
                    await Task.Delay (MAIN_LOOP_INTERVAL);

                    if (engine.Torrents.Count < TORRENT_PARALLEL_LIMIT) {
                        var hash = GetNextHashId ();

                        DownloadAsync (hash, engine, token);
                    } else {
                        // FIFO logic, just remove the oldest

                        var torrent = engine.Torrents.First ();
                        Console.WriteLine ("MainLoop() Removing timedout torrent {0}", torrent.InfoHashes.V1.ToHex ().ToLower ());

                        UpdateHashId (
                          torrent.InfoHashes.V1.ToHex ().ToLower (),
                          null,
                          0,
                          null,
                          true);

                        await torrent.StopAsync ();
                        await engine.RemoveAsync (torrent);
                    }

                    if (token.IsCancellationRequested) {
                        await engine.StopAllAsync ();

                        if (engine.Settings.AllowPortForwarding)
                            Console.WriteLine ("MainLoop() uPnP and NAT-PMP port mappings have been removed");

                        Console.WriteLine ("MainLoop() Cancellation request received, exiting..");

                        break;
                    }
                }
            }


        }

    }
}
