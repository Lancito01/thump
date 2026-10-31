# Roadmap

## Done in 0.3

- Kick and clap/snare detection rebuilt sound-first: a confidence per hit, the rhythm only nudges borderline sounds,
  a ring-out check labels what the models learn from, and a drum that hasn't been heard yet never flashes.
- Tempo followed from every onset with its own confidence (fixes the "locked ↔ learning" flip-flop: the grid used to
  anchor on a bass note and the learned pattern pointed at the wrong 16ths after every relock).
- *Clap* light role, CLAP LED, confidence readout in the live panel.
- Opt-in "Remember songs" with a disk cap, least recently played forgotten first.

## Next: live, instrument-based light sync

DJ software (rekordbox, Serato) splits a track into stems after analysing the whole file. Thump has to do the
simpler version live: from whatever is playing, with no lookahead and no added delay, tell each light which
instrument it follows. Each light picks a source:

| Source | Shown as | Status |
| --- | --- | --- |
| Mix | brightness follows the whole track | done |
| Bass | brightness follows the low end | done |
| Kick | a flash per hit | done (hand-tuned detector), to move to the model |
| Snare / clap | a flash per hit | not reliable yet; the main reason for the model |
| Hats | a gentle shimmer on top of a base level, **not** flashes | new |

- **One small drum model, three outputs** (kick, snare/clap, hats): the standard set in drum transcription. It runs
  every hop (~11 ms) on the past only, so latency stays where it is today (~11–25 ms after the hit), well inside the
  100 ms budget. Small enough for plain C# or ONNX Runtime on the CPU, no GPU.
- **Trained by distillation.** An offline drum-transcription model, which may look ahead, labels a library of real
  tracks (the user's own music, across genres); the live model learns to match it seeing only the past. Where the
  teacher is wrong, the student learns the mistake, so the labels get spot-checked.
- **Hats as shimmer, not strobe.** 16th hats at 128 BPM are ~8.5 hits a second, far past the ~3 flashes a second
  photosensitivity guideline. They modulate brightness slightly instead.
- **Measured before it ships.** Each output is scored separately against labels on a fixed set of tracks (ones the
  current detectors get wrong and ones they get right), compared with 0.3.3. An output that doesn't clearly win stays
  hidden from the light options; the current detectors remain the fallback.
- **Not in scope (yet): synths, vocals.** They overlap in frequency and sustain, so splitting them live is
  real-time source separation, much less reliable than drums. If anything, a continuous "melody energy" source like
  Bass, not hits.
- **Before claiming it's the first:** check for existing live per-instrument light sync (lighting consoles, DJ
  lighting add-ons, smart-light apps) so the README and any store listing say only what's true.

## Other ideas

- Re-check "IRONIC" by ero808, where the BPM used to jump between readings (146 → 125), against the new tempo tracker
  with `RealMusicBench`.
- "Speaker delay" setting: Bluetooth speakers play 150–250 ms after Windows hands over the audio, so the lights could
  be held back to land exactly on what you hear, with no prediction involved.
- Remember each song's hit timeline and replay it in sync with the player's position on later plays.
- Long-tailed kicks (hardstyle, 808): judge ring-out over a longer window when the kick band stays clean.
- Remove the startup Run-key entry on uninstall, so it doesn't linger if "Start with Windows" was on.
- Code-sign the MSI to stop SmartScreen warnings.
