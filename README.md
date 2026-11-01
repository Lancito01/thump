<p align="center"><img src="assets/logo.png" width="120" alt="Thump logo"></p>

<h1 align="center">Thump</h1>

<p align="center"><b>Bass-first music and screen sync for Philips Hue, for Windows 10/11.</b></p>

<p align="center"><img src="docs/screenshot-colors.png" width="820" alt="Thump: album-art colours spread across four lights, live spectrum and kick indicator"></p>

Hue Sync's music mode is tuned for "average" music: it blends the whole spectrum into one loudness value. That's
fine for pop, but on techno and house the lights end up following hats and synths instead of the thing you actually
feel: the kick and the sub. Thump is built the other way round.

- **Bass-first audio engine.** The spectrum is split into six bands (sub, bass, low-mid, mid, high, air). Each band
  levels itself automatically, and you decide how much each one drives the lights. The defaults lean hard on the low end.
- **A learned drum detector: kick, clap/snare and hi-hats, live.** A small neural network (about 37,000 weights,
  a fraction of a millisecond per step) listens to the last ~160 ms of audio and says whether a kick, a clap or snare
  (rims included) or a hi-hat just started, about 30 ms after the hit and with no lookahead. Every hit gets a
  **confidence**: the surer the model is, the brighter the flash, and below a minimum it stays dark, so there are no
  constant dim flickers. The sensitivity sliders move how sure it has to be. The model's kicks also drive the tempo
  tracker, which follows DJ pitch-riding, tempo ramps and hard cuts within 100–180 BPM.

  It was trained offline on hundreds of electronic tracks, labelled by a bigger offline drum transcriber, plus drum
  loops from [WaivOps EDM-HSE](https://doi.org/10.5281/zenodo.13769544) (CC BY 4.0) and synthesised drum-machine
  grooves laid over the songs. It ships inside Thump (`Audio/DrumModel.cs` runs it); the training code isn't public.
  Claps that sound nothing like a drum machine's are still its weak spot.
- **Remembers songs (opt-in).** Keeps what it learned about each song (~2 KB) so the next play is right from the first
  beat. You set the disk space; the least recently played songs are forgotten first.
- **Per-light roles.** Put a light on *Kick* or *Clap* for a hard pulse, *Hats* for a gentle shimmer, others on
  *Bass*, *Mix*, *Mids*, *Highs* or *Ambient*.
- **Album-art colours (the default).** Uses the cover of whatever's playing (Spotify, browsers, Apple Music, Tidal,
  foobar2000, anything that shows up in Windows' media overlay) and changes with every track. **Every light gets its own
  colour.** When a cover has fewer colours than you have lights (a plain orange cover with 4 lights, say), Thump
  derives close variations: orange, red-orange, deep orange, soft orange.
- **Screen colours.** Each light copies the part of the screen it sits next to. The music can still drive brightness.
- **Presets and custom palettes**, with colour stepping on every kick, slow drift or static colours, plus an optional
  hi-hat "sparkle".
- **Hue Entertainment API streaming** (DTLS, ~50 updates/s), the same low-latency path Hue Sync uses.

> Thump is an independent open-source project. It is not affiliated with or endorsed by Philips or Signify.
> "Philips Hue" and "Hue Sync" are trademarks of Signify.

## Download

Grab **`Thump-x.y.z.msi`** from the [latest release](../../releases/latest) and run it. It installs just for you
(no admin prompt) into `%LocalAppData%\Programs\Thump`, adds a Start menu shortcut, and can be removed from
*Settings → Apps*. Prefer no installer? `Thump-win-x64-portable.zip` has the same app as a single exe.

Windows SmartScreen may warn because the installer isn't code-signed yet. Choose *More info → Run anyway*, or build
it yourself.

### Runs in the background

Closing the window doesn't stop Thump: it keeps your lights syncing from the notification area (system tray).
Right-click the tray icon to start or stop syncing, or to quit. On the **App** tab you can turn on:

- **Start with Windows**: starts hidden in the tray when you sign in.
- **Automatically connect**: connects to your entertainment area and starts syncing as soon as Thump starts.
- **Updates**: off unless you enable them. Thump checks this repository's GitHub releases every few hours,
  downloads the new installer, verifies its SHA-256 checksum against the one GitHub publishes, and either asks you or
  (if you opt in) installs it automatically when the lights aren't syncing.

## Setup

1. **Create an entertainment area** in the official Hue app (*Settings → Entertainment areas*) and place the lights
   where they really are. Screen mode uses these positions.
2. Open Thump → **Bridge** tab → *Discover* (or type the bridge IP) → *Pair with bridge*, then press the link
   button on the bridge within 30 seconds.
3. Pick the entertainment area, set up roles on the **Lights** tab, then hit **Start light sync**.

Only one app can stream to an entertainment area at a time, so close Hue Sync first.

## Tuning tips (techno / house)

| Want | Do |
|---|---|
| Lights locked to the kick, dark in between | Set the main lights to *Kick*, raise **Contrast**, lower **Release** |
| Rumbling sub that "breathes" | *Bass* role, **Sub** weight up, **Release** ~300 ms |
| Kicks being missed | Raise **Kick sensitivity** a little |
| Bassline triggering kicks | Lower **Kick sensitivity** |
| A light on the backbeat | *Clap* role (claps, snares, rims); raise **Clap sensitivity** if some are missed |
| A light that shimmers with the hats | *Hats* role |
| Right from the first beat on songs you replay | App tab → **Remember songs** |
| Less flicker on dense tracks | Raise **Attack** to 30–50 ms |

The live panel shows the spectrum, KICK, CLAP and HATS LEDs, and the tempo it's following with its confidence.

## Building from source

Requirements: Windows 10 2004+ and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/Lancito01/thump.git
cd thump
dotnet run --project src/Thump
```

Tests (synthetic dance loops with tempo ramps, breakdowns, broken beats, soft kicks, claps, snares and rolls):

```bash
dotnet test
```

To see what the analyzer makes of a real track (WAV or MP3), run the bench:

```bash
THUMP_WAV=path/to/track.wav dotnet test --filter RealMusicBench --logger "console;verbosity=detailed"
```

Release build (single self-contained exe, then the per-user MSI; needs nothing beyond the .NET SDK, WiX comes from NuGet):

```bash
dotnet publish src/Thump -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
dotnet build installer -c Release -p:Version=0.3.0
```

Pushing a `v*` tag does all of this on GitHub Actions and publishes the release.

## How it works

```
WASAPI loopback ─▶ FFT (2048, hop 512) ─▶ 6 auto-levelled bands ─────────────┐
                                     ├──▶ DrumModel: kick, clap/snare, hats ─┤
                                     └──▶ BeatTracker (all onsets) ──────────┤
Media session (album art) ─▶ palette extraction ──────────────────────────────┤──▶ SyncEngine (50 Hz) ─▶ DTLS ─▶ Hue bridge
Screen (GDI, 64×36) ─▶ per-light sampling by entertainment position ─────────┘
```

| File | What it does |
|---|---|
| `Audio/AudioAnalyzer.cs` | Loopback capture, FFT, per-band adaptive levels |
| `Audio/ModelFeatures.cs`, `Audio/DrumModel.cs` | The learned drum detector: 64 log bands in, one causal step per hop |
| `Audio/HitDetector.cs` | The older hand-tuned per-song detectors (used if the model can't load) |
| `Audio/BeatTracker.cs` | Tempo and beat grid from every onset, with a confidence for how well it predicts |
| `Audio/SpectralFeatures.cs` | The 14-band fingerprint, the all-band onset signal and the kick/clap sound cues |
| `Audio/SongCache.cs` | Opt-in, size-capped store of what was learned per song |
| `Colors/AlbumArtSource.cs` | Now-playing info and cover art via Windows' media session API |
| `Colors/Palettes.cs` | Presets and vivid-colour extraction from images |
| `Colors/ScreenSampler.cs` | Cheap downscaled screen capture |
| `Hue/HueBridgeClient.cs` | Discovery, pairing, entertainment areas (CLIP v2) |
| `Hue/EntertainmentStreamer.cs` | HueStream v2 over DTLS 1.2 PSK (BouncyCastle) |
| `SyncEngine.cs` | The 50 Hz loop: features + colours → per-light output |
| `TrayIcon.cs`, `Startup.cs` | Tray icon, start with Windows |
| `Updater.cs` | Opt-in updates from GitHub Releases (checksum-verified MSI) |
| `installer/` | WiX per-user MSI |

Settings, including the bridge credentials, live in `%APPDATA%\Thump\settings.json`. Remembered songs (only if you
turn that on) live in `%LOCALAPPDATA%\Thump\songs`. A `drums.thdm` in `%LOCALAPPDATA%\Thump` replaces the built-in drum model.

## Known limitations

- The drum model still misses some claps and snares, mostly unusual or heavily processed ones and muted pre-drop
  snares, and now and then takes a snare roll for hats. Raising **Clap sensitivity** catches more, at the cost of
  the odd fake flash.
- Screen mode uses GDI capture: exclusive-fullscreen games and DRM-protected video may show up black (use borderless).
- Only the primary monitor is sampled.

## Contributing

Issues and PRs welcome. If a track fools the kick detector, a short WAV clip and a note on what went wrong is the most
useful bug report.

## License

[MIT](LICENSE)
