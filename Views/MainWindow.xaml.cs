using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DropDown;

public sealed partial class MainWindow : Window
{
    private string _selectedFolder;
    private CobaltInstance _selectedInstance;
    private List<CobaltInstance> _instances;
    private readonly HttpClient _httpClient = new HttpClient();
    // Redirects disabled deliberately: a cobalt instance's resolved download URL is
    // untrusted, and each redirect hop needs its own host check (see
    // EnsureSafeDownloadHostAsync) rather than letting HttpClient follow them blindly.
    private readonly HttpClient _downloadHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DownloadHistory _history = DownloadHistory.Load();

    private CancellationTokenSource _previewCts;
    private CancellationTokenSource _downloadCts;
    private CancellationTokenSource _donateCheckCts;
    private bool _suppressFilenameEvents;
    private bool _filenameEditedByUser;
    private bool _isDownloading;
    private bool _isWindowActive = true;
    private bool _servicesExpanded;
    private readonly List<QueueItem> _queue = new();

    // The services chip grid can run to 5-6+ rows for a fully-tested instance, more than
    // fits in the compact default window; expanding it resizes the window rather than
    // squeezing everything else, and the ScrollViewer inside still catches anything beyond
    // even the expanded height.
    // 620 was tuned only for the no-queue case and cut off the queue row (and its bottom
    // margin) as soon as even one item was queued in "both" display mode; bumped both
    // sizes up and gave the queue/services panels their own bottom margin so nothing sits
    // flush against the window edge regardless of what's currently visible.
    private const int CollapsedWindowHeight = 780;
    private const int ExpandedWindowHeight = 1050;

    public MainWindow()
    {
        InitializeComponent();
        // Window has no Width/Height in WinUI 3 XAML; size it via AppWindow instead
        AppWindow.Resize(new Windows.Graphics.SizeInt32(660, CollapsedWindowHeight));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DropDown/1.0");
        // Splitting the download path onto its own HttpClient (for the SSRF redirect fix)
        // left this one without the UA header its sibling gets, some CDNs/tunnels reject
        // UA-less requests, so keep them consistent.
        _downloadHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DropDown/1.0");
        Activated += (_, e) => _isWindowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        ApplyDefaultsToUi();
        ApplyTheme();
        _ = InitializeLastInstanceAsync();
    }

    /// <summary>Quietly reconnects to the most recently used instance on launch, if it's
    /// still online. Fails silently; the user can always pick manually.</summary>
    private async Task InitializeLastInstanceAsync()
    {
        if (_settings.Recents.Count == 0)
        {
            return;
        }

        try
        {
            DownloadStatusText.Text = "Reconnecting to last instance…";
            _instances = await FetchInstancesAsync();

            var match = _instances.FirstOrDefault(i => i.Api == _settings.Recents[0]);
            if (match != null)
            {
                _selectedInstance = match;
                SelectedInstanceText.Text =
                    $"Selected instance: {match.Api} (score {match.Score:F0}%, v{match.Version})";
                PopulateServicesPanel(match);
                UpdateInstanceServiceWarning();
                TriggerDonateCheck(match);
            }
        }
        catch
        {
            // Instance list unreachable at startup, not worth bothering the user;
            // Select Instance will just fetch fresh when they click it
        }
        finally
        {
            if (DownloadStatusText.Text == "Reconnecting to last instance…")
            {
                DownloadStatusText.Text = "";
            }
        }
    }

    /// <summary>Applies the saved backdrop theme. The root Grid has no background,
    /// so the system backdrop shows through behind the controls.</summary>
    private void ApplyTheme()
    {
        SystemBackdrop = _settings.Theme switch
        {
            "micaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            "acrylic" => new DesktopAcrylicBackdrop(),
            "none" => null,
            _ => new MicaBackdrop()   // "mica" and anything unknown
        };
    }

    /// <summary>Pushes saved defaults into the main window controls.</summary>
    private void ApplyDefaultsToUi()
    {
        SelectComboByTag(DownloadModeCombo, _settings.DownloadMode);
        SelectComboByTag(AudioFormatCombo, _settings.AudioFormat);
        SelectComboByTag(AudioBitrateCombo, _settings.AudioBitrate);
        SelectComboByTag(VideoQualityCombo, _settings.VideoQuality);

        if (string.IsNullOrEmpty(_selectedFolder)
            && !string.IsNullOrEmpty(_settings.DefaultFolder)
            && Directory.Exists(_settings.DefaultFolder))
        {
            _selectedFolder = _settings.DefaultFolder;
            SelectedFolderText.Text = $"Selected folder: {_selectedFolder} (default)";
            OpenFolderButton.IsEnabled = true;
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && (cbi.Tag as string) == tag)
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
    }

