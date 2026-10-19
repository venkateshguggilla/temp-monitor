using Xunit;

namespace TempMonitor.Tests;

public class ReadingTests
{
    private static Reading Cpu() => new() { Key = "cpu:0", Title = "CPU", DeviceName = "Test CPU", WarmAt = 80, HotAt = 95 };

    [Theory]
    [InlineData(45f, TempLevel.Cool)]
    [InlineData(79.9f, TempLevel.Cool)]
    [InlineData(80f, TempLevel.Warm)]
    [InlineData(94.9f, TempLevel.Warm)]
    [InlineData(95f, TempLevel.Hot)]
    public void Level_follows_the_cards_thresholds(float celsius, TempLevel expected) =>
        Assert.Equal(expected, Cpu().Level(celsius));

    [Theory]
    [InlineData(45f, false, "45 °C")]
    [InlineData(45f, true, "113 °F")]
    [InlineData(100f, true, "212 °F")]
    public void FormatTemp(float celsius, bool fahrenheit, string expected) =>
        Assert.Equal(expected, Reading.FormatTemp(celsius, fahrenheit).Replace(',', '.'));
}

public class CardHistoryTests
{
    [Fact]
    public void Tracks_min_and_max_temperature_ignoring_gaps()
    {
        var h = new CardHistory();
        h.Add(50, 10);
        h.Add(null, 20);
        h.Add(70, 30);
        h.Add(45, 40);
        Assert.Equal(45, h.MinTemp);
        Assert.Equal(70, h.MaxTemp);
    }

    [Fact]
    public void No_temperature_means_no_min_max()
    {
        var h = new CardHistory();
        h.Add(null, 5);
        Assert.Null(h.MinTemp);
        Assert.Null(h.MaxTemp);
    }

    [Fact]
    public void Keeps_only_the_last_two_minutes()
    {
        var h = new CardHistory();
        for (var i = 0; i < CardHistory.Length + 30; i++)
            h.Add(40, i % 100);
        Assert.Equal(CardHistory.Length, h.UsagePoints(240, 50).Count);
    }

    [Fact]
    public void Newest_sample_is_at_the_right_edge()
    {
        var h = new CardHistory();
        h.Add(40, 0);
        h.Add(40, 100);
        var pts = h.UsagePoints(238, 50);
        Assert.Equal(2, pts.Count);
        Assert.Equal(238, pts[^1].X, 6);                 // right edge
        Assert.Equal(1, pts[^1].Y, 6);                   // 100 % = top (1 px margin)
        Assert.Equal(238 - 238.0 / (CardHistory.Length - 1), pts[0].X, 6);
        Assert.Equal(49, pts[0].Y, 6);                   // 0 % = bottom (1 px margin)
    }

    [Fact]
    public void Gaps_are_skipped_in_the_temperature_line()
    {
        var h = new CardHistory();
        h.Add(40, 10);
        h.Add(null, 10);
        h.Add(41, 10);
        Assert.Equal(2, h.TempPoints(100, 50, 95).Count);
        Assert.Equal(3, h.UsagePoints(100, 50).Count);
    }

    [Fact]
    public void Temperature_band_is_25_to_hot_plus_5_and_widens()
    {
        var h = new CardHistory();
        h.Add(25, 0);                                     // bottom of the band
        h.Add(100, 0);                                    // hot (95) + 5 = top
        var pts = h.TempPoints(119, 52, 95);
        Assert.Equal(51, pts[0].Y, 6);
        Assert.Equal(1, pts[1].Y, 6);

        var hotter = new CardHistory();
        hotter.Add(110, 0);                               // band widens to the max seen
        Assert.Equal(1, hotter.TempPoints(119, 52, 95)[0].Y, 6);
    }

    [Fact]
    public void Zero_size_area_gives_no_points()
    {
        var h = new CardHistory();
        h.Add(40, 50);
        Assert.Empty(h.UsagePoints(0, 50));
        Assert.Empty(h.TempPoints(100, 0, 95));
    }
}
