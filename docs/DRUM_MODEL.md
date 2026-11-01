# Thump drum model

A tiny, causal (no-lookahead) drum detector for live audio: every 10.7 ms it says whether a **kick**, a
**clap/snare** (rims included) or a **hi-hat** just started. It is what drives Thump's lights, and it's released
under the same MIT licence as Thump. The weights are `src/Thump/Assets/drums.thdm` (also attached to each release).

| | |
|---|---|
| Input | 64 log-spaced bands, 30 Hz – 16 kHz, log magnitude of a 2048-point Hann FFT at 48 kHz, hop 512 (`Audio/ModelFeatures.cs`); minus a running mean level (time constant 5 s) |
| Network | 3 causal dilated 1-D convolutions (64 channels, kernel 3, dilations 1, 2, 4, ReLU), then a 1×1 convolution to 3 sigmoids; ~37k weights, ~160 ms receptive field |
| Output | Probability that each drum started 2 hops (~21 ms) earlier; Thump flashes on a rising edge over a per-drum threshold (stored in the file), at most once per 60 ms |
| Latency | ~30 ms median from the hit to the flash, no lookahead |
| Cost | ~37k multiply-adds per hop: well under 1 % of one CPU core |
| Reference code | `Audio/DrumModel.cs` (streaming inference); its test checks it against the training code to < 1e-4 |

## Training

Distilled offline from three teachers into this small student:
1. ~730 electronic tracks (house, bassline, DnB, pop, riddim, trap house) labelled by a large offline drum
   transcriber with full hindsight;
2. [WaivOps EDM-HSE](https://doi.org/10.5281/zenodo.13769544) house drum loops (CC BY 4.0), with labels corrected
   from each loop's known drum list, laid over the songs;
3. synthesised 808/909-style drum-machine grooves (house, trap house, bassline and random), with exact labels, also
   laid over the songs.

None of the teachers' weights, code or data are included; the training code and the music stay private.

## How good it is

On 116 held-out songs (split by artist), against the transcriber's labels, with the shipped thresholds:

| | F1 | Precision | Recall |
|---|---|---|---|
| Kick | 0.79 | 0.86 | 0.73 |
| Clap / snare | 0.65 | 0.78 | 0.55 |
| Hi-hat | 0.76 | 0.81 | 0.71 |

(The previous hand-tuned detectors: kick 0.69, clap/snare 0.32, no hats.) The shipped clap/snare threshold is lower
than the one these numbers were measured at, trading a little precision for recall. The transcriber itself misses
many electronic claps, so the clap scores undersell the model a bit and say little about those claps.

**Known weak spots:** unusual or heavily processed claps, muted or filtered pre-drop snares, and snare rolls now and
then read as hats.

## File format (.thdm, little-endian)

`"THDM"`, int32 version (1), int32 delay (hops), float32 gain time constant (s), float32 refractory (s),
float32 thresholds[3] (kick, snare, hihat), int32 layer count; then per layer int32 in, out, kernel, dilation,
float32 weights[out][in][kernel] and bias[out]. Hidden layers are ReLU, the last one sigmoid. Kernel tap k looks
(kernel − 1 − k) × dilation hops back.
