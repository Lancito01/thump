using System.IO;

namespace Thump.Audio;

/// <summary>
/// The learned drum detector: a few causal dilated convolutions over the last ~160 ms of <see cref="ModelFeatures"/>,
/// giving the probability that a kick, a snare/clap or a hi-hat started <see cref="Delay"/> hops ago. It runs one
/// hop at a time and only computes the newest step, keeping each layer's recent inputs in a ring, so a hop costs a
/// few tens of thousands of multiply-adds. Trained offline (thump-drums); the weights come from a .thdm file.
/// </summary>
public sealed class DrumModel
{
    public const int Drums = 3; // kick, snare/clap, hi-hat

    sealed class Layer
    {
        public int In, Out, Kernel, Dilation;
        public float[] W = [], B = [];       // W[o, i, k], row-major
        public float[] History = [];         // the last (Kernel - 1) * Dilation + 1 inputs, In floats each
        public int Pos;
        public float[] Output = [];
    }

    readonly Layer[] _layers;
    readonly float[] _input = new float[ModelFeatures.Bands];
    double _gain = double.NaN;

    /// <summary>Hops between a hit and the model's answer about it.</summary>
    public int Delay { get; }
    public double GainTau { get; }
    public double Refractory { get; }
    public float[] Thresholds { get; } = new float[Drums];
    /// <summary>The latest probabilities, one per drum.</summary>
    public float[] Probabilities { get; } = new float[Drums];

    DrumModel(BinaryReader r)
    {
        if (new string(r.ReadChars(4)) != "THDM" || r.ReadInt32() != 1) throw new InvalidDataException("Not a drum model");
        Delay = r.ReadInt32();
        GainTau = r.ReadSingle();
        Refractory = r.ReadSingle();
        for (int d = 0; d < Drums; d++) Thresholds[d] = r.ReadSingle();
        _layers = new Layer[r.ReadInt32()];
        for (int l = 0; l < _layers.Length; l++)
        {
            var y = new Layer { In = r.ReadInt32(), Out = r.ReadInt32(), Kernel = r.ReadInt32(), Dilation = r.ReadInt32() };
            y.W = ReadFloats(r, y.Out * y.In * y.Kernel);
            y.B = ReadFloats(r, y.Out);
            y.History = new float[((y.Kernel - 1) * y.Dilation + 1) * y.In];
            y.Output = new float[y.Out];
            _layers[l] = y;
        }
        if (_layers[0].In != ModelFeatures.Bands || _layers[^1].Out != Drums) throw new InvalidDataException("Wrong model shape");
    }

    static float[] ReadFloats(BinaryReader r, int n)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = r.ReadSingle();
        return a;
    }

    public static DrumModel Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        return new DrumModel(r);
    }

    /// <summary>Forget the past (a new device or a long silence). The layers start from silence, as in training.</summary>
    public void Reset()
    {
        foreach (var y in _layers) { Array.Clear(y.History); y.Pos = 0; }
        _gain = double.NaN;
    }

    /// <summary>Feeds one hop of <see cref="ModelFeatures.Values"/>; updates <see cref="Probabilities"/>.</summary>
    public void Step(float[] features, double hopSeconds)
    {
        // Follow the overall level so a quiet master and a loud one look alike (same as training).
        double level = 0;
        foreach (var v in features) level += v;
        level /= features.Length;
        _gain = double.IsNaN(_gain) ? level : _gain + (1 - Math.Exp(-hopSeconds / GainTau)) * (level - _gain);
        for (int i = 0; i < _input.Length; i++) _input[i] = (float)(features[i] - _gain);

        float[] x = _input;
        for (int l = 0; l < _layers.Length; l++)
        {
            var y = _layers[l];
            int slots = y.History.Length / y.In;
            Array.Copy(x, 0, y.History, y.Pos * y.In, y.In);
            for (int o = 0; o < y.Out; o++)
            {
                float sum = y.B[o];
                for (int k = 0; k < y.Kernel; k++)
                {
                    // Kernel tap k looks (Kernel - 1 - k) * Dilation hops back; before the first hop that is silence (0).
                    int slot = ((y.Pos - (y.Kernel - 1 - k) * y.Dilation) % slots + slots) % slots;
                    var w = y.W.AsSpan((o * y.In) * y.Kernel);
                    var h = y.History.AsSpan(slot * y.In, y.In);
                    for (int i = 0; i < y.In; i++) sum += w[i * y.Kernel + k] * h[i];
                }
                y.Output[o] = l < _layers.Length - 1 ? Math.Max(0, sum) : 1 / (1 + MathF.Exp(-sum));
            }
            y.Pos = (y.Pos + 1) % slots;
            x = y.Output;
        }
        Array.Copy(x, Probabilities, Drums);
    }
}
