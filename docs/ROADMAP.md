# Roadmap

## Done in 0.3

- Kick and clap/snare detection rebuilt sound-first: a confidence per hit, the rhythm only nudges borderline sounds,
  a ring-out check labels what the models learn from, and a drum that hasn't been heard yet never flashes.
- Tempo followed from every onset with its own confidence (fixes the "locked ↔ learning" flip-flop: the grid used to
  anchor on a bass note and the learned pattern pointed at the wrong 16ths after every relock).
- *Clap* light role, CLAP LED, confidence readout in the live panel.
- Opt-in "Remember songs" with a disk cap, least recently played forgotten first.

## Next: a shipped prior for "what a kick / clap sounds like"

The first seconds of a song rely on three hand-written cues per drum (where its energy sits, whether it punches out
of the mix, how it rings out). A tiny classifier trained offline could replace them:

- **Input:** the features the detectors already compute at each onset (14-band rise, absolute spectral shape, the
  ring-out levels a few hops later). **Output:** P(kick), P(clap/snare), P(neither). A 2-layer MLP of a few thousand
  weights runs in microseconds in plain C#, no ML runtime needed.
- **Training data is the hard part.** Public drum-transcription sets (ENST-Drums, MDB Drums, IDMT-SMT-Drums, ADTOF,
  E-GMD) are mostly acoustic kits or have non-commercial licences, which matters for weights we ship. Options: render
  our own labelled loops from royalty-free one-shots across genres, and/or label real tracks semi-automatically with
  the current detector plus manual review.
- **It would only replace the generic prior.** The per-song models keep learning the actual track, and the ring-out
  check keeps labelling, so a wrong prior gets corrected within a few bars.
- **Measure first:** collect a set of real tracks with hand-marked first-kick times and count false flashes before
  the first real kick, with and without the classifier.

## Other ideas

- Re-check "IRONIC" by ero808, where the BPM used to jump between readings (146 → 125), against the new tempo tracker
  with `RealMusicBench`.
- A causal (no-lookahead) drum model, trained by distillation: run the current detectors offline with full hindsight
  as the teacher, train a small model that only sees the past to match it. Same latency as today, no hand-tuned cues.
- "Speaker delay" setting: Bluetooth speakers play 150–250 ms after Windows hands over the audio, so the lights could
  be held back to land exactly on what you hear, with no prediction involved.
- Remember each song's hit timeline and replay it in sync with the player's position on later plays.
- Long-tailed kicks (hardstyle, 808): judge ring-out over a longer window when the kick band stays clean.
- Remove the startup Run-key entry on uninstall, so it doesn't linger if "Start with Windows" was on.
- Code-sign the MSI to stop SmartScreen warnings.
