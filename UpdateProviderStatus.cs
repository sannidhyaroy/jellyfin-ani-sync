#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_ani_sync.Api;
using jellyfin_ani_sync.Api.Anilist;
using jellyfin_ani_sync.Api.Annict;
using jellyfin_ani_sync.Api.Kitsu;
using jellyfin_ani_sync.Api.Shikimori;
using jellyfin_ani_sync.Api.Simkl;
using jellyfin_ani_sync.Configuration;
using jellyfin_ani_sync.Helpers;
using jellyfin_ani_sync.Models;
using jellyfin_ani_sync.Models.Mal;
using Jellyfin.Data.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace jellyfin_ani_sync {
    public class UpdateProviderStatus {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpContextAccessor _httpContextAccessor;

        private readonly ILogger<UpdateProviderStatus> _logger;

        private ApiCallHelpers _apiCallHelpers;
        private UserConfig? _userConfig;
        private Type _animeType;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;

        private ApiName _apiName;
        private readonly ILoggerFactory _loggerFactory;
        private AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse _apiIds = new ();

        public UpdateProviderStatus(IFileSystem fileSystem,
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            IServerApplicationHost serverApplicationHost,
            IHttpClientFactory httpClientFactory,
            IApplicationPaths applicationPaths) {
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _httpContextAccessor = httpContextAccessor;
            _serverApplicationHost = serverApplicationHost;
            _httpClientFactory = httpClientFactory;
            _applicationPaths = applicationPaths;
            _logger = loggerFactory.CreateLogger<UpdateProviderStatus>();
            _loggerFactory = loggerFactory;
        }


        public async Task Update(BaseItem e, Guid userId, bool playedToCompletion) {
            var video = e as Video;
            Episode episode = video as Episode;
            Movie movie = video as Movie;
            if (video is Episode) {
                _animeType = typeof(Episode);
            } else if (video is Movie) {
                _animeType = typeof(Movie);
                video.IndexNumber = 1;
            }

            _userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);
            if (_userConfig == null) {
                _logger.LogWarning($"The user {userId} does not exist in the plugins config file. Skipping");
                return;
            }

            if (_userConfig.UserApiAuth == null) {
                _logger.LogWarning($"The user {userId} is not authenticated. Skipping");
                return;
            }

            if (LibraryCheck(_userConfig, _libraryManager, _fileSystem, _logger, e) && video is Episode or Movie && playedToCompletion) {
                if ((video is Episode && (episode.IndexNumber == null ||
                                          episode.Season.IndexNumber == null)) ||
                    (video is Movie && movie.IndexNumber == null)) {
                    _logger.LogError("Video does not contain required index numbers to sync; skipping");
                    return;
                }

                (int? aniDbId, int? episodeOffset) aniDbId = await GetAniDb(episode, movie);

                foreach (UserApiAuth userApiAuth in _userConfig.UserApiAuth) {
                    _apiName = userApiAuth.Name;
                    _logger.LogInformation($"Using provider {userApiAuth.Name}...");
                    switch (userApiAuth.Name) {
                        case ApiName.Mal:
                            _apiCallHelpers = new ApiCallHelpers(malApiCalls: new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _userConfig));
                            if (_apiIds.MyAnimeList != null && _apiIds.MyAnimeList != 0 && (episode != null && episode.Season.IndexNumber.Value != 0)) {
                                await CheckUserListAnimeStatus(_apiIds.MyAnimeList.Value, _animeType == typeof(Episode)
                                        ? (aniDbId.episodeOffset != null
                                            ? episode.IndexNumber.Value - aniDbId.episodeOffset.Value
                                            : episode.IndexNumber.Value)
                                        : movie.IndexNumber.Value,
                                    false);
                                continue;
                            }

                            break;
                        case ApiName.AniList:
                            _apiCallHelpers = new ApiCallHelpers(aniListApiCalls: new AniListApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _userConfig));
                            if (_apiIds.Anilist != null && _apiIds.Anilist != 0 && (episode != null && episode.Season.IndexNumber.Value != 0)) {
                                await CheckUserListAnimeStatus(_apiIds.Anilist.Value, _animeType == typeof(Episode)
                                        ? (aniDbId.episodeOffset != null
                                            ? episode.IndexNumber.Value - aniDbId.episodeOffset.Value
                                            : episode.IndexNumber.Value)
                                        : movie.IndexNumber.Value,
                                    false);
                                continue;
                            } else if (_animeType == typeof(Episode) ? episode.Series.ProviderIds.ContainsKey("AniList") && episode.Season.IndexNumber.Value == 1 : movie.ProviderIds.ContainsKey("AniList")) {
                                if (_animeType == typeof(Episode) ? int.TryParse(episode.Series.ProviderIds["AniList"], out int aniListId) : int.TryParse(movie.ProviderIds["AniList"], out aniListId)) {
                                    await CheckUserListAnimeStatus(aniListId, _animeType == typeof(Episode)
                                            ? (aniDbId.episodeOffset != null
                                                ? episode.IndexNumber.Value - aniDbId.episodeOffset.Value
                                                : episode.IndexNumber.Value)
                                            : movie.IndexNumber.Value,
                                        false);
                                    continue;
                                }
                            }

                            break;
                        case ApiName.Kitsu:
                            _apiCallHelpers = new ApiCallHelpers(kitsuApiCalls: new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _userConfig));
                            if (_apiIds.Kitsu != null && _apiIds.Kitsu != 0 && (episode != null && episode.Season.IndexNumber.Value != 0)) {
                                await CheckUserListAnimeStatus(_apiIds.Kitsu.Value, _animeType == typeof(Episode)
                                        ? (aniDbId.episodeOffset != null
                                            ? episode.IndexNumber.Value - aniDbId.episodeOffset.Value
                                            : episode.IndexNumber.Value)
                                        : movie.IndexNumber.Value,
                                    false);
                                continue;
                            }

                            break;
                        case ApiName.Annict:
                            // annict works differently to the other providers, so we have to go the traditional route
                            _apiCallHelpers = new ApiCallHelpers(annictApiCalls: new AnnictApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _userConfig));
                            break;
                        case ApiName.Shikimori:
                            string? shikimoriAppName = ConfigHelper.GetShikimoriAppName(_logger);
                            if (shikimoriAppName == null) return;
                            _apiCallHelpers = new ApiCallHelpers(shikimoriApiCalls: new ShikimoriApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, new Dictionary<string, string> { { "User-Agent", shikimoriAppName } }, _userConfig));
                            break;
                        case ApiName.Simkl:
                            string? simklClientId = ConfigHelper.GetSimklClientId(_logger);
                            if (simklClientId == null) return;
                            _apiCallHelpers = new ApiCallHelpers(simklApiCalls: new SimklApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, new Dictionary<string, string> { { "simkl-api-key", simklClientId } }, _userConfig));
                            if (((_apiIds.MyAnimeList != null && _apiIds.MyAnimeList != 0) ||
                                 (_apiIds.Anilist != null && _apiIds.Anilist != 0) ||
                                 (_apiIds.Kitsu != null && _apiIds.Kitsu != 0) ||
                                 (_apiIds.AniDb != null && _apiIds.AniDb != 0)) &&
                                (episode != null && episode.Season.IndexNumber.Value != 0)) {
                                await CheckUserListAnimeStatus(_apiIds, _animeType == typeof(Episode)
                                        ? (aniDbId.episodeOffset != null
                                            ? episode.IndexNumber.Value - aniDbId.episodeOffset.Value
                                            : episode.IndexNumber.Value)
                                        : movie.IndexNumber.Value, _animeType == typeof(Episode) ? episode.SeriesName : video.Name,
                                    false);
                                continue;
                            }

                            break;
                    }

                    List<Anime> animeList = await _apiCallHelpers.SearchAnime(_animeType == typeof(Episode) ? episode.SeriesName : video.Name);
                    bool found = false;
                    if (animeList != null) {
                        foreach (var anime in animeList) {
                            if (_apiName != ApiName.Annict && TitleCheck(anime, episode, movie) || (_apiName == ApiName.Annict &&
                                                                                                    _apiIds.MyAnimeList != null &&
                                                                                                    anime.Id == _apiIds.MyAnimeList)) {
                                _logger.LogInformation($"({_apiName}) Found matching {(_animeType == typeof(Episode) ? "series" : "movie")}: {GetAnimeTitle(anime)}");
                                Anime matchingAnime = anime;
                                if (_animeType == typeof(Episode)) {
                                    int episodeNumber = episode.IndexNumber.Value;
                                    if (_apiName != ApiName.Annict) {
                                        // should have already found the appropriate series/season/movie, no need to do other checks
                                        if (episode?.Season.IndexNumber is > 1) {
                                            // if this is not the first season, then we need to lookup the related season.
                                            matchingAnime = await GetDifferentSeasonAnime(anime.Id, episode.Season.IndexNumber.Value);
                                            if (matchingAnime == null) {
                                                _logger.LogWarning($"({_apiName}) Could not find next season");
                                                found = true;
                                                break;
                                            }

                                            _logger.LogInformation($"({_apiName}) Season being watched is {GetAnimeTitle(matchingAnime)}");
                                        } else if (episode?.Season.IndexNumber == 0) {
                                            // the episode is an ova or special
                                            matchingAnime = await GetOva(anime.Id, episode.Name);
                                            if (matchingAnime == null) {
                                                _logger.LogWarning($"({_apiName}) Could not find OVA");
                                                found = true;
                                                break;
                                            }
                                        } else if (matchingAnime.NumEpisodes < episode?.IndexNumber.Value) {
                                            _logger.LogInformation($"({_apiName}) Watched episode passes total episodes in season! Checking for additional seasons/cours...");
                                            // either we have found the wrong series (highly unlikely) or it is a multi cour series/Jellyfin has grouped next season into the current.
                                            int seasonEpisodeCounter = matchingAnime.NumEpisodes;
                                            int totalEpisodesWatched = 0;
                                            int seasonCounter = episode.Season.IndexNumber.Value;
                                            int episodeCount = episode.IndexNumber.Value;
                                            Anime season = matchingAnime;
                                            bool isRootSeason = false;
                                            while (seasonEpisodeCounter < episodeCount) {
                                                var nextSeason = await GetDifferentSeasonAnime(season.Id, seasonCounter + 1);
                                                if (nextSeason == null) {
                                                    _logger.LogWarning($"({_apiName}) Could not find next season");
                                                    if (matchingAnime.Status == AiringStatus.currently_airing && matchingAnime.NumEpisodes == 0) {
                                                        _logger.LogWarning($"({_apiName}) Show is currently airing and API reports 0 episodes, going to use first season");
                                                        isRootSeason = true;
                                                    }

                                                    found = true;
                                                    break;
                                                }

                                                seasonEpisodeCounter += nextSeason.NumEpisodes;
                                                seasonCounter++;
                                                // complete the current season; we have surpassed it onto the next season/cour
                                                totalEpisodesWatched += season.NumEpisodes;
                                                await CheckUserListAnimeStatus(season.Id, season.NumEpisodes, false);
                                                season = nextSeason;
                                            }

                                            if (!isRootSeason) {
                                                if (season.Id != matchingAnime.Id) {
                                                    matchingAnime = season;
                                                    episodeNumber = episodeCount - totalEpisodesWatched;
                                                } else {
                                                    break;
                                                }
                                            }
                                        }
                                    }

                                    await CheckUserListAnimeStatus(matchingAnime.Id, episodeNumber, alternativeId: matchingAnime.AlternativeId);
                                    found = true;
                                    break;
                                }

                                if (_animeType == typeof(Movie)) {
                                    await CheckUserListAnimeStatus(matchingAnime.Id, movie.IndexNumber.Value, alternativeId: matchingAnime.AlternativeId);
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!found) {
                        _logger.LogWarning($"({_apiName}) Series not found");
                    }
                }
            }
        }

        /// <summary>
        /// Gets the AniDB ID from various metadata providers.
        /// </summary>
        /// <param name="episode">Episode of the season to get the AniDB of.</param>
        /// <param name="movie">Movie to get the AniDB of.</param>
        /// <returns>The AniDB ID and episode offset.</returns>
        private async Task<(int? aniDbId, int? episodeOffset)> GetAniDb(Episode? episode, Movie? movie) {
            (int? aniDbId, int? episodeOffset) aniDbId = (null, null);
            if (_animeType == typeof(Episode)
                    ? episode.ProviderIds != null &&
                      episode.Series.ProviderIds.ContainsKey("AniList") &&
                      episode.Season.IndexNumber.Value == 1 &&
                      int.TryParse(episode.Series.ProviderIds["AniList"], out int retrievedAniListId)
                    : movie.ProviderIds != null &&
                      movie.ProviderIds.ContainsKey("AniList") &&
                      int.TryParse(movie.ProviderIds["AniList"], out retrievedAniListId)) {
                _logger.LogInformation("AniList ID found. Retrieving provider IDs from offline database...");
                _apiIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(NamedClient.Default), retrievedAniListId, AnimeOfflineDatabaseHelpers.Source.Anilist);
                if (_apiIds is null) {
                    _apiIds = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                        Anilist = retrievedAniListId
                    };
                    _logger.LogWarning("Did not get provider IDs, defaulting to episode provided AniList ID");
                } else {
                    _logger.LogInformation("Retrieved provider IDs");
                }
            } else if (_animeType == typeof(Episode)
                           ? (episode.Series.ProviderIds.ContainsKey("Tvdb") ||
                              episode.Season.ProviderIds.ContainsKey("Anidb") ||
                              episode.Series.ProviderIds.ContainsKey("Anidb"))
                           : movie.ProviderIds != null &&
                             movie.ProviderIds.ContainsKey("Anidb")) {
                AnimeListHelpers.AnimeListXml animeListXml = await AnimeListHelpers.GetAnimeListFileContents(_logger, _loggerFactory, _httpClientFactory, _applicationPaths);
                aniDbId = _animeType == typeof(Episode)
                    ? await AnimeListHelpers.GetAniDbId(_logger, _loggerFactory, _httpClientFactory, _applicationPaths, episode, episode.IndexNumber.Value, episode.Season.IndexNumber.Value, animeListXml)
                    : await AnimeListHelpers.GetAniDbId(_logger, _loggerFactory, _httpClientFactory, _applicationPaths, movie, movie.IndexNumber.Value, 1, animeListXml);
                if (aniDbId.aniDbId != null) {
                    _logger.LogInformation("Retrieving provider IDs from offline database...");
                    _apiIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(NamedClient.Default), aniDbId.aniDbId.Value, AnimeOfflineDatabaseHelpers.Source.Anidb);
                    if (_apiIds is null) {
                        _apiIds = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                            AniDb = aniDbId.aniDbId
                        };
                        _logger.LogWarning("Did not get provider IDs, defaulting to episode provided AniDb ID");
                    } else {
                        _logger.LogInformation("Retrieved provider IDs");
                    }
                }
            }

            return aniDbId;
        }

        /// <summary>
        /// Gets anime's title.
        /// </summary>
        /// <param name="anime">The API anime</param>
        /// <returns>
        /// <see cref="Anime.Title"/> if it isn't empty.<br/>
        /// If it is, then the first <see cref="AlternativeTitles.Synonyms">synonym</see>.<br/>
        /// If there isn't any, then the <see cref="AlternativeTitles.Ja">japanese title</see>.
        /// </returns>
        private static string GetAnimeTitle(Anime anime) {
            var title = string.IsNullOrWhiteSpace(anime.Title)
                ? anime.AlternativeTitles.Synonyms.Count > 0
                    ? anime.AlternativeTitles.Synonyms[0]
                    : anime.AlternativeTitles.Ja
                : anime.Title;
            return title;
        }

        /// <summary>
        /// Checks if the Jellyfin library entry matches the API names.
        /// </summary>
        /// <param name="anime">The API anime.</param>
        /// <param name="episode">The episode if its a series.</param>
        /// <param name="movie">The movie if its a single episode movie.</param>
        /// <returns></returns>
        private bool TitleCheck(Anime anime, Episode episode, Movie movie) {
            var title = _animeType == typeof(Episode) ? episode.SeriesName : movie.Name;
            return CompareStrings(anime.Title, title) ||
                   CompareStrings(anime.AlternativeTitles.En, title) ||
                   (anime.AlternativeTitles.Ja != null && CompareStrings(anime.AlternativeTitles.Ja, title)) ||
                   (anime.AlternativeTitles.Synonyms != null && anime.AlternativeTitles.Synonyms.Any(synonym => CompareStrings(synonym, title)));
        }

        /// <summary>
        /// Compare two strings, ignoring symbols and case.
        /// </summary>
        /// <param name="first">The first string.</param>
        /// <param name="second">The second string.</param>
        /// <returns>True if first string is equal to second string, false if not.</returns>
        private bool CompareStrings(string first, string second) {
            return String.Compare(first, second, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols) == 0;
        }

        /// <summary>
        /// Check if a string exists in another, ignoring symbols and case.
        /// </summary>
        /// <param name="first">The first string.</param>
        /// <param name="second">The second string.</param>
        /// <returns>True if first string contains second string, false if not.</returns>
        private bool ContainsExtended(string first, string second) {
            return StringFormatter.RemoveSpecialCharacters(first).Contains(StringFormatter.RemoveSpecialCharacters(second), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the supplied item is in a folder the user wants to track for anime updates.
        /// </summary>
        /// <param name="userConfig">User config.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="fileSystem">File system instance.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="item">Item to check location of.</param>
        /// <returns></returns>
        public static bool LibraryCheck(UserConfig userConfig, ILibraryManager libraryManager, IFileSystem fileSystem, ILogger logger, BaseItem item) {
            try {
                if (userConfig.LibraryToCheck is { Length: > 0 }) {
                    var folders = libraryManager.GetVirtualFolders().Where(item => userConfig.LibraryToCheck.Contains(item.ItemId));

                    foreach (var folder in folders) {
                        foreach (var location in folder.Locations) {
                            if (item.Path != null && fileSystem.ContainsSubPath(location, item.Path)) {
                                // item is in a path of a folder the user wants to be monitored
                                return true;
                            }
                        }
                    }
                } else {
                    // user has no library filters
                    return true;
                }

                logger.LogInformation("Item is in a folder the user does not want to be monitored; ignoring");
                return false;
            } catch (Exception e) {
                logger.LogInformation($"Library check ran into an issue: {e.Message}");
                return false;
            }
        }


        private async Task CheckUserListAnimeStatus(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse matchingIds, int episodeNumber, string title, bool? overrideCheckRewatch = null, string? alternativeId = null) {
            Anime detectedAnime = await GetAnime(matchingIds, title, alternativeId: alternativeId);

            await CheckUserListAnimeStatusBase(detectedAnime, episodeNumber, overrideCheckRewatch, alternativeId);
        }

        private async Task CheckUserListAnimeStatus(int matchingAnimeId, int episodeNumber, bool? overrideCheckRewatch = null, string? alternativeId = null) {
            Anime detectedAnime = await GetAnime(matchingAnimeId, alternativeId: alternativeId);

            await CheckUserListAnimeStatusBase(detectedAnime, episodeNumber, overrideCheckRewatch, alternativeId);
        }

        private async Task CheckUserListAnimeStatusBase(Anime detectedAnime, int episodeNumber, bool? overrideCheckRewatch = null, string? alternativeId = null) {
            if (detectedAnime == null) return;
            if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Watching && _apiName != ApiName.Annict) {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on watching list");
                await UpdateAnimeStatus(detectedAnime, episodeNumber);
                return;
            }

            // only plan to watch
            if (_userConfig.PlanToWatchOnly) {
                bool updated = false;
                if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Plan_to_watch) {
                    _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on plan to watch list");
                    if (_apiName == ApiName.Annict) {
                        updated = true;
                        await UpdateAnnictStatus(detectedAnime, episodeNumber);
                    } else {
                        updated = true;
                        await UpdateAnimeStatus(detectedAnime, episodeNumber);
                    }
                }

                if (_apiName != ApiName.Annict) {
                    // also check if rewatch completed is checked
                    _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) not found in plan to watch list{(_userConfig.RewatchCompleted ? ", checking completed list.." : null)}");
                    updated = true;
                    await CheckIfRewatchCompleted(detectedAnime, episodeNumber, overrideCheckRewatch);
                }

                if (!updated) {
                    _logger.LogInformation($"({_apiName}) Could not update.");
                }

                return;
            }

            _logger.LogInformation("User does not have plan to watch only ticked");

            if (_apiName != ApiName.Annict)
                // check if rewatch completed is checked
                await CheckIfRewatchCompleted(detectedAnime, episodeNumber, overrideCheckRewatch);

            // everything else
            if (detectedAnime.MyListStatus != null) {
                // anime is on user list
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on {detectedAnime.MyListStatus.Status} list");
                if (detectedAnime.MyListStatus.Status == Status.Completed) {
                    if (_apiName != ApiName.Annict)
                        _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on Completed list, but user does not want to automatically set as rewatching. Skipping");
                    return;
                }
            } else {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) not on user list");
            }

            if (_apiName == ApiName.Annict) {
                await UpdateAnnictStatus(detectedAnime, episodeNumber);
            } else {
                await UpdateAnimeStatus(detectedAnime, episodeNumber);
            }
        }

        private async Task UpdateAnnictStatus(Anime detectedAnime, int episodeNumber) {
            // rewatching isnt supported by annict; skip
            if (detectedAnime.MyListStatus?.Status == Status.Completed) return;
            if (_userConfig.PlanToWatchOnly && detectedAnime.MyListStatus is null) {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found, but not on plan to watch list");
                return;
            }

            if (detectedAnime.NumEpisodes == episodeNumber) {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) complete, marking anime as complete");
                await _apiCallHelpers.UpdateAnime(detectedAnime.Id, 1, Status.Completed, alternativeId: detectedAnime.AlternativeId, ids: _apiIds);
                return;
            }

            if (detectedAnime.NumEpisodes > episodeNumber && detectedAnime.MyListStatus?.Status != Status.Watching) {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) being marked as watching");
                await _apiCallHelpers.UpdateAnime(detectedAnime.Id, 1, Status.Watching, alternativeId: detectedAnime.AlternativeId, ids: _apiIds);
            } else {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) already set to watching, not updating again");
            }
        }

        private async Task CheckIfRewatchCompleted(Anime detectedAnime, int indexNumber, bool? overrideCheckRewatch) {
            if (overrideCheckRewatch == null ||
                overrideCheckRewatch.Value ||
                detectedAnime.MyListStatus is { Status: Status.Completed } ||
                detectedAnime.MyListStatus is { Status: Status.Rewatching } && detectedAnime.MyListStatus.NumEpisodesWatched < indexNumber) {
                if (_apiName == ApiName.Simkl) {
                    _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on completed list, but {_apiName} does not support re-watching. Skipping");
                    return;
                }

                if (_userConfig.RewatchCompleted) {
                    if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Completed) {
                        _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on completed list, setting as re-watching");
                        await UpdateAnimeStatus(detectedAnime, indexNumber, true, detectedAnime.MyListStatus.RewatchCount, true);
                    }
                } else {
                    _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on Completed list, but user does not want to automatically set as rewatching. Skipping");
                }
            } else if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.NumEpisodesWatched >= indexNumber) {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found, but provider reports episode already watched. Skipping");
            } else if (_userConfig.PlanToWatchOnly) {
                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found, but not on completed or plan to watch list. Skipping");
            }
        }

        /// <summary>
        /// Get a single result from a user anime search.
        /// </summary>
        /// <param name="animeId">ID of the anime you want to get.</param>
        /// <param name="status">User status of the show.</param>
        /// <returns>Single anime result.</returns>
        private async Task<Anime> GetAnime(int animeId, Status? status = null, string? alternativeId = null) {
            Anime anime = await _apiCallHelpers.GetAnime(animeId);

            if (anime != null && ((status != null && anime.MyListStatus != null && anime.MyListStatus.Status == status) || status == null)) {
                return anime;
            }

            return null;
        }

        private async Task<Anime> GetAnime(AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse animeIds, string title, Status? status = null, string? alternativeId = null) {
            Anime anime = await _apiCallHelpers.GetAnime(animeIds, title);

            if (anime != null && ((status != null && anime.MyListStatus != null && anime.MyListStatus.Status == status) || status == null)) {
                return anime;
            }

            return null;
        }

        /// <summary>
        /// Update a users anime status.
        /// </summary>
        /// <param name="detectedAnime">The anime search result to update.</param>
        /// <param name="episodeNumber">The episode number to update the anime to.</param>
        /// <param name="setRewatching">Whether to set the show as being re-watched or not.</param>
        private async Task UpdateAnimeStatus(Anime detectedAnime, int? episodeNumber, bool? setRewatching = null, int? rewatchCount = null, bool firstTimeRewatch = false) {
            if (episodeNumber != null) {
                UpdateAnimeStatusResponse response;
                if (detectedAnime.MyListStatus != null) {
                    if (detectedAnime.MyListStatus.NumEpisodesWatched < episodeNumber.Value || detectedAnime.NumEpisodes == 1 ||
                        (setRewatching != null && setRewatching.Value && detectedAnime.MyListStatus.NumEpisodesWatched == episodeNumber.Value)) {
                        // covers the very rare occurence of re-watching the show and starting at the last episode
                        // movie or ova has only one episode, so just mark it as finished
                        if (episodeNumber.Value == detectedAnime.NumEpisodes || detectedAnime.NumEpisodes == 1) {
                            // either watched all episodes or the anime only has a single episode (ova)
                            if (detectedAnime.NumEpisodes == 1) {
                                // its a movie or ova since it only has one "episode", so the start and end date is the same
                                response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    1,
                                    Status.Completed,
                                    startDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now,
                                    endDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now,
                                    isRewatching: false,
                                    numberOfTimesRewatched: (setRewatching != null && setRewatching.Value) || detectedAnime.MyListStatus.IsRewatching ? detectedAnime.MyListStatus.RewatchCount + 1 : null,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            } else {
                                // user has reached the number of episodes in the anime, set as completed
                                response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    endDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now,
                                    isRewatching: false,
                                    numberOfTimesRewatched: (setRewatching != null && setRewatching.Value) || detectedAnime.MyListStatus.IsRewatching ? detectedAnime.MyListStatus.RewatchCount + 1 : null,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            }

                            _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) complete, marking anime as complete{(_apiName != ApiName.Mal && (setRewatching != null && setRewatching.Value) ? ", increasing re-watch count by 1" : "")}");
                            if ((detectedAnime.MyListStatus.IsRewatching || (detectedAnime.NumEpisodes == 1 && detectedAnime.MyListStatus.Status == Status.Completed) || (setRewatching != null && setRewatching.Value)) && _apiName == ApiName.Mal) {
                                // also increase number of times re-watched by 1
                                // only way to get the number of times re-watched is by doing the update and capturing the response, and then re-updating for MAL :/
                                _logger.LogInformation($"({_apiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) has also been re-watched, increasing re-watch count by 1");
                                response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    numberOfTimesRewatched: response.NumTimesRewatched + 1,
                                    isRewatching: false,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            }
                        } else {
                            if (detectedAnime.MyListStatus.IsRewatching) {
                                // MAL likes to mark re-watching shows as completed, instead of watching. I guess technically both are correct
                                _logger.LogInformation($"({_apiName}) User is re-watching {(_animeType == typeof(Episode) ? "series" : "movie")} ({GetAnimeTitle(detectedAnime)}), set as completed but update re-watch progress");
                                response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    isRewatching: true,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            } else {
                                if (episodeNumber > 1) {
                                    // don't set start date after first episode
                                    response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                        episodeNumber.Value,
                                        Status.Watching,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                } else {
                                    _logger.LogInformation($"({_apiName}) Setting new {(_animeType == typeof(Episode) ? "series" : "movie")} ({GetAnimeTitle(detectedAnime)}) as watching.");
                                    response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                        episodeNumber.Value,
                                        Status.Watching,
                                        startDate: DateTime.Now,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                }
                            }
                        }

                        if (response != null) {
                            _logger.LogInformation($"({_apiName}) Updated {(_animeType == typeof(Episode) ? "series" : "movie")} ({GetAnimeTitle(detectedAnime)}) progress to {episodeNumber.Value}");
                        } else {
                            _logger.LogError($"({_apiName}) Could not update anime status");
                        }
                    } else {
                        if (setRewatching != null && setRewatching.Value) {
                            _logger.LogInformation($"({_apiName}) Series ({GetAnimeTitle(detectedAnime)}) has already been watched, marking anime as re-watching; progress of {episodeNumber.Value}");
                            if (_apiName == ApiName.Kitsu && firstTimeRewatch) {
                                response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    episodeNumber.Value,
                                    Status.Rewatching,
                                    true,
                                    detectedAnime.MyListStatus.RewatchCount + 1,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            } else {
                                response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    true,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                                // anilist seems to (at the moment) not allow you to set the show as rewatching and the progress at the same time; going to have to do a separate call
                                if (_apiName == ApiName.AniList) {
                                    response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                                        episodeNumber.Value,
                                        Status.Completed,
                                        true,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                }
                            }
                        } else {
                            response = null;
                            _logger.LogInformation($"({_apiName}) Provider reports episode already watched; not updating");
                        }
                    }
                } else {
                    // status is not set, must be a new anime
                    if (episodeNumber.Value == detectedAnime.NumEpisodes) {
                        // anime completed all at once or user has watched last episode
                        _logger.LogInformation($"({_apiName}) Adding new {(_animeType == typeof(Episode) ? "series" : "movie")} ({GetAnimeTitle(detectedAnime)}) to user list as completed with a progress of {episodeNumber.Value}");
                        response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                            episodeNumber.Value,
                            Status.Completed,
                            ids: _apiIds,
                            isShow: _animeType == typeof(Episode));
                    } else {
                        // not on last episodes so must still be watching
                        _logger.LogInformation($"({_apiName}) Adding new {(_animeType == typeof(Episode) ? "series" : "movie")} ({GetAnimeTitle(detectedAnime)}) to user list as watching with a progress of {episodeNumber.Value}");
                        response = await _apiCallHelpers.UpdateAnime(detectedAnime.Id,
                            episodeNumber.Value,
                            Status.Watching,
                            ids: _apiIds,
                            isShow: _animeType == typeof(Episode));
                    }
                }

                if (response == null) {
                    _logger.LogError($"({_apiName}) Could not update anime status");
                }
            }
        }

        /// <summary>
        /// Get further anime seasons. Jellyfin uses numbered seasons whereas MAL uses entirely different entities.
        /// </summary>
        /// <param name="animeId"></param>
        /// <param name="seasonNumber"></param>
        /// <returns></returns>
        private async Task<Anime> GetDifferentSeasonAnime(int animeId, int seasonNumber) {
            _logger.LogInformation($"({_apiName}) Attempting to get season 1...");
            Anime initialSeason = await _apiCallHelpers.GetAnime(animeId, getRelated: true);

            if (initialSeason != null) {
                int i = 1;
                while (i != seasonNumber) {
                    RelatedAnime initialSeasonRelatedAnime = initialSeason.RelatedAnime.FirstOrDefault(item => item.RelationType == RelationType.Sequel);
                    if (initialSeasonRelatedAnime != null) {
                        _logger.LogInformation($"({_apiName}) Attempting to get season {i + 1}...");
                        Anime nextSeason = await _apiCallHelpers.GetAnime(initialSeasonRelatedAnime.Anime.Id, getRelated: true);

                        if (nextSeason != null) {
                            initialSeason = nextSeason;
                        }
                    } else {
                        _logger.LogInformation($"({_apiName}) Could not find any related anime");
                        return null;
                    }

                    i++;
                }

                return initialSeason;
            }

            return null;
        }

        private async Task<Anime> GetOva(int animeId, string episodeName) {
            Anime anime = await _apiCallHelpers.GetAnime(animeId, getRelated: true);

            if (anime != null) {
                var listOfRelatedAnime = anime.RelatedAnime.Where(relation => relation.RelationType is RelationType.Side_Story or RelationType.Alternative_Version or RelationType.Alternative_Setting);
                foreach (RelatedAnime relatedAnime in listOfRelatedAnime) {
                    var detailedRelatedAnime = await _apiCallHelpers.GetAnime(relatedAnime.Anime.Id);
                    if (detailedRelatedAnime is { Title: { }, AlternativeTitles: { En: { } } }) {
                        if (ContainsExtended(detailedRelatedAnime.Title, episodeName) ||
                            ContainsExtended(detailedRelatedAnime.AlternativeTitles.En, episodeName) ||
                            (detailedRelatedAnime.AlternativeTitles.Ja != null && ContainsExtended(detailedRelatedAnime.AlternativeTitles.Ja, episodeName))) {
                            // rough match
                            return detailedRelatedAnime;
                        }
                    }
                }
            }

            // no matches
            return null;
        }
    }
}