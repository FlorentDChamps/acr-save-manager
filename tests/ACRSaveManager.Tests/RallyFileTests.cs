using System.IO;
using System.Text.Json.Nodes;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>A rally kept apart from the save: offline and online in one, its JSON file, the library, and its writing.</summary>
public class RallyFileTests
{
    private static readonly GameCatalog Catalog = GameCatalog.Default;

    private static Rally Sample(string name = "Night in Testland") =>
        Rally.From(name, SaveBuilder.OfflineWeekend(), SaveBuilder.OnlineWeekend());

    // ── Offline and online in one ─────────────────────────────────────────────

    [Fact]
    public void A_rally_from_an_offline_weekend_takes_its_lobby_from_the_online_one()
    {
        var rally = Sample();
        Assert.Equal(SaveBuilder.OfflineWeekend().Ai, rally.Weekend.Ai);
        Assert.Equal(SaveBuilder.OnlineWeekend().Lobby, rally.Lobby);
        Assert.Null(rally.Weekend.Lobby);

        Assert.Equal(Rally.DefaultLobby, Rally.From("x", SaveBuilder.OfflineWeekend(), null).Lobby);
    }

    [Fact]
    public void A_rally_from_an_online_weekend_takes_its_opponents_from_the_offline_one()
    {
        var rally = Rally.From("x", SaveBuilder.OnlineWeekend(), SaveBuilder.OfflineWeekend());
        Assert.Equal(SaveBuilder.OfflineWeekend().Ai, rally.Weekend.Ai);
        Assert.Equal(SaveBuilder.OfflineWeekend().RunningOrder, rally.Weekend.RunningOrder);
        Assert.Equal(SaveBuilder.OnlineWeekend().Lobby, rally.Lobby);
        Assert.True(rally.Weekend.HardCore);                  // the rules are the online weekend's

        var alone = Rally.From("x", SaveBuilder.OnlineWeekend(), null);
        Assert.Equal(Rally.DefaultAi, alone.Weekend.Ai);
        Assert.Equal(Rally.DefaultRunningOrder, alone.Weekend.RunningOrder);
    }

    [Fact]
    public void A_new_rally_is_one_of_the_game_s_calendars_the_game_takes()
    {
        var rally = Rally.New("New rally", Catalog, new Random(1));
        Assert.NotEmpty(rally.Weekend.Stages);
        Assert.Empty(rally.Problems(Catalog));
        Assert.False(rally.For(SaveBuilder.OfflineWeekend(), Catalog).IsCustom);
        Assert.Equal(rally.Weekend.Calendar, RallyFile.Parse(RallyFile.ToJson(rally, Catalog), Catalog).Weekend.Calendar);
    }

    [Fact]
    public void Written_into_a_weekend_the_rally_takes_its_layout_and_keeps_what_no_menu_shows()
    {
        var slot = SaveBuilder.OnlineWeekend(seed: 3) with { TyresConsumption = false, CustomName = "Mine" };
        var online = Sample().For(slot, Catalog);

        Assert.True(online.Online);
        Assert.Null(online.Ai);
        Assert.Null(online.RunningOrder);
        Assert.Equal(SaveBuilder.OnlineWeekend().Lobby, online.Lobby);
        Assert.False(online.TyresConsumption);
        Assert.Equal("Mine", online.CustomName);
        Assert.Equal(slot.CalendarInstanceId, online.CalendarInstanceId);
        Assert.Equal(SaveBuilder.OfflineWeekend().Stages.Select(s => s.Stage), online.Stages.Select(s => s.Stage));
        Assert.Equal(online.Stages[0].Weather, online.NextWeather);

        var offline = Sample().For(SaveBuilder.OfflineWeekend(seed: 3), Catalog);
        Assert.False(offline.Online);
        Assert.Equal(7, offline.Ai!.Opponents);
        Assert.Equal(offline.Write(), RallyWeekend.Read(offline.Write()).Write());
    }

    // ── The file ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_file_reads_back_to_the_same_rally()
    {
        var rally = Sample();
        string json = RallyFile.ToJson(rally, Catalog);
        var back = RallyFile.Parse(json, Catalog);

        Assert.Equal(json, RallyFile.ToJson(back, Catalog));
        Assert.Equal(rally.Name, back.Name);
        Assert.Equal(rally.Lobby, back.Lobby);
        Assert.Equal(rally.Weekend.Ai! with { CustomSlots = [] }, back.Weekend.Ai! with { CustomSlots = [] });
        Assert.Equal(rally.Weekend.RunningOrder, back.Weekend.RunningOrder);
        Assert.Equal(rally.Weekend.Stages.Select(s => (s.Day, s.Stage, s.Weather, s.Zone)),
                     back.Weekend.Stages.Select(s => (s.Day, s.Stage, s.Weather, s.Zone)));
        Assert.Equal((rally.Weekend.Collisions, rally.Weekend.Wear, rally.Weekend.MechanicalFailures, rally.Weekend.Penalty),
                     (back.Weekend.Collisions, back.Weekend.Wear, back.Weekend.MechanicalFailures, back.Weekend.Penalty));
    }

