using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Applications;
using NzbDrone.Core.Applications.Radarr;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Events;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    public interface IReleaseSearchService
    {
        Task<NewznabResults> Search(NewznabRequest request, List<int> indexerIds, bool interactiveSearch);
    }

    public class ReleaseSearchService : IReleaseSearchService
    {
        private readonly IIndexerLimitService _indexerLimitService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IApplicationFactory _applicationFactory;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public ReleaseSearchService(IEventAggregator eventAggregator,
                                IIndexerFactory indexerFactory,
                                IIndexerLimitService indexerLimitService,
                                IApplicationFactory applicationFactory,
                                IHttpClient httpClient,
                                Logger logger)
        {
            _eventAggregator = eventAggregator;
            _indexerFactory = indexerFactory;
            _indexerLimitService = indexerLimitService;
            _applicationFactory = applicationFactory;
            _httpClient = httpClient;
            _logger = logger;
        }

        public Task<NewznabResults> Search(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            return request.t switch
            {
                "movie" => MovieSearch(request, indexerIds, interactiveSearch),
                "music" => MusicSearch(request, indexerIds, interactiveSearch),
                "tvsearch" => TvSearch(request, indexerIds, interactiveSearch),
                "book" => BookSearch(request, indexerIds, interactiveSearch),
                _ => BasicSearch(request, indexerIds, interactiveSearch)
            };
        }

        private async Task<NewznabResults> MovieSearch(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            var searchSpec = Get<MovieSearchCriteria>(request, indexerIds, interactiveSearch);

            var imdbId = ParseUtil.GetImdbId(request.imdbid);

            searchSpec.ImdbId = imdbId?.ToString("D7");
            searchSpec.TmdbId = request.tmdbid;
            searchSpec.TraktId = request.traktid;
            searchSpec.DoubanId = request.doubanid;
            searchSpec.Year = request.year;
            searchSpec.Genre = request.genre;

            var releases = new List<ReleaseInfo>();

            foreach (var spec in ExpandMovieSearchCriteria(searchSpec))
            {
                var specReleases = await Dispatch(indexer => indexer.Fetch(spec), spec);

                if (!MovieSearchTermEquals(spec.SearchTerm, searchSpec.SearchTerm))
                {
                    specReleases = AddOriginalMovieSearchTermToReleases(specReleases, searchSpec.SearchTerm);
                }

                releases.AddRange(specReleases);
            }

            return new NewznabResults { Releases = DeDupeReleases(releases) };
        }

        private async Task<NewznabResults> MusicSearch(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            var searchSpec = Get<MusicSearchCriteria>(request, indexerIds, interactiveSearch);

            searchSpec.Artist = request.artist;
            searchSpec.Album = request.album;
            searchSpec.Label = request.label;
            searchSpec.Genre = request.genre;
            searchSpec.Track = request.track;
            searchSpec.Year = request.year;

            var releases = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);

            return new NewznabResults { Releases = DeDupeReleases(releases) };
        }

        private async Task<NewznabResults> TvSearch(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            var searchSpec = Get<TvSearchCriteria>(request, indexerIds, interactiveSearch);

            var imdbId = ParseUtil.GetImdbId(request.imdbid);

            searchSpec.ImdbId = imdbId?.ToString("D7");
            searchSpec.Season = request.season;
            searchSpec.Episode = request.ep;
            searchSpec.TvdbId = request.tvdbid;
            searchSpec.TraktId = request.traktid;
            searchSpec.TmdbId = request.tmdbid;
            searchSpec.DoubanId = request.doubanid;
            searchSpec.RId = request.rid;
            searchSpec.TvMazeId = request.tvmazeid;
            searchSpec.Year = request.year;
            searchSpec.Genre = request.genre;

            var releases = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);

            return new NewznabResults { Releases = DeDupeReleases(releases) };
        }

        private async Task<NewznabResults> BookSearch(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            var searchSpec = Get<BookSearchCriteria>(request, indexerIds, interactiveSearch);

            searchSpec.Author = request.author;
            searchSpec.Title = request.title;
            searchSpec.Publisher = request.publisher;
            searchSpec.Year = request.year;
            searchSpec.Genre = request.genre;

            var releases = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);

            return new NewznabResults { Releases = DeDupeReleases(releases) };
        }

        private async Task<NewznabResults> BasicSearch(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            var searchSpec = Get<BasicSearchCriteria>(request, indexerIds, interactiveSearch);

            var releases = new List<ReleaseInfo>();

            foreach (var spec in ExpandBasicMovieSearchCriteria(searchSpec))
            {
                var specReleases = await Dispatch(indexer => indexer.Fetch(spec), spec);

                if (!MovieSearchTermEquals(spec.SearchTerm, searchSpec.SearchTerm))
                {
                    specReleases = AddOriginalMovieSearchTermToReleases(specReleases, searchSpec.SearchTerm);
                }

                releases.AddRange(specReleases);
            }

            return new NewznabResults { Releases = DeDupeReleases(releases) };
        }

        private TSpec Get<TSpec>(NewznabRequest query, List<int> indexerIds, bool interactiveSearch)
            where TSpec : SearchCriteriaBase, new()
        {
            var spec = new TSpec
            {
                InteractiveSearch = interactiveSearch
            };

            spec.Categories = query.cat != null ? query.cat.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).Select(int.Parse).ToArray() : Array.Empty<int>();

            spec.SearchTerm = query.q?.Trim();
            spec.SearchType = query.t;
            spec.Limit = query.limit;
            spec.Offset = query.offset;
            spec.MinAge = query.minage;
            spec.MaxAge = query.maxage;
            spec.MinSize = query.minsize;
            spec.MaxSize = query.maxsize;
            spec.Source = query.source;
            spec.Host = query.host;

            spec.IndexerIds = indexerIds;

            return spec;
        }

        private async Task<IList<ReleaseInfo>> Dispatch(Func<IIndexer, Task<IndexerPageableQueryResult>> searchAction, SearchCriteriaBase criteriaBase)
        {
            var indexers = _indexerFactory.Enabled();

            if (criteriaBase.IndexerIds is { Count: > 0 })
            {
                indexers = indexers.Where(i => criteriaBase.IndexerIds.Contains(i.Definition.Id) ||
                    (criteriaBase.IndexerIds.Contains(-1) && i.Protocol == DownloadProtocol.Usenet) ||
                    (criteriaBase.IndexerIds.Contains(-2) && i.Protocol == DownloadProtocol.Torrent))
                    .ToList();

                if (criteriaBase.InteractiveSearch && indexers.Count == 0)
                {
                    _logger.Debug("Search failed due to all selected indexers being unavailable: {0}", string.Join(", ", criteriaBase.IndexerIds));

                    throw new SearchFailedException("Search failed due to all selected indexers being unavailable");
                }
            }

            if (criteriaBase.Categories is { Length: > 0 })
            {
                // Only query supported indexers
                indexers = indexers.Where(i => i.GetCapabilities().Categories.SupportedCategories(criteriaBase.Categories).Any()).ToList();

                if (indexers.Count == 0)
                {
                    _logger.Debug("All provided categories are unsupported by selected indexers: {0}", string.Join(", ", criteriaBase.Categories));

                    return Array.Empty<ReleaseInfo>();
                }
            }

            _logger.ProgressInfo("Searching indexer(s): [{0}] for {1}", string.Join(", ", indexers.Select(i => i.Definition.Name).ToList()), criteriaBase.ToString());

            var tasks = indexers.Select(x => DispatchIndexer(searchAction, x, criteriaBase));

            var batch = await Task.WhenAll(tasks);

            var reports = batch.SelectMany(x => x).ToList();

            _logger.ProgressDebug("Total of {0} reports were found for {1} from {2} indexer(s)", reports.Count, criteriaBase, indexers.Count);

            return reports;
        }

        private async Task<IList<ReleaseInfo>> DispatchIndexer(Func<IIndexer, Task<IndexerPageableQueryResult>> searchAction, IIndexer indexer, SearchCriteriaBase criteriaBase)
        {
            if (_indexerLimitService.AtQueryLimit((IndexerDefinition)indexer.Definition))
            {
                return Array.Empty<ReleaseInfo>();
            }

            try
            {
                var indexerReports = await searchAction(indexer);

                var releases = indexerReports.Releases;

                //Filter results to only those in searched categories
                if (criteriaBase.Categories.Length > 0)
                {
                    var expandedQueryCats = indexer.GetCapabilities().Categories.ExpandTorznabQueryCategories(criteriaBase.Categories);

                    releases = releases.Where(result => result.Categories?.Any() != true || expandedQueryCats.Intersect(result.Categories.Select(c => c.Id)).Any()).ToList();

                    if (releases.Count != indexerReports.Releases.Count)
                    {
                        _logger.Trace("{0} releases from {1} ({2}) which didn't contain search categories [{3}] were filtered", indexerReports.Releases.Count - releases.Count, ((IndexerDefinition)indexer.Definition).Name, indexer.Name, string.Join(", ", expandedQueryCats));
                    }
                }

                if (criteriaBase.MinAge is > 0)
                {
                    var cutoffDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(criteriaBase.MinAge.Value));

                    releases = releases.Where(r => r.PublishDate <= cutoffDate).ToList();
                }

                if (criteriaBase.MaxAge is > 0)
                {
                    var cutoffDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(criteriaBase.MaxAge.Value));

                    releases = releases.Where(r => r.PublishDate >= cutoffDate).ToList();
                }

                if (criteriaBase.MinSize is > 0)
                {
                    var minSize = criteriaBase.MinSize.Value;

                    releases = releases.Where(r => r.Size >= minSize).ToList();
                }

                if (criteriaBase.MaxSize is > 0)
                {
                    var maxSize = criteriaBase.MaxSize.Value;

                    releases = releases.Where(r => r.Size <= maxSize).ToList();
                }

                foreach (var query in indexerReports.Queries)
                {
                    _eventAggregator.PublishEvent(new IndexerQueryEvent(indexer.Definition.Id, criteriaBase, query));
                }

                return releases;
            }
            catch (Exception e)
            {
                _eventAggregator.PublishEvent(new IndexerQueryEvent(indexer.Definition.Id, criteriaBase, new IndexerQueryResult()));
                _logger.Error(e, "Error while searching for {0}", criteriaBase);
            }

            return Array.Empty<ReleaseInfo>();
        }

        private List<ReleaseInfo> DeDupeReleases(IList<ReleaseInfo> releases)
        {
            // De-dupe reports by guid so duplicate results aren't returned. Pick the one with the higher indexer priority.
            return releases.GroupBy(r => r.Guid)
                .Select(r => r.OrderBy(v => v.IndexerPriority).First())
                .ToList();
        }

        private IEnumerable<MovieSearchCriteria> ExpandMovieSearchCriteria(MovieSearchCriteria searchSpec)
        {
            yield return searchSpec;

            if (searchSpec.Offset is > 0)
            {
                yield break;
            }

            var alternateTerms = GetRadarrAlternateMovieTitles(searchSpec)
                .Where(t => t.IsNotNullOrWhiteSpace())
                .OrderBy(GetMovieTitleSearchPriority)
                .SelectMany(t => BuildMovieSearchTerms(t, searchSpec.Year))
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .Where(t => !MovieSearchTermEquals(t, searchSpec.SearchTerm))
                .Take(40)
                .ToList();

            foreach (var term in alternateTerms)
            {
                var expanded = CopyMovieSearchCriteria(searchSpec);
                expanded.SearchTerm = term;
                expanded.ImdbId = null;
                expanded.TmdbId = null;
                expanded.TraktId = null;
                expanded.DoubanId = null;
                expanded.Genre = null;

                yield return expanded;
            }
        }

        private IEnumerable<BasicSearchCriteria> ExpandBasicMovieSearchCriteria(BasicSearchCriteria searchSpec)
        {
            yield return searchSpec;

            if (searchSpec.Offset is > 0 ||
                searchSpec.SearchTerm.IsNullOrWhiteSpace() ||
                searchSpec.Categories?.Any(c => c >= 2000 && c < 3000) != true)
            {
                yield break;
            }

            var movieSearchSpec = new MovieSearchCriteria
            {
                SearchTerm = searchSpec.SearchTerm,
                Categories = searchSpec.Categories,
                IndexerIds = searchSpec.IndexerIds,
                Limit = searchSpec.Limit,
                Offset = searchSpec.Offset,
                MinAge = searchSpec.MinAge,
                MaxAge = searchSpec.MaxAge,
                MinSize = searchSpec.MinSize,
                MaxSize = searchSpec.MaxSize,
                Source = searchSpec.Source,
                Host = searchSpec.Host,
                SearchType = searchSpec.SearchType,
                InteractiveSearch = searchSpec.InteractiveSearch
            };

            var alternateTerms = GetRadarrAlternateMovieTitles(movieSearchSpec)
                .Where(t => t.IsNotNullOrWhiteSpace())
                .OrderBy(GetMovieTitleSearchPriority)
                .SelectMany(t => BuildMovieSearchTerms(t, null))
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .Where(t => !MovieSearchTermEquals(t, searchSpec.SearchTerm))
                .Take(40)
                .ToList();

            foreach (var term in alternateTerms)
            {
                var expanded = CopyBasicSearchCriteria(searchSpec);
                expanded.SearchTerm = term;

                yield return expanded;
            }
        }

        private IEnumerable<string> GetRadarrAlternateMovieTitles(MovieSearchCriteria searchSpec)
        {
            if (!searchSpec.ImdbId.IsNotNullOrWhiteSpace() &&
                !searchSpec.TmdbId.HasValue &&
                !searchSpec.SearchTerm.IsNotNullOrWhiteSpace())
            {
                return Enumerable.Empty<string>();
            }

            var titles = new List<string>();

            foreach (var app in _applicationFactory.SyncEnabled(false).Where(a => a.Definition.Implementation == nameof(Radarr)))
            {
                if (app.Definition.Settings is not RadarrSettings settings)
                {
                    continue;
                }

                try
                {
                    var request = BuildApplicationRequest(settings.BaseUrl, "/api/v3/movie", settings.ApiKey, settings.AuthUsername, settings.AuthPassword);
                    var response = _httpClient.Execute(request);

                    if ((int)response.StatusCode >= 300)
                    {
                        continue;
                    }

                    var movies = Json.Deserialize<List<RadarrMovieMetadata>>(response.Content);
                    var movie = movies.FirstOrDefault(m => MovieMatches(m, searchSpec));

                    if (movie == null)
                    {
                        continue;
                    }

                    titles.Add(movie.Title);
                    titles.Add(movie.OriginalTitle);
                    titles.AddRange(movie.AlternateTitles?.Select(t => t.Title) ?? Enumerable.Empty<string>());
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to retrieve Radarr alternate titles from {0}", app.Definition.Name);
                }
            }

            return titles
                .Where(t => t.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase);
        }

        private static HttpRequest BuildApplicationRequest(string baseUrl, string resource, string apiKey, string username, string password)
        {
            var requestBuilder = new HttpRequestBuilder(baseUrl.TrimEnd('/'))
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .SetHeader("X-Api-Key", apiKey);

            if (username.IsNotNullOrWhiteSpace() || password.IsNotNullOrWhiteSpace())
            {
                requestBuilder.NetworkCredential = new BasicNetworkCredential(username, password);
            }

            var request = requestBuilder.Build();
            request.Headers.ContentType = "application/json";
            request.Method = HttpMethod.Get;
            request.AllowAutoRedirect = true;

            return request;
        }

        private static bool MovieMatches(RadarrMovieMetadata movie, MovieSearchCriteria searchSpec)
        {
            if (searchSpec.TmdbId.HasValue && movie.TmdbId == searchSpec.TmdbId.Value)
            {
                return true;
            }

            if (searchSpec.ImdbId.IsNotNullOrWhiteSpace() &&
                ParseUtil.GetImdbId(movie.ImdbId)?.ToString("D7") == searchSpec.ImdbId)
            {
                return true;
            }

            if (searchSpec.SearchTerm.IsNotNullOrWhiteSpace())
            {
                var requested = NormalizeMovieSearchTerm(searchSpec.SearchTerm);
                var titles = new List<string> { movie.Title, movie.OriginalTitle };
                titles.AddRange(movie.AlternateTitles?.Select(t => t.Title) ?? Enumerable.Empty<string>());

                return titles.Any(t => NormalizeMovieSearchTerm(t) == requested);
            }

            return false;
        }

        private static IEnumerable<string> BuildMovieSearchTerms(string title, int? year)
        {
            title = title.Trim();

            yield return title;

            if (year.HasValue && !title.Contains(year.Value.ToString()))
            {
                yield return $"{title} {year.Value}";
            }
        }

        private static int GetMovieTitleSearchPriority(string title)
        {
            if (title.Any(c => c >= '\u0400' && c <= '\u04FF'))
            {
                return 0;
            }

            if (title.All(c => c <= '\u024F'))
            {
                return 1;
            }

            return 2;
        }

        private static bool MovieSearchTermEquals(string left, string right)
        {
            return NormalizeMovieSearchTerm(left) == NormalizeMovieSearchTerm(right);
        }

        private static IList<ReleaseInfo> AddOriginalMovieSearchTermToReleases(IList<ReleaseInfo> releases, string originalSearchTerm)
        {
            if (originalSearchTerm.IsNullOrWhiteSpace())
            {
                return releases;
            }

            var normalizedOriginal = NormalizeMovieSearchTerm(originalSearchTerm);
            var originalTitlePrefix = FormatMovieSearchTermForReleaseTitle(originalSearchTerm);
            var originalYear = GetMovieSearchTermYear(originalSearchTerm);

            foreach (var release in releases)
            {
                if (release.Title.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (NormalizeMovieSearchTerm(release.Title).Contains(normalizedOriginal))
                {
                    continue;
                }

                if (originalYear.HasValue && HasConflictingReleaseYear(release.Title, originalYear.Value))
                {
                    continue;
                }

                release.Title = $"{originalTitlePrefix} {release.Title}";
            }

            return releases;
        }

        private static string FormatMovieSearchTermForReleaseTitle(string searchTerm)
        {
            var normalized = string.Join(" ", searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

            if (TryGetTrailingMovieYear(normalized, out var year))
            {
                return $"{normalized.Substring(0, normalized.Length - 5).Trim()} ({year})";
            }

            return normalized;
        }

        private static string NormalizeMovieSearchTerm(string term)
        {
            if (term.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var normalized = string.Join(" ", term.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Trim()
                .ToLowerInvariant();

            if (TryGetTrailingMovieYear(normalized, out _))
            {
                normalized = normalized.Substring(0, normalized.Length - 5).Trim();
            }

            return normalized;
        }

        private static int? GetMovieSearchTermYear(string searchTerm)
        {
            var normalized = string.Join(" ", searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

            if (TryGetTrailingMovieYear(normalized, out var year))
            {
                return year;
            }

            return null;
        }

        private static bool TryGetTrailingMovieYear(string searchTerm, out int year)
        {
            if (searchTerm.Length > 5 && searchTerm[^5] == ' ')
            {
                return int.TryParse(searchTerm.AsSpan(searchTerm.Length - 4), out year);
            }

            year = 0;
            return false;
        }

        private static bool HasConflictingReleaseYear(string releaseTitle, int expectedYear)
        {
            return Regex.Matches(releaseTitle, @"(?:19|20)\d{2}")
                .Select(m => ParseUtil.CoerceInt(m.Value))
                .Any(year => year != expectedYear);
        }

        private static MovieSearchCriteria CopyMovieSearchCriteria(MovieSearchCriteria source)
        {
            return new MovieSearchCriteria
            {
                InteractiveSearch = source.InteractiveSearch,
                IndexerIds = source.IndexerIds,
                SearchTerm = source.SearchTerm,
                Categories = source.Categories,
                SearchType = source.SearchType,
                Limit = source.Limit,
                Offset = source.Offset,
                MinAge = source.MinAge,
                MaxAge = source.MaxAge,
                MinSize = source.MinSize,
                MaxSize = source.MaxSize,
                Source = source.Source,
                Host = source.Host,
                ImdbId = source.ImdbId,
                TmdbId = source.TmdbId,
                TraktId = source.TraktId,
                DoubanId = source.DoubanId,
                Year = source.Year,
                Genre = source.Genre
            };
        }

        private static BasicSearchCriteria CopyBasicSearchCriteria(BasicSearchCriteria source)
        {
            return new BasicSearchCriteria
            {
                InteractiveSearch = source.InteractiveSearch,
                IndexerIds = source.IndexerIds,
                SearchTerm = source.SearchTerm,
                Categories = source.Categories,
                SearchType = source.SearchType,
                Limit = source.Limit,
                Offset = source.Offset,
                MinAge = source.MinAge,
                MaxAge = source.MaxAge,
                MinSize = source.MinSize,
                MaxSize = source.MaxSize,
                Source = source.Source,
                Host = source.Host
            };
        }

        private class RadarrMovieMetadata
        {
            public string Title { get; set; }
            public string OriginalTitle { get; set; }
            public string ImdbId { get; set; }
            public int TmdbId { get; set; }
            public List<RadarrAlternateTitle> AlternateTitles { get; set; }
        }

        private class RadarrAlternateTitle
        {
            public string Title { get; set; }
        }
    }
}
