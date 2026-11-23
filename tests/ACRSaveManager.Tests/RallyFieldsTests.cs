using ACRSaveManager.Services;

namespace ACRSaveManager.Tests;

/// <summary>The rally editor's boxes: times of day, percentages, and the ▲ and ▼ beside them.</summary>
public class RallyFieldsTests
{
    [Theory]
    [InlineData(28800f, "08:00")]
    [InlineData(86340f, "23:59")]
    [InlineData(0f, "00:00")]
    [InlineData(86385f, "23:59")]   // 23:59:45 stays in the day
    public void Clock_shows_seconds_since_midnight(float seconds, string text) =>
        Assert.Equal(text, RallyFields.Clock(seconds));

    [Theory]
    [InlineData("8:30", 30600f)]
    [InlineData(" 08:30 ", 30600f)]
    [InlineData("9h30", null)]
    [InlineData("9.30", null)]
    [InlineData("25:00", null)]
    public void ParseClock_reads_a_time_of_day_only(string text, float? seconds) =>
        Assert.Equal(seconds, RallyFields.ParseClock(text));

    [Theory]
    [InlineData("35", 0.35f)]
    [InlineData("35 %", 0.35f)]
    [InlineData("150", null)]
    [InlineData("12,5", null)]
    [InlineData("-5", null)]
    public void ParsePercent_reads_a_whole_number_from_0_to_100(string text, float? value) =>
        Assert.Equal(value, RallyFields.ParsePercent(text));

    [Fact]
    public void Percent_shows_a_stored_share_as_the_menu_does() =>
        Assert.Equal("80", RallyFields.Percent(0.79999995f));

    [Theory]
    [InlineData(80, 5, 50, 120, 1, 85)]
    [InlineData(82, 5, 50, 120, 1, 85)]     // to the next multiple of the step
    [InlineData(82, 5, 50, 120, -1, 80)]
    [InlineData(120, 5, 50, 120, 1, null)]  // at the end of the range
    [InlineData(118, 5, 50, 120, 1, 120)]
    [InlineData(130, 5, 50, 120, 1, null)]  // past it already: ▲ does not bring it back
    [InlineData(130, 5, 50, 120, -1, 120)]
    [InlineData(1410, 30, 0, 1410, 1, null)] // 23:30, the clock's last step
    [InlineData(0, 30, 0, 1410, -1, null)]
    [InlineData(3, 1, 1, 0, 1, null)]       // a range upside down holds its minimum only
    public void Step_goes_to_the_next_multiple_within_the_range(int now, int step, int min, int max, int direction, int? next) =>
        Assert.Equal(next, RallyFields.Step(now, step, min, max, direction));
}
