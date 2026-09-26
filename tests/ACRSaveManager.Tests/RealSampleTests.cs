using System.IO;
using ACRSaveManager.Services;

namespace ACRSaveManager.Tests;

/// <summary>
/// The same checks against real saves, which carry player names and so never
/// leave the machine they are on. Point ACRSM_SAMPLES at a folder of them —
/// CarSetupsDataSaveSlot*.sav, PlayerDataSaveSlot*.sav / .bak, *.setup — or
/// these tests are reported as skipped. The files are only read.
/// </summary>
public class RealSampleTests
{
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
            Assert.NotEmpty(SaveFileParser.ReadRallyName(d, rally.Start));
        }
    }

    [SamplesFact]
    public void Every_player_save_takes_the_event_list_of_every_other()
    {
        var saves = PlayerSaves.Select(File.ReadAllBytes).ToList();
        Assert.NotEmpty(saves);
        foreach (byte[] target in saves)
        {
            foreach (byte[] source in saves)
            {
                var from = EventList.Find(source);
                Assert.NotNull(from.Selection);
                byte[] file = source[from.Start..from.End];

                string[] names = [.. from.Events.Select(e => e.Name)];
                var write = EventList.Replace(target, file, names, selection: true);
                EventList.VerifyIntegrity(target, write, file);
                if (ReferenceEquals(source, target)) Assert.Equal(target, write.Data);
            }
        }
    }
}
