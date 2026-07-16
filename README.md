# DropDown 🎬⬇️

**Drop a link and Download with DropDown!**

Paste a link, pick a server, hit **Get**. That's the whole app.

DropDown is a flashy little Windows desktop app for downloading videos and audio from
social media and video sites, powered by community-run [cobalt](https://github.com/imputnet/cobalt)
instances tracked by [cobalt.directory](https://cobalt.directory). No ads, no bundled
toolbar, no "premium tier". Just downloads.

Built with WinUI 3 (Windows App SDK 1.8) on .NET 10.

## ✨ What it does

**The instance picker is the star.** Cobalt instances are volunteer-run servers, and
they're not all equal:

- 🟢 **Live instance list**: every online instance from cobalt.directory's public list,
  sorted by tested compatibility score with a color-coded badge (green ≥ 80%, amber ≥ 50%,
  red below). That score is the percentage of services currently passing automated tests,
  **not** a safety rating (nobody publishes one of those). Every row has a 🔗 button that
  opens the instance's own site, so you can see who runs it yourself
- 🧠 **Smart sort**: paste a URL and the picker detects the service (YouTube, TikTok,
  Twitter/X, and 20+ others), tags each instance ✓/✕ for it, and floats the working ones
  to the top. If your current instance is known to fail your link, an amber warning tells
  you before you waste a click
- ⭐ **Favorites & recents**: star the instances you trust; All / ★ Favorites / Recently
  Used views; auto-reconnects to your last instance on launch
- 💝 **Donation detection**: if an instance's site has a donate page, a link appears so
  you can support the person actually paying for the bandwidth

**Downloading, the nice way:**

- 🎚️ **Video, Audio Only, or Video (Muted)**: full video with a 144p-to-8K quality picker,
  audio-only (best/mp3/ogg/opus/wav, 64 to 320 kbps), or video with its sound stripped
- 📝 **Filename preview**: about a second after you paste, the app asks the instance what
  the file will be called and pre-fills the name box. Type your own to override it
- 📊 **A real progress bar**: live byte counter, determinate when the server reports a
  size, and a Cancel that actually cancels (partial files get cleaned up automatically)
- 🔁 **Retry on failure**: one button re-attempts a failed item from scratch, so a network
  hiccup doesn't mean redoing everything by hand
- 📋 **Download queue**: stash URLs with "Add to Queue", then process them all
  sequentially with per-item progress. Canceling stops only the current item
- 🖼️ **Picker chooser**: multi-photo posts and carousels open a thumbnail grid so you
  grab exactly the items you want (or all of them)
- 🔔 **Toast notifications**: finish a download while the window's unfocused and Windows
  tells you, with an "Open Folder" button right in the toast
- 🕐 **Download history**: everything you've downloaded (name, size, source, instance,
  timestamp) with Open File / Show in Folder; rows for deleted files gray themselves out

**The considerate details:**

- 🧹 **Tracker stripping**: `utm_source`, `fbclid`, `gclid`, and friends are removed from
  pasted URLs before they're used or stored, so your queue and history hold clean links.
  Toggle off in Options if you want links kept exactly as pasted
- 🔒 **Automatic HTTPS**: pasted `http://` links are upgraded to `https://` silently
- 🕵️ **Always-proxy privacy toggle**: route downloads through the instance instead of a
  direct-to-origin redirect, hiding your IP (at the cost of the instance's bandwidth,
  so use it thoughtfully)
- ⚙️ **Saved defaults**: theme (Mica / Mica Alt / Acrylic / Classic), default mode, format,
  bitrate, quality, download folder, file naming style, metadata stripping, GIF conversion,
  H.265 allowance, TikTok full audio, YouTube codec/container. Set once, forget forever
- 🤝 **Never overwrites**: existing files get `(1)`, `(2)`, ... appended instead
- ⓘ **About**: credits cobalt and cobalt.directory with real links, right in the app,
  because DropDown is just a client standing on their shoulders

## 💻 Requirements

- Windows 10 build 19041 (May 2020 Update) or later, x64
- That's it. The release zip is fully self-contained (.NET runtime and Windows App SDK
  included), so there's nothing to install
- Building from source needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## 🔨 Building from source

```
dotnet build
```

The exe lands in `bin\Debug\net10.0-windows10.0.19041.0\win-x64\DropDown.exe`.
For a release build: `dotnet build -c Release`.

## 🚀 Usage

1. Paste a video URL
2. Pick an instance (**Select Instance**): higher score = more services working; skip
   ones marked *verification required* (they demand a browser bot-check that a desktop
   app can't complete)
3. Pick a download folder (or set a default in Options and never think about it again)
4. Want just the sound? Flip **Download Mode** to Audio Only and pick your format
5. Check the auto-filled file name, rename if you like
6. Hit **Get** and watch the bar go

Settings live in `%LOCALAPPDATA%\DropDown\settings.json` (created on first save,
delete it to reset everything).

## ⚠️ Notes & limitations

- **YouTube is moody.** It blocks datacenter IPs, so public instances frequently fail on
  it. Check an instance's YouTube test status on cobalt.directory, or download from
  another service. Scores change hourly; **Refresh List** if things seem stale
- Instances marked *verification required* enforce Cloudflare Turnstile, which this app
  can't satisfy. Downloads from them will usually fail
- Picker item extensions are guessed from the declared type (photo/video/gif), not the
  actual bytes. Some services (looking at you, Bluesky) serve WebP photos under a generic
  "photo" type, so you can get a `.jpg`-named file that's technically WebP. Every modern
  viewer opens it fine anyway
- Downloads go through the selected community instance, so don't paste anything private,
  and be a good citizen: these are volunteer-run servers

## 🛡️ Security

DropDown routes downloads through third-party community-run servers, so it treats what
comes back as untrusted rather than assuming it's safe media: suspicious file extensions
are blocked, downloaded content is checked against its actual bytes (not just its claimed
filename), and download URLs can't be redirected to your own machine or local network.
HTTPS is used wherever possible, and known ad-tracking parameters are stripped from links
by default.

## 📜 Disclaimer

DropDown is an independent, unofficial project. It is not made, endorsed, or affiliated
with [imput](https://imput.net) (the team behind cobalt) or
[cobalt.directory](https://cobalt.directory); it's simply a client built on top of their
public tools. The same disclaimer, with direct links to both, is shown in the app itself
via the ⓘ About button.

## License

MIT. See LICENSE file.
