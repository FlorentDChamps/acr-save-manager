using System.IO;
using ACRSaveManager.Services;

namespace ACRSaveManager.Tests;

/// <summary>
/// The same checks against real saves, which carry player names and so never
/// leave the machine they are on. Point ACRSM_SAMPLES at a folder of them —
/// CarSetupsDataSaveSlot*.sav, PlayerDataSaveSlot*.sav / .bak, *.setup — or
/// these tests are reported as skipped. The files are only read.
/// </summary>
public partial class RealSampleTests
{
    /// <summary>What changes from one value of the selection to another: numbers, the game's enumerators, flags.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("[0-9.]+|WT_[A-Z_]+|True|False")]
    private static partial System.Text.RegularExpressions.Regex Values();

    private static readonly string? Dir =
        Environment.GetEnvironmentVariable("ACRSM_SAMPLES") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class SamplesFactAttribute : FactAttribute
    {
        public SamplesFactAttribute()
        {
            if (Dir is null) Skip = "ACRSM_SAMPLES is not set to a folder of real saves.";
        }
    }

    private static IEnumerable<string> Files(params string[] patterns) =>
        patterns.SelectMany(p => Directory.GetFiles(Dir!, p)).Distinct().OrderBy(f => f);

    private static IEnumerable<string> SetupSaves => Files("CarSetups*.sav");
    private static IEnumerable<string> PlayerSaves => Files("PlayerData*.sav", "PlayerData*.bak");

    [SamplesFact]
    public void Every_setups_save_parses_and_verifies()
    {
        Assert.NotEmpty(SetupSaves);
        foreach (string f in SetupSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            CarSetupsParser.VerifyIntegrity(d, CarSetupsParser.ParseSetups(d).Count);
        }
    }

    [SamplesFact]
    public void Every_real_entry_survives_a_remove_and_reinsert()
    {
        foreach (string f in SetupSaves)
        {
            byte[] d0 = File.ReadAllBytes(f);
            var l0 = CarSetupsParser.ParseSetups(d0);
            foreach (var e in l0)
            {
                byte[] raw = CarSetupsParser.ExtractEntry(d0, e);
                CarSetupsParser.ValidateEntry(raw);
                byte[] d1 = CarSetupsParser.RemoveEntry(d0, e);
                CarSetupsParser.VerifyIntegrity(d1, l0.Count - 1);
                byte[] d2 = CarSetupsParser.InsertEntry(d1, raw);
                CarSetupsParser.VerifyIntegrity(d2, l0.Count);
                Assert.Equal(d0.Length, d2.Length);
            }
        }
    }

    [SamplesFact]
    public void Every_real_save_empties_down_to_nothing()
    {
        foreach (string f in SetupSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            for (var list = CarSetupsParser.ParseSetups(d); list.Count > 0; list = CarSetupsParser.ParseSetups(d))
            {
                d = CarSetupsParser.RemoveEntry(d, list[0]);
                CarSetupsParser.VerifyIntegrity(d, list.Count - 1);
            }
        }
    }

    [SamplesFact]
    public void Every_real_setup_file_validates_and_damaged_copies_do_not()
    {
        var rnd = new Random(1);
        foreach (string f in Files("*.setup"))
        {
            byte[] s = File.ReadAllBytes(f);
            CarSetupsParser.ValidateEntry(s);
            for (int n = 1; n <= 64; n++)
            {
                Assert.Throws<InvalidDataException>(() => CarSetupsParser.ValidateEntry(s[..^n]));
                byte[] tail = new byte[n];
                rnd.NextBytes(tail);
                Assert.Throws<InvalidDataException>(() => CarSetupsParser.ValidateEntry([.. s, .. tail]));
            }
        }
    }

    [SamplesFact]
    public void Every_player_save_exports_and_names_its_rally()
    {
        Assert.NotEmpty(PlayerSaves);
        foreach (string f in PlayerSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            var doc = PlayerSaveJson.Export(d, f);
            Assert.DoesNotContain("_error", doc.ToJsonString());
            Assert.NotEqual("(unknown)", doc["weather_settings"]!["stage"]!.GetValue<string>());

            var rally = EventList.Find(d).Events.Single(e => e.Name == "DefaultRally");
            Assert.NotEmpty(RallyWeekend.Find(d, rally.Start)!.Calendar);
        }
    }

    [SamplesFact]
    public void Every_rally_weekend_reads_and_writes_back_to_the_same_bytes()
    {
        foreach (string f in PlayerSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            foreach (var ev in EventList.Find(d).Events)
            {
                int pos = ev.Start;
                Assert.True(EventConfig.TryRead(d, ref pos, out _, out var components, out _));
                foreach (var c in components.Where(c => c.Name == RallyWeekend.ComponentName))
                {
                    byte[] data = d[c.DataStart..(c.DataStart + c.DataSize)];
                    var weekend = RallyWeekend.Read(data);
                    Assert.Equal(ev.Name.Contains("Online", StringComparison.Ordinal), weekend.Online);
                    Assert.Equal(data, weekend.Write());
                }
            }
        }
    }

