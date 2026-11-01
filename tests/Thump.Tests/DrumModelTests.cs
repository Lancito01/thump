using System.IO;
using Thump.Audio;

namespace Thump.Tests;

public class DrumModelTests
{
    /// <summary>
    /// The C# streaming inference gives the same probabilities as PyTorch running the whole sequence at once. The
    /// fixture (thump-drums: export.py --fixture) is an untrained model on random input.
    /// </summary>
    [Fact]
    public void Matches_the_training_code_hop_by_hop()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var model = DrumModel.Load(Path.Combine(dir, "fixture.thdm"));
        var input = Floats(Path.Combine(dir, "fixture.in.f32"));
        var expected = Floats(Path.Combine(dir, "fixture.out.f32"));
        int hops = input.Length / ModelFeatures.Bands;
        var frame = new float[ModelFeatures.Bands];
        double worst = 0;
        for (int t = 0; t < hops; t++)
        {
            Array.Copy(input, t * frame.Length, frame, 0, frame.Length);
            model.Step(frame, 512 / 48000.0);
            for (int d = 0; d < DrumModel.Drums; d++)
                worst = Math.Max(worst, Math.Abs(model.Probabilities[d] - expected[t * DrumModel.Drums + d]));
        }
        Assert.True(worst < 1e-4, $"worst difference {worst}");
    }

    static float[] Floats(string path)
    {
        var b = File.ReadAllBytes(path);
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, b.Length);
        return f;
    }

    [Fact]
    public void Ships_inside_Thump()
    {
        using var s = typeof(DrumModel).Assembly.GetManifestResourceStream("drums.thdm");
        Assert.NotNull(s);
        var m = DrumModel.Load(s);
        m.Step(new float[ModelFeatures.Bands], 512 / 48000.0);
        Assert.All(m.Probabilities, p => Assert.InRange(p, 0, 1));
    }
}