    private async void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };
        folderPicker.FileTypeFilter.Add("*");

        // Desktop apps must associate the picker with a window handle or it throws
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        StorageFolder folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null)
        {
            _selectedFolder = folder.Path;
            SelectedFolderText.Text = $"Selected folder: {_selectedFolder}";
            OpenFolderButton.IsEnabled = true;

            // Whatever folder you pick becomes the remembered one for next launch,
            // same field the Options dialog edits, no separate "last used" to track
            _settings.DefaultFolder = _selectedFolder;
            _settings.Save();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedFolder) || !Directory.Exists(_selectedFolder))
        {
            ShowError("The selected folder no longer exists.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_selectedFolder}\"")
        {
            UseShellExecute = true
        });
    }

    private async void SelectInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        SelectInstanceButton.IsEnabled = false;
        try
        {
            if (_instances == null)
            {
                SelectInstanceButton.Content = "Loading instances…";
                _instances = await FetchInstancesAsync();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Could not load instances from cobalt.directory: {ex.Message}");
            return;
        }
        finally
        {
            SelectInstanceButton.Content = "Select Instance";
            SelectInstanceButton.IsEnabled = true;
        }

        if (_instances.Count == 0)
        {
            ShowError("cobalt.directory reports no online instances right now.");
            return;
        }

        await ShowInstancePickerAsync();
    }

    private async Task ShowInstancePickerAsync()
    {
        // If a URL is already typed in, detect its service so we can flag/sort
        // instances by whether they actually support it (roadmap #1)
        var serviceKey = DetectServiceKey(UrlTextBox.Text);
        var serviceFriendly = serviceKey != null ? GetServiceFriendlyName(serviceKey) : null;

        bool ServiceOk(CobaltInstance i) =>
            serviceKey != null && i.Tests != null
            && i.Tests.TryGetValue(serviceKey, out var t) && t.Status;

        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 340,
            MinWidth = 440
        };

        var viewCombo = new ComboBox { MinWidth = 180 };
        viewCombo.Items.Add(new ComboBoxItem { Content = "All Instances", Tag = "all" });
        viewCombo.Items.Add(new ComboBoxItem { Content = "★ Favorites", Tag = "fav" });
        viewCombo.Items.Add(new ComboBoxItem { Content = "Recently Used", Tag = "recent" });
        viewCombo.SelectedIndex = 0;

        void RebuildList()
        {
            listView.Items.Clear();
            var view = ComboTag(viewCombo, "all");

            IEnumerable<CobaltInstance> source = view switch
            {
                "fav" => _instances.Where(i => _settings.Favorites.Contains(i.Api))
                                   .OrderByDescending(ServiceOk)
                                   .ThenByDescending(i => i.Score),
                // Stable OrderByDescending keeps recency order among ties, so this
                // only reshuffles when a service is detected, otherwise recency wins
                "recent" => _settings.Recents
                                     .Select(host => _instances.FirstOrDefault(i => i.Api == host))
                                     .Where(i => i != null)
                                     .OrderByDescending(ServiceOk),
                // "All": favorites first, then service match, then score
                _ => _instances.OrderByDescending(i => _settings.Favorites.Contains(i.Api))
                               .ThenByDescending(ServiceOk)
                               .ThenByDescending(i => i.Score)
            };

            foreach (var instance in source)
            {
                var item = new ListViewItem
                {
                    Content = BuildInstanceRow(instance, serviceKey, serviceFriendly),
                    Tag = instance,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                listView.Items.Add(item);
                if (_selectedInstance != null && instance.Api == _selectedInstance.Api)
                {
                    listView.SelectedItem = item;
                }
            }

            if (listView.Items.Count == 0)
            {
                var hint = view == "fav"
                    ? "No favorites yet. Star an instance in the All Instances view."
                    : "No recently used instances that are currently online.";
                listView.Items.Add(new ListViewItem
                {
                    Content = new TextBlock { Text = hint, Opacity = 0.6, TextWrapping = TextWrapping.Wrap },
                    IsEnabled = false
                });
            }
        }

        viewCombo.SelectionChanged += (_, _) => RebuildList();
        RebuildList();

        var dialogContent = new StackPanel { Spacing = 10 };
        dialogContent.Children.Add(new TextBlock
        {
            Text = "The % badge is a compatibility score (services currently working, per " +
                   "cobalt.directory's tests), not a safety or trust rating. The 🔗 icon opens " +
                   "an instance's own site so you can see who runs it yourself.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });
        if (serviceFriendly != null)
        {
            dialogContent.Children.Add(new TextBlock
            {
                Text = $"Matching against {serviceFriendly}: instances that support it are marked ✓ and sorted first.",
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap
            });
        }
        dialogContent.Children.Add(viewCombo);
        dialogContent.Children.Add(new ScrollViewer { Content = listView, MaxHeight = 360 });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Select Instance",
            Content = dialogContent,
            PrimaryButtonText = "Select",
            SecondaryButtonText = "Refresh List",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowDialogAsync(dialog);

        if (result == ContentDialogResult.Primary)
        {
            if ((listView.SelectedItem as ListViewItem)?.Tag is CobaltInstance chosen)
            {
                _selectedInstance = chosen;
                SelectedInstanceText.Text =
                    $"Selected instance: {chosen.Api} (score {chosen.Score:F0}%, v{chosen.Version})";
                _settings.AddRecent(chosen.Api);
                _settings.Save();
                PopulateServicesPanel(chosen);
                UpdateInstanceServiceWarning();
                TriggerDonateCheck(chosen);
                TriggerFilenamePreview();
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            // Refresh: drop the cache and reopen the picker with fresh data
            _instances = null;
            SelectInstanceButton_Click(this, null);
        }
    }

    // ---------- Smart instance sort (URL → cobalt.directory service key) ----------

    private static readonly (string Host, string Key)[] ServiceHostMap =
    {
        ("tiktok.com", "tiktok"),
        ("twitter.com", "twitter"), ("x.com", "twitter"),
        ("instagram.com", "instagram"),
        ("facebook.com", "facebook"), ("fb.watch", "facebook"),
        ("soundcloud.com", "soundcloud"),
        ("bsky.app", "bluesky"),
        ("tumblr.com", "tumblr"),
        ("bilibili.com", "bilibili"), ("b23.tv", "bilibili"),
        ("pinterest.com", "pinterest"), ("pin.it", "pinterest"),
        ("ok.ru", "odnoklassniki"),
        ("dailymotion.com", "dailymotion"), ("dai.ly", "dailymotion"),
        ("snapchat.com", "snapchat"),
        ("vimeo.com", "vimeo"),
        ("vk.com", "vk"),
        ("streamable.com", "streamable"),
        ("twitch.tv", "twitch-clips"),
        ("reddit.com", "reddit"),
        ("newgrounds.com", "newgrounds"),
        ("rutube.ru", "rutube"),
    };

    /// <summary>Maps a pasted URL to the cobalt.directory test key for its service, or null if unrecognized.</summary>
    private static string DetectServiceKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www."))
        {
            host = host[4..];
        }

        // YouTube has multiple test keys depending on subdomain/path
        if (host is "youtube.com" or "m.youtube.com" or "youtu.be")
        {
            return uri.AbsolutePath.Contains("/shorts/", StringComparison.OrdinalIgnoreCase)
                ? "youtube-shorts" : "youtube";
        }
        if (host == "music.youtube.com")
        {
            return "youtube-music";
        }

        foreach (var (suffix, key) in ServiceHostMap)
        {
            if (host == suffix || host.EndsWith("." + suffix))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>Looks up the human-readable service name cobalt.directory itself uses,
    /// falling back to a title-cased version of the key if no instance data is loaded yet.</summary>
    private string GetServiceFriendlyName(string key)
    {
        if (_instances != null)
        {
            foreach (var instance in _instances)
            {
                if (instance.Tests != null && instance.Tests.TryGetValue(key, out var test)
                    && !string.IsNullOrEmpty(test.Friendly))
                {
                    return test.Friendly;
                }
            }
        }

        return string.Join(" ", key.Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Shows/hides the amber warning under the instance line when the selected
    /// instance is known (tested) to fail the service of the currently pasted URL.</summary>
    private void UpdateInstanceServiceWarning()
    {
        if (_selectedInstance == null)
        {
            InstanceWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        var key = DetectServiceKey(UrlTextBox.Text);
        if (key == null || _selectedInstance.Tests == null
            || !_selectedInstance.Tests.TryGetValue(key, out var test) || test.Status)
        {
            InstanceWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        var friendly = GetServiceFriendlyName(key);
        InstanceWarningText.Text = $"⚠ {_selectedInstance.Api} may not support {friendly}. Tap Select Instance to pick a better one.";
        InstanceWarningText.Visibility = Visibility.Visible;
    }

    /// <summary>Best-effort check for a "/donate" page on the selected instance's frontend.
    /// cobalt's own web frontend template ships a built-in donate route (web/src/routes/donate),
    /// so a 200 there is a real, reliable signal, not a guess, unlike scraping a page for known
    /// donation-platform links (which most instances don't even have detectable in raw HTML).
    /// Silently shows nothing if there's no frontend, the check fails, or it times out.</summary>
    private async void TriggerDonateCheck(CobaltInstance instance)
    {
        _donateCheckCts?.Cancel();
        var cts = _donateCheckCts = new CancellationTokenSource();
        DonateLink.Visibility = Visibility.Collapsed;

        if (!LooksLikeSafeHostname(instance?.Frontend))
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

        var url = $"https://{instance.Frontend}/donate";
        var found = await UrlRespondsOkAsync(url, timeoutCts.Token);
        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (found)
        {
            DonateLink.NavigateUri = new Uri(url);
            DonateLink.Visibility = Visibility.Visible;
        }
    }

    private UIElement BuildInstanceRow(CobaltInstance instance, string serviceKey = null, string serviceFriendly = null)
    {
        // Colored score badge: green = good, amber = mixed, red = poor
        var badgeColor = instance.Score >= 80 ? Windows.UI.Color.FromArgb(255, 46, 125, 50)
                       : instance.Score >= 50 ? Windows.UI.Color.FromArgb(255, 178, 106, 0)
                       : Windows.UI.Color.FromArgb(255, 198, 40, 40);

        var badge = new Border
        {
            Background = new SolidColorBrush(badgeColor),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 52,
            Child = new TextBlock
            {
                Text = $"{instance.Score:F0}%",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        // This is NOT a trust/safety rating; cobalt.directory doesn't publish one.
        // It's the percentage of services that currently pass their automated tests.
        ToolTipService.SetToolTip(badge,
            $"{instance.Score:F0}% of services are currently working, per cobalt.directory's automated tests.\n" +
            "This measures compatibility, not the operator's trustworthiness.");

        var details = $"v{instance.Version}";
        if (instance.Turnstile)
        {
            details += " • verification required (may not work)";
        }

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = instance.Api },
                new TextBlock { Text = details, FontSize = 12, Opacity = 0.6 }
            }
        };

        if (serviceKey != null)
        {
            var match = instance.Tests != null && instance.Tests.TryGetValue(serviceKey, out var t) ? t : null;
            var (label, brush) = match == null
                ? ($"? not tested for {serviceFriendly}", new SolidColorBrush(Windows.UI.Color.FromArgb(180, 150, 150, 150)))
                : match.Status
                    ? ($"✓ supports {serviceFriendly}", new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80)))
                    : ($"✕ fails {serviceFriendly}", new SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 115, 115)));

            textStack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = brush,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
        }

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { badge, textStack }
        };

        // Star toggle: favorite / unfavorite, persisted immediately
        var isFavorite = _settings.Favorites.Contains(instance.Api);
        var starIcon = new FontIcon { Glyph = isFavorite ? "\uE735" : "\uE734", FontSize = 14 };
        var star = new ToggleButton
        {
            IsChecked = isFavorite,
            Content = starIcon,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 4, 6, 4)
        };
        ToolTipService.SetToolTip(star, "Favorite this instance");
        star.Checked += (_, _) =>
        {
            starIcon.Glyph = "\uE735";
            if (!_settings.Favorites.Contains(instance.Api))
            {
                _settings.Favorites.Add(instance.Api);
                _settings.Save();
            }
        };
        star.Unchecked += (_, _) =>
        {
            starIcon.Glyph = "\uE734";
            _settings.Favorites.Remove(instance.Api);
            _settings.Save();
        };

        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        if (LooksLikeSafeHostname(instance.Frontend))
        {
            var visitButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE8A7", FontSize = 14 }, // OpenInNewWindow
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 4, 6, 4)
            };
            ToolTipService.SetToolTip(visitButton, $"Open {instance.Frontend} in your browser");
            visitButton.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo($"https://{instance.Frontend}/") { UseShellExecute = true });
                }
                catch { /* no default browser association, etc. \u2014 not fatal */ }
            };
            rightButtons.Children.Add(visitButton);
        }

        rightButtons.Children.Add(star);

        var row = new Grid { Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(rightButtons, 1);
        row.Children.Add(left);
        row.Children.Add(rightButtons);
        return row;
    }

    /// <summary>
    /// Fills the bottom panel with green/red chips for every service cobalt.directory
    /// tested on this instance. Tooltip = full name + the tester's message.
    /// </summary>
    /// <remarks>
    /// Chips are packed into rows manually (StackPanel-of-StackPanels) rather than via
    /// an ItemsControl + VariableSizedWrapGrid: that combination can silently force every
    /// item to a shared uniform cell width, clipping longer service names, and an attempt
    /// to fix it with an ItemContainerStyle crashed the app outright. This is the same
    /// plain-composition approach already used elsewhere (BuildInstanceRow, BuildHistoryRow).
    /// </remarks>
    private void PopulateServicesPanel(CobaltInstance instance)
    {
        ServicesList.Children.Clear();

        if (instance.Tests == null || instance.Tests.Count == 0)
        {
            ServicesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var services = instance.Tests
            .Where(kv => !string.Equals(kv.Key, "Frontend", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .Where(t => !string.IsNullOrEmpty(t?.Friendly))
            .OrderByDescending(t => t.Status)
            .ThenBy(t => t.Friendly)
            .ToList();

        var workingCount = services.Count(t => t.Status);
        ServicesHeader.Text = $"Supported Services on {instance.Api}: {workingCount}/{services.Count} working (tap to expand)";

        const double rowWidthBudget = 590; // approx content width inside the window
        StackPanel currentRow = null;
        double currentRowWidth = 0;

        foreach (var service in services)
        {
            var chip = BuildServiceChip(service);

            // Rough width estimate (chars * avg glyph width + padding/margin), doesn't need
            // to be pixel-perfect, just close enough to decide when to wrap to a new row
            var label = (service.Status ? "✓ " : "✕ ") + service.Friendly;
            var estimatedWidth = label.Length * 7.5 + 24 + 8 + 20;

            if (currentRow == null || (currentRowWidth + estimatedWidth > rowWidthBudget && currentRow.Children.Count > 0))
            {
                currentRow = new StackPanel { Orientation = Orientation.Horizontal };
                ServicesList.Children.Add(currentRow);
                currentRowWidth = 0;
            }

            currentRow.Children.Add(chip);
            currentRowWidth += estimatedWidth;
        }

        ServicesPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Toggles the services chip grid open/closed and resizes the window to match,
    /// rather than squeezing the grid into whatever space happens to be left over.</summary>
    private void ServicesToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _servicesExpanded = !_servicesExpanded;
        ServicesScrollViewer.Visibility = _servicesExpanded ? Visibility.Visible : Visibility.Collapsed;
        ServicesChevron.Glyph = _servicesExpanded ? "" : ""; // ChevronUp : ChevronDown
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            660, _servicesExpanded ? ExpandedWindowHeight : CollapsedWindowHeight));
    }

    private static Border BuildServiceChip(ServiceTest service)
    {
        var background = service.Status
            ? Windows.UI.Color.FromArgb(48, 46, 175, 80)
            : Windows.UI.Color.FromArgb(40, 210, 60, 60);

        var chip = new Border
        {
            Background = new SolidColorBrush(background),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = (service.Status ? "✓ " : "✕ ") + service.Friendly,
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap
            }
        };

        // Full name + the tester's message, so e.g. "YouTube" vs "YouTube Music"
        // is never ambiguous even if the chip text itself gets visually tight
        var statusWord = service.Status ? "Working" : "Not working";
        ToolTipService.SetToolTip(chip, $"{service.Friendly}: {statusWord}\n{service.Message}");
        return chip;
    }

    /// <summary>True if `value` parses as a bare hostname with nothing left over when
    /// given an assumed https:// prefix. Confirmed empirically that .NET's own URI parser
    /// resolves the connection host from "user@host" syntax to whatever comes AFTER the
    /// @, not before, so a crafted value like "trusted.tld@evil.com" would silently
    /// connect to evil.com while looking like trusted.tld. This also rejects embedded
    /// paths, whitespace, or a smuggled non-default port. Used to gate any community-
    /// sourced hostname before it's used to build a request URL or handed to Process.Start.</summary>
    private static bool LooksLikeSafeHostname(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && Uri.TryCreate($"https://{value}/", UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, value, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort;

    private async Task<List<CobaltInstance>> FetchInstancesAsync()
    {
        // Public API documented at https://cobalt.directory/api
        using var response = await _httpClient.GetAsync("https://cobalt.directory/api/tests");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var parsed = await JsonSerializer.DeserializeAsync<DirectoryTestsResponse>(stream);

        var instances = parsed?.Data?
            .Where(i => i.Online && !string.IsNullOrEmpty(i.Api))
            // Api gets interpolated directly into a request URL ($"{protocol}://{api}/"),
            // so reject it outright if it isn't a clean bare hostname, rather than trying to
            // "fix" it the way Protocol is normalized below (there's no sensible default
            // hostname to fall back to the way there's a sensible default scheme).
            .Where(i => LooksLikeSafeHostname(i.Api))
            .OrderByDescending(i => i.Score)
            .ToList() ?? new List<CobaltInstance>();

        // Normalize here, once, so every downstream use (request URLs, queued items) is
        // automatically safe; cobalt.directory's own data has only ever shown "https" in
        // practice, but this field flows straight into a request scheme, so don't trust an
        // unexpected value without at least constraining it to the two schemes we support.
        foreach (var instance in instances)
        {
            if (instance.Protocol != "http" && instance.Protocol != "https")
            {
                instance.Protocol = "https";
            }
        }

        return instances;
    }

    // ---------- About ----------

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 380,
            Children =
            {
                new TextBlock
                {
                    Text = "DropDown is a downloader for video and audio from social media and " +
                           "video sites. It works by sending the URLs you paste to a community-run " +
                           "cobalt instance, which does the actual downloading.",
                    TextWrapping = TextWrapping.Wrap
                },
                BuildAboutCreditRow(
                    "cobalt", "https://github.com/imputnet/cobalt",
                    "The open-source downloader engine that does the actual work."),
                BuildAboutCreditRow(
                    "cobalt.directory", "https://cobalt.directory",
                    "The community-maintained list of public cobalt instances DropDown pulls from."),
                BuildAboutCreditRow(
                    "☕ Buy hyperdefined a coffee", "https://buymeacoffee.com/hyperdefined",
                    "cobalt.directory is maintained solo; if it's useful to you, consider supporting them."),
                new TextBlock
                {
                    Text = "DropDown is an independent, unofficial project. It is not made, endorsed, " +
                           "or affiliated with imput (the team behind cobalt, imput.net) or " +
                           "cobalt.directory; it's simply a client built on top of their public tools.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.6
                }
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "About DropDown",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        await ShowDialogAsync(dialog);
    }

    // Hardcoded, app-authored URLs (not community-sourced data), so none of the
    // hostname validation used elsewhere for instance-provided links applies here.
    private static UIElement BuildAboutCreditRow(string name, string url, string description)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new HyperlinkButton
        {
            Content = name,
            NavigateUri = new Uri(url),
            Padding = new Thickness(0),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7
        });
        return stack;
    }

    // ---------- Download history ----------

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 420,
            MinWidth = 460
        };

        if (_history.Entries.Count == 0)
        {
            listView.Items.Add(new ListViewItem
            {
                Content = new TextBlock
                {
                    Text = "No downloads yet. Completed downloads will show up here.",
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap
                },
                IsEnabled = false
            });
        }
        else
        {
            foreach (var entry in _history.Entries)
            {
                listView.Items.Add(new ListViewItem
                {
                    Content = BuildHistoryRow(entry),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Download History",
            Content = new ScrollViewer { Content = listView, MaxHeight = 440 },
            CloseButtonText = "Close",
            SecondaryButtonText = "Clear History",
            IsSecondaryButtonEnabled = _history.Entries.Count > 0,
            DefaultButton = ContentDialogButton.Close
        };

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Secondary)
        {
            _history.Entries.Clear();
            _history.Save();
        }
    }

    private UIElement BuildHistoryRow(DownloadHistoryEntry entry)
    {
        var exists = File.Exists(entry.FullPath);

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = exists ? 1.0 : 0.45,
            Children =
            {
                new TextBlock { Text = entry.FileName, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = $"{entry.TimestampUtc.ToLocalTime():g} • {FormatBytes(entry.SizeBytes)} • via {entry.InstanceApi}"
                           + (exists ? "" : "  •  file no longer exists"),
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        var openFileButton = new Button { Content = "Open File", IsEnabled = exists, FontSize = 12 };
        openFileButton.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        };

        var showInFolderButton = new Button
        {
            Content = "Show in Folder",
            IsEnabled = exists,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0)
        };
        showInFolderButton.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.FullPath}\"") { UseShellExecute = true });
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { openFileButton, showInFolderButton }
        };

        var row = new Grid { Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textStack, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(textStack);
        row.Children.Add(buttons);
        return row;
    }

    // ---------- Options dialog ----------

    private static readonly (string Label, string Tag)[] ThemeChoices =
        { ("Mica (frosted)", "mica"), ("Mica Alt (deeper frost)", "micaAlt"),
          ("Acrylic (glassy)", "acrylic"), ("Classic (solid)", "none") };
    private static readonly (string Label, string Tag)[] AudioFormatChoices =
        { ("Best (original)", "best"), (".mp3", "mp3"), (".ogg", "ogg"), (".opus", "opus"), (".wav", "wav") };
    private static readonly (string Label, string Tag)[] AudioBitrateChoices =
        { ("320 kbps", "320"), ("256 kbps", "256"), ("128 kbps", "128"), ("96 kbps", "96"), ("64 kbps", "64") };
    private static readonly (string Label, string Tag)[] VideoQualityChoices =
        { ("Max", "max"), ("8K (4320p)", "4320"), ("4K (2160p)", "2160"), ("1440p (2K)", "1440"),
          ("1080p", "1080"), ("720p", "720"), ("480p", "480"), ("360p", "360"), ("240p", "240"), ("144p", "144") };
    private static readonly (string Label, string Tag)[] QueueDisplayChoices =
        { ("Both (filename + URL)", "both"), ("Filename only", "filename"), ("URL only", "url") };
    private static readonly (string Label, string Tag)[] FilenameStyleChoices =
        { ("Classic", "classic"), ("Basic (default)", "basic"), ("Pretty", "pretty"), ("Nerdy (full details)", "nerdy") };
    private static readonly (string Label, string Tag)[] YoutubeVideoCodecChoices =
        { ("H.264 (default, most compatible)", "h264"), ("AV1", "av1"), ("VP9", "vp9") };
    private static readonly (string Label, string Tag)[] YoutubeVideoContainerChoices =
        { ("Auto (default)", "auto"), (".mp4", "mp4"), (".webm", "webm"), (".mkv", "mkv") };
    private static readonly (string Label, string Tag)[] DownloadModeChoices =
        { ("Video", "video"), ("Audio Only", "audio"), ("Video (Muted)", "mute") };

    private static ComboBox MakeCombo(string header, (string Label, string Tag)[] choices, string selectedTag)
    {
        var combo = new ComboBox { Header = header, MinWidth = 150 };
        foreach (var (label, tag) in choices)
        {
            var item = new ComboBoxItem { Content = label, Tag = tag };
            combo.Items.Add(item);
            if (tag == selectedTag)
            {
                combo.SelectedItem = item;
            }
        }
        if (combo.SelectedItem == null)
        {
            combo.SelectedIndex = 0;
        }
        return combo;
    }

    private async void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var themeCombo = MakeCombo("Theme", ThemeChoices, _settings.Theme);
        var modeCombo = MakeCombo("Default Download Mode", DownloadModeChoices, _settings.DownloadMode);
        var formatCombo = MakeCombo("Default Audio Format", AudioFormatChoices, _settings.AudioFormat);
        var bitrateCombo = MakeCombo("Default Bitrate", AudioBitrateChoices, _settings.AudioBitrate);
        var qualityCombo = MakeCombo("Default Video Quality", VideoQualityChoices, _settings.VideoQuality);

        var pendingFolder = _settings.DefaultFolder;
        var folderText = new TextBlock
        {
            Text = string.IsNullOrEmpty(pendingFolder) ? "No default folder" : pendingFolder,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };
        var browseButton = new Button { Content = "Browse…" };
        browseButton.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                pendingFolder = folder.Path;
                folderText.Text = pendingFolder;
            }
        };
        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += (_, _) =>
        {
            pendingFolder = null;
            folderText.Text = "No default folder";
        };

        var pickerChooserToggle = new ToggleSwitch
        {
            Header = "Multi-Item Posts (carousels)",
            OffContent = "Auto-download the first item",
            OnContent = "Let me choose which items",
            IsOn = _settings.EnablePickerChooser
        };

        var toastToggle = new ToggleSwitch
        {
            Header = "Notifications",
            OffContent = "Off",
            OnContent = "Notify me when a download finishes (if the window isn't focused)",
            IsOn = _settings.EnableToastNotifications
        };

        var trackerStrippingToggle = new ToggleSwitch
        {
            Header = "URL Tracking Parameters",
            OffContent = "Keep links as pasted",
            OnContent = "Strip tracking params (utm_source, fbclid, gclid, etc.)",
            IsOn = _settings.EnableTrackerStripping
        };

        var queueDisplayCombo = MakeCombo("Queue Row Display", QueueDisplayChoices, _settings.QueueDisplayMode);

        var filenameStyleCombo = MakeCombo("File Naming Style", FilenameStyleChoices, _settings.FilenameStyle);
        var youtubeCodecCombo = MakeCombo("YouTube Video Codec", YoutubeVideoCodecChoices, _settings.YoutubeVideoCodec);
        var youtubeContainerCombo = MakeCombo("YouTube Container", YoutubeVideoContainerChoices, _settings.YoutubeVideoContainer);

        var disableMetadataToggle = new ToggleSwitch
        {
            Header = "File Metadata",
            OffContent = "Include title/artist metadata (default)",
            OnContent = "Strip metadata (privacy)",
            IsOn = _settings.DisableMetadata
        };

        var convertGifToggle = new ToggleSwitch
        {
            Header = "Looping Video",
            OffContent = "Keep as .webp",
            OnContent = "Convert to .gif (default)",
            IsOn = _settings.ConvertGif
        };

        var allowH265Toggle = new ToggleSwitch
        {
            Header = "H.265/HEVC Video",
            OffContent = "Prefer H.264 (default, most compatible)",
            OnContent = "Allow H.265 (smaller files, less compatible)",
            IsOn = _settings.AllowH265
        };

        var tiktokFullAudioToggle = new ToggleSwitch
        {
            Header = "TikTok Audio",
            OffContent = "Use video's own audio track (default)",
            OnContent = "Use the original sound's full audio track",
            IsOn = _settings.TiktokFullAudio
        };

        var youtubeBetterAudioToggle = new ToggleSwitch
        {
            Header = "YouTube Audio Quality",
            OffContent = "Standard (default)",
            OnContent = "Prefer higher-quality audio track when available",
            IsOn = _settings.YoutubeBetterAudio
        };

        var alwaysProxyToggle = new ToggleSwitch
        {
            Header = "Download Privacy",
            OffContent = "Allow direct redirects when possible (default, lighter on the instance)",
            OnContent = "Always proxy through the instance (hides your IP from the origin, uses more of the instance's bandwidth)",
            IsOn = _settings.AlwaysProxy
        };

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380,
            Children =
            {
                themeCombo,
                modeCombo,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { formatCombo, bitrateCombo } },
                qualityCombo,
                pickerChooserToggle,
                toastToggle,
                trackerStrippingToggle,
                queueDisplayCombo,
                filenameStyleCombo,
                disableMetadataToggle,
                convertGifToggle,
                allowH265Toggle,
                tiktokFullAudioToggle,
                youtubeBetterAudioToggle,
                alwaysProxyToggle,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { youtubeCodecCombo, youtubeContainerCombo } },
                new TextBlock { Text = "Default Download Folder", Margin = new Thickness(0, 4, 0, -8) },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { browseButton, clearButton } },
                folderText
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Options",
            Content = new ScrollViewer { Content = content, MaxHeight = 480 },
            PrimaryButtonText = "Save Defaults",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            _settings.Theme = ComboTag(themeCombo, "mica");
            _settings.DownloadMode = ComboTag(modeCombo, "video");
            _settings.AudioFormat = ComboTag(formatCombo, "mp3");
            _settings.AudioBitrate = ComboTag(bitrateCombo, "320");
            _settings.VideoQuality = ComboTag(qualityCombo, "1080");
            _settings.EnablePickerChooser = pickerChooserToggle.IsOn;
            _settings.EnableToastNotifications = toastToggle.IsOn;
            _settings.EnableTrackerStripping = trackerStrippingToggle.IsOn;
            _settings.QueueDisplayMode = ComboTag(queueDisplayCombo, "both");
            _settings.FilenameStyle = ComboTag(filenameStyleCombo, "basic");
            _settings.DisableMetadata = disableMetadataToggle.IsOn;
            _settings.ConvertGif = convertGifToggle.IsOn;
            _settings.AllowH265 = allowH265Toggle.IsOn;
            _settings.TiktokFullAudio = tiktokFullAudioToggle.IsOn;
            _settings.YoutubeBetterAudio = youtubeBetterAudioToggle.IsOn;
            _settings.YoutubeVideoCodec = ComboTag(youtubeCodecCombo, "h264");
            _settings.YoutubeVideoContainer = ComboTag(youtubeContainerCombo, "auto");
            _settings.AlwaysProxy = alwaysProxyToggle.IsOn;
            _settings.DefaultFolder = pendingFolder;
            _settings.Save();
            ApplyDefaultsToUi();
            ApplyTheme();
        }
    }

    // ---------- Download options / filename preview ----------

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // A new URL means the old custom name no longer applies
        _filenameEditedByUser = false;
        UpdateInstanceServiceWarning();
        TriggerFilenamePreview();
    }

    private void FileNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressFilenameEvents)
        {
            // User typed a name, stop auto-filling until they clear it or change the URL
            _filenameEditedByUser = !string.IsNullOrWhiteSpace(FileNameTextBox.Text);
        }
    }

    private void DownloadModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // DownloadModeCombo is declared before VideoQualityCombo/AudioFormatCombo/
        // AudioBitrateCombo in the XAML, and applying its XAML-declared SelectedIndex="0"
        // fires this handler synchronously during InitializeComponent, before those later
        // sibling fields are assigned (still null at that point). Confirmed via a real
        // crash (0xc000027b in Microsoft.UI.Xaml.dll) on every launch where DownloadMode
        // stays "video" (the default), since that's the one case nothing else later
        // programmatically re-triggers this handler to paper over the early null dereference.
        // Safe to no-op here: XAML's own default Visibility values already match "video"
        // mode correctly (VideoQualityCombo visible, the two audio combos Collapsed).
        if (VideoQualityCombo == null)
        {
            return;
        }

        // Video quality still applies to a muted video, so it only hides for pure audio.
        var audio = ComboTag(DownloadModeCombo, "video") == "audio";
        VideoQualityCombo.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        AudioFormatCombo.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        AudioBitrateCombo.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        TriggerFilenamePreview();
    }

    private void DownloadOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        TriggerFilenamePreview();
    }

    private static string ComboTag(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    // Dictionary<string, object> (not <string, string>) is deliberate: System.Text.Json
    // serializes each value by its own runtime type, so a boxed bool still comes out as a
    // real JSON boolean literal (true, not "true") while strings still come out quoted.
    // cobalt's own request schema expects real booleans for the flag-style params below.
    private static Dictionary<string, object> BuildRequestPayload(string mediaUrl, DownloadOptions options)
    {
        var payload = new Dictionary<string, object> { ["url"] = mediaUrl };

        switch (options.DownloadMode)
        {
            case "audio":
                payload["downloadMode"] = "audio";
                payload["audioFormat"] = options.AudioFormat;
                payload["audioBitrate"] = options.AudioBitrate;
                break;
            case "mute":
                payload["downloadMode"] = "mute";
                payload["videoQuality"] = options.VideoQuality;
                break;
            default: // "video": cobalt's own default ("auto"), no explicit downloadMode needed
                payload["videoQuality"] = options.VideoQuality;
                break;
        }

        payload["filenameStyle"] = options.FilenameStyle;
        payload["disableMetadata"] = options.DisableMetadata;
        payload["convertGif"] = options.ConvertGif;
        payload["allowH265"] = options.AllowH265;
        payload["tiktokFullAudio"] = options.TiktokFullAudio;
        payload["youtubeBetterAudio"] = options.YoutubeBetterAudio;
        payload["youtubeVideoCodec"] = options.YoutubeVideoCodec;
        payload["youtubeVideoContainer"] = options.YoutubeVideoContainer;
        payload["alwaysProxy"] = options.AlwaysProxy;

        return payload;
    }

    /// <summary>
    /// Debounced: asks the instance what the file would be named and pre-fills the
    /// File Name box. Runs ~1s after the URL or any download option changes.
    /// </summary>
    private async void TriggerFilenamePreview()
    {
        if (_selectedInstance == null || _filenameEditedByUser || _isDownloading
            || string.IsNullOrWhiteSpace(UrlTextBox?.Text))
        {
            return;
        }

        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(1000, cts.Token);

            DownloadStatusText.Text = "Fetching file name…";
            var previewUrl = NormalizeSourceUrl(UrlTextBox.Text.Trim());
            var result = await RequestDownloadAsync(
                previewUrl, _selectedInstance.Protocol, _selectedInstance.Api,
                BuildCurrentDownloadOptions(), FileNameTextBox.Text, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (result.Kind == ResolveKind.Direct && !string.IsNullOrEmpty(result.Filename))
            {
                _suppressFilenameEvents = true;
                FileNameTextBox.Text = SanitizeFileName(result.Filename);
                _suppressFilenameEvents = false;
            }
            // Picker (multi-item) results don't have a single filename to preview:
            // names are generated per item after you choose which ones to download
            DownloadStatusText.Text = "";
        }
        catch (TaskCanceledException)
        {
            // Either superseded by a newer edit (fine) or the HTTP request timed out
            if (!cts.IsCancellationRequested)
            {
                DownloadStatusText.Text = "Couldn't fetch file name: the instance took too long to respond.";
            }
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                DownloadStatusText.Text = $"Couldn't fetch file name: {ex.Message}";
            }
        }
    }

    // ---------- Download queue ----------

    private enum QueueItemStatus { Pending, Downloading }
    private enum DownloadOutcome { Success, Failed, Canceled, Skipped }

    /// <summary>Every cobalt request parameter that depends on user preference rather than
    /// the URL/instance itself. Bundled into one type so RequestDownloadAsync and QueueItem
    /// don't need a growing list of individual parameters/fields for each new cobalt option.</summary>
    private sealed class DownloadOptions
    {
        /// <summary>cobalt downloadMode: "video" (cobalt's "auto") | "audio" | "mute".</summary>
        public string DownloadMode;
        public string AudioFormat;
        public string AudioBitrate;
        public string VideoQuality;
        public string FilenameStyle;
        public bool DisableMetadata;
        public bool ConvertGif;
        public bool AllowH265;
        public bool TiktokFullAudio;
        public bool YoutubeBetterAudio;
        public string YoutubeVideoCodec;
        public string YoutubeVideoContainer;
        public bool AlwaysProxy;

        /// <summary>True only for pure audio-only mode; a muted video is still a video for
        /// filename-extension purposes.</summary>
        public bool AudioOnly => DownloadMode == "audio";
    }

    /// <summary>Reads the download-mode/quality controls on the main form plus the
    /// Options-only settings from _settings, combining both into one DownloadOptions.</summary>
    private DownloadOptions BuildCurrentDownloadOptions() => new DownloadOptions
    {
        DownloadMode = ComboTag(DownloadModeCombo, "video"),
        AudioFormat = ComboTag(AudioFormatCombo, "mp3"),
        AudioBitrate = ComboTag(AudioBitrateCombo, "320"),
        VideoQuality = ComboTag(VideoQualityCombo, "1080"),
        FilenameStyle = _settings.FilenameStyle,
        DisableMetadata = _settings.DisableMetadata,
        ConvertGif = _settings.ConvertGif,
        AllowH265 = _settings.AllowH265,
        TiktokFullAudio = _settings.TiktokFullAudio,
        YoutubeBetterAudio = _settings.YoutubeBetterAudio,
        YoutubeVideoCodec = _settings.YoutubeVideoCodec,
        YoutubeVideoContainer = _settings.YoutubeVideoContainer,
        AlwaysProxy = _settings.AlwaysProxy
    };

    /// <summary>A queued download, a full snapshot of the URL, instance, mode/quality
    /// settings, and destination folder at the moment it was added, so later edits to the
    /// main form (for the next URL you're about to add) can't retroactively change an
    /// already-queued item. Bug fixed 2026-07-14: SaveFolder used to be missing from this
    /// snapshot, so changing "Select Download Folder" mid-queue (never disabled during
    /// processing) silently redirected every remaining item to the new folder.</summary>
    private sealed class QueueItem
    {
        public string SourceUrl;
        public string InstanceApi;
        public string InstanceProtocol;
        public DownloadOptions Options;
        public string DesiredFileName;
        public string SaveFolder;
        public QueueItemStatus Status = QueueItemStatus.Pending;
    }

    private bool TrySnapshotQueueItem(out QueueItem item, out string error)
    {
        item = null;

        if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            error = "Please enter a video URL.";
            return false;
        }
        if (_selectedInstance == null)
        {
            error = "Please select an instance first.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_selectedFolder))
        {
            error = "Please select a download folder first.";
            return false;
        }

        item = new QueueItem
        {
            SourceUrl = NormalizeSourceUrl(UrlTextBox.Text.Trim()),
            InstanceApi = _selectedInstance.Api,
            InstanceProtocol = _selectedInstance.Protocol,
            Options = BuildCurrentDownloadOptions(),
            DesiredFileName = string.IsNullOrWhiteSpace(FileNameTextBox.Text) ? null : FileNameTextBox.Text.Trim(),
            SaveFolder = _selectedFolder
        };
        error = null;
        return true;
    }

    private void AddToQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySnapshotQueueItem(out var item, out var error))
        {
            ShowError(error);
            return;
        }

        _queue.Add(item);
        UrlTextBox.Text = "";
        FileNameTextBox.Text = "";
        RefreshQueuePanel();
    }

    private void RefreshQueuePanel()
    {
        QueueList.Children.Clear();

        if (_queue.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        // The item actively downloading is still in _queue (only removed on completion),
        // so it needs to be called out separately rather than folded into "waiting".
        var downloadingCount = _queue.Count(i => i.Status == QueueItemStatus.Downloading);
        var pendingCount = _queue.Count - downloadingCount;
        QueueHeader.Text = downloadingCount > 0
            ? $"Queue: 1 downloading, {pendingCount} waiting"
            : $"Queue: {_queue.Count} item(s) waiting";
        foreach (var item in _queue)
        {
            QueueList.Children.Add(BuildQueueRow(item));
        }
        QueuePanel.Visibility = Visibility.Visible;
    }

    private UIElement BuildQueueRow(QueueItem item)
    {
        var isDownloading = item.Status == QueueItemStatus.Downloading;
        var statusColor = isDownloading
            ? Windows.UI.Color.FromArgb(255, 76, 175, 80)
            : Windows.UI.Color.FromArgb(180, 150, 150, 150);

        // The filename is only known this early if the live preview (TriggerFilenamePreview)
        // already resolved it before "Add to Queue" was clicked, usually true in practice,
        // since typing/pasting + clicking takes longer than the preview's ~1s debounce, but
        // not guaranteed. Fall back to the URL wherever a filename would go if it isn't set.
        var hasFilename = !string.IsNullOrWhiteSpace(item.DesiredFileName);
        var displayMode = _settings.QueueDisplayMode;

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        if (displayMode == "filename")
        {
            textStack.Children.Add(new TextBlock
            {
                Text = hasFilename ? item.DesiredFileName : item.SourceUrl,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        else if (displayMode == "both" && hasFilename)
        {
            textStack.Children.Add(new TextBlock
            {
                Text = item.DesiredFileName,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = item.SourceUrl,
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        else // "url", or "both" without a resolved filename yet
        {
            textStack.Children.Add(new TextBlock
            {
                Text = item.SourceUrl,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        textStack.Children.Add(new TextBlock
        {
            Text = $"{(isDownloading ? "Downloading…" : "Pending")} • {item.InstanceApi} • " +
                   (item.Options.DownloadMode switch { "audio" => "Audio", "mute" => "Video (Muted)", _ => "Video" }),
            FontSize = 11,
            Foreground = new SolidColorBrush(statusColor)
        });

        var removeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Padding = new Thickness(6, 4, 6, 4),
            IsEnabled = !isDownloading
        };
        ToolTipService.SetToolTip(removeButton, "Remove from queue");
        removeButton.Click += (_, _) =>
        {
            _queue.Remove(item);
            RefreshQueuePanel();
        };

        var row = new Grid { Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textStack, 0);
        Grid.SetColumn(removeButton, 1);
        row.Children.Add(textStack);
        row.Children.Add(removeButton);
        return row;
    }

    // ---------- Download ----------

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        // Whatever's currently typed joins the queue too, "Get" with one URL and no
        // prior Add-to-Queue clicks is just a queue-of-one, same engine either way
        if (!string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            if (!TrySnapshotQueueItem(out var item, out var error))
            {
                ShowError(error);
                return;
            }
            _queue.Add(item);
            UrlTextBox.Text = "";
            FileNameTextBox.Text = "";
        }

        if (_queue.Count == 0)
        {
            ShowError("Please enter a video URL.");
            return;
        }

        RefreshQueuePanel();
        await ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        // A filename-preview fetch may still be in flight from before Get was clicked;
        // without this it can resolve later and overwrite the download's status text
        _previewCts?.Cancel();

        var totalCount = _queue.Count;
        var succeeded = 0;
        var failedCount = 0;
        var skippedCount = 0;
        var wasCanceled = false;

        _isDownloading = true;
        DownloadButton.IsEnabled = false;
        AddToQueueButton.IsEnabled = false;
        ProgressRow.Visibility = Visibility.Visible;
        CancelDownloadButton.IsEnabled = true;

        while (_queue.Count > 0)
        {
            var queueItem = _queue[0];
            queueItem.Status = QueueItemStatus.Downloading;
            RefreshQueuePanel();

            DownloadProgressBar.IsIndeterminate = true;
            DownloadStatusText.Text = $"Asking {queueItem.InstanceApi}…";

            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

            // Only pop the single-item confirmation dialog when this run isn't really
            // a batch; a real multi-item queue gets one summary dialog at the end instead
            var outcome = await DownloadOneAsync(queueItem, token, announceSingle: totalCount == 1);

            _downloadCts?.Dispose();
            _downloadCts = null;

            if (outcome == DownloadOutcome.Canceled)
            {
                // Stop the whole batch, but leave this item (and anything after it) queued
                // so the user can resume later; this matches what Cancel means everywhere else.
                // No batch summary here: DownloadOneAsync already set "Canceled." and that
                // should stand, not get overwritten by a "0 finished" dialog.
                queueItem.Status = QueueItemStatus.Pending;
                RefreshQueuePanel();
                wasCanceled = true;
                break;
            }

            _queue.RemoveAt(0);
            switch (outcome)
            {
                case DownloadOutcome.Success: succeeded++; break;
                case DownloadOutcome.Skipped: skippedCount++; break;
                default: failedCount++; break;
            }
            RefreshQueuePanel();
        }

        _isDownloading = false;
        DownloadButton.IsEnabled = true;
        AddToQueueButton.IsEnabled = true;
        ProgressRow.Visibility = Visibility.Collapsed;
        DownloadProgressBar.IsIndeterminate = false;

        if (totalCount > 1 && !wasCanceled)
        {
            var parts = new List<string> { $"{succeeded} finished" };
            if (failedCount > 0) parts.Add($"{failedCount} failed");
            if (skippedCount > 0) parts.Add($"{skippedCount} skipped");
            var summary = $"Queue complete: {string.Join(", ", parts)}.";
            DownloadStatusText.Text = summary;
            ShowInfo(summary);
        }
    }

    /// <summary>Resolves and downloads a single queue item (or a single-item post's worth
    /// of picker items). This is the one download engine, used identically whether it's
    /// a lone "Get" click or one entry in a multi-item queue.</summary>
    private async Task<DownloadOutcome> DownloadOneAsync(QueueItem queueItem, CancellationToken token, bool announceSingle)
    {
        // Positions (within the full picker item list, not the selected subset) already
        // downloaded successfully in an earlier attempt within this same call. Persists
        // across retry iterations so retrying after a later item in a multi-item post fails
        // can't silently re-download items you already have; the resolved tunnel URL for a
        // given item differs between resolve calls, but its position in the post is stable,
        // so position (not URL) is the identity that survives a retry's fresh resolve.
        var completedPickerIndices = new HashSet<int>();

        // Loops on a real failure only if the user chooses Retry in the error dialog;
        // Success/Canceled/Skipped all return directly and exit the loop immediately.
        while (true)
        {
        // Tracks whichever file is actively being written, so the catch block only
        // cleans up an in-flight file; items that already finished are kept
        string currentSavePath = null;
        try
        {
            // Always re-resolve at download time: tunnel URLs expire quickly
            var result = await RequestDownloadAsync(
                queueItem.SourceUrl, queueItem.InstanceProtocol, queueItem.InstanceApi,
                queueItem.Options, queueItem.DesiredFileName, token);

            if (result.Kind == ResolveKind.Picker)
            {
                var chosen = await ShowPickerChooserAsync(result.PickerItems, completedPickerIndices);
                if (chosen == null || chosen.Count == 0)
                {
                    // Not a failure: the user just chose not to download this particular
                    // multi-item post. Counting it as "failed" would be misleading in the
                    // batch summary (it reads like something went wrong).
                    DownloadStatusText.Text = "No items selected. Nothing downloaded.";
                    return DownloadOutcome.Skipped;
                }

                var baseName = DerivePickerBaseName(queueItem.SourceUrl, queueItem.DesiredFileName);
                for (var n = 0; n < chosen.Count; n++)
                {
                    token.ThrowIfCancellationRequested();

                    var (originalIndex, item) = chosen[n];
                    if (completedPickerIndices.Contains(originalIndex))
                    {
                        // Belt and suspenders: the chooser UI already disables these tiles,
                        // this just guarantees it regardless of how the selection got built.
                        continue;
                    }

                    DownloadStatusText.Text = $"Item {n + 1} of {chosen.Count}…";
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = 0;

                    // Numbered by original post position, not selection order, so the name
                    // stays the same across a retry regardless of what subset gets picked
                    // each time (and so "item 2 of 5" in the post is always named "-2", even
                    // if you only ever selected items 2 and 4).
                    var itemFilename = SanitizeFileName($"{baseName}-{originalIndex + 1}{GuessPickerExtension(item)}");
                    currentSavePath = GetUniquePath(Path.Combine(queueItem.SaveFolder, itemFilename));

                    await DownloadFileAsync(item.Url, currentSavePath, token);

                    var executableType = await DetectExecutableSignatureAsync(currentSavePath, token);
                    if (executableType != null)
                    {
                        throw new InvalidOperationException(
                            $"Blocked: the downloaded content is actually {executableType}, not media. " +
                            "The instance may be compromised or misbehaving. Try a different one.");
                    }

                    _history.AddEntry(new DownloadHistoryEntry
                    {
                        FileName = Path.GetFileName(currentSavePath),
                        FullPath = currentSavePath,
                        SourceUrl = queueItem.SourceUrl,
                        InstanceApi = queueItem.InstanceApi,
                        SizeBytes = new FileInfo(currentSavePath).Length,
                        TimestampUtc = DateTime.UtcNow
                    });
                    completedPickerIndices.Add(originalIndex);
                    currentSavePath = null; // this item is complete, don't clean it up on a later failure
                }
                _history.Save();

                DownloadStatusText.Text = $"Saved {chosen.Count} item(s).";
                if (announceSingle)
                {
                    ShowInfo($"Downloaded {chosen.Count} item(s) to {queueItem.SaveFolder}");
                }
                ShowDownloadCompleteToast($"{chosen.Count} item(s) saved", queueItem.SaveFolder);
            }
            else
            {
                var filename = ResolveFinalFilename(
                    result.Filename, result.Url, queueItem.DesiredFileName, queueItem.Options.AudioOnly, queueItem.Options.AudioFormat);
                // Never overwrite an existing file, append (1), (2), …
                currentSavePath = GetUniquePath(Path.Combine(queueItem.SaveFolder, filename));

                await DownloadFileAsync(result.Url, currentSavePath, token);

                var executableType = await DetectExecutableSignatureAsync(currentSavePath, token);
                if (executableType != null)
                {
                    throw new InvalidOperationException(
                        $"Blocked: the downloaded content is actually {executableType}, not media. " +
                        "The instance may be compromised or misbehaving. Try a different one.");
                }

                _history.AddEntry(new DownloadHistoryEntry
                {
                    FileName = Path.GetFileName(currentSavePath),
                    FullPath = currentSavePath,
                    SourceUrl = queueItem.SourceUrl,
                    InstanceApi = queueItem.InstanceApi,
                    SizeBytes = new FileInfo(currentSavePath).Length,
                    TimestampUtc = DateTime.UtcNow
                });
                _history.Save();

                DownloadStatusText.Text = $"Saved: {Path.GetFileName(currentSavePath)}";
                if (announceSingle)
                {
                    ShowInfo($"Download completed: {currentSavePath}");
                }
                ShowDownloadCompleteToast(Path.GetFileName(currentSavePath), queueItem.SaveFolder);
                currentSavePath = null;
            }

            return DownloadOutcome.Success;
        }
        catch (Exception ex)
        {
            // Don't leave a truncated file behind (a user cancel included), only the
            // item that was actively downloading when this happened, not prior completed ones.
            // A partial file is never resumable (cobalt tunnels don't honor Range requests,
            // confirmed by direct test) and is essentially always an invalid, unplayable
            // container, so there's nothing worth preserving here, unlike a completed file.
            if (currentSavePath != null && File.Exists(currentSavePath))
            {
                try { File.Delete(currentSavePath); } catch { /* locked or already gone, leave it */ }
            }

            if (token.IsCancellationRequested)
            {
                DownloadStatusText.Text = "Canceled.";
                return DownloadOutcome.Canceled;
            }

            DownloadStatusText.Text = "Download failed.";
            var retry = await ShowDownloadFailedDialogAsync(queueItem.SourceUrl, ex.Message);
            if (retry)
            {
                // Loop back to the top: re-resolves a fresh tunnel from scratch, since the
                // old one may well be the reason this failed (tunnels expire in roughly a
                // minute or two, confirmed by direct test) and can't be resumed either way.
                // Reset status/progress feedback so a retry doesn't just silently sit there
                // still showing "Download failed." while it's actually re-resolving.
                DownloadStatusText.Text = $"Asking {queueItem.InstanceApi}…";
                DownloadProgressBar.IsIndeterminate = true;
                DownloadProgressBar.Value = 0;
                continue;
            }
            return DownloadOutcome.Failed;
        }
        }
    }

    /// <summary>Shows the download-failure dialog with a Retry option. Retrying re-attempts
    /// the same queue item from scratch (fresh tunnel resolve included), not a byte-level
    /// resume; cobalt tunnels don't support that (confirmed by direct test: a Range request
    /// against a real tunnel was silently ignored and the full stream sent back anyway).</summary>
    private async Task<bool> ShowDownloadFailedDialogAsync(string url, string errorMessage)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Download Failed",
            Content = new TextBlock
            {
                Text = $"Error downloading {url}: {errorMessage}",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Retry",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        DownloadStatusText.Text = "Canceling…";
        _downloadCts?.Cancel();
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    // A malicious or compromised cobalt instance controls the "filename" field in its
    // response, and a picker item's extension is read straight from its server-provided
    // URL; nothing stops either from claiming to be "video.mp4.exe" or "clip.scr". Since
    // Download History's "Open File" launches the saved file via UseShellExecute (i.e.
    // runs it if it's an executable type), an unvalidated server-chosen extension is a
    // real code-execution vector disguised as a media download. Character-level sanitizing
    // (SanitizeFileName) doesn't catch this; a dangerous extension is made of otherwise
    // perfectly legal filename characters. Block known-dangerous extensions explicitly.
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".bat", ".cmd", ".com", ".pif", ".msi", ".msp", ".msix", ".msixbundle",
        ".gadget", ".application", ".jar", ".vb", ".vbs", ".vbe", ".js", ".jse", ".ws", ".wsf",
        ".wsc", ".wsh", ".ps1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".msh", ".msh1",
        ".msh2", ".mshxml", ".msh1xml", ".msh2xml", ".scf", ".lnk", ".inf", ".reg", ".hta",
        ".cpl", ".dll", ".sys", ".drv", ".sh", ".appref-ms", ".url", ".website", ".webloc"
    };

    private static bool IsDangerousExtension(string extension) =>
        !string.IsNullOrEmpty(extension) && DangerousExtensions.Contains(extension);

    /// <summary>User-typed name wins; otherwise the instance's name; otherwise a fallback.
    /// Ensures the result has a sensible, non-executable extension either way.</summary>
    private static string ResolveFinalFilename(
        string serverFilename, string downloadUrl, string desiredFileName, bool audioOnly, string audioFormat)
    {
        var filename = !string.IsNullOrWhiteSpace(desiredFileName)
            ? desiredFileName.Trim()
            : serverFilename;

        if (string.IsNullOrWhiteSpace(filename))
        {
            filename = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        }

        if (string.IsNullOrWhiteSpace(filename))
        {
            filename = $"download-{DateTime.Now:yyyyMMdd-HHmmss}";
        }

        // Strip any chained dangerous extensions (e.g. "video.mp4.exe" → "video.mp4")
        while (IsDangerousExtension(Path.GetExtension(filename)))
        {
            filename = Path.GetFileNameWithoutExtension(filename);
        }

        if (!Path.HasExtension(filename))
        {
            var extension = Path.GetExtension(serverFilename ?? "");
            if (string.IsNullOrEmpty(extension) || IsDangerousExtension(extension))
            {
                extension = audioOnly ? "." + audioFormat.Replace("best", "m4a") : ".mp4";
            }
            filename += extension;
        }

        return SanitizeFileName(filename);
    }

    // ---------- Picker chooser (multi-item posts) ----------

    /// <summary>Shows a thumbnail grid for a multi-item post. Returns the chosen items paired
    /// with their position in the full item list (needed for stable, retry-safe filename
    /// numbering; see the caller), an empty/null result if the user canceled, or every
    /// not-already-downloaded item if "Download All" was picked.
    /// <paramref name="alreadyDownloadedIndices"/> marks positions completed in an earlier
    /// attempt within the same DownloadOneAsync call (i.e. this is a retry after a later item
    /// in the post failed): those tiles are dimmed, tagged "SAVED", and can't be re-selected,
    /// so retrying a multi-item post can't silently re-download items you already have.</summary>
    private async Task<List<(int Index, PickerItem Item)>> ShowPickerChooserAsync(
        List<PickerItem> items, HashSet<int> alreadyDownloadedIndices)
    {
        var gridView = new GridView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 420,
            MinWidth = 460
        };

        for (var i = 0; i < items.Count; i++)
        {
            var isDone = alreadyDownloadedIndices.Contains(i);
            gridView.Items.Add(new GridViewItem
            {
                Content = BuildPickerTile(items[i], isDone),
                Tag = i,
                IsEnabled = !isDone
            });
        }

        var dialogBody = new StackPanel { Spacing = 8 };
        if (alreadyDownloadedIndices.Count > 0)
        {
            dialogBody.Children.Add(new TextBlock
            {
                Text = $"{alreadyDownloadedIndices.Count} item(s) from this post already downloaded " +
                       "before this retry; they're dimmed and can't be re-selected.",
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap
            });
        }
        dialogBody.Children.Add(new TextBlock
        {
            Text = "Tap items to select them, or use Download All.",
            FontSize = 12,
            Opacity = 0.7
        });
        dialogBody.Children.Add(new ScrollViewer { Content = gridView, MaxHeight = 420 });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Choose Items ({items.Count} in this post)",
            Content = dialogBody,
            PrimaryButtonText = "Download Selected",
            SecondaryButtonText = "Download All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowDialogAsync(dialog);

        List<int> selectedIndices;
        if (result == ContentDialogResult.Secondary)
        {
            // "All" means all remaining, not literally every item; re-grabbing an
            // already-completed one would just be a silent duplicate download.
            selectedIndices = Enumerable.Range(0, items.Count)
                .Where(i => !alreadyDownloadedIndices.Contains(i))
                .ToList();
        }
        else if (result == ContentDialogResult.Primary)
        {
            selectedIndices = gridView.SelectedItems.Cast<GridViewItem>().Select(gi => (int)gi.Tag).ToList();
        }
        else
        {
            return null;
        }

        if (selectedIndices.Count == 0)
        {
            return null;
        }
        return selectedIndices.Select(i => (i, items[i])).ToList();
    }

    private static UIElement BuildPickerTile(PickerItem item, bool alreadyDownloaded)
    {
        var tile = new Grid
        {
            Width = 120,
            Height = 120,
            Opacity = alreadyDownloaded ? 0.35 : 1.0,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255))
        };

        // Scheme check matters here: unlike HttpClient (which rejects non-http(s) schemes
        // itself), WinRT's BitmapImage will happily resolve file:// and UNC paths. A
        // malicious instance could set Thumb to a UNC-style URL to trigger an outbound
        // SMB/NTLM handshake just by rendering the "thumbnail"; this is the same trick used
        // against Outlook/Office auto-loaded remote images. Restrict to http/https.
        if (!string.IsNullOrEmpty(item.Thumb) && Uri.TryCreate(item.Thumb, UriKind.Absolute, out var thumbUri)
            && (thumbUri.Scheme == Uri.UriSchemeHttp || thumbUri.Scheme == Uri.UriSchemeHttps))
        {
            tile.Children.Add(new Image
            {
                Source = new BitmapImage(thumbUri),
                Stretch = Stretch.UniformToFill
            });
        }

        tile.Children.Add(new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = (item.Type ?? "item").ToUpperInvariant(),
                FontSize = 10,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            }
        });

        if (alreadyDownloaded)
        {
            tile.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 0, 0, 0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = "SAVED",
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80))
                }
            });
        }

        return tile;
    }

    /// <summary>Base filename for generated picker items, e.g. "postname" → "postname-1.jpg".
    /// Prefers a typed-in File Name; falls back to the URL's last path segment.</summary>
    private static string DerivePickerBaseName(string sourceUrl, string desiredFileName)
    {
        if (!string.IsNullOrWhiteSpace(desiredFileName))
        {
            return SanitizeFileName(Path.GetFileNameWithoutExtension(desiredFileName.Trim()));
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var last = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (!string.IsNullOrWhiteSpace(last))
            {
                return SanitizeFileName(last);
            }
        }

        return "post";
    }

    private static string GuessPickerExtension(PickerItem item)
    {
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
        {
            var fromUrl = Path.GetExtension(uri.LocalPath);
            if (!string.IsNullOrEmpty(fromUrl) && !IsDangerousExtension(fromUrl))
            {
                return fromUrl;
            }
        }

        return item.Type switch
        {
            "video" => ".mp4",
            "gif" => ".gif",
            _ => ".jpg"
        };
    }

    /// <summary>
    /// Asks the selected cobalt instance (v10+ API: POST to the instance root) to resolve
    /// a media URL. Returns either a single direct download (Direct) or, for multi-item
    /// posts, the full item list (Picker), unless the picker chooser is disabled in
    /// Options, in which case the first item is resolved automatically as before.
    /// </summary>
    private async Task<CobaltResolveResult> RequestDownloadAsync(
        string mediaUrl, string instanceProtocol, string instanceApi,
        DownloadOptions options, string desiredFileName, CancellationToken cancellationToken = default)
    {
        var baseUrl = $"{instanceProtocol}://{instanceApi}/";

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new StringContent(
            JsonSerializer.Serialize(BuildRequestPayload(mediaUrl, options)),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // HTML block page, proxy error, etc.
            throw new InvalidOperationException(
                $"The instance sent a non-JSON response (HTTP {(int)response.StatusCode}). " +
                "It may be offline or blocking requests. Try another instance.");
        }

        using var _ = doc;
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;

        switch (status)
        {
            case "redirect":
            case "tunnel":
                var url = root.GetProperty("url").GetString();
                var filename = root.TryGetProperty("filename", out var nameProp) ? nameProp.GetString() : null;
                return new CobaltResolveResult { Kind = ResolveKind.Direct, Url = url, Filename = filename };

            case "picker":
                var picker = root.GetProperty("picker");
                if (picker.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("The instance returned an empty item list for this URL.");
                }

                if (!_settings.EnablePickerChooser)
                {
                    // Picker chooser disabled in Options: silently grab the first item.
                    // Picker items rarely have a server filename, and the generic
                    // fallback below defaults non-audio downloads to .mp4, which is wrong
                    // for a photo/gif, so guess a sensible extension from the item type instead.
                    var firstItem = new PickerItem
                    {
                        Type = picker[0].TryGetProperty("type", out var firstTypeProp) ? firstTypeProp.GetString() : null,
                        Url = picker[0].GetProperty("url").GetString()
                    };
                    return new CobaltResolveResult
                    {
                        Kind = ResolveKind.Direct,
                        Url = firstItem.Url,
                        Filename = DerivePickerBaseName(mediaUrl, desiredFileName) + GuessPickerExtension(firstItem)
                    };
                }

                var items = new List<PickerItem>();
                foreach (var element in picker.EnumerateArray())
                {
                    items.Add(new PickerItem
                    {
                        Type = element.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null,
                        Url = element.GetProperty("url").GetString(),
                        Thumb = element.TryGetProperty("thumb", out var thumbProp) ? thumbProp.GetString() : null
                    });
                }
                return new CobaltResolveResult { Kind = ResolveKind.Picker, PickerItems = items };

            case "error":
                var code = root.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var codeProp)
                    ? codeProp.GetString() : "unknown";
                var hint = code != null && code.Contains("auth")
                    ? " This instance requires browser verification. Try one without 'verification required'."
                    : "";
                throw new InvalidOperationException($"Instance returned error '{code}'.{hint}");

            default:
                throw new InvalidOperationException($"Unexpected response from instance (status '{status}').");
        }
    }

    private const long DiskSpaceSafetyMarginBytes = 100L * 1024 * 1024; // leave 100MB headroom

    /// <summary>Best-effort free-space check. Returns true (and skips the check) for
    /// anything DriveInfo can't handle cleanly: UNC/network shares, permission quirks,
    /// etc. This only ever blocks a download when it's confident there's a problem.</summary>
    private static bool HasEnoughFreeSpace(string driveRoot, long neededBytes, out long availableBytes)
    {
        availableBytes = 0;
        if (string.IsNullOrEmpty(driveRoot))
        {
            return true;
        }
        try
        {
            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady)
            {
                return true;
            }
            availableBytes = drive.AvailableFreeSpace;
            return availableBytes >= neededBytes;
        }
        catch
        {
            return true;
        }
    }

    // Renaming away a dangerous extension (see ResolveFinalFilename/GuessPickerExtension)
    // stops Explorer/ShellExecute from launching the file by its now-safe name, but does
    // nothing about the actual bytes: a malicious instance could still serve real
    // executable content under a media-looking name, and a confused user renaming the
    // "broken video" back to .exe (or any tool that sniffs content instead of trusting
    // the extension) would run it. This checks what's actually on disk, not just the label.
    private static readonly (byte[] Signature, string Description)[] ExecutableSignatures =
    {
        (new byte[] { 0x4D, 0x5A }, "a Windows executable (MZ/PE)"),
        (new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, "a Linux executable (ELF)"),
        (new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, "a macOS executable (Mach-O)"),
        (new byte[] { 0xCE, 0xFA, 0xED, 0xFE }, "a macOS executable (Mach-O)"),
        (new byte[] { 0xFE, 0xED, 0xFA, 0xCE }, "a macOS executable (Mach-O)"),
        (new byte[] { 0xFE, 0xED, 0xFA, 0xCF }, "a macOS executable (Mach-O)"),
        (new byte[] { 0x23, 0x21 }, "a script (shebang)"),
        // Not an executable itself, but a common next stage in multi-layer droppers
        // (payload.zip → extract → base64-decode → run), same logic applies: no
        // legitimate video/audio/image file legitimately starts with a ZIP header.
        (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "a ZIP archive"),
        (new byte[] { 0x50, 0x4B, 0x05, 0x06 }, "a ZIP archive (empty)"),
        (new byte[] { 0x50, 0x4B, 0x07, 0x08 }, "a ZIP archive (spanned)"),
    };

    /// <summary>Peeks the first few bytes of a just-downloaded file and returns a
    /// description if they match a known executable/script signature, or null if the
    /// content doesn't look like a binary in disguise. Blocklist, not allowlist: media
    /// containers don't legitimately start with any of these, so false positives are
    /// effectively impossible without needing to enumerate every valid media format.</summary>
    private static async Task<string> DetectExecutableSignatureAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        int read;
        await using (var stream = File.OpenRead(path))
        {
            read = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        }

        foreach (var (signature, description) in ExecutableSignatures)
        {
            if (read >= signature.Length && header.AsSpan(0, signature.Length).SequenceEqual(signature))
            {
                return description;
            }
        }
        return null;
    }

    // The resolve step (RequestDownloadAsync) gets cobalt's own structured error codes to
    // work with; the actual tunnel fetch has no such thing, just a raw HTTP status, so
    // EnsureSuccessStatusCode()'s default wording ("Response status code does not indicate
    // success: 403 (Forbidden).") is all a user would otherwise see. One-sentence, plain-
    // language versions for the common cases instead.
    private static void EnsureDownloadSuccessStatusCode(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = (int)response.StatusCode;
        var message = code switch
        {
            403 => "The instance refused this download (403 Forbidden); it may have blocked or rate-limited the request.",
            404 => "The download link has expired or no longer exists (404 Not Found).",
            429 => "The instance is rate-limiting downloads right now (429 Too Many Requests); try again shortly or pick another instance.",
            >= 500 and <= 599 => $"The instance had a server error while sending the file (HTTP {code}).",
            _ => $"The download failed with an unexpected server response (HTTP {code})."
        };
        throw new InvalidOperationException(message);
    }

    private async Task DownloadFileAsync(string downloadUrl, string savePath, CancellationToken cancellationToken = default)
    {
        using var response = await GetDownloadResponseAsync(downloadUrl, cancellationToken);
        EnsureDownloadSuccessStatusCode(response);

        long? totalBytes = response.Content.Headers.ContentLength;
        // cobalt tunnels often only know an estimate
        if (totalBytes == null
            && response.Headers.TryGetValues("Estimated-Content-Length", out var estimates)
            && long.TryParse(estimates.FirstOrDefault(), out var estimated))
        {
            totalBytes = estimated;
        }

        // Bail out before writing a single byte if the target drive obviously can't fit
        // this; better than silently filling the disk over a multi-gigabyte download that
        // was always going to fail. A community-run instance controls this response, so
        // treat an implausible or huge size as something to guard against, not just trust.
        var driveRoot = Path.GetPathRoot(savePath);
        if (totalBytes is > 0
            && !HasEnoughFreeSpace(driveRoot, totalBytes.Value + DiskSpaceSafetyMarginBytes, out var available))
        {
            throw new IOException(
                $"Not enough free space on {driveRoot}: need {FormatBytes(totalBytes.Value)}, " +
                $"only {FormatBytes(available)} available.");
        }

        DownloadProgressBar.IsIndeterminate = totalBytes == null;
        DownloadProgressBar.Value = 0;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(savePath);

        var buffer = new byte[81920];
        long received = 0;
        long lastUiUpdate = 0;
        long lastSpaceCheck = 0;
        int read;

        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            // The size wasn't known upfront (or was underreported), so periodically confirm
            // we're not about to run the disk dry on what could be an unbounded stream.
            // Checked every ~50MB rather than every chunk to keep this cheap.
            if (received - lastSpaceCheck > 50L * 1024 * 1024)
            {
                lastSpaceCheck = received;
                if (!HasEnoughFreeSpace(driveRoot, DiskSpaceSafetyMarginBytes, out var remaining))
                {
                    throw new IOException(
                        $"Stopped: only {FormatBytes(remaining)} of free space left on {driveRoot}.");
                }
            }

            // Throttle UI updates to ~10/sec
            var now = Environment.TickCount64;
            if (now - lastUiUpdate > 100)
            {
                lastUiUpdate = now;
                if (totalBytes is > 0)
                {
                    DownloadProgressBar.Value = Math.Min(100, received * 100.0 / totalBytes.Value);
                    DownloadStatusText.Text = $"{FormatBytes(received)} / {FormatBytes(totalBytes.Value)}";
                }
                else
                {
                    DownloadStatusText.Text = $"{FormatBytes(received)} downloaded…";
                }
            }
        }

        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 100;
    }

    private const int MaxDownloadRedirects = 5;

    // The download URL a cobalt instance hands back is just as untrusted as any other
    // instance-controlled data; a compromised/malicious instance could point it at
    // localhost, a LAN device, or a cloud metadata endpoint (169.254.169.254) instead
    // of real media, and the app would happily save whatever came back to disk. Follows
    // redirects manually (AllowAutoRedirect is off on _downloadHttpClient) so every hop,
    // not just the first, gets checked against the same private/loopback/link-local block.
    private async Task<HttpResponseMessage> GetDownloadResponseAsync(string url, CancellationToken cancellationToken)
    {
        var currentUrl = url;
        for (var hop = 0; hop <= MaxDownloadRedirects; hop++)
        {
            await EnsureSafeDownloadHostAsync(currentUrl, cancellationToken);

            var response = await _downloadHttpClient.GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirectStatus(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location == null)
            {
                throw new InvalidOperationException("Blocked: the download server sent a redirect with no destination.");
            }
            currentUrl = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(currentUrl), location).ToString();
        }

        throw new InvalidOperationException("Blocked: too many redirects while resolving the download.");
    }

    private static bool IsRedirectStatus(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Best-effort "does this URL respond successfully" check, reusing the same
    /// safe-redirect-following/SSRF-protected fetch as the download path. A frontend hostname
    /// is community-sourced data too (same as a download URL), so it gets the same protection
    /// rather than a plain unguarded HttpClient.GetAsync. Never throws; any failure just means
    /// "no."</summary>
    private async Task<bool> UrlRespondsOkAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetDownloadResponseAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Takes a CancellationToken deliberately: DNS resolution here was originally
    // uncancellable, which meant the Cancel button couldn't interrupt a slow/hanging
    // lookup on a redirect hop, the one gap in an otherwise fully cancellable pipeline.
    private static async Task EnsureSafeDownloadHostAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Blocked: the download URL isn't a valid http(s) address.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new InvalidOperationException($"Blocked: couldn't resolve the download host \"{uri.Host}\".");
        }

        if (addresses.Length == 0 || addresses.Any(IsDisallowedDownloadAddress))
        {
            throw new InvalidOperationException(
                $"Blocked: the download URL points to a private/internal address ({uri.Host}). " +
                "The instance may be compromised or misbehaving. Try a different one.");
        }
    }

    // Loopback, RFC1918 private ranges, link-local (incl. the 169.254.169.254 cloud
    // metadata address), and IPv6 unique-local/link-local equivalents: anything that
    // shouldn't be reachable by "download this video from the internet."
    private static bool IsDisallowedDownloadAddress(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 127
                || b[0] == 0;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true; // fe80::/10 link-local
        }

        return false;
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F0} KB",
            _ => $"{bytes} B"
        };
    }

    // Windows doesn't consider these invalid filename characters; confirmed via direct
    // enumeration of Path.GetInvalidFileNameChars(), which does NOT include them, but
    // Unicode bidi-override characters (RTLO, U+202E, being the classic one) are a known
    // technique for visually disguising a filename, e.g. making "cod.exe" *display* as
    // "exe.doc" by reversing rendering order without touching the underlying bytes.
    // This is a display-hygiene measure on top of, not instead of, the extension/content
    // checks in ResolveFinalFilename/DetectExecutableSignatureAsync; those operate on the
    // real character/byte sequence and were never fooled by visual rendering either way.
    private static readonly char[] BidiControlChars =
    {
        '\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
        '\u2066', '\u2067', '\u2068', '\u2069'
    };

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        foreach (var c in BidiControlChars)
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    // Analytics/click-tracking query parameters: none of these affect which video, audio,
    // or image a service actually serves, so stripping them is free: no functional risk,
    // cleaner URLs in history and the queue, and one less thing leaked to whichever
    // community-run instance ends up handling the request.
    private static readonly HashSet<string> TrackingParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name", "utm_referrer",
        "fbclid", "gclid", "gclsrc", "dclid", "wbraid", "gbraid",
        "msclkid", "twclid", "ttclid", "yclid",
        "igshid", "igsh",
        "si", // YouTube/Spotify share-tracking id, doesn't change which video/track loads
        "ref", "ref_src", "ref_url",
        "mc_cid", "mc_eid",
        "_hsenc", "_hsmi",
        "mkt_tok", "vero_id", "vero_conv",
        "oly_anon_id", "oly_enc_id",
        "spm", "scm",
        "s_cid", "cndid",
    };

    /// <summary>Removes known tracking query parameters from a URL, leaving everything
    /// else (including functional params like YouTube's "v" or "t") untouched. Falls
    /// back to the original string unchanged if it doesn't parse as an absolute URL or
    /// has no query string at all; never throws over what's a cosmetic/privacy nicety.</summary>
    private static string StripTrackingParameters(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
        {
            return url;
        }

        var keptParams = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !TrackingParameterNames.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0])))
            .ToList();

        var basePart = uri.GetLeftPart(UriPartial.Path);
        var query = keptParams.Count > 0 ? "?" + string.Join("&", keptParams) : "";
        return basePart + query + uri.Fragment;
    }

    /// <summary>Applies both URL normalizations to a pasted source URL: upgrade
    /// http:// to https://, then (if enabled) strip tracking parameters.</summary>
    private string NormalizeSourceUrl(string url)
    {
        var normalized = UpgradeToHttps(url);
        return _settings.EnableTrackerStripping ? StripTrackingParameters(normalized) : normalized;
    }

    /// <summary>Silently rewrites a plain http:// source URL to https:// before it's ever
    /// sent anywhere. Deliberately one-directional: if the https version doesn't work, the
    /// download just fails normally like any other bad URL; it does NOT fall back to the
    /// original http:// link. A silent downgrade-on-failure is the actual security hole here
    /// (an on-path attacker could simply make the https attempt fail to force plaintext), so
    /// there's no fallback path to exploit. Leaves the port alone unless it was the untyped
    /// default (so "http://x.com" becomes "https://x.com", not "https://x.com:80").</summary>
    private static string UpgradeToHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            return url;
        }

        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps };
        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }
        return builder.Uri.ToString();
    }

    // WinUI allows only one ContentDialog open at a time; calling ShowAsync() while another
    // is already showing throws synchronously. A user can open Select Instance/History/Options
    // while a queue runs in the background, and a queue item's error dialog can fire moments
    // before the batch-summary dialog; without this semaphore, any of those collisions throws
    // inside an async void call chain with nowhere to go but a hard native crash (confirmed via
    // Windows Event Viewer: STATUS_STOWED_EXCEPTION in Microsoft.UI.Xaml.dll). Every ContentDialog
    // in this app goes through ShowDialogAsync so a second one just waits its turn instead.
    private readonly SemaphoreSlim _dialogSemaphore = new(1, 1);

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogSemaphore.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogSemaphore.Release();
        }
    }

    private async void ShowError(string message) => await ShowMessageDialogAsync(message, "Error");

    private async void ShowInfo(string message) => await ShowMessageDialogAsync(message, "Info");

    private async Task ShowMessageDialogAsync(string message, string title)
    {
        try
        {
            await ShowDialogAsync(new MessageDialog(message, title) { XamlRoot = Content.XamlRoot });
        }
        catch
        {
            // A dialog is a convenience; never let a presentation failure crash the app
        }
    }

    /// <summary>Shows a Windows notification for a finished download, but only when the
    /// window isn't focused (e.g. minimized), while it's active, the Info dialog already
    /// tells you, and only if the user hasn't turned notifications off in Options.
    /// Takes the folder explicitly (the queue item's snapshot, not the live selection):
    /// this used to read _selectedFolder directly, which meant the "Open Folder" button
    /// could point at a folder you'd since switched to instead of where the file actually
    /// landed, if you changed it while this item was still downloading.</summary>
    private void ShowDownloadCompleteToast(string bodyText, string folder)
    {
        if (_isWindowActive || !_settings.EnableToastNotifications)
        {
            return;
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            var notification = new AppNotificationBuilder()
                .AddText("Download complete")
                .AddText(bodyText)
                .AddButton(new AppNotificationButton("Open Folder")
                    .AddArgument("action", "openFolder")
                    .AddArgument("folder", folder))
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Notifications are a nicety; never let a failure here affect the download itself
        }
    }
}

