using System;

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MetadataDownloader
{
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

        [BsonElement ("countSeen")]
        public int CountSeen { get; set; }

        [BsonElement ("lastSeen")]
        [BsonRepresentation (BsonType.DateTime)]
        public DateTime LastSeen { get; set; }

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
}
