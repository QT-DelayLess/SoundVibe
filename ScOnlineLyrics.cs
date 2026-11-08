// SoundVibe - optional online lyrics for SoundCloud playback.
// LRCLIB (https://lrclib.net/docs), with title+artist verification.
// No audio download, SoundCloud scraping or extra NuGet dependency.
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SoundVibe
{
    public partial class MainWindow
    {
        private string _scLyricsSongKey = "";
        private string _scLyricsStatus = "♪ Lirik Bulunamadı ♪";
        private bool _scLyricsLoading;
        private int _scLyricsRevision;
        private CancellationTokenSource? _scLyricsCts;

        // Called when official widget reports track metadata; only one search per track.
        private void ScStartLyricsSearch(string title, string artist)
        {
            if (!_scActive) return;
            title = (title ?? "").Trim();
            artist = (artist ?? "").Trim();

            // SoundCloud oEmbed sometimes returns "Artist - Song" as the title.
            string prefix = artist + " - ";
            if (artist.Length > 0 && title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                title = title.Substring(prefix.Length).Trim();
            if (title.Length == 0) return;

            string key = ScLyricsNormalize(title) + "|" + ScLyricsNormalize(artist);
            if (key == _scLyricsSongKey) return;
            _scLyricsSongKey = key;
            _scLyricsRevision++;
            _scLyricsCts?.Cancel();
            _scLyricsCts?.Dispose();
            _scLyricsCts = new CancellationTokenSource();

            LyricEngine.Instance.LoadFromText(null, null);
            _scLyricsLoading = true;
            _scLyricsStatus = "♪ Lirik aranıyor... ♪";
            if (artist.Length == 0)
            {
                _scLyricsLoading = false;
                _scLyricsStatus = "♪ Sanatçı bilgisi yok ♪";
                return;
            }
            _ = ScLookupLyricsAsync(title, artist, _scLyricsRevision, _scLyricsCts.Token);
        }

        private static string ScLyricsNormalize(string value) =>
            Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^\p{L}\p{Nd}]", "");

        private static string? ScLyricsField(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        private static bool ScLyricsIsMatch(JsonElement record, string title, string artist)
        {
            string realTitle = ScLyricsField(record, "trackName") ?? ScLyricsField(record, "name") ?? "";
            string realArtist = ScLyricsField(record, "artistName") ?? "";
            return ScLyricsNormalize(realTitle) == ScLyricsNormalize(title) &&
                   ScLyricsNormalize(realArtist) == ScLyricsNormalize(artist);
        }

        private async Task<JsonDocument?> ScLyricsFetchJsonAsync(string uri, CancellationToken token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("SoundVibe/0.4 (https://github.com/QT-DelayLess/SoundVibe)");
            using var response = await ScHttp.SendAsync(req, token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == (HttpStatusCode)429)
                throw new HttpRequestException("LRCLIB istek limiti. Daha sonra tekrar dene.");
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token);
        }

        private async Task ScLookupLyricsAsync(string title, string artist, int revision, CancellationToken token)
        {
            try
            {
                string args = "track_name=" + Uri.EscapeDataString(title) +
                              "&artist_name=" + Uri.EscapeDataString(artist);
                string? synced = null, plain = null;
                bool matched = false;
                using (var exact = await ScLyricsFetchJsonAsync("https://lrclib.net/api/get?" + args, token))
                {
                    if (exact != null && ScLyricsIsMatch(exact.RootElement, title, artist))
                    {
                        matched = true;
                        synced = ScLyricsField(exact.RootElement, "syncedLyrics");
                        plain = ScLyricsField(exact.RootElement, "plainLyrics");
                    }
                }

                // Search is a fallback only, with strict artist+title verification.
                if (!matched)
                {
                    await Task.Delay(250, token);
                    using var results = await ScLyricsFetchJsonAsync("https://lrclib.net/api/search?" + args, token);
                    if (results?.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in results.RootElement.EnumerateArray())
                        {
                            if (!ScLyricsIsMatch(item, title, artist)) continue;
                            matched = true;
                            synced = ScLyricsField(item, "syncedLyrics");
                            plain = ScLyricsField(item, "plainLyrics");
                            break;
                        }
                    }
                }

                if (token.IsCancellationRequested || !_scActive || revision != _scLyricsRevision) return;
                _scLyricsLoading = false;
                if (!string.IsNullOrWhiteSpace(synced) || !string.IsNullOrWhiteSpace(plain))
                {
                    LyricEngine.Instance.LoadFromText(synced, plain);
                    _scLyricsStatus = "";
                    if (_scDockStatus != null) _scDockStatus.Text = "☁ SoundCloud • Lirik: LRCLIB";
                }
                else
                {
                    _scLyricsStatus = "♪ Lirik Bulunamadı ♪";
                }
                ScUpdatePlaybackUi();
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (token.IsCancellationRequested || !_scActive || revision != _scLyricsRevision) return;
                _scLyricsLoading = false;
                _scLyricsStatus = "♪ Lirik hizmetine ulaşılamadı ♪";
                ScUpdatePlaybackUi();
            }
        }

        // Called when switching back to SoundVibe local playback.
        private void ScStopLyricsSearch()
        {
            _scLyricsRevision++;
            _scLyricsCts?.Cancel();
            _scLyricsCts?.Dispose();
            _scLyricsCts = null;
            _scLyricsSongKey = "";
            _scLyricsLoading = false;
            _scLyricsStatus = "♪ Lirik Bulunamadı ♪";
            if (PlaybackManager.Instance.CurrentSong is SongModel song)
                LyricEngine.Instance.LoadLyrics(song.FilePath);
            else
                LyricEngine.Instance.LoadFromText(null, null);
        }
    }
}