    [SamplesFact]
    public void Every_rally_weekend_takes_a_calendar_of_the_game_and_reads_it_back()
    {
        var catalog = GameCatalog.Default;
        foreach (string f in PlayerSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            foreach (var ev in EventList.Find(d).Events.Where(e => !e.RallyInProgress))
            {
                if (RallyWeekend.Find(d, ev.Start) is not { } weekend) continue;
                var calendar = catalog.CalendarsIn(catalog.Stage(weekend.Stages[0].Stage)!.Location!)[^1];
                var changed = RallyWeekendEdit.Normalize(
                    RallyWeekendEdit.FromCalendar(weekend with { HardCore = !weekend.HardCore }, calendar, new Random(2)), catalog);
                Assert.Empty(RallyWeekendEdit.Problems(changed, catalog));

                var write = RallyWeekendEdit.Replace(d, ev.Name, changed);
                RallyWeekendEdit.Verify(d, write, changed);
                var back = RallyWeekend.Find(write.Data, EventList.Find(write.Data).Events.Single(e => e.Name == ev.Name).Start)!;
                Assert.Equal(calendar.Id, back.Calendar);
                Assert.Equal(changed.Write(), back.Write());
                Assert.Equal(weekend.CalendarInstanceId, back.CalendarInstanceId);
            }
        }
    }

    [SamplesFact]
    public void Every_rally_weekend_goes_through_a_library_file_and_into_both_weekends()
    {
        var catalog = GameCatalog.Default;
        foreach (string f in PlayerSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            var weekends = EventList.Find(d).Events.Where(e => !e.RallyInProgress)
                .Select(e => (e.Name, Weekend: RallyWeekend.Find(d, e.Start)))
                .Where(x => x.Weekend is not null).ToList();
            foreach (var (name, weekend) in weekends)
            {
                var other = weekends.FirstOrDefault(x => x.Name != name).Weekend;
                var rally = Rally.From("Sample", weekend!, other);
                string json = RallyFile.ToJson(rally, catalog);
                var back = RallyFile.Parse(json, catalog);
                Assert.Equal(json, RallyFile.ToJson(back, catalog));
                Assert.Empty(back.Problems(catalog));

                string[] targets = [.. weekends.Select(x => x.Name)];
                var write = RallyWeekendEdit.Replace(d, back, targets, catalog);
                RallyWeekendEdit.Verify(write);

                // written back where it came from, the menus' settings are the save's own
                var list = EventList.Find(write.Data);
                var written = RallyWeekend.Find(write.Data, list.Events.Single(e => e.Name == name).Start)!;
                Assert.Equal(weekend!.Stages.Select(s => (s.Day, s.Stage, s.Weather, s.Zone)),
                             written.Stages.Select(s => (s.Day, s.Stage, s.Weather, s.Zone)));
                Assert.Equal(weekend.Ai, written.Ai is null ? null : written.Ai with { CustomSlots = weekend.Ai!.CustomSlots });
                Assert.Equal(weekend.Lobby, written.Lobby);
                Assert.Equal(weekend.CalendarInstanceId, written.CalendarInstanceId);
            }
        }
    }

    [SamplesFact]
    public void Every_player_save_takes_a_rally_s_first_stage_in_its_single_stage_modes()
    {
        var catalog = GameCatalog.Default;
        foreach (string f in PlayerSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            var list = EventList.Find(d);
            var rallyEvent = list.Events.FirstOrDefault(e => e.Name == "DefaultRally" && !e.RallyInProgress);
            if (rallyEvent.Name is null || list.Selection is null) continue;

            var rally = Rally.From("Sample", RallyWeekend.Find(d, rallyEvent.Start)!, null);
            var write = SingleStageEdit.Replace(d, rally, catalog);
            SingleStageEdit.Verify(d, write, rally);

            // the options keep the save's own order and the game's own spelling
            var before = list.Selection;
            var after = EventList.Find(write.Data).Selection!;
            Assert.Equal(before.Options.Select(o => o.Path), after.Options.Select(o => o.Path).Take(before.Options.Count));
            foreach (var (path, value) in after.Options)
            {
                string old = before.Options.FirstOrDefault(o => o.Path == path).Value ?? value;
                Assert.Equal(Values().Replace(old, "#"), Values().Replace(value, "#"));
            }
        }
    }

    [SamplesFact]
    public void Every_player_save_lists_its_cars_and_takes_another()
    {
        var catalog = GameCatalog.Default;
        foreach (string f in PlayerSaves)
        {
            byte[] d = File.ReadAllBytes(f);
            var cars = CarSelection.Recorded(d);
            Assert.NotEmpty(cars);
            Assert.All(cars, c => Assert.NotNull(catalog.Car(c)));
            if (EventList.Find(d).Selection?.Car is null) continue;

            string other = cars.First(c => c != EventList.Find(d).Selection?.Car);
            var write = CarSelection.Replace(d, other);
            CarSelection.Verify(d, write);
            Assert.Equal(cars, CarSelection.Recorded(write.Data));
        }
    }
}
