using System.IO;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>Changing a Rally Weekend: the game's rules, its calendars, and the write into the save.</summary>
public class RallyWeekendEditTests
{
    private static readonly GameCatalog Catalog = GameCatalog.Default;

    /// <summary>The synthetic weekend on one of the game's calendars, so the catalog knows its stages.</summary>
    private static RallyWeekend Sweden(string calendar = "SwedenWeekendShort", bool online = false) =>
        RallyWeekendEdit.FromCalendar(online ? SaveBuilder.OnlineWeekend() : SaveBuilder.OfflineWeekend(),
                                      Catalog.CalendarInfoOf(calendar)!, new Random(1));

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_calendar_of_the_game_loads_as_the_menu_shows_it_and_stays_the_game_s()
    {
        var weekend = Sweden("SwedenWeekendMedium");
        var calendar = Catalog.CalendarInfoOf("SwedenWeekendMedium")!;

        Assert.Equal("SwedenWeekendMedium", weekend.Calendar);
        Assert.Equal(calendar.Days.Sum(d => d.Count), weekend.Stages.Count);
        Assert.Equal(1, weekend.Stages[^1].Day);
        Assert.Equal("ServiceParkDefault", weekend.Stages[0].Zone);
        Assert.Equal(calendar.Days[0][0].StartSeconds, weekend.Stages[0].Weather.StartTime);
        Assert.Empty(RallyWeekendEdit.Problems(weekend, Catalog));

        var normal = RallyWeekendEdit.Normalize(weekend, Catalog);
        Assert.False(normal.IsCustom);
        Assert.Equal(7, normal.Ai!.Opponents);                // the rules are kept
        Assert.Equal(normal.Stages[0].Weather, normal.NextWeather);
    }

    [Fact]
    public void A_changed_calendar_is_marked_as_changed()
    {
        var weekend = Sweden();
        var stages = weekend.Stages.ToList();
        stages[0] = stages[0] with { Weather = stages[0].Weather with { Weather = 9 } };

        Assert.True(RallyWeekendEdit.Normalize(weekend with { Stages = stages }, Catalog).IsCustom);
    }

    [Fact]
    public void Hardcore_forces_damage_and_the_realistic_penalty()
    {
        var weekend = Sweden() with { HardCore = true, DamagesEnabled = false, GraphicalDamages = false, Penalty = 0 };
        var normal = RallyWeekendEdit.Normalize(weekend, Catalog);
        Assert.True(normal.DamagesEnabled);
        Assert.True(normal.GraphicalDamages);
        Assert.Equal(2, normal.Penalty);

        var free = RallyWeekendEdit.Normalize(weekend with { HardCore = false }, Catalog);
        Assert.False(free.DamagesEnabled);
        Assert.Equal(0, free.Penalty);
    }

    [Fact]
    public void A_calendar_the_game_would_not_take_says_why()
    {
        var weekend = Sweden("SwedenWeekendMedium");
        var stages = weekend.Stages.ToList();

        string Problem(List<RallyWeekendStage> s, RallyWeekend? w = null) =>
            Assert.Single(RallyWeekendEdit.Problems((w ?? weekend) with { Stages = s }, Catalog));

        Assert.Contains("does not start with a service park",
                        Problem([stages[0] with { Zone = RallyWeekendEdit.NoZone }, .. stages[1..]]));
        Assert.Contains("is not in Sweden",
                        Problem([.. stages[..^1], stages[^1] with { Stage = "AlsaceS2MunsterFullForward" }]));
        Assert.Contains("starts before the stage ahead of it",
                        Problem([stages[0], stages[1] with { Weather = stages[1].Weather with { StartTime = 0 } }, .. stages[2..]]));
        Assert.Contains("Day 2 has no stage",
                        Problem([.. stages.Select(s => s.Day == 1 ? s with { Day = 2 } : s)]));
        Assert.Contains("no stage", Assert.Single(RallyWeekendEdit.Problems(weekend with { Stages = [] }, Catalog)));

        var fiveDays = stages.Select((s, i) => s with { Day = Math.Min(i, 4), Zone = "ServiceParkDefault" }).ToList();
        Assert.Contains(RallyWeekendEdit.Problems(weekend with { Stages = fiveDays }, Catalog), p => p.Contains("4 days at most"));

        Assert.Contains("1 to 50 opponents", Assert.Single(RallyWeekendEdit.Problems(
            weekend with { Ai = weekend.Ai! with { Opponents = 51 } }, Catalog)));
        Assert.Contains("start position", Assert.Single(RallyWeekendEdit.Problems(
            weekend with { RunningOrder = new RunningOrderSettings(1, 9, 0.5f) }, Catalog)));
        Assert.Contains("2 to 16 players", Assert.Single(RallyWeekendEdit.Problems(
            Sweden(online: true) with { Lobby = new LobbySettings(17, 0) }, Catalog)));
    }

