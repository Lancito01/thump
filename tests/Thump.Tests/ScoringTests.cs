using ThumpTool;

namespace Thump.Tests;

public class ScoringTests
{
    [Fact]
    public void Matches_late_hits_once_and_counts_the_rest_as_misses_and_false_flashes()
    {
        Hit[] labels = [new(1.00, "kick"), new(1.50, "kick"), new(2.00, "kick"), new(2.00, "snare")];
        Hit[] hits =
        [
            new(1.02, "kick"), new(1.03, "kick"), // second one is a double flash
            new(1.70, "kick"),                    // too late for 1.50
            new(2.06, "kick"),
            new(2.01, "hihat"),                   // wrong drum
        ];
        var s = Scoring.Score(labels, hits, "kick");
        Assert.Equal(3, s.Labels);
        Assert.Equal(4, s.Detections);
        Assert.Equal(2, s.Matched);
        Assert.Equal(0.5, s.Precision, 3);
        Assert.Equal(2 / 3.0, s.Recall, 3);
        Assert.Equal(0.06, s.MedianLatency, 3);
        Assert.Equal(0, Scoring.Score(labels, hits, "snare").Matched);
    }
}
