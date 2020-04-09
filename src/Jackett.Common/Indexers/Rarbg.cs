using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using static Jackett.Common.Models.IndexerConfig.ConfigurationData;

namespace Jackett.Common.Indexers
{
    public class Rarbg : BaseWebIndexer
    {
        // API doc: https://torrentapi.org/apidocs_v2.txt?app_id=Jackett
        private static readonly string defaultSiteLink = "https://torrentapi.org/";

        private Uri BaseUri
        {
            get => new Uri(configData.Url.Value);
            set => configData.Url.Value = value.ToString();
        }

        private string ApiEndpoint => BaseUri + "pubapi_v2.php";

        private new ConfigurationDataUrl configData
        {
            get => (ConfigurationDataUrl)base.configData;
            set => base.configData = value;
        }

        private DateTime lastTokenFetch;
        private string token;
        private readonly string app_id;
        private bool _provideTorrentLink;
        private string _sort;

        private readonly TimeSpan TOKEN_DURATION = TimeSpan.FromMinutes(10);

        private bool HasValidToken => !string.IsNullOrEmpty(token) && lastTokenFetch > DateTime.Now - TOKEN_DURATION;

        public Rarbg(IIndexerConfigurationService configService, Utils.Clients.WebClient wc, Logger l, IProtectionService ps)
            : base(name: "RARBG",
                description: "RARBG is a Public torrent site for MOVIES / TV / GENERAL",
                link: "https://rarbg.to/",
                caps: new TorznabCapabilities
                {
                    SupportsImdbMovieSearch = true
                },
                configService: configService,
                client: wc,
                logger: l,
                p: ps,
                configData: new ConfigurationDataUrl(defaultSiteLink))
        {
            Encoding = Encoding.GetEncoding("windows-1252");
            Language = "en-us";
            Type = "public";

            var sort = new SelectItem(new Dictionary<string, string>
            {
                {"last", "created"},
                {"seeders", "seeders"},
                {"leechers", "leechers"}
            })
            { Name = "Sort requested from site", Value = "last" };
            configData.AddDynamic("sort", sort);

            var provideTorrentLinkItem = new BoolItem { Value = false };
            provideTorrentLinkItem.Name = "Generate torrent download link additionally to magnet (not recommended due to DDoS protection).";
            configData.AddDynamic("providetorrentlink", provideTorrentLinkItem);

            webclient.requestDelay = 2.1; // The api has a 1req/2s limit.

            AddCategoryMapping(4, TorznabCatType.XXX, "XXX (18+)");
            AddCategoryMapping(14, TorznabCatType.MoviesSD, "Movies/XVID");
            AddCategoryMapping(17, TorznabCatType.MoviesSD, "Movies/x264");
            AddCategoryMapping(18, TorznabCatType.TVSD, "TV Episodes");
            AddCategoryMapping(23, TorznabCatType.AudioMP3, "Music/MP3");
            AddCategoryMapping(25, TorznabCatType.AudioLossless, "Music/FLAC");
            AddCategoryMapping(27, TorznabCatType.PCGames, "Games/PC ISO");
            AddCategoryMapping(28, TorznabCatType.PCGames, "Games/PC RIP");
            AddCategoryMapping(32, TorznabCatType.ConsoleXbox360, "Games/XBOX-360");
            AddCategoryMapping(33, TorznabCatType.PCISO, "Software/PC ISO");
            AddCategoryMapping(35, TorznabCatType.BooksEbook, "e-Books");
            AddCategoryMapping(40, TorznabCatType.ConsolePS3, "Games/PS3");
            AddCategoryMapping(41, TorznabCatType.TVHD, "TV HD Episodes");
            AddCategoryMapping(42, TorznabCatType.MoviesBluRay, "Movies/Full BD");
            AddCategoryMapping(44, TorznabCatType.MoviesHD, "Movies/x264/1080");
            AddCategoryMapping(45, TorznabCatType.MoviesHD, "Movies/x264/720");
            AddCategoryMapping(46, TorznabCatType.MoviesBluRay, "Movies/BD Remux");
            AddCategoryMapping(47, TorznabCatType.Movies3D, "Movies/x264/3D");
            AddCategoryMapping(48, TorznabCatType.MoviesHD, "Movies/XVID/720");
            AddCategoryMapping(49, TorznabCatType.TVUHD, "TV UHD Episodes");        // torrentapi.org returns "Movies/TV-UHD-episodes" for some reason
            AddCategoryMapping(49, TorznabCatType.TVUHD, "Movies/TV-UHD-episodes"); // possibly because thats what the category is called on the /top100.php page
            AddCategoryMapping(50, TorznabCatType.MoviesUHD, "Movies/x264/4k");
            AddCategoryMapping(51, TorznabCatType.MoviesUHD, "Movies/x265/4k");
            AddCategoryMapping(52, TorznabCatType.MoviesUHD, "Movs/x265/4k/HDR");
            AddCategoryMapping(53, TorznabCatType.ConsolePS4, "Games/PS4");
            AddCategoryMapping(54, TorznabCatType.MoviesHD, "Movies/x265/1080");

            app_id = "jackett_v" + EnvironmentUtil.JackettVersion;
        }

