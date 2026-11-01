# Thump drum model

A small, causal (no-lookahead) drum detector for live audio: every 10.7 ms it says whether a **kick**, a
**clap/snare** (rims included) or a **hi-hat** just started. It hears the last ~4 s, so it judges each hit by its
sound *and* by where it falls in the groove, without any notion of songs, tempo or bars. It is what drives Thump's
lights, and it's released under the same MIT licence as Thump. The weights are `src/Thump/Assets/drums.thdm` (also attached to each release).

| | |
|---|---|
| Input | 64 log-spaced bands, 30 Hz – 16 kHz, log magnitude of a 2048-point Hann FFT at 48 kHz, hop 512 (`Audio/ModelFeatures.cs`); minus a running mean level (time constant 5 s) |
| Network | 8 causal dilated 1-D convolutions (96 channels, kernel 3, dilations 1, 2, 4, 8, 16, 32, 64, 64, ReLU; every layer after the first is residual, adding its output to its input), then a 1×1 convolution to 3 sigmoids; ~213k weights, 383-hop (~4.1 s) receptive field |
| Output | Probability that each drum started 4 hops (~43 ms) earlier; Thump flashes on a rising edge over a per-drum threshold (stored in the file), at most once per 60 ms |
| Latency | ~50 ms median from the hit to the flash, no lookahead. Waiting 2 hops longer than 0.4 did lets the model hear a clap's body before deciding, which cut fake claps by about two-thirds |
| Cost | ~210k multiply-adds per hop: well under 1 % of one CPU core |
| Reference code | `Audio/DrumModel.cs` (streaming inference); its test checks it against the training code to < 1e-4 |

## Training

Distilled offline from three teachers into this small student:
1. ~730 electronic tracks (house, bassline, DnB, pop, riddim, trap house) labelled by a large offline drum
   transcriber with full hindsight;
2. [WaivOps EDM-HSE](https://doi.org/10.5281/zenodo.13769544) house drum loops (CC BY 4.0), with labels corrected
   from each loop's known drum list, laid over the songs;
3. synthesised 808/909-style drum-machine grooves (house, trap house, bassline and random), with exact labels, also
   laid over the songs.

Training crops are ~11 s long and built like a DJ set rather than one song at a time: half blend into another song
(over up to 8.5 s with both at full volume in the middle, or a hard cut), half of the songs and loops are played up
to 6 % faster or slower, and drum loops come in and drop out partway through. So the model learns that a groove can
change at any moment and keeps listening to the audio rather than to what it expects.

None of the teachers' weights, code or data are included; the training code and the music stay private.

## How good it is

On 116 held-out songs (split by artist), against the transcriber's labels, with the shipped thresholds:

| | F1 | Precision | Recall |
|---|---|---|---|
| Kick | 0.86 | 0.96 | 0.78 |
| Clap / snare | 0.72 | 0.92 | 0.60 |
| Hi-hat | 0.82 | 0.89 | 0.75 |

0.4's model: kick 0.79, clap/snare 0.65 (precision 0.78), hats 0.76; the hand-tuned detectors before it: kick 0.69,
clap/snare 0.32, no hats. Against 0.4 on the same songs, this one has about a third as many fake claps (1.5k vs 4.9k),
a third as many clap/snare flashes in drum-free stretches (3.8 vs 10.9 a minute) and a quarter as many fake kicks
there (1.9 vs 7.3). On simulated DJ mixes of held-out songs it scores kick 0.82, clap/snare 0.65, hats 0.77 overall,
and slightly better in the first 4 s after the new song takes over, so its memory doesn't hold on to the old groove.

The transcriber itself misses many electronic claps, so the clap scores undersell the model and say little about
those claps: some of its "fake" claps are real ones the transcriber didn't mark.

**Known weak spots:** unusual or heavily processed claps, muted or filtered pre-drop snares, and snare rolls now and
then read as hats.

## File format (.thdm, little-endian)

`"THDM"`, int32 version (2), int32 delay (hops), float32 gain time constant (s), float32 refractory (s),
float32 thresholds[3] (kick, snare, hihat), int32 layer count; then per layer int32 in, out, kernel, dilation,
flags (bit 0: residual, the layer's input is added to its output after the ReLU), float32 weights[out][in][kernel]
and bias[out]. Hidden layers are ReLU, the last one sigmoid. Kernel tap k looks (kernel − 1 − k) × dilation hops
back. Version 1 (0.4) is the same without the flags field; Thump still reads it.
