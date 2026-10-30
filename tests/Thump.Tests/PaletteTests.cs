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

    [Fact]
    public void Many_lights_still_get_distinct_colours()
    {
        var p = Palettes.Expand([Rgb.FromHex("#FF7A00")], 12);
        Assert.Equal(12, p.Count);
        AssertAllDistinct(p);
    }
}
