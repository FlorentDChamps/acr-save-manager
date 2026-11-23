using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>EventList: the event config list of the player save and the selection past it.</summary>
public class EventListTests
{
    private static readonly string[] AllEvents =
        ["DefaultFreePractice", "DefaultTimeAttack", "DefaultSingleStage", "DefaultRally", "DefaultOnlineSingleStage"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finds_the_list_and_the_selection_past_it(bool leaderboardFirst)
    {
        var list = EventList.Find(SaveBuilder.PlayerData(leaderboardFirst));

        Assert.Equal(AllEvents[..4], list.Events.Select(e => e.Name));
        Assert.Equal(SaveBuilder.WeatherStage, list.Selection!.Stage);
        Assert.Equal(SaveBuilder.WeatherCar, list.Selection.Car);
        Assert.Equal(3, list.Selection.Options.Count);
        Assert.Equal(list.EventsEnd, list.Selection.Start);
    }

    [Fact]
    public void A_new_player_save_with_the_rally_alone_is_found()
    {
        byte[] save = SaveBuilder.PlayerData(newPlayer: true);
        var list = EventList.Find(save);

        Assert.Equal(["DefaultRally"], list.Events.Select(e => e.Name));
        Assert.Equal(SaveBuilder.WeatherStage, list.Selection!.Stage);
        Assert.Empty(list.Selection.Options);
        Assert.Equal(SaveBuilder.RallyName, RallyWeekend.Find(save, list.Events[0].Start)!.Calendar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_rally_in_progress_is_read_with_its_settings(bool leaderboardFirst)
    {
        byte[] save = SaveBuilder.PlayerData(leaderboardFirst, rallyInProgress: true);
        var list = EventList.Find(save);

        Assert.Equal(AllEvents[..4], list.Events.Select(e => e.Name));
        Assert.Equal(["DefaultRally"], list.Events.Where(e => e.RallyInProgress).Select(e => e.Name));
        Assert.Equal(SaveBuilder.WeatherStage, list.Selection!.Stage);
        var rally = list.Events.Single(e => e.Name == "DefaultRally");
        Assert.Equal(SaveBuilder.RallyName, RallyWeekend.Find(save, rally.Start)!.Calendar);
    }
}
