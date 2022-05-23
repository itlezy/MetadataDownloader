﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SQLite;

namespace MetadataDownloader
{
    public class MTorrLog
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        [Indexed]
        public DateTime SeenAt { get; set; }
        [Indexed]
        public String HashId { get; set; }
    }

    public class MTorr
    {
        [PrimaryKey, MaxLength (40)]
        public String HashId { get; set; }

        public String Name { get; set; }
        public String Comment { get; set; }

        [Indexed]
        public int CountSeen { get; set; }
        public long Length { get; set; }

        public bool Processed { get; set; }
        public bool Timeout { get; set; }
        public bool Downloaded { get; set; }

        public DateTime DownloadedTime { get; set; }
        public DateTime LastSeen { get; set; }

        public DateTime ProcessedTime { get; set; }

    }

}