        public override void LoadValuesFromJson(JToken jsonConfig, bool useProtectionService = false)
        {
            base.LoadValuesFromJson(jsonConfig, useProtectionService);

            var sort = (SelectItem)configData.GetDynamic("sort");
            _sort = sort != null ? sort.Value : "last";

            var provideTorrentLinkItem = (BoolItem)configData.GetDynamic("providetorrentlink");
            _provideTorrentLink = provideTorrentLinkItem != null && provideTorrentLinkItem.Value;
        }

        private async Task CheckToken()
        {
            if (!HasValidToken)
            {
                var queryCollection = new NameValueCollection
                {
                    { "get_token", "get_token" },
                    { "app_id", app_id }
                };

                var tokenUrl = ApiEndpoint + "?" + queryCollection.GetQueryString();

                var result = await RequestStringWithCookiesAndRetry(tokenUrl);
                var json = JObject.Parse(result.Content);
                token = json.Value<string>("token");
                lastTokenFetch = DateTime.Now;
            }
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Any(), () =>
                throw new Exception("Could not find releases from this URL"));

            return IndexerConfigurationStatus.Completed;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query) => await PerformQuery(query, 0);

        private async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query, int attempts)
        {
            await CheckToken();
            var releases = new List<ReleaseInfo>();
            var searchString = query.GetQueryString();

            var queryCollection = new NameValueCollection
            {
                { "token", token },
                { "format", "json_extended" },
                { "app_id", app_id },
                { "limit", "100" },
                { "ranked", "0" },
                { "sort", _sort }
            };

            if (query.ImdbID != null)
            {
                queryCollection.Add("mode", "search");
                queryCollection.Add("search_imdb", query.ImdbID);
            }
            else if (query.RageID != null)
            {
                queryCollection.Add("mode", "search");
                queryCollection.Add("search_tvrage", query.RageID.ToString());
            }
            /*else if (query.TvdbID != null)
            {
                queryCollection.Add("mode", "search");
                queryCollection.Add("search_tvdb", query.TvdbID);
            }*/
            else if (!string.IsNullOrWhiteSpace(searchString))
            {
                searchString = searchString.Replace("'", ""); // ignore ' (e.g. search for america's Next Top Model)
                queryCollection.Add("mode", "search");
                queryCollection.Add("search_string", searchString);
            }
            else
            {
                queryCollection.Add("mode", "list");
                queryCollection.Remove("sort");
            }

            var querycats = MapTorznabCapsToTrackers(query);
            if (querycats.Count == 0)
                querycats = GetAllTrackerCategories(); // default to all, without specifing it some categories are missing (e.g. games), see #4146
            var cats = string.Join(";", querycats);
            queryCollection.Add("category", cats);

            var searchUrl = ApiEndpoint + "?" + queryCollection.GetQueryString();
            var response = await RequestStringWithCookiesAndRetry(searchUrl, string.Empty);

            try
            {
                var jsonContent = JObject.Parse(response.Content);

                var errorCode = jsonContent.Value<int>("error_code");
                if (errorCode == 20) // no results found
                {
                    return releases.ToArray();
                }

                // return empty results in case of invalid imdb ID, see issue #1486
                if (errorCode == 10) // Cant find imdb in database. Are you sure this imdb exists?
                    return releases;

                if (errorCode == 2  // Invalid token set!
                    || errorCode == 4) // Invalid token. Use get_token for a new one!
                {
                    token = null;
                    if (attempts < 3)
                    {
                        return await PerformQuery(query, ++attempts);
                    }
                    else
                    {
                        throw new Exception("error " + errorCode.ToString() + " after " + attempts.ToString() + " attempts: " + jsonContent.Value<string>("error"));
                    }
                }

                if (errorCode > 0) // too many requests per seconds ???
                {
                    // we use the IwebClient rate limiter now, this shouldn't happen
                    throw new Exception("error " + errorCode.ToString() + ": " + jsonContent.Value<string>("error"));
                }

                foreach (var item in jsonContent.Value<JArray>("torrent_results"))
                {
                    var magnetUri = new Uri(item.Value<string>("download"));
                    // append app_id to prevent api server returning 403 forbidden
                    var comments = new Uri(item.Value<string>("info_page") + "&app_id=" + app_id);
                    // ex: 2015-08-16 21:25:08 +0000
                    var dateStr = item.Value<string>("pubdate").Replace(" +0000", "");
                    var dateTime = DateTime.ParseExact(dateStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var seeders = item.Value<int>("seeders");
                    var title = WebUtility.HtmlDecode(item.Value<string>("title"));
                    var infoHash = magnetUri.ToString().Split(':')[3].Split('&')[0];
                    var publishDate = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime();
                    var leechers = item.Value<int>("leechers");
                    var size = item.Value<long>("size");
                    // in case of a torrent download we grab the link from the details page in Download()
                    var link = _provideTorrentLink ? comments : default;
                    var release = new ReleaseInfo
                    {
                        Title = title,
                        Category = MapTrackerCatDescToNewznab(item.Value<string>("category")),
                        MagnetUri = magnetUri,
                        InfoHash = infoHash,
                        Comments = comments,
                        Link = link,
                        PublishDate = publishDate,
                        Guid = magnetUri,
                        Seeders = seeders,
                        Peers = leechers + seeders,
                        Size = size,
                        MinimumRatio = 1,
                        MinimumSeedTime = 172800, // 48 hours
                        DownloadVolumeFactor = 0,
                        UploadVolumeFactor = 1
                    };

                    var episodeInfo = item.Value<JToken>("episode_info");

                    if (episodeInfo.HasValues)
                    {
                        release.Imdb = ParseUtil.GetImdbID(episodeInfo.Value<string>("imdb"));
                        release.TVDBId = episodeInfo.Value<long?>("tvdb");
                        release.RageID = episodeInfo.Value<long?>("tvrage");
                        release.TMDb = episodeInfo.Value<long?>("themoviedb");
                    }


                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(response.Content, ex);
            }

            return releases;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            // build download link from info redirect link
            var slink = link.ToString();
            var response = await RequestStringWithCookies(slink);
            if (!response.IsRedirect && response.Content.Contains("Invalid token."))
            {
                // get new token
                token = null;
                await CheckToken();
                slink += "&token=" + token;
                response = await RequestStringWithCookies(slink);
            }
            if (!response.IsRedirect)
                throw new Exception("Downlaod Failed, expected redirect");

            var targeturi = new Uri(response.RedirectingTo);
            var id = targeturi.Segments.Last();
            var dluri = new Uri(targeturi, "/download.php?id=" + id + "&f=jackett.torrent");
            return await base.Download(dluri, RequestType.GET);
        }
    }
}
