# Roadmap

## Next: clap detection ("sync lights to only claps")

Teach the per-song model to learn the track's **clap/snare** alongside the kick, so a light role can follow claps only.

Plan, reusing what `src/Thump/Audio/KickDetector.cs` already does for kicks:

- **Onset band:** claps and snares live roughly in 1–5 kHz (body around 200–400 Hz for snares). Compute a second
  onset strength there, normalised by its own recent peak, the same way `KickBandOnset` does for 40–130 Hz.
- **Timbre:** reuse the 12-band fingerprint (`ComputeTimbre`). Keep a clap template plus a "not clap" template (hats,
  vocal consonants, synth stabs).
- **Rhythm prior:** claps sit on beats 2 and 4. Reuse the locked beat grid from the kick model and learn per-slot
  probability over a *bar* (16 slots), not a beat, so 2-and-4 can be told apart from 1-and-3. This needs a bar/downbeat
  estimate: take the bar phase from where kicks and claps alternate.
- **Probably refactor** `KickDetector` into a generic `HitDetector` (band, templates, slot prior) with `Kick` and
  `Clap` instances; tempo/grid stays owned by the kick instance.
- **Output:** add `ClapEnvelope` / `ClapCount` to `AudioFeatures`, a `Clap` value to `LightRole` and the roles
  description on the Lights tab, plus a CLAP pill next to KICK in the live panel.
- **Tests:** extend the synthetic generator in `tests/Thump.Tests/KickDetectionTests.cs` with a clap (filtered noise
  burst plus a short tonal body) on 2 and 4, with hats as decoys; check recall and false positives as for kicks. Then
  check on real music with `RealMusicBench` (`THUMP_WAV`).

## Other ideas

- BPM on some tracks jumps between readings (seen on "IRONIC" by ero808: 146 → 125). Record it and check with
  `RealMusicBench`.
- Remove the startup Run-key entry on uninstall, so it doesn't linger if "Start with Windows" was on.
- Code-sign the MSI to stop SmartScreen warnings.
