# Tools

`ThumpTool` prepares and scores data for the drum model (see [docs/ROADMAP.md](../docs/ROADMAP.md)).

```bash
dotnet run --project tools/ThumpTool -- analyze <music dir> <out dir>
dotnet run --project tools/ThumpTool -- score <labels dir> <hits dir>
```

**analyze** runs Thump's live pipeline over every track (decoded to mono 48 kHz by ffmpeg, which must be on the PATH,
as the teacher's is; in parallel, skipping tracks already done) and writes, mirroring the folder layout:

- `<track>.thf`: what the model hears, one row per 10.7 ms hop. Header: `"THF1"`, then int32 bands (64), sample
  rate, hop in samples, row count; then rows × bands float16, the log magnitude of 64 bands from 30 Hz to 16 kHz
  (`ModelFeatures`). In NumPy: `np.frombuffer(b[20:], '<f2').reshape(rows, 64)`.
- `<track>.hits.csv`: what the current detectors flashed on: `time,drum,confidence`, drum `kick` or `snare`.

**score** pairs `<labels dir>/<track>.csv` (`time,drum[,confidence]`, drum `kick`, `snare` or `hihat`) with
`<hits dir>/<track>.hits.csv` and prints F1, precision, recall and median latency per drum, per top-level folder
(genre) and overall. A hit counts for a label from 50 ms before to 80 ms after it.

Neither the audio nor these files belong in the repository.
