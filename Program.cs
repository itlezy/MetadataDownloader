using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Text;
using System.Threading;

using MongoDB.Driver;


namespace MetadataDownloader
{
    partial class Program
    {
        static void Main (string[] args)
        {
            if (args.Length <= 0) {
                MetadataDownload ();
            } else {

                switch (args[0].ToCharArray ()[0]) {
                    case 'l':
                        LoadHashesToDBMongo (args[1]);
                        break;

                    case 'c':
                        new DAO ().CreateSQLiteTables ();
                        break;

                    case 's':
                        new DAO ().LoadHashesToDBSQLite (args[1]);
                        break;

                    default:
                        break;
                }

            }

        }

        private static void LoadHashesToDBMongo (String inputFile)
        {
            var lines = File.ReadAllLines (inputFile);
            var dic = new Dictionary<String, MTorrent> ();

            Console.WriteLine ("Loaded {0} lines from file [{1}]", lines.Length, inputFile);
            //Console.ReadLine ();

            foreach (var line in lines) {
                if (line.Length < 50) { continue; }

                try {
                    var segs = line.Split (' ');
                    var dateSeen = DateTime.Parse (segs[1]);
                    var hashId = segs[4].Substring (1, 40).ToLower ();

                    MTorrent t;

                    if (dic.TryGetValue (hashId, out t)) {
                        t.CountSeen++;

                        if (dateSeen > t.LastSeen) {
                            t.LastSeen = dateSeen;
                        }

                    } else {
                        t = new MTorrent () {
                            Id = hashId,
                            CountSeen = 1,
                            LastSeen = dateSeen
                        };

                        dic.Add (hashId, t);
                    }

                } catch (Exception ex) {
                    Console.Error.WriteLine ("Error processing line [{0}] {1}", line, ex.Message);
                }

                // Console.WriteLine ("Date [{0}], HashId [{1}]", dateSeen, hashId);
            }

            Console.WriteLine ("Parsed [{0}] records", dic.Count);

            ApplcConfig ac = new ApplcConfig ();

            var client = new MongoClient (ac.DB_URL);
            var database = client.GetDatabase (ac.DB_NAME);
            var collection = database.GetCollection<MTorrent> (ac.DB_COLLECTION_NAME);

            try {
                collection.InsertMany (
                    dic.Values,
                    new InsertManyOptions () { IsOrdered = false }
                    );
            } catch (Exception ex) {
                Console.Error.WriteLine ("Error InsertMany {0}", ex.Message);
            }
        }

        private static void MetadataDownload ()
        {
            CancellationTokenSource cancellation = new CancellationTokenSource ();

            var task = new SQueueManager ().MainLoop (cancellation.Token);

            // We need to cleanup correctly when the user closes the window by using ctrl-c
            // or an unhandled exception happens

            Console.CancelKeyPress += delegate { cancellation.Cancel (); task.Wait (); };
            AppDomain.CurrentDomain.ProcessExit += delegate { cancellation.Cancel (); task.Wait (); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine (e.ExceptionObject); cancellation.Cancel (); task.Wait (); };
            Thread.GetDomain ().UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine (e.ExceptionObject); cancellation.Cancel (); task.Wait (); };

            task.Wait ();
        }
    }
}
