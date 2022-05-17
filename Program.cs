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

        class QueueManager
        {
            private const int MAIN_LOOP_INTERVAL = 3000;
            private const int TORRENT_PARALLEL_LIMIT = 5;

            String[] hashes = File.ReadAllLines (@"c:\tmp\hashes.txt");

            int i = 0;

            private String GetNextHashId ()
            {
                return hashes[i++];
            }

            public async Task DownloadAsync (
                String hash,
                ClientEngine engine,
                CancellationToken token)
            {
                if (MagnetLink.TryParse ("magnet:?xt=urn:btih:" + hash, out MagnetLink link)) {

                    var manager = await engine.AddAsync (link, ".");

                    Console.WriteLine ("Adding hash {0}", link.InfoHashes.V1.ToHex ());

                    await manager.StartAsync ();
                    await manager.WaitForMetadataAsync (token);

                    if (manager.HasMetadata && manager.Files.Count > 0) {
                        Console.WriteLine ("Metadata Received {0}\n\t{1}\n\t{2}\n\t{3}\n",
                            link.InfoHashes.V1.ToHex (),
                            manager.MetadataPath,
                            manager.Torrent.Name,
                            manager.Files.OrderByDescending (t => t.Length).First ().FullPath);
                    }

                    File.Copy (manager.MetadataPath, @"c:\tmp_out\" + manager.Torrent.Name + ".torrent");

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
                    Console.WriteLine ("uPnP or NAT-PMP port mappings will be created for any ports needed by MonoTorrent");

                var loopCount = 0;

                while (true) {
                    Console.WriteLine ("MainLoop checking for torrents count {0} / {1}", engine.Torrents.Count, TORRENT_PARALLEL_LIMIT);
                    await Task.Delay (MAIN_LOOP_INTERVAL);

                    if (engine.Torrents.Count < TORRENT_PARALLEL_LIMIT) {
                        var hash = GetNextHashId ();

                        DownloadAsync (hash, engine, token);
                    }

                    if (loopCount++ % 10 == 0 && engine.Torrents.Count > 0) {
                        var torrent = engine.Torrents.First ();
                        Console.WriteLine ("Removing timedout torrent {0}", torrent.InfoHashes.V1.ToHex ());

                        await torrent.StopAsync ();
                        await engine.RemoveAsync (torrent);
                    }

                    if (token.IsCancellationRequested) {
                        await engine.StopAllAsync ();

                        if (engine.Settings.AllowPortForwarding)
                            Console.WriteLine ("uPnP and NAT-PMP port mappings have been removed");

                        Console.WriteLine ("Cancellation request received, exiting..");

                        break;
                    }
                }
            }


        }

    }
}
