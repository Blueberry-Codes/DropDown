# DropDown

A flashy little Windows desktop app for downloading videos and audio from social media
and video sites, powered by community-run [cobalt](https://github.com/imputnet/cobalt)
instances tracked by [cobalt.directory](https://cobalt.directory).

Built with WinUI 3 (Windows App SDK 1.8) on .NET 10.

## Features

- **Live instance list**: pulls every online cobalt instance from cobalt.directory's
  public list, sorted by their tested compatibility score, shown with a color-coded
  badge (green ≥ 80%, amber ≥ 50%, red below). This is the percentage of services
  currently passing cobalt.directory's automated tests, **not** a safety/trust
  rating (cobalt.directory doesn't publish one). Each row also has a 🔗 button that
  opens the instance's own site in your browser, so you can see who runs it yourself
- **Smart instance sort**: paste a URL and the picker detects its service (YouTube,
  TikTok, Twitter/X, and 20+ others), tags each instance ✓/✕ for that service, and
  sorts working ones first; an amber warning appears on the main window if your
  currently selected instance is known to fail the pasted URL
- **Supported Services panel**: see every service the selected instance was tested
  against, collapsed by default to keep the window compact; tap to expand
- **Favorites & recents**: star instances you trust; the picker has All / ★ Favorites /
  Recently Used views, and favorites float to the top of the full list. The app also
  reconnects to your last-used instance automatically on launch
- **Donation link detection**: if the selected instance's own site has a donate
  page, a "💝 This instance accepts donations" link appears so you can support
  the person actually paying to run it
- **Video, Audio Only, or Video (Muted)**: pick full video (144p to 8K/Max
  quality picker), audio-only (format: best/mp3/ogg/opus/wav, bitrate: 64 to
  320 kbps), or a video with its audio track stripped by the instance itself
- **Extra cobalt options in Options**: file naming style, metadata stripping,
  GIF conversion, H.265/HEVC allowance, TikTok full audio track, YouTube
  better-audio preference, YouTube video codec/container choice, and an
  always-proxy privacy toggle (routes downloads through the instance instead
  of a direct-to-origin redirect, hiding your IP at the cost of the
  instance's own bandwidth), all saved as defaults and snapshotted per
  queued item
- **Filename preview**: about a second after you paste a URL, the app asks the selected
  instance what the file will be called and pre-fills the File Name box; type your own
  name to override it
- **Real progress bar with Cancel**: live byte counter with a determinate bar when the
  server reports a size, indeterminate when it doesn't; cancel any in-flight request or
  download and any partial file is cleaned up automatically
- **Retry on failure**: a failed download offers a Retry button that re-attempts the
  same item from scratch. Not a resume (cobalt's tunnel URLs don't support that), but
  it saves you from having to manually redo everything after a network hiccup
- **Download queue**: "Add to Queue" stashes a URL and clears the form for the next
  one; Get then processes everything sequentially with per-item progress. Canceling
  stops only the current item, the rest stay queued for you to resume later. Choose
  what each queue row shows in Options: URL, filename, or both
- **Tracker stripping**: known tracking parameters (`utm_source`, `fbclid`, `gclid`,
  and friends) are stripped from pasted URLs before they're used or stored, so what's
  saved to your queue/history is the clean link. Toggle this off in Options if you'd
  rather keep links exactly as pasted
- **Automatic HTTPS**: a pasted `http://` link is upgraded to `https://` automatically
- **Download history**: every completed download is logged (name, size, source URL,
  instance, timestamp); the clock-icon button opens a list with Open File / Show in
  Folder for each entry, and rows for deleted files gray themselves out
- **Picker chooser**: multi-item posts (carousels) open a thumbnail grid so you can
  pick which items to download or grab all of them; toggle this off in Options to
  always auto-download just the first item instead
- **Toast notifications**: get a Windows notification with an "Open Folder" button
  when a download finishes while the window isn't focused; toggle this off in Options
- **Themes**: Mica, Mica Alt, Acrylic, or Classic backdrop, picked in Options and
  applied instantly
- **Saved defaults**: the gear (⚙) button opens Options: theme, default mode, format,
  bitrate, quality, and a default download folder. Whatever folder you pick from the
  main window is remembered automatically for next launch too
- **Quality-of-life**: "Open Download Folder" button, never overwrites existing files
  (appends `(1)`, `(2)`, ...), cleans up partial files if a download fails or is canceled
- **About**: the ⓘ button credits cobalt and cobalt.directory directly in the app,
  with real links to both (since DropDown is just a client built on top of them)
  plus a direct link to support cobalt.directory's maintainer

## Requirements

- Windows 10 build 19041 (May 2020 Update) or later, x64
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
  (the SDK if you're building from source)
- No separate Windows App SDK runtime install needed, the app is self-contained

## Building from source

```
dotnet build
```

The exe lands in `bin\Debug\net10.0-windows10.0.19041.0\win-x64\DropDown.exe`.
For a release build: `dotnet build -c Release`.

## Usage

1. Paste a video URL
2. Pick an instance (**Select Instance**): higher score means more services working;
   avoid ones marked *verification required* (they need a browser bot-check that a
   desktop app can't complete)
3. Pick a download folder (or set a default in Options and skip this forever)
4. Flip **Download Mode** to Audio Only if you just want the sound, adjust
   quality/format/bitrate to taste
5. Check the auto-filled file name, rename it if you like
6. Hit **Get** and watch the progress bar

Settings live in `%LOCALAPPDATA%\DropDown\settings.json` (created on first save,
delete it to reset everything).

## Notes & limitations

- YouTube frequently fails on public instances because YouTube blocks datacenter
  IPs. Check an instance's YouTube test status on cobalt.directory, or use another
  service. Instance scores change hourly; use **Refresh List** in the picker if
  things seem stale.
- Instances marked *verification required* enforce Cloudflare Turnstile, which this
  app can't satisfy, so downloads from them will usually fail.
- Picker item file extensions are guessed from the item's declared type
  (photo/video/gif), not the actual downloaded bytes. A service that serves photos
  in WebP under a generic "photo" type (Bluesky does this) can produce a `.jpg`-named
  file that's technically WebP. Virtually every modern viewer opens it fine anyway.
- Downloads go through the selected community instance, so don't paste anything
  private, and be a good citizen: these are volunteer-run servers.

## Security

DropDown routes downloads through third-party community-run servers, so it treats
what comes back from them as untrusted rather than assuming it's safe media:
suspicious file extensions are blocked, downloaded content is checked against its
actual bytes (not just its claimed filename), and download URLs can't be redirected
to your own machine or local network. HTTPS is used wherever possible, and known
ad-tracking parameters are stripped from links by default.

## Disclaimer

DropDown is an independent, unofficial project. It is not made, endorsed, or
affiliated with [imput](https://imput.net) (the team behind cobalt) or
[cobalt.directory](https://cobalt.directory); it's simply a client built on
top of their public tools. The same disclaimer, with direct links to both, is
also shown in the app itself via the ⓘ About button.

## License

MIT
