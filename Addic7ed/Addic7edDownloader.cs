﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace Addic7ed
{
    class Addic7edDownloader : ISubtitleProvider, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;

        private readonly IServerConfigurationManager _config;
        private readonly IEncryptionManager _encryption;

        private readonly IJsonSerializer _json;
        private readonly IFileSystem _fileSystem;
        private ILocalizationManager _localizationManager;

        private readonly string baseurl = "http://www.addic7ed.com";

        public Addic7edDownloader(ILogManager logManager, ILocalizationManager localizationManager, IHttpClient httpClient, IServerConfigurationManager config, IEncryptionManager encryption, IJsonSerializer json, IFileSystem fileSystem)
        {
            _logger = logManager.GetLogger(GetType().Name);
            _httpClient = httpClient;
            _config = config;
            _encryption = encryption;
            _json = json;
            _fileSystem = fileSystem;
            _localizationManager = localizationManager;

            _config.NamedConfigurationUpdating += _config_NamedConfigurationUpdating;
        }

        private const string PasswordHashPrefix = "h:";
        void _config_NamedConfigurationUpdating(object sender, ConfigurationUpdateEventArgs e)
        {
            if (!string.Equals(e.Key, "addic7ed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = (Addic7edOptions)e.NewConfiguration;

            if (options != null &&
                !string.IsNullOrWhiteSpace(options.Addic7edPasswordHash) &&
                !options.Addic7edPasswordHash.StartsWith(PasswordHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                options.Addic7edPasswordHash = EncryptPassword(options.Addic7edPasswordHash);
            }
        }

        private string EncryptPassword(string password)
        {
            return PasswordHashPrefix + _encryption.EncryptString(password);
        }

        private string DecryptPassword(string password)
        {
            if (password == null ||
                !password.StartsWith(PasswordHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return _encryption.DecryptString(password.Substring(2));
        }

        public string Name
        {
            get { return "Addic7ed"; }
        }

        private Addic7edOptions GetOptions()
        {
            return _config.GetAddic7edConfiguration();
        }

        public IEnumerable<VideoContentType> SupportedMediaTypes
        {
            get
            {
                return new[] { VideoContentType.Episode, VideoContentType.Movie };
            }
        }

        private string NormalizeLanguage(string language)
        {
            if (language != null)
            {
                var culture = _localizationManager.FindLanguageInfo(language);
                if (culture != null)
                {
                    return culture.ThreeLetterISOLanguageName;
                }
            }

            return language;
        }

        private MatchCollection GetMatches(Stream stream, string pattern)
        {
            var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Replace("\n", "").Replace("\t", "");

            return Regex.Matches(HttpUtility.HtmlDecode(text), pattern);
        }

        private List<MatchCollection> GetMatches(Stream stream, string[] patterns)
        {
            var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Replace("\n", "").Replace("\t", "");

            var matches = new List<MatchCollection>();
            foreach (var pattern in patterns)
            {
                matches.Add(Regex.Matches(HttpUtility.HtmlDecode(text), pattern));
            }
            return matches;
        }

        private async Task<HttpResponseInfo> GetResponse(string url, CancellationToken cancellationToken)
        {
            var res = await _httpClient.GetResponse(new HttpRequestOptions
            {
                Url = $"{baseurl}{url}",
                CancellationToken = cancellationToken,
                Referer = baseurl
            }).ConfigureAwait(false);

            return res;
        }

        private async Task Login(CancellationToken cancellationToken)
        {
            var options = GetOptions();

            var username = options.Addic7edUsername ?? string.Empty;
            var password = DecryptPassword(options.Addic7edPasswordHash);

            var contentData = new Dictionary<string, string>
            {
                { "username", username },
                { "password", password },
                { "Submit", "Log in" }
            };

            var formUrlEncodedContent = new FormUrlEncodedContent(contentData);
            var res = await _httpClient.Post(new HttpRequestOptions
            {
                Url = baseurl + "/dologin.php",
                RequestContentType = "application/x-www-form-urlencoded",
                RequestContentBytes = await formUrlEncodedContent.ReadAsByteArrayAsync(),
                CancellationToken = cancellationToken,
                Referer = baseurl
            }).ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.OK)
            {
                _logger.Debug($"{username} Logged in");
            }
        }

        private async Task<string> GetShow(string name, CancellationToken cancellationToken)
        {
            var shows = await GetShows(cancellationToken).ConfigureAwait(false);

            var showPattern = "show/(\\d+)";
            return shows.ContainsKey(name) ? Regex.Match(shows[name], showPattern).Groups[1].Value : "";
        }

        private async Task<Dictionary<string, string>> GetShows(CancellationToken cancellationToken)
        {
            var shows = new Dictionary<string, string>();
            var res = await GetResponse("/shows.php", cancellationToken);

            if (res.StatusCode == HttpStatusCode.OK)
            {
                var showPattern = "<a href=\"/(show/\\d+)\">(.*?)</a>";
                var showMatches = GetMatches(res.Content, showPattern);
                foreach (Match show in showMatches)
                {
                    if (!shows.ContainsKey(show.Groups[2].Value))
                    {
                        shows.Add(show.Groups[2].Value, show.Groups[1].Value);
                    }
                }
            }

            return shows;
        }

        private IEnumerable<Addic7edResult> GetEpisode(IEnumerable<Addic7edResult> episodes, int? episodeNum, string language)
        {
            return episodes.Where(i => int.Parse(i.Episode) == episodeNum)
            .Where(i => i.Language.Equals(language));
        }

        private IEnumerable<Addic7edResult> ParseEpisode(HttpResponseInfo res)
        {
            var trPattern = "<tr class=\"epeven completed\">(.*?)</tr>";
            var tdPattern = "<td.*?>(.*?)</td>";

            var trMatches = GetMatches(res.Content, trPattern);
            var episodes = new List<Addic7edResult>();
            foreach (Match tr in trMatches)
            {
                var tds = Regex.Matches(tr.Groups[1].Value, tdPattern);
                var result = new Addic7edResult
                {
                    Season = tds[0].Groups[1].Value,
                    Episode = tds[1].Groups[1].Value,
                    Title = (Regex.Match(tds[2].Groups[1].Value, "<a.*?>(.+?)</a>")).Groups[1].Value,
                    Language = NormalizeLanguage(tds[3].Groups[1].Value),
                    Version = tds[4].Groups[1].Value,
                    Completed = tds[5].Groups[1].Value,
                    HearingImpaired = tds[6].Groups[1].Value,
                    Corrected = tds[7].Groups[1].Value,
                    HD = tds[8].Groups[1].Value,
                    Download = (Regex.Match(tds[9].Groups[1].Value, "<a href=\"(.+?)\">Download</a>")).Groups[1].Value,
                    Multi = tds[10].Groups[1].Value
                };
                episodes.Add(result);
            }

            return episodes;
        }

        private async Task<IEnumerable<Addic7edResult>> GetSeason(string id, int? season, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return new List<Addic7edResult>();
            }
            var res = await GetResponse($"/ajax_loadShow.php?show={id}&season={season}", cancellationToken);

            return ParseEpisode(res);
        }

        private async Task<Dictionary<string, string>> GetMovies(CancellationToken cancellationToken)
        {
            var res = await GetResponse("/movie-subtitles", cancellationToken);

            var aPattern = "<a href=\"(movie/\\d+)\">(.*?)</a><";
            var aMatches = GetMatches(res.Content, aPattern);
            var movies = new Dictionary<string, string>();
            foreach (Match a in aMatches)
            {
                if (!movies.ContainsKey(a.Groups[2].Value))
                {
                    movies.Add(a.Groups[2].Value, a.Groups[1].Value);
                }
            }

            return movies;
        }

        private async Task<IEnumerable<Addic7edResult>> ParseMovie(string movie, CancellationToken cancellationToken)
        {
            var res = await GetResponse($"/{movie}", cancellationToken);

            var verPattern = "(Version.*?)</td>";
            var langPattern = "class=\"language\">(.*?)<";
            var downPattern = "<a class=\"buttonDownload\" href=\"(.*?)\">";

            var matches = GetMatches(res.Content, new[] { verPattern, langPattern, downPattern });

            var results = new List<Addic7edResult>();
            for (int i = 0; i < matches.FirstOrDefault().Count; i++)
            {
                var result = new Addic7edResult
                {
                    Version = matches[0][i].Groups[1].Value,
                    Language = NormalizeLanguage(matches[1][i].Groups[1].Value),
                    Download = matches[2][i].Groups[1].Value,
                };
                results.Add(result);
            }

            return results;
        }

        private async Task<IEnumerable<Addic7edResult>> GetMovie(string name, int? productionYear, string language, CancellationToken cancellationToken)
        {
            var movies = await GetMovies(cancellationToken);
            var namePattern = $"{name} ({productionYear})";
            var movie = movies.ContainsKey(namePattern) ? movies[namePattern] : "";
            if (string.IsNullOrWhiteSpace(movie))
            {
                return new List<Addic7edResult>();
            }
            var results = await ParseMovie(movie, cancellationToken);

            return results.Where(i => i.Language.Equals(language));
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> SearchEpisode(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.SeriesName) &&
                request.ParentIndexNumber.HasValue &&
                request.IndexNumber.HasValue &&
                !string.IsNullOrWhiteSpace(request.Language))
            {

                var showId = await GetShow(request.SeriesName, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(showId))
                {
                    return Array.Empty<RemoteSubtitleInfo>();
                }
                var season = await GetSeason(showId, request.ParentIndexNumber, cancellationToken).ConfigureAwait(false);
                var episode = GetEpisode(season, request.IndexNumber, request.Language);

                return episode.Select(i => new RemoteSubtitleInfo
                {
                    Id = $"{i.Download}:{i.Language}",
                    ProviderName = Name,
                    Name = $"{i.Title} - {i.Version}",
                    Format = "srt",
                    ThreeLetterISOLanguageName = i.Language
                });

            }

            return Array.Empty<RemoteSubtitleInfo>();
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> SearchMovie(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.Name) &&
                request.ProductionYear.HasValue &&
                !string.IsNullOrWhiteSpace(request.Language))
            {
                var movie = await GetMovie(request.Name, request.ProductionYear, request.Language, cancellationToken);
                if (movie.Count() == 0)
                {
                    return Array.Empty<RemoteSubtitleInfo>();
                }
                return movie.Select(i => new RemoteSubtitleInfo
                {
                    Id = $"{i.Download}:{i.Language}",
                    ProviderName = Name,
                    Name = $"{i.Title} - {i.Version}",
                    Format = "srt",
                    ThreeLetterISOLanguageName = i.Language
                });
            }

            return Array.Empty<RemoteSubtitleInfo>();
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            await Login(cancellationToken);
            if (request.ContentType.Equals(VideoContentType.Episode))
            {
                return await SearchEpisode(request, cancellationToken);
            }
            if (request.ContentType.Equals(VideoContentType.Movie))
            {
                return await SearchMovie(request, cancellationToken);
            }

            return Array.Empty<RemoteSubtitleInfo>();
        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            var idParts = id.Split(new[] { ':' }, 2);
            var download = idParts[0];
            var language = idParts[1];
            var format = "srt";

            var stream = await GetResponse(download, cancellationToken);
            if (string.IsNullOrWhiteSpace(stream.ContentType) ||
                stream.ContentType.Contains(format))
            {
                return new SubtitleResponse()
                {
                    Language = language,
                    Stream = stream.Content,
                    Format = format
                };
            }

            return new SubtitleResponse();
        }

        public void Dispose()
        {
            _config.NamedConfigurationUpdating -= _config_NamedConfigurationUpdating;
        }

        public class Addic7edResult
        {
            public string Season { get; set; }
            public string Episode { get; set; }
            public string Title { get; set; }
            public string Language { get; set; }
            public string Version { get; set; }
            public string Completed { get; set; }
            public string HearingImpaired { get; set; }
            public string Corrected { get; set; }
            public string HD { get; set; }
            public string Download { get; set; }
            public string Multi { get; set; }
            public string Id { get; set; }
        }
    }
}