public enum ResolveKind { Direct, Picker }

/// <summary>Result of resolving a media URL against a cobalt instance, either a single
/// direct download, or (when the picker chooser is enabled) the full multi-item list.</summary>
public sealed class CobaltResolveResult
{
    public ResolveKind Kind { get; set; }
    public string Url { get; set; }
    public string Filename { get; set; }
    public List<PickerItem> PickerItems { get; set; }
}

/// <summary>One item from a cobalt "picker" response (image carousel, multi-photo post, etc.).</summary>
public sealed class PickerItem
{
    /// <summary>"photo" | "video" | "gif"</summary>
    public string Type { get; set; }
    public string Url { get; set; }
    public string Thumb { get; set; }
}

/// <summary>One cobalt instance as reported by cobalt.directory's /api/tests endpoint.</summary>
public sealed class CobaltInstance
{
    [JsonPropertyName("api")] public string Api { get; set; }
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "https";
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; }
    [JsonPropertyName("turnstile")] public bool Turnstile { get; set; }
    [JsonPropertyName("tests")] public Dictionary<string, ServiceTest> Tests { get; set; }
    /// <summary>Hostname of the instance's own web UI, separate from its API hostname
    /// (<see cref="Api"/>), used for the "Visit Instance" browser link. Not every
    /// instance publishes one.</summary>
    [JsonPropertyName("frontend")] public string Frontend { get; set; }
}

/// <summary>Result of cobalt.directory's per-service test on an instance.</summary>
public sealed class ServiceTest
{
    [JsonPropertyName("friendly")] public string Friendly { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; }
    [JsonPropertyName("status")] public bool Status { get; set; }
}

public sealed class DirectoryTestsResponse
{
    [JsonPropertyName("data")] public List<CobaltInstance> Data { get; set; }
}

// Helper class for message dialog (since we don't have MessageDialog in WinUI 3, we use ContentDialog)
internal class MessageDialog : ContentDialog
{
    public MessageDialog(string content, string title)
    {
        Title = title;
        Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap };
        CloseButtonText = "OK";
    }
}