    [Fact]
    public void The_file_names_values_as_the_game_does()
    {
        var root = JsonNode.Parse(RallyFile.ToJson(Sample(), Catalog))!;

        Assert.Equal("acr-rally", (string)root["format"]!);
        Assert.Equal("Light", (string)root["rules"]!["collisions"]!);
        Assert.Equal("Realistic", (string)root["rules"]!["mechanicalFailures"]!);
        Assert.Equal("Realistic", (string)root["rules"]!["respawn"]!);
        Assert.Equal(0.9, (double)root["offline"]!["skill"]!);
        Assert.Equal("Custom", (string)root["offline"]!["runningOrder"]!);
        Assert.Equal("SameCar", (string)root["online"]!["cars"]!);

        var first = root["days"]![0]!["stages"]![0]!;
        Assert.Equal(SaveBuilder.RallyStage, (string)first["stage"]!);
        Assert.Equal("ServiceParkDefault", (string)first["servicePark"]!);
        Assert.Equal("08:30", (string)first["start"]!);
        Assert.Equal("WT_LIGHT_FOG", (string)first["weather"]!["preset"]!);
        Assert.Null(root["days"]![1]!["stages"]![0]!["servicePark"]);   // no service park before it
    }

    [Fact]
    public void A_number_stands_for_a_value_the_catalog_does_not_name()
    {
        var root = JsonNode.Parse(RallyFile.ToJson(Sample(), Catalog))!;
        root["rules"]!["collisions"] = 3;
        root["rules"]!["wear"] = "severe";                    // ids, case aside

        var rally = RallyFile.Parse(root.ToJsonString(), Catalog);
        Assert.Equal(3, rally.Weekend.Collisions);
        Assert.Equal(2, rally.Weekend.Wear);
    }

    [Theory]
    [InlineData("rules", "collisions", "\"Hard\"", "rules.collisions")]
    [InlineData("offline", "opponents", "\"seven\"", "offline.opponents")]
    [InlineData("online", "maxPlayers", null, "online.maxPlayers is missing")]
    public void A_field_that_does_not_read_is_named(string group, string field, string? value, string expected)
    {
        var root = JsonNode.Parse(RallyFile.ToJson(Sample(), Catalog))!.AsObject();
        if (value is null) root[group]!.AsObject().Remove(field);
        else root[group]![field] = JsonNode.Parse(value);

        var ex = Assert.Throws<InvalidDataException>(() => RallyFile.Parse(root.ToJsonString(), Catalog));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_stage_start_that_is_no_time_of_day_is_named()
    {
        var root = JsonNode.Parse(RallyFile.ToJson(Sample(), Catalog))!;
        root["days"]![1]!["stages"]![0]!["start"] = "25:00";
        var ex = Assert.Throws<InvalidDataException>(() => RallyFile.Parse(root.ToJsonString(), Catalog));
        Assert.Contains("days[1].stages[0].start", ex.Message);
    }

    [Fact]
    public void Refuses_what_is_not_a_rally_file_and_a_newer_format()
    {
        Assert.Throws<InvalidDataException>(() => RallyFile.Parse("{ \"name\": \"x\" }", Catalog));
        Assert.Throws<InvalidDataException>(() => RallyFile.Parse("not json", Catalog));

        var root = JsonNode.Parse(RallyFile.ToJson(Sample(), Catalog))!;
        root["version"] = RallyFile.Version + 1;
        var ex = Assert.Throws<InvalidDataException>(() => RallyFile.Parse(root.ToJsonString(), Catalog));
        Assert.Contains("newer", ex.Message);
    }

    // ── The library ───────────────────────────────────────────────────────────

    [Fact]
    public void The_library_names_files_after_their_rally_and_renames_them_with_it()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rallies-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(RallyLibrary.List(folder, Catalog));

            string first = RallyLibrary.Save(folder, Sample("Sweden: night?"), Catalog);
            Assert.Equal("Sweden_ night_.json", Path.GetFileName(first));
            Assert.Equal("Sweden: night? (2)", RallyLibrary.FreeName(folder, "Sweden: night?"));

            string renamed = RallyLibrary.Save(folder, Sample("Arctic"), Catalog, replacing: first);
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(renamed));

