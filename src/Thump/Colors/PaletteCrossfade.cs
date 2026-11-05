namespace Thump.Colors;

/// <summary>
/// Blends from the palette on the lights to a new one over a few seconds. The two can differ in length, so colours are
/// blended by position rather than by index: palette motion carries on through the fade without a jump.
/// </summary>
public sealed class PaletteCrossfade(double seconds)
{
    List<Rgb> _from = [], _to = [];
    double _progress = 1;

    public bool IsFading => _progress < 1;

    /// <summary>Starts fading to <paramref name="target"/> from whatever is showing now, even if that is itself mid-fade.</summary>
    public void FadeTo(List<Rgb> target)
    {
        if (_to.SequenceEqual(target)) return;
        if (_to.Count == 0) { SetTarget(target); return; } // nothing on the lights yet: nothing to fade from
        _from = Snapshot(Lcm(_to.Count, IsFading ? _from.Count : _to.Count));
        _to = target;
        _progress = 0;
    }

    /// <summary>
    /// Applies <paramref name="target"/> at once, for changes the user is making by hand (a slider should respond while
    /// it moves). A fade in progress keeps going, towards the adjusted colours.
    /// </summary>
    public void SetTarget(List<Rgb> target)
    {
        _to = target;
        if (!IsFading) _from = target;
    }

    public void Advance(double dt) => _progress = Math.Min(1, _progress + dt / seconds);

    /// <summary>The colour at a position in the palette; whole numbers are its colours, in between blends neighbours.</summary>
    public Rgb At(double pos)
    {
        if (!IsFading) return PaletteAt(_to, pos);
        double t = _progress * _progress * (3 - 2 * _progress); // eased, so it neither lurches off nor stops dead
        return Rgb.Lerp(PaletteAt(_from, pos), PaletteAt(_to, pos), t);
    }

    /// <summary>What is showing now, one colour per entry of the target palette.</summary>
    public Rgb[] Current() => Snapshot(_to.Count).ToArray();

    List<Rgb> Snapshot(int count)
    {
        var colors = new List<Rgb>(count);
        for (int i = 0; i < count; i++) colors.Add(At(i));
        return colors;
    }

    static int Lcm(int a, int b)
    {
        int x = a, y = b;
        while (y != 0) (x, y) = (y, x % y);
        return a / x * b;
    }

    static Rgb PaletteAt(List<Rgb> p, double pos)
    {
        if (p.Count == 1) return p[0];
        pos = ((pos % p.Count) + p.Count) % p.Count;
        int i = (int)pos;
        double t = pos - i;
        // Ease so colours dwell, then move quickly — feels more on-beat than a linear fade.
        t = t * t * (3 - 2 * t);
        return Rgb.Lerp(p[i], p[(i + 1) % p.Count], t);
    }
}
