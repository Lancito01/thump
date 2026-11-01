<p align="center"><img src="assets/logo.png" width="120" alt="Thump logo"></p>

<h1 align="center">Thump</h1>

<p align="center"><b>Drums-first music and screen sync for Philips Hue, for Windows 10/11.</b></p>

<p align="center"><img src="docs/screenshot-colors.png" width="820" alt="Thump: album-art colours spread across four lights, live spectrum and the KICK, CLAP and HATS indicators"></p>

Hue Sync's music mode blends the whole spectrum into one loudness value, so on electronic music the lights end up
following synths, vocals and whatever is loudest instead of the thing you actually move to: the drums. Thump listens
for the drums. **Kickstep**, its own small neural network, picks out every kick, clap/snare and hi-hat live, and each
light follows the drum you give it: a hard flash on the kick, another on the backbeat, a shimmer with the hats.

- **Kickstep, a live drum detector that hears the groove.** It catches kicks, claps/snares (rims included) and
  hi-hats about 50 ms after they hit, judging each one by its sound and by where it sits in the groove, so a quiet
  clap on the 2 and 4 still flashes.
- **Confidence, not just on/off.** The surer Kickstep is, the brighter the flash; below a minimum the light stays
  dark, so there are no constant dim flickers. The sensitivity sliders move how sure it has to be.
- **Works however you play music.** Spotify, YouTube, a DJ set in rekordbox or Serato: Thump hears what your PC
  plays, not the player, and never ties a pattern to a song. Crossfades, live blends, hard cuts and pitch-riding are
  all fine, and it lets go of the old groove within a couple of bars. Kickstep's kicks also drive a tempo tracker
  that follows tempo ramps and DJ pitch-riding within 100–180 BPM.
- **Per-light roles.** *Kick* and *Clap* flash on their drum and stay dark in between, *Hats* shimmers, *Bass* and
  *Mix* follow the low end or the whole track, *Mids* and *Highs* the synths and cymbals, *Ambient* ignores the music.
- **The rest of the mix, too.** Six auto-levelled bands (sub, bass, low-mid, mid, high, air) drive the *Mix* and
  *Bass* roles, so lights that should breathe with the sub can, and kicks add a punch on top.
- **Album-art colours (the default).** Uses the cover of whatever's playing (Spotify, browsers, Apple Music, Tidal,
  foobar2000, anything that shows up in Windows' media overlay) and changes with every track. **Every light gets its own
  colour.** When a cover has fewer colours than you have lights (a plain orange cover with 4 lights, say), Thump
  derives close variations: orange, red-orange, deep orange, soft orange.
- **Screen colours.** Each light copies the part of the screen it sits next to. The music can still drive brightness.
- **Presets and custom palettes**, with colour stepping on every kick, slow drift or static colours, plus an optional
  hi-hat "sparkle".
- **Remembers songs (opt-in).** Keeps what it learned about each song (~2 KB) so the next play is right from the first
  beat. You set the disk space; the least recently played songs are forgotten first.
- **Hue Entertainment API streaming** (DTLS, ~50 updates/s), the same low-latency path Hue Sync uses.

## Kickstep

Thump's drum detection is [Kickstep](https://github.com/Lancito01/kickstep), our own model, also released on its own
(MIT) for use in other projects; its scores, details and roadmap live there. Its training used house drum loops from
[WaivOps EDM-HSE](https://doi.org/10.5281/zenodo.13769544) (CC BY 4.0).

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

## Tuning tips

| Want | Do |
|---|---|
| Lights locked to the kick, dark in between | Set the main lights to *Kick*, raise **Contrast**, lower **Release** |
| A light on the backbeat | *Clap* role (claps, snares, rims); raise **Clap sensitivity** if some are missed |
| A light that shimmers with the hats | *Hats* role |
| Kicks being missed | Raise **Kick sensitivity** a little |
| Bassline triggering kicks | Lower **Kick sensitivity** |
| Rumbling sub that "breathes" | *Bass* role, **Sub** weight up, **Release** ~300 ms |
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
dotnet build installer -c Release -p:Version=0.5.0
```

Pushing a `v*` tag does all of this on GitHub Actions and publishes the release.

## How it works

```
WASAPI loopback ─▶ FFT (2048, hop 512) ─▶ 6 auto-levelled bands ─────────────┐
                                     ├──▶ Kickstep: kick, clap/snare, hats ──┤
                                     └──▶ BeatTracker (all onsets) ──────────┤
Media session (album art) ─▶ palette extraction ──────────────────────────────┤──▶ SyncEngine (50 Hz) ─▶ DTLS ─▶ Hue bridge
Screen (GDI, 64×36) ─▶ per-light sampling by entertainment position ─────────┘
```

| File | What it does |
|---|---|
| `Audio/AudioAnalyzer.cs` | Loopback capture, FFT, per-band adaptive levels |
| `Audio/ModelFeatures.cs`, `Audio/DrumModel.cs` | Kickstep: 64 log bands in, one causal step per hop |
| `Audio/HitDetector.cs` | The older hand-tuned per-song detectors (used if Kickstep can't load) |
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
turn that on) live in `%LOCALAPPDATA%\Thump\songs`. A `drums.thdm` in `%LOCALAPPDATA%\Thump` replaces the built-in Kickstep weights (for trying out new ones).

## Known limitations

- Kickstep still misses some claps and snares, mostly unusual or heavily processed ones and muted pre-drop
  snares, and now and then takes a snare roll for hats. It would rather miss a clap than fake one; raising
  **Clap sensitivity** catches more, at the cost of the odd fake flash.
- Screen mode uses GDI capture: exclusive-fullscreen games and DRM-protected video may show up black (use borderless).
- Only the primary monitor is sampled.

## Contributing

Issues and PRs welcome. If a track fools Kickstep (missed or fake kicks, claps or hats), a short WAV clip and a note on what went wrong is the most
useful bug report.

## License

[MIT](LICENSE)