            File.WriteAllText(Path.Combine(folder, "broken.json"), "{}");
            var entries = RallyLibrary.List(folder, Catalog);
            Assert.Equal(["Arctic", "broken"], entries.Select(e => e.Name));
            Assert.NotNull(entries[0].Rally);
            Assert.NotNull(entries[1].Error);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_field_given_twice_is_refused_as_a_file_that_does_not_read()
    {
        string json = RallyFile.ToJson(Sample(), Catalog).Replace("\"name\":", "\"name\": \"Twice\",\n  \"name\":", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => RallyFile.Parse(json, Catalog));

        string folder = Path.Combine(Path.GetTempPath(), "rallies-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "twice.json"), json);
            var entry = Assert.Single(RallyLibrary.List(folder, Catalog));
            Assert.Null(entry.Rally);
            Assert.NotNull(entry.Error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Any_float_of_the_save_goes_through_the_file_and_back()
    {
        var rally = Sample();
        var ai = rally.Weekend.Ai!;
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e30f, 0.79999995f })
        {
            var odd = rally with { Weekend = rally.Weekend with { Ai = ai with { RaceCraft = value } } };
            string json = RallyFile.ToJson(odd, Catalog);
            var back = RallyFile.Parse(json, Catalog);
            Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(back.Weekend.Ai!.RaceCraft));
            Assert.Equal(json, RallyFile.ToJson(back, Catalog));
        }
    }

    [Fact]
    public void A_start_at_the_very_end_of_the_day_stays_in_it()
    {
        Assert.Equal("23:59:59", RallyFile.Clock(86399.7f));
        Assert.Equal("08:30", RallyFile.Clock(30600));
    }

    [Fact]
    public void The_library_keeps_off_the_names_windows_keeps_and_counts_a_rally_s_own_file_as_free()
    {
        Assert.Equal("_CON.json", Path.GetFileName(RallyLibrary.PathFor("lib", "CON")));
        Assert.Equal("_nul.old.json", Path.GetFileName(RallyLibrary.PathFor("lib", "nul.old")));
        Assert.Equal("_Com1.json", Path.GetFileName(RallyLibrary.PathFor("lib", "Com1 ")));
        Assert.Equal("CONSOLE.json", Path.GetFileName(RallyLibrary.PathFor("lib", "CONSOLE")));

        string folder = Path.Combine(Path.GetTempPath(), "rallies-" + Guid.NewGuid().ToString("N"));
        try
        {
            RallyLibrary.Save(folder, Sample("Sweden"), Catalog);
            string own = RallyLibrary.Save(folder, Sample("Sweden (2)"), Catalog);
            Assert.Equal("Sweden (3)", RallyLibrary.FreeName(folder, "Sweden"));
            Assert.Equal("Sweden (2)", RallyLibrary.FreeName(folder, "Sweden", own));

            var removed = new List<string>();
            string renamed = RallyLibrary.Save(folder, Sample("Arctic"), Catalog, replacing: own, remove: removed.Add);
            Assert.Equal([own], removed);
            Assert.True(File.Exists(own));   // what to do with it was the caller's
            Assert.True(File.Exists(renamed));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ── Writing into the save ─────────────────────────────────────────────────

    [Fact]
    public void Writes_one_rally_into_both_weekends_at_once()
    {
        byte[] save = SaveBuilder.PlayerData(online: true, onlineRally: true);
        var rally = RallyFile.Parse(RallyFile.ToJson(Sample(), Catalog), Catalog);

        var write = RallyWeekendEdit.Replace(save, rally, ["DefaultRally", "DefaultOnlineRally"], Catalog);
        RallyWeekendEdit.Verify(write);
        Assert.Equal(2, write.Steps.Count);

        var list = EventList.Find(write.Data);
        var offline = RallyWeekend.Find(write.Data, list.Events.Single(e => e.Name == "DefaultRally").Start)!;
        var online = RallyWeekend.Find(write.Data, list.Events.Single(e => e.Name == "DefaultOnlineRally").Start)!;
        Assert.False(offline.Online);
        Assert.True(online.Online);
        Assert.Equal(rally.Weekend.Stages.Select(s => s.Stage), offline.Stages.Select(s => s.Stage));
        Assert.Equal(rally.Weekend.Stages.Select(s => s.Stage), online.Stages.Select(s => s.Stage));
        Assert.Equal(rally.Lobby, online.Lobby);
        Assert.Equal(SaveBuilder.OnlineWeekend().CalendarInstanceId, online.CalendarInstanceId);

        // the selection and what follows the list are untouched
        var before = EventList.Find(save);
        Assert.Equal(save[before.EventsEnd..], write.Data[list.EventsEnd..]);
    }

    [Fact]
    public void Writing_into_a_weekend_the_save_does_not_hold_is_refused()
    {
        byte[] save = SaveBuilder.PlayerData();
        var ex = Assert.Throws<InvalidDataException>(
            () => RallyWeekendEdit.Replace(save, Sample(), ["DefaultRally", "DefaultOnlineRally"], Catalog));
        Assert.Contains("DefaultOnlineRally", ex.Message);
        Assert.Throws<InvalidDataException>(() => RallyWeekendEdit.Replace(save, Sample(), [], Catalog));
    }
}
