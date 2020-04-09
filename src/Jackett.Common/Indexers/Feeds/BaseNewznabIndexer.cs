using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using NLog;

namespace Jackett.Common.Indexers.Feeds
{
    public abstract class BaseNewznabIndexer : BaseFeedIndexer
    {
        protected BaseNewznabIndexer(string name, string link, string description, IIndexerConfigurationService configService, WebClient client, Logger logger, ConfigurationData configData, IProtectionService p, TorznabCapabilities caps = null, string downloadBase = null) : base(name, link, description, configService, client, logger, configData, p, caps, downloadBase)
        {
        }

        protected override IEnumerable<ReleaseInfo> ParseFeedForResults(string feedContent)
        {
            var doc = XDocument.Parse(feedContent);

            var results = doc.Descendants("item").Select(ResultFromFeedItem);

            return results;
        }

        protected virtual ReleaseInfo ResultFromFeedItem(XElement item)
        {
            var attributes = item.Descendants().Where(e => e.Name.LocalName == "attr");
            var size = long.TryParse(ReadAttribute(attributes, "size"), out var longVal) ? (long?)longVal : null;
            var files = long.TryParse(ReadAttribute(attributes, "files"), out longVal) ? (long?)longVal : null;
            var seeders = int.TryParse(ReadAttribute(attributes, "seeders"), out var intVal) ? (int?)intVal : null;
            var peers = int.TryParse(ReadAttribute(attributes, "peers"), out intVal) ? (int?)intVal : null;
            var release = new ReleaseInfo
            {
                Title = item.FirstValue("title"),
                Guid = new Uri(item.FirstValue("guid")),
                Link = new Uri(item.FirstValue("link")),
                Comments = new Uri(item.FirstValue("comments")),
                PublishDate = DateTime.Parse(item.FirstValue("pubDate")),
                Category = new List<int> { int.Parse(attributes.First(e => e.Attribute("name").Value == "category").Attribute("value").Value) },
                Size = size,
                Files = files,
                Description = item.FirstValue("description"),
                Seeders = seeders,
                Peers = peers,
                InfoHash = attributes.First(e => e.Attribute("name").Value == "infohash").Attribute("value").Value,
                MagnetUri = new Uri(attributes.First(e => e.Attribute("name").Value == "magneturl").Attribute("value").Value),
            };
            return release;
        }

        private string ReadAttribute(IEnumerable<XElement> attributes, string attributeName)
        {
            var attribute = attributes.FirstOrDefault(e => e.Attribute("name").Value == attributeName);
            if (attribute == null)
                return "";
            return attribute.Attribute("value").Value;
        }
    }
}
