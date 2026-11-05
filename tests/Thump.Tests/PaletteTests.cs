using Thump.Colors;

namespace Thump.Tests;

public class PaletteTests
{
    static double Dist(Rgb a, Rgb b) => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    static void AssertAllDistinct(List<Rgb> p)
    {
        for (int i = 0; i < p.Count; i++)
        for (int j = i + 1; j < p.Count; j++)
            Assert.True(Dist(p[i], p[j]) >= 0.15, $"{p[i].ToHex()} and {p[j].ToHex()} are too close");
    }

    [Fact]
    public void Plain_orange_cover_gets_one_variation_per_light()
    {
        var orange = Rgb.FromHex("#FF7A00");
        var p = Palettes.Expand([orange], 4);

        Assert.Equal(4, p.Count);
        Assert.Equal(orange, p[0]);
        AssertAllDistinct(p);
        // Redder, darker and lighter orange, in that order.
        Assert.True(p[1].ToHsv().H < orange.ToHsv().H, "second colour should lean red");
        Assert.True(p[2].Max < orange.Max - 0.2, "third colour should be darker");
        Assert.True(p[3].ToHsv().S < orange.ToHsv().S - 0.2, "fourth colour should be lighter / softer");
    }

    [Fact]
    public void Colourful_cover_is_used_as_is()
    {
        List<Rgb> cover = [Rgb.FromHex("#FF0040"), Rgb.FromHex("#00C8FF"), Rgb.FromHex("#FFD000"), Rgb.FromHex("#7A00FF"), Rgb.FromHex("#00FF60")];
        Assert.Equal(cover, Palettes.Expand(cover, 4));
    }

    [Fact]
    public void Two_colour_cover_fills_up_from_both_colours()
    {
        var p = Palettes.Expand([Rgb.FromHex("#FF0000"), Rgb.FromHex("#0040FF")], 6);
        Assert.Equal(6, p.Count);
        AssertAllDistinct(p);
    }

    [Fact]
    public void Black_and_white_cover_varies_colour_temperature()
    {
        var p = Palettes.Expand([Rgb.White], 4);
        Assert.Equal(4, p.Count);
        AssertAllDistinct(p);
    }

    static readonly Rgb Red = new(1, 0, 0), Blue = new(0, 0, 1), Green = new(0, 1, 0);

    [Fact]
    public void Crossfade_moves_gradually_and_lands_on_the_new_colours()
    {
        var fade = new PaletteCrossfade(3);
        fade.FadeTo([Red]);
        Assert.Equal(Red, fade.At(0)); // first palette: nothing to fade from

        fade.FadeTo([Blue]);
        Assert.Equal(Red, fade.At(0));

        fade.Advance(1.5);
        Assert.Equal(new Rgb(0.5, 0, 0.5), fade.At(0));

        fade.Advance(1.5);
        Assert.False(fade.IsFading);
        Assert.Equal(Blue, fade.At(0));
    }

    [Fact]
    public void Skipping_mid_fade_continues_from_what_is_showing()
    {
        var fade = new PaletteCrossfade(3);
        fade.FadeTo([Red]);
        fade.FadeTo([Blue]);
        fade.Advance(1.5);
        var showing = fade.At(0);

        fade.FadeTo([Green]);
        Assert.Equal(showing, fade.At(0));
    }

    [Fact]
    public void Crossfade_handles_palettes_of_different_lengths_without_a_jump()
    {
        var fade = new PaletteCrossfade(3);
        fade.FadeTo([Red, Blue, Green]);
        var before = Enumerable.Range(0, 3).Select(i => fade.At(i + 0.25)).ToList();

        fade.FadeTo([Green, Blue]);
        Assert.Equal(before, Enumerable.Range(0, 3).Select(i => fade.At(i + 0.25)));
    }

    [Fact]
    public void Refetching_the_same_colours_does_not_restart_a_fade()
    {
        var fade = new PaletteCrossfade(3);
        fade.FadeTo([Red]);
        fade.FadeTo([Blue]);
        fade.Advance(1.5);
        var showing = fade.At(0);

        fade.FadeTo([Blue]);
        Assert.Equal(showing, fade.At(0));
    }

    [Fact]
    public void Slider_changes_apply_at_once_unless_fading()
    {
        var fade = new PaletteCrossfade(3);
        fade.SetTarget([Red]);
        fade.SetTarget([Blue]);
        Assert.Equal(Blue, fade.At(0));

        fade.FadeTo([Red]);
        fade.Advance(1.5);
        fade.SetTarget([Green]);
        Assert.True(fade.IsFading);
        fade.Advance(1.5);
        Assert.Equal(Green, fade.At(0));
    }

    [Fact]
    public void Many_lights_still_get_distinct_colours()
    {
        var p = Palettes.Expand([Rgb.FromHex("#FF7A00")], 12);
        Assert.Equal(12, p.Count);
        AssertAllDistinct(p);
    }
}
