using BeatSaverSharp;
using BeatSaverSharp.Models.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

// TODO: Clean up entire code
namespace SpotToBeat
{
    internal class Program
    {
        public static readonly Version version = new Version(1, 0, 0, 0);

        private static EmbedIOAuthServer _server;

        public static SpotifyOptions spotifyOptions = new SpotifyOptions();

        public static string downloadDir = "C:";

        // from stackoverflow https://stackoverflow.com/questions/61920206/how-to-make-a-link-clickable-in-c-sharp-console
        public static string TerminalURL(string caption, string url) => $"\u001B]8;;{url}\a{caption}\u001B]8;;\a";

        public class SpotifyOptions
        {
            public string ClientID { get; set; }
            public string ClientSecret { get; set; }
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
        }

        public static async Task Main()
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();


            builder.Configuration.Sources.Clear();

            IHostEnvironment env = builder.Environment;
            builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            spotifyOptions.ClientID = builder.Configuration.GetValue<string>("ClientID");
            spotifyOptions.ClientSecret = builder.Configuration.GetValue<string>("ClientSecret");
            spotifyOptions.AccessToken = builder.Configuration.GetValue<string>("AccessToken");
            spotifyOptions.RefreshToken = builder.Configuration.GetValue<string>("RefreshToken");

            Console.WriteLine($"Client ID: {spotifyOptions.ClientID}");
            Console.WriteLine($"Client Secret: {spotifyOptions.ClientSecret}");
            Console.WriteLine($"Access Token: {spotifyOptions.AccessToken}");
            Console.WriteLine($"Refresh Token: {spotifyOptions.RefreshToken}");

            IHost host = builder.Build();
            IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            lifetime.ApplicationStopping.Register(() =>
            {
                // save token to appsettings
                var json = JsonSerializer.Serialize(spotifyOptions);
                File.WriteAllText("appsettings.json", json);
                Console.WriteLine("Saved tokens to appsettings.json, shutting down!");
            });

            lifetime.ApplicationStarted.Register(async () =>
            {
                if (spotifyOptions.AccessToken != "" && spotifyOptions.RefreshToken != "")
                {
                    // refresh token first, before intializing the client
                    var newResponse = await new OAuthClient().RequestToken(
                        new AuthorizationCodeRefreshRequest(spotifyOptions.ClientID, spotifyOptions.ClientSecret, spotifyOptions.RefreshToken)
                    );
                    spotifyOptions.AccessToken = newResponse.AccessToken;
                    spotifyOptions.RefreshToken = newResponse.RefreshToken;
                    SpotToBeat(spotifyOptions.AccessToken);
                }
                else
                {
                    // Make sure "http://localhost:5543/callback" is in your spotify application as redirect uri!
                    _server = new EmbedIOAuthServer(new Uri("http://localhost:5543/callback"), 5543);
                    await _server.Start();

                    _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                    _server.ErrorReceived += OnErrorReceived;

                    var request = new LoginRequest(_server.BaseUri, spotifyOptions.ClientID, LoginRequest.ResponseType.Code)
                    {
                        Scope = new List<string> { Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative }
                    };
                    BrowserUtil.Open(request.ToUri());
                }
            });

            await host.RunAsync();
        }

        private static async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            await _server.Stop();

            var config = SpotifyClientConfig.CreateDefault();
            var tokenResponse = await new OAuthClient(config).RequestToken(
              new AuthorizationCodeTokenRequest(
                spotifyOptions.ClientID, spotifyOptions.ClientSecret, response.Code, new Uri("http://localhost:5543/callback")
              )
            );

            spotifyOptions.AccessToken = tokenResponse.AccessToken;
            spotifyOptions.RefreshToken = tokenResponse.RefreshToken;

            // then startup main program
            SpotToBeat(tokenResponse.AccessToken);
        }

        private static async void SpotToBeat(string token)
        {
            var spotify = new SpotifyClient(token);

            // get user data
            var user = await spotify.UserProfile.Current();
            // get all playlists
            var playlists = await spotify.Playlists.GetUsers(user.Id);

            for (int i = 0; i < playlists.Items.Count; i++)
            {
                Console.WriteLine(i + 1 + " " + playlists.Items[i].Name);
            }

            Console.WriteLine("Please select which playlist to convert:");
            int selection = int.Parse(Console.ReadLine()) - 1;

            var playlistID = playlists.Items[selection].Id;

            var trackList = await spotify.Playlists.GetItems(playlistID);

            var allPages = await spotify.PaginateAll(trackList);

            BeatSaver beatSaver = new BeatSaver("Spot To Beat", version);

            Console.WriteLine("Please enter download directory: ");
            downloadDir = Console.ReadLine();

            foreach (PlaylistTrack<IPlayableItem> item in allPages)
            {
                if (item.Track is FullTrack track)
                {
                    var message = await DownloadBeatMap(track, beatSaver);
                    Console.WriteLine(message);
                }
                if (item.Track is FullEpisode episode)
                {
                    Console.WriteLine("Skipping episode: " + episode.Name);
                }
            }
            Console.WriteLine("Done!");
        }

        public static async Task<string> DownloadBeatMap(FullTrack track, BeatSaver beatSaver)
        {
            SearchTextFilterOption searchTextFilterOption = new SearchTextFilterOption
            {
                IncludeAutomappers = true,
                SortOrder = SortingOptions.Relevance,
                Query = track.Name
            };

            Page searchResults = await beatSaver.SearchBeatmaps(searchTextFilterOption);

            if (searchResults == null)
            {
                return $"No results found for {track.Name}";
            }

            // console clear hack
            Console.Clear();
            Console.WriteLine("\x1b[3J"); // ??? i love stackoverflow
            Console.Clear();

            Console.WriteLine($"Search Results for {track.Name}" + "\n");

            // display search results 
            for (int i = 0; i < searchResults.Beatmaps.Count; i++)
            {
                // TODO: make this look better 
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{i + 1} {TerminalURL(searchResults.Beatmaps[i].Name, $"https://beatsaver.com/maps/{searchResults.Beatmaps[i].ID}")} - {searchResults.Beatmaps[i].Uploader.Name} - {searchResults.Beatmaps[i].Uploaded.ToShortDateString()} - {searchResults.Beatmaps[i].Stats.Score}");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine(searchResults.Beatmaps[i].Description);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("--------------------------------------------------");
                Console.ResetColor();
            }

            Console.WriteLine("Please select which beatmap to download: (0 for none)");
            int selection2 = int.Parse(Console.ReadLine()) - 1;

            // try to download map selected
            if (!(selection2 >= 0 && selection2 < searchResults.Beatmaps.Count))
            {
                return "Skipping Download";
            }

            Console.WriteLine("Downloading " + searchResults.Beatmaps[selection2].Name);
            byte[] zip = await searchResults.Beatmaps[selection2].LatestVersion.DownloadZIP();

            try
            {
                File.WriteAllBytes($"{downloadDir}/{searchResults.Beatmaps[selection2].Name}.zip", zip);
                return "Downloaded " + searchResults.Beatmaps[selection2].Name;
            }
            catch (Exception e)
            {
                Console.WriteLine("Error downloading " + searchResults.Beatmaps[selection2].Name);
                return e.Message;
            }
        }

        private static async Task OnErrorReceived(object sender, string error, string state)
        {
            Console.WriteLine($"Aborting authorization, error received: {error}");
            await _server.Stop();
        }
    }
}
