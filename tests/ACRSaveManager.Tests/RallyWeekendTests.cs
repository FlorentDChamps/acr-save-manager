using System.IO;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>The Rally Weekend settings, offline and online (docs §3.3), and how they read in the menus' words.</summary>
public class RallyWeekendTests
{
    /// <summary>
    /// The offline layout written by hand, field by field, as the docs give it:
    /// what the reader is checked against, so that it and the writer cannot
    /// agree on a mistake.
    /// </summary>
    private static byte[] OfflineByHand()
    {
        var w = new Writer();
        w.Int32(1).Byte(2).Byte(2).Int32(0)            // +0x00 tyres consumption, tyres damage, respawn, fuel
         .Int32(1).Int32(0)                            // +0x0A damage, +0x0E graphical damage
         .Byte(1).Byte(2).Byte(3);                     // +0x12 collisions, wear, failures
        Weather(w, 3, 30600);                          // +0x15 the stage to be driven next
        w.Int32(0)                                     // +0x3F hardcore
         .Int32(7).Float(0.9f).Byte(3).Byte(1).Float(0.1f)   // +0x43 opponents, skill, consistency, spread, racecraft
         .Byte(1).Int32(0)                             // +0x51 AI cars, no custom slots
         .Byte(1)                                      // +0x56 penalty
         .Byte(1).Int32(5).Float(0.5f)                 // +0x57 running order: custom, 5th, its roll
         .FString(SaveBuilder.RallyName)               // +0x60 calendar
         .Int32(1)
         .Int32(0).FString(SaveBuilder.RallyStage);
        Weather(w, 3, 30600);
        Weather(w, 0, 28800);
        w.FString("ServiceParkDefault")
         .FString("").Int32(1).Raw([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);
        return w.ToArray();
    }

    private static void Weather(Writer w, byte weather, float start) =>
        w.Byte(weather).Float(0.25f).Int32(0)          // starting weather, its roll, random
         .Byte(7).Float(0.75f).Int32(0)                // forecast, its roll, random
         .Byte(2).Byte(3).Int32(1)                     // probability, persistency, dynamic
         .Float(start).Int32(4242).Float(0.2f).Float(0.6f)   // start time, seed, wetness, snow
         .Byte(4).Byte(1);                             // time speed, grip

    [Fact]
    public void Reads_every_field_of_an_offline_weekend_where_the_docs_put_it()
    {
        byte[] data = OfflineByHand();
        Assert.Equal(SaveBuilder.RallyName.Length + 1, BitConverter.ToInt32(data, 0x60));   // the calendar at +0x60

        var w = RallyWeekend.Read(data);

        Assert.False(w.Online);
        Assert.True(w.TyresConsumption);
        Assert.Equal(2, w.ManualRespawn);
        Assert.True(w.DamagesEnabled);
        Assert.False(w.GraphicalDamages);
        Assert.Equal((1, 2, 3), (w.Collisions, w.Wear, w.MechanicalFailures));
        Assert.Equal(3, w.NextWeather.Weather);
        Assert.Equal(7, w.NextWeather.TargetWeather);
        Assert.True(w.NextWeather.DynamicWeather);
        Assert.Equal(30600f, w.NextWeather.StartTime);
        Assert.Equal(0.2f, w.NextWeather.Wetness);
        Assert.Equal(0.6f, w.NextWeather.Snow);
        Assert.False(w.HardCore);
        Assert.Equal(7, w.Ai!.Opponents);
        Assert.Equal(0.9f, w.Ai.Skill);
        Assert.Equal(1, w.Penalty);
        Assert.Equal((byte)1, w.RunningOrder!.Type);
        Assert.Equal(5, w.RunningOrder.Position);
        Assert.Equal(SaveBuilder.RallyName, w.Calendar);
        var stage = Assert.Single(w.Stages);
        Assert.Equal(SaveBuilder.RallyStage, stage.Stage);
        Assert.Equal(0, stage.DefaultWeather.Weather);
        Assert.Equal("ServiceParkDefault", stage.Zone);
        Assert.True(w.IsCustom);

        Assert.Equal(data, w.Write());
    }

    [Fact]
    public void Reads_an_online_weekend_with_its_lobby_in_place_of_the_opponents()
    {
        byte[] data = SaveBuilder.OnlineWeekend().Write();

        // past the hardcore flag: penalty, max players, cars available, then the calendar
        Assert.Equal(2, data[0x43]);
        Assert.Equal(16, BitConverter.ToInt32(data, 0x44));
        Assert.Equal(2, data[0x48]);
        Assert.Equal("TestWeekendMedium".Length + 1, BitConverter.ToInt32(data, 0x49));

        var w = RallyWeekend.Read(data);
        Assert.True(w.Online);
        Assert.Null(w.Ai);
        Assert.Null(w.RunningOrder);
        Assert.Equal(new LobbySettings(16, 2), w.Lobby);
        Assert.Equal(2, w.Penalty);
        Assert.True(w.HardCore);
        Assert.Equal("TestWeekendMedium", w.Calendar);
        Assert.Equal(2, w.Stages.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Writing_what_was_read_gives_the_same_bytes(bool online)
    {
        byte[] data = (online ? SaveBuilder.OnlineWeekend(3) : SaveBuilder.OfflineWeekend(3)).Write();
        Assert.Equal(data, RallyWeekend.Read(data).Write());
    }

    [Fact]
    public void A_name_typed_with_accents_survives_the_round_trip()
    {
        var weekend = SaveBuilder.OfflineWeekend() with { CustomName = "Rallye d'Été" };
        byte[] data = weekend.Write();
        Assert.Equal("Rallye d'Été", RallyWeekend.Read(data).CustomName);
        Assert.Equal(data, RallyWeekend.Read(data).Write());
    }

    [Fact]
    public void Custom_opponent_slots_shift_the_calendar_and_still_read()
    {
        var weekend = SaveBuilder.OfflineWeekend() with { Ai = new AiSettings(7, 0.9f, 3, 1, 0.1f, 3, [0, 1, 2]) };
        var back = RallyWeekend.Read(weekend.Write());
        Assert.Equal(new byte[] { 0, 1, 2 }, back.Ai!.CustomSlots);
        Assert.Equal(SaveBuilder.RallyName, back.Calendar);
    }

    [Fact]
    public void Damaged_data_is_refused_not_misread()
    {
        byte[] data = SaveBuilder.OfflineWeekend().Write();

        for (int cut = 1; cut <= 60; cut++)
            Assert.Throws<InvalidDataException>(() => RallyWeekend.Read(data[..^cut]));
        Assert.Throws<InvalidDataException>(() => RallyWeekend.Read([.. data, 0]));

        byte[] badFlag = (byte[])data.Clone();
        badFlag[0x3F] = 2;                             // hardcore is 0 or 1
        Assert.Throws<InvalidDataException>(() => RallyWeekend.Read(badFlag));
    }

    [Fact]
    public void Finds_both_weekends_in_a_save()
    {
        byte[] save = SaveBuilder.PlayerData(online: true, onlineRally: true);
        var list = EventList.Find(save);

        var offline = RallyWeekend.Find(save, list.Events.Single(e => e.Name == "DefaultRally").Start);
        var online = RallyWeekend.Find(save, list.Events.Single(e => e.Name == "DefaultOnlineRally").Start);
        Assert.False(offline!.Online);
        Assert.True(online!.Online);
        Assert.Equal(SaveBuilder.RallyName, RallyWeekend.Find(save, list.Events.Single(e => e.Name == "DefaultRally").Start)!.Calendar);
        Assert.Equal("TestWeekendMedium", RallyWeekend.Find(save, list.Events.Single(e => e.Name == "DefaultOnlineRally").Start)!.Calendar);

        Assert.Null(RallyWeekend.Find(save, list.Events.Single(e => e.Name == "DefaultTimeAttack").Start));
    }

    [Fact]
    public void A_rally_in_progress_still_shows_its_settings()
    {
        byte[] save = SaveBuilder.PlayerData(rallyInProgress: true);
        var rally = EventList.Find(save).Events.Single(e => e.Name == "DefaultRally");
        Assert.True(rally.RallyInProgress);
        Assert.Equal(SaveBuilder.RallyName, RallyWeekend.Find(save, rally.Start)!.Calendar);
    }

    // ── The catalog ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("RaceOptions.Respawn.ManualRespawn", 3)]
    [InlineData("RaceOptions.CarDamage.bDamagesEnabled", 2)]
    [InlineData("RaceOptions.CarDamage.bGraphicalDamages", 2)]
    [InlineData("RaceOptions.CarDamage.Collisions", 4)]
    [InlineData("RaceOptions.CarDamage.Wear", 4)]
    [InlineData("RaceOptions.CarDamage.MechanicalFailures", 4)]
    [InlineData("HardCoreOptions.bHardCore", 2)]
    [InlineData("AIOptions.Consistency", 4)]
    [InlineData("AIOptions.Spread", 2)]
    [InlineData("AIOptions.CarClassSelection.Type", 4)]
    [InlineData("RacePenaltyOptions.RacePenalty", 3)]
    [InlineData("RunningOrderOptions.RunningOrderType", 2)]
    [InlineData("LobbyOptions.CarSelectionOption", 3)]
    [InlineData("WeatherOptions.Preset", 11)]
    [InlineData("WeatherOptions.TargetPreset", 11)]
    [InlineData("WeatherOptions.TargetWeatherProbability", 4)]
    [InlineData("WeatherOptions.TimeSpeed", 6)]
    [InlineData("WeatherOptions.StartingGripLevel", 5)]
    public void The_embedded_catalog_names_every_value_of_the_settings_shown(string property, int values)
    {
        var catalog = GameCatalog.Default;
        Assert.NotNull(catalog.OptionName(property));
        for (int v = 0; v < values; v++) Assert.NotNull(catalog.OptionValue(property, v));
    }
}