    [Fact]
    public void Every_value_a_rally_file_may_hold_is_checked_against_the_menus()
    {
        var weekend = Sweden("SwedenWeekendMedium");
        var stage = weekend.Stages[1];
        var x = stage.Weather;

        string Problem(RallyWeekendStage changed) =>
            Assert.Single(RallyWeekendEdit.Problems(weekend with { Stages = [weekend.Stages[0], changed, .. weekend.Stages.Skip(2)] }, Catalog));
        string Weather(WeatherSettings w) => Problem(stage with { Weather = w });

        Assert.Contains("road wetness", Weather(x with { Wetness = 5 }));
        Assert.Contains("road wetness", Weather(x with { Wetness = -0.01f }));
        Assert.Contains("road wetness", Weather(x with { Wetness = float.NaN }));
        Assert.Contains("snow level", Weather(x with { Snow = 1.5f }));
        Assert.Contains("random draws", Weather(x with { TargetRoll = 2 }));
        Assert.Contains("seed", Weather(x with { Seed = -5 }));
        Assert.Contains("not one of the game's values", Weather(x with { Weather = 200 }));
        Assert.Contains("not one of the game's values", Weather(x with { Grip = 99 }));
        Assert.Contains("starts outside the day", Weather(x with { StartTime = float.NaN }));

        Assert.Contains("racecraft", Assert.Single(RallyWeekendEdit.Problems(
            weekend with { Ai = weekend.Ai! with { RaceCraft = 50 } }, Catalog)));
        Assert.Contains("skill", Assert.Single(RallyWeekendEdit.Problems(
            weekend with { Ai = weekend.Ai! with { Skill = float.NaN } }, Catalog)));
        Assert.Contains("not one of the game's values", Assert.Single(RallyWeekendEdit.Problems(
            weekend with { Penalty = 9 }, Catalog)));
    }

    [Theory]
    [InlineData("swedens1_vargasenfullforward")]
    [InlineData("SwedenS1VargåsenFullForward")]
    public void A_stage_id_the_game_would_not_spell_so_is_refused(string id)
    {
        var weekend = Sweden("SwedenWeekendMedium");
        Assert.NotNull(Catalog.Stage(id));   // the catalog reads past the spelling, the game would not
        var stages = weekend.Stages.ToList();
        stages[0] = stages[0] with { Stage = id };
        Assert.Contains("is not known", Assert.Single(RallyWeekendEdit.Problems(weekend with { Stages = stages }, Catalog)));

        stages[0] = weekend.Stages[0] with { Zone = "Service_ParkDefault" };
        Assert.Contains("service park", Assert.Single(RallyWeekendEdit.Problems(weekend with { Stages = stages }, Catalog)));
    }

