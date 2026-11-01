# Roadmap

## Done in 0.3

- Kick and clap/snare detection rebuilt sound-first: a confidence per hit, the rhythm only nudges borderline sounds,
  a ring-out check labels what the models learn from, and a drum that hasn't been heard yet never flashes.
- Tempo followed from every onset with its own confidence (fixes the "locked ↔ learning" flip-flop: the grid used to
  anchor on a bass note and the learned pattern pointed at the wrong 16ths after every relock).
- *Clap* light role, CLAP LED, confidence readout in the live panel.
- Opt-in "Remember songs" with a disk cap, least recently played forgotten first.

## Done in 0.4: live, instrument-based light sync

DJ software (rekordbox, Serato) splits a track into stems after analysing the whole file. Thump does the simpler
version live: from whatever is playing, with no lookahead and no added delay, each light follows an instrument.

| Source | Shown as |
| --- | --- |
| Mix | brightness follows the whole track |
| Bass | brightness follows the low end |
| Kick | a flash per hit |
| Snare / clap (rims included) | a flash per hit |
| Hats | a gentle shimmer on top of a base level, **not** flashes (16th hats are far past ~3 flashes a second) |

- **One small learned drum model, three outputs** (kick, snare/clap, hats), ~37k weights, ~30 ms after the hit, no
  lookahead. The flash is as bright as the model is sure, with a minimum below which it stays dark. See
  [the model card](DRUM_MODEL.md).
- **Trained by distillation from three teachers:** an offline drum transcriber on the user's own music library,
  EDM-HSE house loops (CC BY 4.0) and synthesised drum-machine grooves, both laid over the songs.
- **Training stays private; the weights are MIT.** The training code, music and labels live in a local repo. The
  model is our own, trained on labels; none of the teachers' weights, code or data are bundled. The weights ship
  inside Thump and are attached to each release.
- Scores on 116 held-out songs, F1: kick 0.79, clap/snare 0.65, hats 0.76 (0.3.3: kick 0.69, clap/snare 0.32).

## Next: a bigger model that knows the groove

The model still misses unusual or processed claps (Typo - Stick Move is the reference track), muted pre-drop
snares, and sometimes takes a snare roll for hats. Its misses mostly sit just under the threshold, and training was
still improving when it stopped, so it is too small rather than wrongly taught.

- **Bigger.** Wider layers (96–128 channels), maybe a 2-D convolution over frequency first, and longer training.
  Cost stays tiny: ~10× today's work is still ~0.3 ms per hop and well under 1 % of a core, and the ~30 ms reaction
  time comes from the FFT, not the network, so it doesn't change.
- **Pattern memory.** Today the model hears ~160 ms and judges every hit by sound alone. With a few seconds of memory
  (more dilated layers or a small recurrent layer) it can learn the groove: "the last four kicks were this far
  apart, so the next one is about due", "claps here land on 2 and 4". No BPM or bar counter needed: it learns the
  spacing from the hits themselves. That should make borderline hits come out the same way every bar. Watch for it
  "expecting" a hit and flashing on silence (a fake drop): the labels only mark real hits, but listen for it.
- **Measured the same way**, against 0.4 on the same held-out songs, plus by ear on the reference tracks.

## Then: Andy as the teacher

A labelling tool for songs from bug reports: open a song, see what the current model thinks is a kick, clap or hat
on a waveform you can play, then move, delete or tap in hits (taps snap to the nearest onset). ~20 minutes a song.
These hand-checked labels are the ground truth: in training they count far more than the teachers' (10–50×), and a
few are kept back as an exam the model never trains on, so there's finally a score that measures what a person
hears rather than what the teacher says. Same `time,drum` CSVs as the teachers, so training reads them as is.

## Later: tidy up after the model

With the model on, the beat tracker only feeds the BPM readout, the old hand-tuned detectors are only a fallback
for a model that is built into the exe, and "Remember songs" stores what those detectors learned plus the tempo.
Keep them for now (a pattern-aware model may make the call clearer), then decide:
- **Beat tracker:** keep as a BPM readout, or remove.
- **Old detectors:** remove once the model has proven itself.
- **Remember songs:** remove, or repurpose for per-song things the model can't know, like hand-made corrections from
  the labelling tool or a per-song sensitivity nudge ("this track's claps need +0.5").
- **Publish the model more widely** (ONNX export, a model page) once it's good enough that others would want it.

## Still worth checking

- **Before claiming it's the first:** check for existing live per-instrument light sync (lighting consoles, DJ
  lighting add-ons, smart-light apps) so the README and any store listing say only what's true.
- **Not in scope (yet): synths, vocals.** They overlap in frequency and sustain, so splitting them live is
  real-time source separation, much less reliable than drums. If anything, a continuous "melody energy" source like
  Bass, not hits.

## Other ideas

- Re-check "IRONIC" by ero808, where the BPM used to jump between readings (146 → 125), against the new tempo tracker
  with `RealMusicBench`.
- "Speaker delay" setting: Bluetooth speakers play 150–250 ms after Windows hands over the audio, so the lights could
  be held back to land exactly on what you hear, with no prediction involved.
- Remember each song's hit timeline and replay it in sync with the player's position on later plays.
- Long-tailed kicks (hardstyle, 808): judge ring-out over a longer window when the kick band stays clean.
- Remove the startup Run-key entry on uninstall, so it doesn't linger if "Start with Windows" was on.
- Code-sign the MSI to stop SmartScreen warnings.
