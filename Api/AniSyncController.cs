#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_ani_sync.Api.Anilist;
using jellyfin_ani_sync.Api.Annict;
using jellyfin_ani_sync.Api.Kitsu;
using jellyfin_ani_sync.Api.Shikimori;
using jellyfin_ani_sync.Api.Simkl;
using jellyfin_ani_sync.Configuration;
using jellyfin_ani_sync.Helpers;
using jellyfin_ani_sync.Models;
using jellyfin_ani_sync.Models.Mal;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace jellyfin_ani_sync.Api {
    [ApiController]
    [Route("[controller]")]
    public class AniSyncController : ControllerBase {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IUserDataManager _userDataManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<AniSyncController> _logger;

        public AniSyncController(IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            IServerApplicationHost serverApplicationHost,
            IHttpContextAccessor httpContextAccessor,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IApplicationPaths applicationPaths,
            IUserDataManager userDataManager,
            IFileSystem fileSystem) {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _serverApplicationHost = serverApplicationHost;
            _httpContextAccessor = httpContextAccessor;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _applicationPaths = applicationPaths;
            _userDataManager = userDataManager;
            _fileSystem = fileSystem;
            _logger = loggerFactory.CreateLogger<AniSyncController>();
        }

        [HttpGet]
        [Route("buildAuthorizeRequestUrl")]
        public string BuildAuthorizeRequestUrl(ApiName provider, string clientId, string clientSecret, string? url) {
            return new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, new ProviderApiAuth { ClientId = clientId, ClientSecret = clientSecret }, url).BuildAuthorizeRequestUrl();
        }

        [HttpGet]
        [Route("testAnimeListSaveLocation")]
        public async Task<IActionResult> TestAnimeSaveLocation(string saveLocation) {
            try {
                await using (System.IO.File.Create(
                                 Path.Combine(
                                     saveLocation,
                                     Path.GetRandomFileName()
                                 ),
                                 1,
                                 FileOptions.DeleteOnClose)) {
                }

                return Ok(string.Empty);
            } catch (Exception e) {
                return BadRequest(e.Message);
            }
        }

        [HttpGet]
        [Route("passwordGrant")]
        public async Task<IActionResult> PasswordGrantAuthentication(ApiName provider, string userId, string username, string password) {
            if (new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, new ProviderApiAuth { ClientId = username, ClientSecret = password }).GetToken(Guid.Parse(userId)) != null) {
                if (provider == ApiName.Kitsu) {
                    var userConfig = Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId));

                    if (userConfig != null) {
                        KitsuApiCalls kitsuApiCalls = new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, userConfig);
                        var kitsuUserConfig = await kitsuApiCalls.GetUserInformation();
                        var existingKeyPair = userConfig.KeyPairs.FirstOrDefault(item => item.Key == "KitsuUserId");
                        if (existingKeyPair != null) {
                            existingKeyPair.Value = kitsuUserConfig.Id.ToString();
                        } else {
                            userConfig.KeyPairs.Add(new KeyPairs { Key = "KitsuUserId", Value = kitsuUserConfig.Id.ToString() });
                        }

                        Plugin.Instance.SaveConfiguration();
                    }
                }

                return Ok();
            }

            return BadRequest();
        }

        [HttpGet]
        [Route("authCallback")]
        public IActionResult MalCallback(string code) {
            Guid userId = Plugin.Instance.PluginConfiguration.currentlyAuthenticatingUser;
            ApiName provider = Plugin.Instance.PluginConfiguration.currentlyAuthenticatingProvider;
            if (userId != null && provider != null) {
                new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory).GetToken(userId, code);
                Plugin.Instance.PluginConfiguration.currentlyAuthenticatingUser = Guid.Empty;
                Plugin.Instance.SaveConfiguration();
                if (!string.IsNullOrEmpty(Plugin.Instance?.PluginConfiguration.callbackRedirectUrl)) {
                    string replacedCallbackRedirectUrl = Plugin.Instance.PluginConfiguration.callbackRedirectUrl.Replace("{{LocalIpAddress}}", Request.HttpContext.Connection.LocalIpAddress != null ? Request.HttpContext.Connection.LocalIpAddress.ToString() : "localhost")
                        .Replace("{{LocalPort}}", _serverApplicationHost.ListenWithHttps ? _serverApplicationHost.HttpsPort.ToString() : _serverApplicationHost.HttpPort.ToString());

                    if (Uri.TryCreate(replacedCallbackRedirectUrl, UriKind.Absolute, out _)) {
                        return Redirect(replacedCallbackRedirectUrl);
                    } else {
                        _logger.LogWarning($"Invalid redirect URL ({replacedCallbackRedirectUrl}), skipping redirect.");
                    }
                }

                return Ok();
            } else {
                _logger.LogError("Authenticated user ID could not be found in the configuration. Please regenerate the authentication URL and try again");
                return StatusCode(500);
            }
        }

        [HttpGet]
        [Route("user")]
        // this only works for mal atm, needs to work for anilist as well
        public async Task<ActionResult> GetUser(ApiName apiName, string userId) {
            UserConfig? userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId));
            if (userConfig == null) {
                _logger.LogError("User not found");
                return new StatusCodeResult(500);
            }

            switch (apiName) {
                case ApiName.Mal:
                    MalApiCalls malApiCalls = new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId)));

                    return new OkObjectResult(await malApiCalls.GetUserInformation());
                case ApiName.AniList:
                    AniListApiCalls aniListApiCalls = new AniListApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId)));

                    AniListViewer.Viewer? user = await aniListApiCalls.GetCurrentUser();
                    return new OkObjectResult(new User {
                        Name = user?.Name
                    });
                case ApiName.Kitsu:
                    KitsuApiCalls kitsuApiCalls;
                    try {
                        kitsuApiCalls = new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId)));
                    } catch (ArgumentNullException) {
                        _logger.LogError("User could not be retrieved from API");
                        return new StatusCodeResult(500);
                    }

                    var apiCall = await kitsuApiCalls.GetUserInformation();
                    return new OkObjectResult(new User {
                        Name = apiCall.Name
                    });
                case ApiName.Annict:
                    // sleep the thread for a short amount of time to let the user config save
                    Thread.Sleep(100);
                    AnnictApiCalls annictApiCalls;
                    try {
                        annictApiCalls = new AnnictApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId)));
                    } catch (ArgumentNullException) {
                        _logger.LogError("User could not be retrieved from API");
                        return new StatusCodeResult(500);
                    }

                    var annictApiCall = await annictApiCalls.GetCurrentUser();
                    return new OkObjectResult(new User {
                        Name = annictApiCall.AnnictSearchData.Viewer.username
                    });
                case ApiName.Shikimori:
                    string? shikimoriAppName = ConfigHelper.GetShikimoriAppName(_logger);
                    if (string.IsNullOrEmpty(shikimoriAppName)) {
                        return new StatusCodeResult(500);
                    }

                    ShikimoriApiCalls shikimoriApiCalls = new ShikimoriApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, new Dictionary<string, string> { { "User-Agent", shikimoriAppName } }, userConfig);

                    ShikimoriApiCalls.User? shikimoriUserApiCall = await shikimoriApiCalls.GetUserInformation();
                    if (shikimoriUserApiCall != null) {
                        return new OkObjectResult(new User {
                            Name = shikimoriUserApiCall.Name
                        });
                    } else {
                        _logger.LogError("User could not be retrieved from API");
                        return new StatusCodeResult(500);
                    }
                case ApiName.Simkl:
                    string? simklClientId = ConfigHelper.GetSimklClientId(_logger);
                    if (string.IsNullOrEmpty(simklClientId)) {
                        return new StatusCodeResult(500);
                    }

                    var simklApiCalls = new SimklApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, new Dictionary<string, string> { { "simkl-api-key", simklClientId } }, userConfig);

                    if (await simklApiCalls.GetLastActivity()) {
                        return new OkObjectResult(new User {
                            Name = null
                        });
                    } else {
                        return new StatusCodeResult(500);
                    }
            }

            throw new Exception("Provider not supported.");
        }

        [HttpGet]
        [Route("parameters")]
        public object GetFrontendParameters() {
            Parameters toReturn = new Parameters();
            toReturn.providerList = new List<ExpandoObject>();
            foreach (ApiName apiName in Enum.GetValues<ApiName>()) {
                dynamic provider = new ExpandoObject();
                provider.Name = apiName.GetType()
                    .GetMember(apiName.ToString())
                    .First()
                    .GetCustomAttribute<DisplayAttribute>()
                    ?.GetName();
                provider.Key = apiName;
                toReturn.providerList.Add(provider);
            }

            toReturn.localIpAddress = Request.HttpContext.Connection.LocalIpAddress != null ? Request.HttpContext.Connection.LocalIpAddress.ToString() : "localhost";
            toReturn.localPort = _serverApplicationHost.ListenWithHttps ? _serverApplicationHost.HttpsPort : _serverApplicationHost.HttpPort;
            toReturn.https = _serverApplicationHost.ListenWithHttps;
            return toReturn;
        }

        private class Parameters {
            public string localIpAddress { get; set; }
            public int localPort { get; set; }
            public bool https { get; set; }
            public List<ExpandoObject> providerList { get; set; }
        }

        [HttpGet]
        [Route("apiUrlTest")]
        public string ApiUrlTest() {
            return "This is the correct URL.";
        }

        [HttpPost]
        [Route("sync")]
        public Task Sync(ApiName provider, string userId, SyncHelper.Status status, SyncAction syncAction) {
            switch (syncAction) {
                case SyncAction.UpdateProvider:
                    SyncProviderFromLocal syncProviderFromLocal = new SyncProviderFromLocal(_userManager, _libraryManager, _loggerFactory, _httpClientFactory, _applicationPaths, _userDataManager, _fileSystem, userId);
                    return syncProviderFromLocal.SyncFromLocal();
                case SyncAction.UpdateJellyfin:
                    Sync sync = new Sync(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _userManager, _libraryManager, _applicationPaths, _userDataManager, provider, status);
                    return sync.SyncFromProvider(userId);
            }

            return Task.CompletedTask;
        }

        public enum SyncAction {
            UpdateProvider,
            UpdateJellyfin
        }
    }
}