    [Fact]
    public void A_day_holds_as_many_stages_as_the_game_s_own_calendars_and_no_more()
    {
        // the game's Alsace calendar has 5 stages on its first day, past the menu's 4
        var alsace = Sweden("AlsaceWeekendMedium");
        Assert.Empty(RallyWeekendEdit.Problems(alsace, Catalog));
        int most = RallyWeekendEdit.MostStagesInADay(Catalog);
        Assert.Equal(5, most);

        var day1 = alsace.Stages.Where(s => s.Day == 0).ToList();
        var crowded = alsace with { Stages = [.. day1, day1[^1], .. alsace.Stages.Where(s => s.Day != 0)] };
        Assert.Contains($"{most} at most", Assert.Single(RallyWeekendEdit.Problems(crowded, Catalog)));
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DefaultRally", false)]
    [InlineData("DefaultOnlineRally", true)]
    public void Writes_a_changed_weekend_and_nothing_else(string eventName, bool online)
    {
        byte[] save = SaveBuilder.PlayerData(online: true, onlineRally: true);
        var weekend = RallyWeekendEdit.Normalize(Sweden("SwedenWeekendLong", online), Catalog);

        var write = RallyWeekendEdit.Replace(save, eventName, weekend);
        RallyWeekendEdit.Verify(save, write, weekend);

        var list = EventList.Find(write.Data);
        var back = RallyWeekend.Find(write.Data, list.Events.Single(e => e.Name == eventName).Start)!;
        Assert.Equal(weekend.Write(), back.Write());
        Assert.Equal(write.NewSizeField - write.OldSizeField, write.Data.Length - save.Length);

        // the other weekend, and the selection past the list, are untouched
        string other = online ? "DefaultRally" : "DefaultOnlineRally";
        var before = EventList.Find(save);
        Assert.Equal(save[before.Events.Single(e => e.Name == other).Start..before.Events.Single(e => e.Name == other).End],
                     write.Data[list.Events.Single(e => e.Name == other).Start..list.Events.Single(e => e.Name == other).End]);
        Assert.Equal(save[before.EventsEnd..], write.Data[list.EventsEnd..]);
    }

    [Fact]
    public void Writing_the_weekend_as_it_was_gives_back_the_same_file()
    {
        byte[] save = SaveBuilder.PlayerData(onlineRally: true);
        var rally = EventList.Find(save).Events.Single(e => e.Name == "DefaultRally");
        var weekend = RallyWeekend.Find(save, rally.Start)!;

        var write = RallyWeekendEdit.Replace(save, "DefaultRally", weekend);
        Assert.Equal(save, write.Data);
    }

    [Fact]
    public void Refuses_a_rally_under_way_and_a_weekend_of_the_other_kind()
    {
        byte[] busy = SaveBuilder.PlayerData(rallyInProgress: true);
        var ex = Assert.Throws<InvalidDataException>(() => RallyWeekendEdit.Replace(busy, "DefaultRally", Sweden()));
        Assert.Contains("under way", ex.Message);

        byte[] save = SaveBuilder.PlayerData(onlineRally: true);
        Assert.Throws<InvalidDataException>(() => RallyWeekendEdit.Replace(save, "DefaultRally", Sweden(online: true)));
        Assert.Throws<InvalidDataException>(() => RallyWeekendEdit.Replace(save, "DefaultOnlineRally", Sweden()));
        Assert.Throws<InvalidDataException>(() => RallyWeekendEdit.Replace(SaveBuilder.PlayerData(), "DefaultOnlineRally", Sweden(online: true)));
    }

    [Fact]
    public void Verify_catches_a_write_that_went_wrong()
    {
        byte[] save = SaveBuilder.PlayerData(onlineRally: true);
        var weekend = RallyWeekendEdit.Normalize(Sweden(), Catalog);
        var write = RallyWeekendEdit.Replace(save, "DefaultRally", weekend);

        byte[] tampered = (byte[])write.Data.Clone();
        tampered[^3] ^= 0xFF;                                   // past the list: the profile
        Assert.Throws<InvalidDataException>(() => RallyWeekendEdit.Verify(save, write with { Data = tampered }, weekend));

        var other = weekend with { Ai = weekend.Ai! with { Opponents = 12 } };
        Assert.Throws<InvalidDataException>(() => RallyWeekendEdit.Verify(save, write, other));
    }
}
