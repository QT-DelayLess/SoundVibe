// SoundVibe Inline SoundCloud - WPF .NET 8, WebView2.
// Replaces the earlier two-WebView standalone SoundCloudWindow.cs.
// One official SoundCloud embed in the *main* SoundVibe player; search overlay is muted.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
// WPF aliases: the host project also imports System.Drawing via WinForms.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SoundVibe
{
    public partial class MainWindow
    {
        private static readonly HttpClient ScHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
        private WebView2? _scSearchBrowser, _scPlayerBrowser;
        private Border? _scSearchOverlay, _scDock;
        private Grid? _scPanel;
        // Keep the full SoundVibe player at viewport height. The SoundCloud widget
        // lives BELOW it in a scrolling host; do not compress the visualizer rows.
        private ScrollViewer? _scScrollViewer;
        private StackPanel? _scScrollContent;
        private TextBox? _scSearchText, _scLinkText;
        private TextBlock? _scStatus, _scDockStatus;
        private Button? _scSelectButton;
        private bool _scInitialized, _scBusy, _scActive, _scReady, _scPlaying;
        private bool _scRestoredCapture;
        private double _scPositionSeconds, _scDurationSeconds;
        private string _scTrackLink = "";
        private Task? _scBrowserInitTask;

        private static SolidColorBrush ScBrush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private Button ScButton(string caption, bool primary = false)
        {
            var b = new Button
            {
                Content = caption, Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = primary ? (TryFindResource("ThemeAccentBrush") as Brush ?? ScBrush("#FFFF006E")) : ScBrush("#FF28182F"),
                BorderBrush = TryFindResource("ThemeAccentBrush") as Brush ?? ScBrush("#FFFF006E"), BorderThickness = new Thickness(1),
                MinHeight = 32
            };
            // Template avoids the default grey Windows button.
            var template = new ControlTemplate(typeof(Button));
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(cp);
            template.VisualTree = frame;
            b.Template = template;
            return b;
        }

        private TextBox ScTextBox(string tooltip)
        {
            return new TextBox
            {
                ToolTip = tooltip, Background = ScBrush("#FF25172D"), Foreground = Brushes.White,
                CaretBrush = TryFindResource("ThemeAccentBrush") as Brush ?? Brushes.DeepPink,
                BorderBrush = ScBrush("#FF914575"), BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8), FontSize = 13, MinHeight = 36,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private void MenuSoundCloud_Click(object sender, RoutedEventArgs e)
        {
            LibraryToolsPopup.IsOpen = false;
            try
            {
                if (!_scInitialized) ScBuildInterface();
                if (isMiniPlayer) ToggleMiniPlayer(this, new RoutedEventArgs());
                _scScrollViewer?.ScrollToTop();
                if (FindName("TabLibrary") is RadioButton libraryTab) libraryTab.IsChecked = true;
                if (_scSearchOverlay != null) _scSearchOverlay.Visibility = Visibility.Visible;
                _scSearchText?.Focus();
                _ = ScInitializeBrowserAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "SoundCloud arayuzu acilamadi: " + ex.Message,
                    "SoundVibe Online", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ScBuildInterface()
        {
            _scPanel = FindName("PlayerViewPanel") as Grid
                ?? throw new InvalidOperationException("PlayerViewPanel bulunamadi. Temali SoundVibe surumu gerekli.");
            if (_scPanel.RowDefinitions.Count < 4) throw new InvalidOperationException("PlayerViewPanel duzeni beklenmedik.");
            // Keep all existing rows, including the Queue/Lyrics/EQ cards.
            // The official SoundCloud player is placed AFTER the full main player.
            var dockGrid = new Grid();
            dockGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            dockGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 0, 8, 0) };
            var stop = ScButton("✕ YEREL MUZIGE DON");
            stop.Click += (_, _) => ScExitOnline();
            DockPanel.SetDock(stop, Dock.Right);
            header.Children.Add(stop);
            var search = ScButton("⌕ YENI ARA", true);
            search.Click += (_, _) => MenuSoundCloud_Click(this, new RoutedEventArgs());
            DockPanel.SetDock(search, Dock.Right);
            header.Children.Add(search);
            _scDockStatus = new TextBlock
            {
                Text = "☁ SoundCloud • Resmi oynatici", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            header.Children.Add(_scDockStatus);
            dockGrid.Children.Add(header);
            _scPlayerBrowser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 22, 12, 30) };
            Grid.SetRow(_scPlayerBrowser, 1);
            dockGrid.Children.Add(_scPlayerBrowser);
            _scDock = new Border
            {
                Visibility = Visibility.Collapsed, Height = 230, Margin = new Thickness(40, 18, 40, 24),
                Background = ScBrush("#FF1A1024"),
                BorderBrush = TryFindResource("ThemeAccentBrush") as Brush ?? ScBrush("#FFFF006E"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(5), Child = dockGrid
            };
            // Reparent only PlayerViewPanel at runtime. No XAML replacement is
            // required and all named controls remain part of the same WPF window.
            if (_scPanel.Parent is not Grid rightHost)
                throw new InvalidOperationException("SoundVibe player parent layout was unexpected.");
            int playerRow = Grid.GetRow(_scPanel);
            int playerColumn = Grid.GetColumn(_scPanel);
            int playerRowSpan = Grid.GetRowSpan(_scPanel);
            int playerColumnSpan = Grid.GetColumnSpan(_scPanel);

            _scScrollContent = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _scScrollViewer = new ScrollViewer
            {
                Content = _scScrollContent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Focusable = false,
                Background = Brushes.Transparent
            };
            Grid.SetRow(_scScrollViewer, playerRow);
            Grid.SetColumn(_scScrollViewer, playerColumn);
            Grid.SetRowSpan(_scScrollViewer, playerRowSpan);
            Grid.SetColumnSpan(_scScrollViewer, playerColumnSpan);
            rightHost.Children.Remove(_scPanel);
            Grid.SetRow(_scPanel, 0);
            Grid.SetColumn(_scPanel, 0);
            Grid.SetRowSpan(_scPanel, 1);
            Grid.SetColumnSpan(_scPanel, 1);
            _scScrollContent.Children.Add(_scPanel);
            _scScrollContent.Children.Add(_scDock);
            rightHost.Children.Add(_scScrollViewer);

            // The player's star row must still measure to the *visible viewport*,
            // not to the expanded scroll content's infinite available height.
            // Otherwise the visualizer, album art and transport buttons shift.
            _scScrollViewer.SizeChanged += (_, args) =>
            {
                if (_scPanel != null && args.NewSize.Height > 0)
                    _scPanel.Height = args.NewSize.Height;
            };
            _scScrollViewer.Loaded += (_, _) =>
            {
                if (_scPanel != null && _scScrollViewer.ActualHeight > 0)
                    _scPanel.Height = _scScrollViewer.ActualHeight;
            };
            _scScrollViewer.SetBinding(UIElement.VisibilityProperty,
                new System.Windows.Data.Binding("Visibility")
                {
                    Source = _scPanel,
                    Mode = System.Windows.Data.BindingMode.OneWay
                });

            // Search is a temporary panel over the main player, NOT a separate window.
            var searchGrid = new Grid { Margin = new Thickness(15) };
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            var titleRow = new DockPanel { LastChildFill = true };
            var close = ScButton("✕ KAPAT");
            close.Click += (_, _) => { if (_scSearchOverlay != null) _scSearchOverlay.Visibility = Visibility.Collapsed; };
            DockPanel.SetDock(close, Dock.Right);
            titleRow.Children.Add(close);
            titleRow.Children.Add(new TextBlock
            {
                Text = "☁ SOUNDVIBE / SOUNDCLOUD", FontSize = 19, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
            });
            searchGrid.Children.Add(titleRow);
            var searchRow = new Grid();
            searchRow.ColumnDefinitions.Add(new ColumnDefinition());
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            _scSearchText = ScTextBox("Sarki veya sanatci ara");
            _scSearchText.KeyDown += (_, a) => { if (a.Key == Key.Enter) { ScSearch(); a.Handled = true; } };
            searchRow.Children.Add(_scSearchText);
            var find = ScButton("⌕ ARA", true);
            find.Click += (_, _) => ScSearch();
            Grid.SetColumn(find, 1);
            searchRow.Children.Add(find);
            Grid.SetRow(searchRow, 1);
            searchGrid.Children.Add(searchRow);
            _scSearchBrowser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 18, 12, 27) };
            Grid.SetRow(_scSearchBrowser, 2);
            searchGrid.Children.Add(_scSearchBrowser);
            var selectRow = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            selectRow.ColumnDefinitions.Add(new ColumnDefinition());
            selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            _scLinkText = ScTextBox("SoundCloud parca veya playlist baglantisi");
            _scLinkText.KeyDown += async (_, a) => { if (a.Key == Key.Enter) { a.Handled = true; await ScPlaySelectedAsync(); } };
            selectRow.Children.Add(_scLinkText);
            _scSelectButton = ScButton("▶ CAL", true);
            _scSelectButton.Click += async (_, _) => await ScPlaySelectedAsync();
            Grid.SetColumn(_scSelectButton, 1);
            selectRow.Children.Add(_scSelectButton);
            Grid.SetRow(selectRow, 3);
            searchGrid.Children.Add(selectRow);
            _scStatus = new TextBlock
            {
                Text = "SoundCloud'da ara, parcayi sec ve CAL'a bas.", Foreground = ScBrush("#FFE0B2D7"),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_scStatus, 4);
            searchGrid.Children.Add(_scStatus);
            _scSearchOverlay = new Border
            {
                Visibility = Visibility.Collapsed, Margin = new Thickness(12),
                Background = ScBrush("#FF110B1C"), BorderBrush = TryFindResource("ThemeAccentBrush") as Brush ?? ScBrush("#FFFF006E"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Child = searchGrid
            };
            Grid.SetRow(_scSearchOverlay, 0);
            Grid.SetRowSpan(_scSearchOverlay, 4);
            Panel.SetZIndex(_scSearchOverlay, 30);
            _scPanel.Children.Add(_scSearchOverlay);
            _scInitialized = true;
            Closing += (_, _) =>
            {
                try { _scPlayerBrowser?.CoreWebView2?.Navigate("about:blank"); } catch { }
                try { _scSearchBrowser?.CoreWebView2?.Navigate("about:blank"); } catch { }
                _scSearchBrowser?.Dispose();
                _scPlayerBrowser?.Dispose();
            };
        }

        private Task ScInitializeBrowserAsync()
        {
            return _scBrowserInitTask ??= ScInitializeBrowserCoreAsync();
        }

        private async Task ScInitializeBrowserCoreAsync()
        {
            try
            {
                if (_scSearchBrowser?.CoreWebView2 == null)
                {
                    await _scSearchBrowser!.EnsureCoreWebView2Async();
                    _scSearchBrowser.CoreWebView2.IsMuted = true;
                    _scSearchBrowser.CoreWebView2.WebMessageReceived += (_, a) =>
                    {
                        try
                        {
                            using var j = JsonDocument.Parse(a.WebMessageAsJson);
                            if (j.RootElement.TryGetProperty("type", out var t) && t.GetString() == "select")
                                ScSelectLink(j.RootElement.GetProperty("url").GetString());
                        }
                        catch { }
                    };
                    _scSearchBrowser.CoreWebView2.SourceChanged += (_, _) =>
                    {
                        ScSelectLink(_scSearchBrowser.Source?.AbsoluteUri);
                    };
                    _scSearchBrowser.CoreWebView2.NewWindowRequested += (_, a) =>
                    {
                        a.Handled = true;
                        if (ScValidSoundCloudUrl(a.Uri)) ScSelectLink(a.Uri);
                    };
                    const string script = @"document.addEventListener('click', function(e) {
                        try {
                            const a=e.target.closest('a[href]'); if(!a)return;
                            const u=new URL(a.href);
                            if(u.protocol!=='https:' || !['soundcloud.com','www.soundcloud.com'].includes(u.hostname))return;
                            const s=u.pathname.split('/').filter(Boolean);
                            if(s.length<2 || ['search','discover','login','you','stream','settings'].includes(s[0]))return;
                            window.chrome.webview.postMessage({type:'select',url:u.href});
                            e.preventDefault(); e.stopImmediatePropagation();
                        } catch(_){}
                    },true);";
                    await _scSearchBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
                }
                if (_scSearchBrowser?.Source == null || _scSearchBrowser.Source.AbsoluteUri == "about:blank")
                    _scSearchBrowser?.CoreWebView2?.Navigate("https://soundcloud.com/discover");
            }
            catch (Exception ex)
            {
                ScNotice("WebView2 acilamadi: " + ex.Message);
            }
        }

        private void ScSearch()
        {
            string query = _scSearchText?.Text.Trim() ?? "";
            if (query.Length == 0) { _scSearchText?.Focus(); return; }
            if (_scSearchBrowser?.CoreWebView2 == null) { ScNotice("WebView2 hazir degil."); return; }
            _scSearchBrowser.CoreWebView2.Navigate("https://soundcloud.com/search/sounds?q=" + Uri.EscapeDataString(query));
            ScNotice("Sonuclardan sarkiyi sec; alt satirdaki CAL dugmesine bas.");
        }

        private static bool ScValidSoundCloudUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            if (uri.Host != "soundcloud.com" && uri.Host != "www.soundcloud.com" && uri.Host != "m.soundcloud.com") return false;
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return false;
            return segments[0] != "search" && segments[0] != "discover" && segments[0] != "stream" &&
                   segments[0] != "you" && segments[0] != "login" && segments[0] != "settings";
        }

        private void ScSelectLink(string? url)
        {
            if (!ScValidSoundCloudUrl(url)) return;
            if (_scLinkText != null) _scLinkText.Text = url!;
            ScNotice("Parca secildi. CAL dugmesine bas.");
        }

        private void ScNotice(string message)
        {
            if (_scStatus != null) _scStatus.Text = message;
        }

        private async Task ScPlaySelectedAsync()
        {
            if (_scBusy) return;
            string link = _scLinkText?.Text.Trim() ?? "";
            if (!ScValidSoundCloudUrl(link)) { ScNotice("Gecerli bir SoundCloud parca baglantisi sec."); return; }
            _scBusy = true;
            if (_scSelectButton != null) _scSelectButton.IsEnabled = false;
            try
            {
                await ScInitializeBrowserAsync();
                string endpoint = "https://soundcloud.com/oembed?format=json&url=" + Uri.EscapeDataString(link);
                using var resp = await ScHttp.GetAsync(endpoint);
                resp.EnsureSuccessStatusCode();
                using var obj = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string embedHtml = obj.RootElement.GetProperty("html").GetString() ?? "";
                var match = Regex.Match(embedHtml, "src\\s*=\\s*\"(?<url>[^\"]+)\"", RegexOptions.IgnoreCase);
                if (!match.Success) throw new InvalidOperationException("Resmi SoundCloud widget adresi bulunamadi.");
                string src = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!Uri.TryCreate(src, UriKind.Absolute, out var iframeUri) || iframeUri.Scheme != "https" ||
                    iframeUri.Host != "w.soundcloud.com" || !iframeUri.AbsolutePath.StartsWith("/player/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Embed adresi gecersiz.");

                // Switch to online playback after a valid official embed was retrieved.
                if (AudioEngine.Instance.IsPlaying) { AudioEngine.Instance.Pause(); UpdatePlayButtons(false); }
                if (!_scActive) _scRestoredCapture = _systemAudioMode;
                _scActive = true;
                _scReady = false;
                _scPlaying = false;
                _scTrackLink = link;
                _scPositionSeconds = 0;
                _scDurationSeconds = 0;
                if (_scDock != null) _scDock.Visibility = Visibility.Visible;
                if (_scScrollViewer != null)
                {
                    _scScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    _scScrollViewer.ScrollToTop();
                }
                // Initialize player only after its host is visible in the visual tree.
                if (_scPlayerBrowser?.CoreWebView2 == null)
                {
                    await _scPlayerBrowser!.EnsureCoreWebView2Async();
                    _scPlayerBrowser.CoreWebView2.WebMessageReceived += ScWidgetMessage;
                    _scPlayerBrowser.CoreWebView2.NavigationStarting += (_, _) => { _scReady = false; };
                }
                if (_scSearchOverlay != null) _scSearchOverlay.Visibility = Visibility.Collapsed;
                // Navigate the search page away so no second player can ever be heard.
                _scSearchBrowser?.CoreWebView2?.Navigate("about:blank");

                string title = obj.RootElement.TryGetProperty("title", out var t) ? (t.GetString() ?? "SoundCloud") : "SoundCloud";
                string artist = obj.RootElement.TryGetProperty("author_name", out var a) ? (a.GetString() ?? "") : "";
                ScSetMetadata(title, artist);
                if (obj.RootElement.TryGetProperty("thumbnail_url", out var cover) && cover.ValueKind == JsonValueKind.String)
                    _ = ScSetArtworkAsync(cover.GetString());

                if (!_systemAudioMode)
                {
                    MenuSystemAudio.IsChecked = true;
                    MenuSystemAudio_Click(this, new RoutedEventArgs());
                }
                if (CanVisualize) StartRendering();
                else _playbackUiTimer.Start();
                ScNavigateOfficialWidget(iframeUri.AbsoluteUri);
                if (_scDockStatus != null) _scDockStatus.Text = "☁ " + title;
            }
            catch (Exception ex)
            {
                if (_scActive) ScExitOnline();
                if (_scSearchOverlay != null) _scSearchOverlay.Visibility = Visibility.Visible;
                ScNotice("Sarki acilamadi: " + ex.Message);
            }
            finally { _scBusy = false; if (_scSelectButton != null) _scSelectButton.IsEnabled = true; }
        }

        private void ScNavigateOfficialWidget(string url)
        {
            string safe = WebUtility.HtmlEncode(url);
            int volume = (int)Math.Clamp(SldVolume.Value, 0, 100);
            string html = @"<!DOCTYPE html><html><head><meta charset='utf-8'>
            <style>html,body{background:#1a1024;margin:0;overflow:hidden}iframe{width:100%;height:166px;border:0}</style>
            </head><body><iframe id='widget' allow='autoplay' scrolling='no' src='" + safe + @"'></iframe>
            <script src='https://w.soundcloud.com/player/api.js'></script>
            <script>
            (function(){
              var notify=function(type,obj){try{window.chrome.webview.postMessage(Object.assign({type:type},obj||{}));}catch(e){}};
              var start=function(){
                if(!window.SC||!SC.Widget){setTimeout(start,150);return;}
                var w=SC.Widget(document.getElementById('widget')); window.svWidget=w;
                var last=0;
                var info=function(){w.getCurrentSound(function(s){if(s) notify('track',{title:s.title||'SoundCloud',artist:(s.user&&s.user.username)||''});});};
                w.bind(SC.Widget.Events.READY,function(){notify('ready');w.setVolume(" + volume + @");w.getDuration(function(n){notify('duration',{ms:n||0});});info();});
                w.bind(SC.Widget.Events.PLAY,function(){notify('play');info();});
                w.bind(SC.Widget.Events.PAUSE,function(){notify('pause');});
                w.bind(SC.Widget.Events.FINISH,function(){notify('pause');});
                w.bind(SC.Widget.Events.PLAY_PROGRESS,function(e){var now=Date.now();if(now-last>220){last=now;notify('progress',{ms:e.currentPosition||0});}});
                w.bind(SC.Widget.Events.ERROR,function(){notify('error');});
              }; start();
            })();
            </script></body></html>";
            _scPlayerBrowser!.CoreWebView2.NavigateToString(html);
        }

        private async Task ScSetArtworkAsync(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return;
            try
            {
                // Load on UI thread with UriSource; WPF downloads asynchronously when possible.
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.UriSource = u; bitmap.CacheOption = BitmapCacheOption.OnDemand;
                bitmap.EndInit();
                if (_scActive)
                {
                    if (_imgAlbumCover != null) _imgAlbumCover.Source = bitmap;
                    if (_imgMiniCover != null) _imgMiniCover.Source = bitmap;
                }
            }
            catch { }
            await Task.CompletedTask;
        }

        private void ScSetMetadata(string title, string artist)
        {
            ScStartLyricsSearch(title, artist);
            if (_txtNowPlayingTitle != null) _txtNowPlayingTitle.Text = title;
            if (_txtNowPlayingArtist != null) _txtNowPlayingArtist.Text = string.IsNullOrWhiteSpace(artist) ? "SoundCloud" : artist;
            if (_txtMetaData != null) _txtMetaData.Text = "SoundCloud • Online • System Audio Visualizer";
            if (_txtSidebarNowPlaying != null) _txtSidebarNowPlaying.Text = title;
            if (_txtSidebarArtist != null) _txtSidebarArtist.Text = artist;
            if (_txtMiniTitle != null) _txtMiniTitle.Text = title;
            if (_txtMiniArtist != null) _txtMiniArtist.Text = artist;
            TxtFullscreenTitle.Text = title;
            TxtFullscreenArtist.Text = artist;
            UpdatePlayButtons(_scPlaying);
        }

        private void ScWidgetMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!_scActive) return;
            try
            {
                using var json = JsonDocument.Parse(args.WebMessageAsJson);
                var j = json.RootElement;
                if (!j.TryGetProperty("type", out var prop)) return;
                string type = prop.GetString() ?? "";
                switch (type)
                {
                    case "ready": _scReady = true; if (_scDockStatus != null) _scDockStatus.Text = "☁ Hazir • Ana ▶ dugmesiyle cal"; break;
                    case "play": _scPlaying = true; UpdatePlayButtons(true); break;
                    case "pause": _scPlaying = false; UpdatePlayButtons(false); break;
                    case "duration":
                        if (j.TryGetProperty("ms", out var dur) && dur.TryGetDouble(out var ms)) _scDurationSeconds = Math.Max(0, ms / 1000.0);
                        break;
                    case "progress":
                        if (j.TryGetProperty("ms", out var pos) && pos.TryGetDouble(out var p)) _scPositionSeconds = Math.Max(0, p / 1000.0);
                        break;
                    case "track":
                        var title = j.TryGetProperty("title", out var tl) ? tl.GetString() ?? "SoundCloud" : "SoundCloud";
                        var artist = j.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : "";
                        ScSetMetadata(title, artist);
                        break;
                    case "error": if (_scDockStatus != null) _scDockStatus.Text = "☁ Bu parca oynatilamiyor olabilir"; break;
                }
            }
            catch { }
        }

        private async void ScRunWidget(string method)
        {
            if (!_scActive || !_scReady || _scPlayerBrowser?.CoreWebView2 == null) return;
            if (method != "play" && method != "pause" && method != "toggle" && method != "next" && method != "prev") return;
            try { await _scPlayerBrowser.CoreWebView2.ExecuteScriptAsync("if(window.svWidget)window.svWidget." + method + "();"); }
            catch { }
        }

        private async void ScSetOnlineVolume(double volume)
        {
            if (!_scActive || !_scReady || _scPlayerBrowser?.CoreWebView2 == null) return;
            try { await _scPlayerBrowser.CoreWebView2.ExecuteScriptAsync("if(window.svWidget)window.svWidget.setVolume(" + ((int)Math.Clamp(volume, 0, 100)) + ");"); }
            catch { }
        }

        private async void ScSeekTo(double seconds)
        {
            if (!_scActive || !_scReady || _scPlayerBrowser?.CoreWebView2 == null) return;
            try { await _scPlayerBrowser.CoreWebView2.ExecuteScriptAsync("if(window.svWidget)window.svWidget.seekTo(" + ((long)Math.Round(Math.Max(0, seconds) * 1000)) + ");"); }
            catch { }
        }

        private void ScUpdatePlaybackUi()
        {
            var now = TimeSpan.FromSeconds(_scPositionSeconds);
            var end = TimeSpan.FromSeconds(_scDurationSeconds);
            if (_txtCurrentTime != null) _txtCurrentTime.Text = now.TotalHours >= 1 ? now.ToString(@"h\:mm\:ss") : now.ToString(@"mm\:ss");
            if (_txtTotalTime != null) _txtTotalTime.Text = end.TotalHours >= 1 ? end.ToString(@"h\:mm\:ss") : end.ToString(@"mm\:ss");
            if (!isUpdatingSlider)
            {
                if (SldTimeline != null) { SldTimeline.Maximum = Math.Max(1, _scDurationSeconds); SldTimeline.Value = Math.Min(_scPositionSeconds, SldTimeline.Maximum); }
                if (SldMiniTimeline != null) { SldMiniTimeline.Maximum = Math.Max(1, _scDurationSeconds); SldMiniTimeline.Value = Math.Min(_scPositionSeconds, SldMiniTimeline.Maximum); }
            }

            string lyricText = _scLyricsLoading
                ? _scLyricsStatus
                : string.IsNullOrWhiteSpace(_scLyricsStatus)
                    ? LyricEngine.Instance.GetCurrentLyric(
                        TimeSpan.FromSeconds(_scPositionSeconds))
                    : _scLyricsStatus;

            if (_txtWidgetLyrics != null)
                _txtWidgetLyrics.Text = lyricText;

            if (_txtBigKaraokeLyrics != null &&
                (CmbVisualizerMode?.SelectedIndex ?? 0) ==
                KARAOKE_VISUALIZER_INDEX)
            {
                _txtBigKaraokeLyrics.Text = lyricText;
            }

        }

        private void ScExitOnline()
        {
            if (!_scActive) return;
            ScStopLyricsSearch();
            _scActive = false;
            _scReady = false;
            _scPlaying = false;
            _scPositionSeconds = 0;
            _scDurationSeconds = 0;
            try { _scPlayerBrowser?.CoreWebView2?.Navigate("about:blank"); } catch { }
            if (_scDock != null) _scDock.Visibility = Visibility.Collapsed;
            if (_scSearchOverlay != null) _scSearchOverlay.Visibility = Visibility.Collapsed;
            if (_scScrollViewer != null)
            {
                _scScrollViewer.ScrollToTop();
                _scScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            if (!_scRestoredCapture && _systemAudioMode)
            {
                MenuSystemAudio.IsChecked = false;
                MenuSystemAudio_Click(this, new RoutedEventArgs());
            }
            _scRestoredCapture = false;
            if (PlaybackManager.Instance.CurrentSong is SongModel song)
                UpdateUI(song, Color.FromRgb(74, 144, 226));
            else
            {
                if (_txtNowPlayingTitle != null) _txtNowPlayingTitle.Text = "SoundVibe";
                if (_txtNowPlayingArtist != null) _txtNowPlayingArtist.Text = "Waiting for music...";
            }
            UpdatePlayButtons(false);
            if (!AudioEngine.Instance.IsPlaying && !_systemAudioMode) StopRendering();
        }
    }
}
