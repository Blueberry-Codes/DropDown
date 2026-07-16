using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DropDown;

/// <summary>
/// User preferences, persisted to %LOCALAPPDATA%\DropDown\settings.json.
/// (Unpackaged WinUI apps can't use Windows.Storage.ApplicationData.)
/// </summary>
public sealed class AppSettings
{
    /// <summary>"mica" | "micaAlt" | "acrylic" | "none"</summary>
    public string Theme { get; set; } = "mica";
    /// <summary>Legacy field, replaced 2026-07-14 by DownloadMode (which also supports a
    /// muted-video mode). Kept only so Load() can migrate an existing true value into the
    /// new field on first run after the upgrade; nothing else reads or writes this anymore.</summary>
    public bool AudioOnly { get; set; }
    /// <summary>cobalt downloadMode: "video" (cobalt's "auto") | "audio" | "mute" (video, no audio track).</summary>
    public string DownloadMode { get; set; } = "video";
    public string AudioFormat { get; set; } = "mp3";
    public string AudioBitrate { get; set; } = "320";
    public string VideoQuality { get; set; } = "1080";
    public string DefaultFolder { get; set; }
    /// <summary>When true, a multi-item post (carousel) opens a picker to choose which
    /// items to download; when false, the first item is grabbed automatically.</summary>
    public bool EnablePickerChooser { get; set; } = true;
    /// <summary>When true, a Windows notification appears when a download finishes
    /// while the window isn't focused (e.g. minimized).</summary>
    public bool EnableToastNotifications { get; set; } = true;
    /// <summary>When true, known tracking query parameters (utm_source, fbclid, gclid,
    /// etc.) are stripped from a URL before it's used/stored/sent anywhere.</summary>
    public bool EnableTrackerStripping { get; set; } = true;
    /// <summary>How each row in the download queue is displayed: "url" | "filename" | "both".</summary>
    public string QueueDisplayMode { get; set; } = "both";
    /// <summary>cobalt filenameStyle: "classic" | "basic" | "pretty" | "nerdy".</summary>
    public string FilenameStyle { get; set; } = "basic";
    /// <summary>When true, cobalt strips embedded metadata (title, artist, etc.) from the file.</summary>
    public bool DisableMetadata { get; set; }
    /// <summary>When true, cobalt converts single-frame/looping video into an actual .gif.</summary>
    public bool ConvertGif { get; set; } = true;
    /// <summary>When true, cobalt may return H.265/HEVC video (smaller files, less compatible).</summary>
    public bool AllowH265 { get; set; }
    /// <summary>When true, TikTok downloads use the original sound's full audio track instead of the video's.</summary>
    public bool TiktokFullAudio { get; set; }
    /// <summary>When true, cobalt prefers a higher-quality YouTube audio track when available.</summary>
    public bool YoutubeBetterAudio { get; set; }
    /// <summary>cobalt youtubeVideoCodec: "h264" | "av1" | "vp9".</summary>
    public string YoutubeVideoCodec { get; set; } = "h264";
    /// <summary>cobalt youtubeVideoContainer: "auto" | "mp4" | "webm" | "mkv".</summary>
    public string YoutubeVideoContainer { get; set; } = "auto";
    /// <summary>When true, cobalt always proxies the download through the instance itself
    /// (a "tunnel" response) instead of sometimes redirecting straight to the origin CDN,
    /// hiding your IP from the origin at the cost of the instance's own bandwidth.</summary>
    public bool AlwaysProxy { get; set; }
    public List<string> Favorites { get; set; } = new();
    public List<string> Recents { get; set; } = new();

    public const int MaxRecents = 8;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DropDown", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                // A hand-edited file could set these to null explicitly
                settings.Favorites ??= new List<string>();
                settings.Recents ??= new List<string>();
                // One-time migration from the old binary AudioOnly flag; cleared immediately
                // so this can never re-fire and clobber a later, deliberate DownloadMode choice.
                if (settings.AudioOnly)
                {
                    settings.DownloadMode = "audio";
                    settings.AudioOnly = false;
                }
                return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults rather than crash
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; never let a save failure break the app
        }
    }

    public void AddRecent(string apiHost)
    {
        Recents.Remove(apiHost);
        Recents.Insert(0, apiHost);
        if (Recents.Count > MaxRecents)
        {
            Recents.RemoveRange(MaxRecents, Recents.Count - MaxRecents);
        }
    }
}
