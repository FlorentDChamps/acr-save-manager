using System.IO;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>CarSetupsParser against synthetic saves: no real data involved.</summary>
public class CarSetupsTests
{
    private static readonly Setup[] Three =
    [
        new("Alpha", "TestCarA"),
        new("Bravo", "TestCarB", Author: "Someone"),
        new("Charlie", "TestCarB"),
        new("Delta", "TestCarB", Surface: "Gravel"),
        new("Echo", "TestCarC", Parameters: 20),
    ];

    /// <summary>Ticks of 2026-03-04 12:00:00, whatever the machine's time zone.</summary>
    private static readonly long Noon = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Unspecified).Ticks;

    private static byte[] Save() => SaveBuilder.CarSetups(Three);

    private static List<SetupEntry> Parse(byte[] data) => CarSetupsParser.ParseSetups(data);

    // ── reading ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parses_every_entry_with_its_fields()
    {
        byte[] data = Save();
        var list = Parse(data);

        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta", "Echo"], list.Select(e => e.SetupName));
        Assert.Equal(["TestCarA", "TestCarB", "TestCarB", "TestCarB", "TestCarC"], list.Select(e => e.CarName));
        Assert.Equal("Someone", list[1].Author);
        Assert.Equal("", list[0].Author);
        Assert.Equal(DateTimeKind.Unspecified, list[0].Timestamp.Kind);   // a wall clock, no zone
        CarSetupsParser.VerifyIntegrity(data, 5);
    }

    [Fact] // a setup kept for several weathers threw the walk of the whole list off
    public void A_setup_kept_for_several_weathers_and_modes_is_read_with_its_neighbours()
    {
        byte[] data = SaveBuilder.CarSetups(
            new Setup("Alpha", "TestCarA", Parameters: 20,
                      Weathers: ["Clear", "LightRain", "HeavyRain"],
                      Tags: ["TestMode_Tag", "OtherMode_Tag"]),
            new Setup("Bravo", "TestCarA", Weathers: ["Clear"], Tags: ["TestMode_Tag"]),
            new Setup("Charlie", "TestCarB"));
        var list = Parse(data);

        Assert.Equal(["Alpha", "Bravo", "Charlie"], list.Select(e => e.SetupName));
        CarSetupsParser.VerifyIntegrity(data, 3);

        var alpha = CarSetupsParser.ReadEntryData(CarSetupsParser.ExtractEntry(data, list[0]));
        Assert.Equal(["Tarmac"], alpha.Surfaces);
        Assert.Equal(["Clear", "LightRain", "HeavyRain"], alpha.Weathers);
        Assert.Equal(["TestMode_Tag", "OtherMode_Tag"], alpha.Tags);
        Assert.Equal(20, alpha.Parameters.Count);
    }

    [Fact]
    public void Identifies_a_setups_save()
    {
        Assert.Same(SaveKind.CarSetups, SaveKind.Identify(Save()));
        Assert.False(CarSetupsParser.IsSetupsSave("not a save"u8.ToArray()));
    }

    [Fact]
    public void The_stored_time_is_shown_and_exported_as_it_was_written()
    {
        var e = Parse(SaveBuilder.CarSetups(new Setup("Alpha", "TestCarA", Ticks: Noon)))[0];

        // no time zone conversion anywhere: 12:00 in the file is 12:00 everywhere
        Assert.Equal(new DateTime(2026, 3, 4, 12, 0, 0), e.Timestamp);
        Assert.Equal(e.Timestamp, e.DisplayTimestamp);
        var data = CarSetupsParser.ReadEntryData(CarSetupsParser.ExtractEntry(
            SaveBuilder.CarSetups(new Setup("Alpha", "TestCarA", Ticks: Noon)), e));
        Assert.Contains("20260304_120000", CarSetupsParser.BuildExportFileName(data));
    }

    [Fact]
    public void DisplayTimestamp_is_null_for_a_missing_timestamp_and_outside_equality()
    {
        var e = Parse(Save())[0];
        Assert.Null((e with { Timestamp = DateTime.MinValue }).DisplayTimestamp);
        Assert.Equal(e.Timestamp, e.DisplayTimestamp);
        Assert.Equal(e, Parse(Save())[0]);
    }

    // ── writing ────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_rename_insert_remove_round_trips_byte_for_byte()
    {
        byte[] d0 = Save();
        var l0 = Parse(d0);

        byte[] raw = CarSetupsParser.ExtractEntry(d0, l0[2]);
        byte[] d1 = CarSetupsParser.InsertEntry(d0, CarSetupsParser.RenameEntry(raw, "Zulu"));
        CarSetupsParser.VerifyIntegrity(d1, 6);

        var added = Parse(d1).Single(e => e.SetupName == "Zulu");
        byte[] d2 = CarSetupsParser.RemoveEntry(d1, added);
        CarSetupsParser.VerifyIntegrity(d2, 5);
        Assert.Equal(d0, d2);
    }

    [Fact]
    public void Every_entry_survives_a_remove_and_reinsert()
    {
        byte[] d0 = Save();
        var l0 = Parse(d0);
        foreach (var e in l0)
        {
            byte[] raw = CarSetupsParser.ExtractEntry(d0, e);
            byte[] d1 = CarSetupsParser.RemoveEntry(d0, e);
            CarSetupsParser.VerifyIntegrity(d1, l0.Count - 1);
            byte[] d2 = CarSetupsParser.InsertEntry(d1, raw);
            CarSetupsParser.VerifyIntegrity(d2, l0.Count);

            Assert.Equal(d0.Length, d2.Length);
            Assert.Equal(l0.Select(x => (x.CarName, x.SetupName)).Order(),
                         Parse(d2).Select(x => (x.CarName, x.SetupName)).Order());
        }
    }

    [Fact] // bug 2: the last removal used to fail the integrity check
    public void The_last_setup_can_be_removed_and_one_imported_into_the_empty_file()
    {
        byte[] data = Save();
        for (var list = Parse(data); list.Count > 0; list = Parse(data))
        {
            data = CarSetupsParser.RemoveEntry(data, list[0]);
            CarSetupsParser.VerifyIntegrity(data, list.Count - 1);
        }

        byte[] back = CarSetupsParser.InsertEntry(data, SaveBuilder.Entry(new("Fresh", "TestCarZ")));
        CarSetupsParser.VerifyIntegrity(back, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_removal_leaves_every_other_entry_its_own_int32(int removed)
    {
        // TestCarB holds Bravo, Charlie and Delta: give each a value of its own
        byte[] d0 = Save();
        var block = Parse(d0).Where(e => e.CarName == "TestCarB").ToList();
        int[] values = [18, 2, 34];
        for (int i = 0; i < block.Count; i++) BitConverter.GetBytes(values[i]).CopyTo(d0, block[i].EntryStart - 4);

        byte[] d1 = CarSetupsParser.RemoveEntry(d0, block[removed]);

        CarSetupsParser.VerifyIntegrity(d1, 4);
        var left = Parse(d1).Where(e => e.CarName == "TestCarB").ToList();
        Assert.Equal(block.Where((_, i) => i != removed).Select(e => e.SetupName), left.Select(e => e.SetupName));
        Assert.Equal(values.Where((_, i) => i != removed), left.Select(e => CarSetupsParser.ReadEntryPrefix(d1, e)));
    }

    [Fact] // bug 1: offsets from a stale list hit another entry
    public void A_stale_entry_is_refused_instead_of_touching_its_neighbour()
    {
        byte[] d0 = Save();
        var l0 = Parse(d0);
        var bravo = l0.Single(e => e.SetupName == "Bravo");

        // "the game" removes Bravo and adds another setup for the same car
        byte[] d1 = CarSetupsParser.RemoveEntry(d0, bravo);
        d1 = CarSetupsParser.InsertEntry(d1, SaveBuilder.Entry(new("Newcomer", "TestCarB")));

        // Bravo's old offset now starts Charlie
        var ex = Assert.Throws<InvalidDataException>(() => CarSetupsParser.RemoveEntry(d1, bravo));
        Assert.Contains("'Charlie', not 'Bravo'", ex.Message);
        Assert.Throws<InvalidDataException>(() =>
            CarSetupsParser.ReplaceEntry(d1, bravo, CarSetupsParser.RenameEntry(CarSetupsParser.ExtractEntry(d0, bravo), "X")));
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.ExtractEntry(d1, bravo));

        // the list a UI would compare against no longer matches
        Assert.NotEqual(l0, Parse(d1));
    }

    [Fact]
    public void The_same_setup_rewritten_with_another_length_is_refused()
    {
        byte[] d0 = Save();
        var charlie = Parse(d0).Single(e => e.SetupName == "Charlie");

        // "the game" rewrites Charlie with an author: same start, same name, later end
        byte[] d1 = SaveBuilder.CarSetups(
            Three.Select(s => s.Name == "Charlie" ? s with { Author = "SomeoneLonger" } : s).ToArray());
        Assert.Equal(charlie.EntryStart, Parse(d1).Single(e => e.SetupName == "Charlie").EntryStart);

        byte[] renamed = CarSetupsParser.RenameEntry(CarSetupsParser.ExtractEntry(d0, charlie), "X");
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.ExtractEntry(d1, charlie));
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.ReplaceEntry(d1, charlie, renamed));
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.RemoveEntry(d1, charlie));

        // against the file it was listed from, the entry still works
        CarSetupsParser.VerifyIntegrity(CarSetupsParser.ReplaceEntry(d0, charlie, renamed), 5);
        CarSetupsParser.VerifyIntegrity(CarSetupsParser.RemoveEntry(d0, charlie), 4);
    }

    [Fact]
    public void An_offset_past_the_end_is_an_InvalidDataException()
    {
        byte[] data = Save();
        var e = Parse(data)[0] with { EntryStart = data.Length - 2, EntryEnd = data.Length + 40 };
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.ExtractEntry(data, e));
    }

    [Fact] // bug 4: a car id differing only by case created a second block
    public void A_car_id_differing_only_by_case_joins_the_existing_block()
    {
        byte[] d0 = SaveBuilder.CarSetups(
            ("TestCarA", [new("Alpha", "TestCarA")]),
            ("HyundaiI20NRally2", [new("Keep", "HyundaiI20NRally2")]));
        byte[] entry = SaveBuilder.Entry(new("MIN", "Hyundaii20NRally2"));

        byte[] d1 = CarSetupsParser.InsertEntry(d0, entry);
        var l1 = Parse(d1);

        Assert.Equal(2, l1.Select(e => e.CarName).Distinct().Count());
        var added = l1[^1];
        Assert.Equal("HyundaiI20NRally2", added.CarName);              // the block's spelling
        Assert.Equal(entry, d1[added.EntryStart..added.EntryEnd]);       // the entry's own bytes
        CarSetupsParser.VerifyIntegrity(d1, 3);
        Assert.Equal(d0, CarSetupsParser.RemoveEntry(d1, added));
    }

    // ── strings ────────────────────────────────────────────────────────────────

    [Fact] // bug 8: a UTF-16 string walked the cursor backwards
    public void A_non_ascii_author_is_read_as_utf16()
    {
        byte[] data = SaveBuilder.CarSetups(new Setup("Alpha", "TestCarA", Author: "Jérôme"),
                                            new Setup("Bravo", "TestCarA"));
        var list = Parse(data);
        Assert.Equal("Jérôme", list[0].Author);
        Assert.Equal("Bravo", list[1].SetupName);
        CarSetupsParser.VerifyIntegrity(data, 2);
        Assert.Equal("Jérôme", CarSetupsParser.ReadEntryData(CarSetupsParser.ExtractEntry(data, list[0])).Author);
    }

    [Fact]
    public void A_non_ascii_name_is_written_as_utf16_and_read_back()
    {
        byte[] src = SaveBuilder.Entry(new("Alpha", "TestCarA"));
        byte[] ren = CarSetupsParser.RenameEntry(src, "Été");

        Assert.Equal(-4, BitConverter.ToInt32(ren, 5));
        Assert.Equal("C9007400E9000000", Convert.ToHexString(ren, 9, 8));
        Assert.Equal("Été", CarSetupsParser.ReadEntrySetupName(ren));
        CarSetupsParser.ValidateEntry(ren);
        Assert.Equal(src, CarSetupsParser.RenameEntry(ren, "Alpha"));
        Assert.Equal(6, BitConverter.ToInt32(CarSetupsParser.RenameEntry(src, "Plain"), 5));
    }

    [Fact]
    public void An_empty_name_is_written_as_a_bare_zero_length()
    {
        byte[] ren = CarSetupsParser.RenameEntry(SaveBuilder.Entry(new("Alpha", "TestCarA")), "");

        Assert.Equal(SaveBuilder.Entry(new("", "TestCarA")), ren);   // length 0, no terminator
        Assert.Equal("", CarSetupsParser.ReadEntrySetupName(ren));
        CarSetupsParser.ValidateEntry(ren);
    }

    // ── damaged .setup files ───────────────────────────────────────────────────

    public static TheoryData<int> Cuts => [1, 2, 3, 4, 7, 16, 33, 64];

    [Theory, MemberData(nameof(Cuts))]
    public void A_truncated_setup_is_refused_plainly(int cut)
    {
        byte[] entry = SaveBuilder.Entry(new("Alpha", "TestCarA"));
        var ex = Assert.Throws<InvalidDataException>(() => CarSetupsParser.ValidateEntry(entry[..^cut]));
        Assert.Contains("truncated", ex.Message);
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.InsertEntry(Save(), entry[..^cut]));
    }

    [Theory, MemberData(nameof(Cuts))]
    public void A_padded_setup_is_refused_plainly(int extra)
    {
        byte[] entry = SaveBuilder.Entry(new("Alpha", "TestCarA"));
        byte[] padded = [.. entry, .. new byte[extra]];
        var ex = Assert.Throws<InvalidDataException>(() => CarSetupsParser.ValidateEntry(padded));
        Assert.Contains("ends at byte", ex.Message);
    }

    [Fact]
    public void Something_else_than_a_setup_is_refused_plainly()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CarSetupsParser.ValidateEntry(Save()));
        Assert.Contains("not a setup file", ex.Message);
    }

    [Theory]
    [InlineData(4096, 4097)]   // ASCII: the length counts bytes
    [InlineData(4095, -4097)]  // UTF-16: a negative length counting code units
    public void A_string_longer_than_any_setup_holds_is_refused_not_read_as_empty(int chars, int length)
    {
        string name = length > 0 ? new string('x', chars) : "é" + new string('x', chars);
        var setup = new Setup(name, "TestCarA");

        var ex = Assert.Throws<InvalidDataException>(() => CarSetupsParser.ValidateEntry(SaveBuilder.Entry(setup)));
        Assert.Contains($"Implausible string length {length}", ex.Message);
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.ParseSetups(SaveBuilder.CarSetups(setup)));
    }

    [Fact]
    public void A_name_at_the_bound_still_reads()
    {
        var setup = new Setup(new string('x', 4095), "TestCarA");   // length 4096, terminator included

        CarSetupsParser.ValidateEntry(SaveBuilder.Entry(setup));
        Assert.Equal(setup.Name, Assert.Single(CarSetupsParser.ParseSetups(SaveBuilder.CarSetups(setup))).SetupName);
    }

    [Fact]
    public void A_setup_that_names_no_car_is_refused_plainly()
    {
        byte[] entry = SaveBuilder.Entry(new("Alpha", ""));
        var ex = Assert.Throws<InvalidDataException>(() => CarSetupsParser.ValidateEntry(entry));
        Assert.Contains("does not name the car", ex.Message);
        Assert.Throws<InvalidDataException>(() => CarSetupsParser.InsertEntry(Save(), entry));
    }

    // ── export file name ───────────────────────────────────────────────────────

    [Fact]
    public void The_export_name_is_a_valid_file_name_without_the_windows_account()
    {
        var data = new SetupExportData
        {
            Car = "TestCarA",
            Name = "Gravel/Wet: v2*?",
            Track = "TestStage",
            Author = "Someone",
            Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        string name = CarSetupsParser.BuildExportFileName(data);

        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.StartsWith("TestCarA_TestStage_Gravel_Wet_ v2___Someone_", name);
        Assert.DoesNotContain("__", CarSetupsParser.BuildExportFileName(new SetupExportData
        {
            Car = "C",
            Name = "N",
            Track = "T",
            Timestamp = data.Timestamp,
        }));
    }

    [Fact]
    public void The_export_name_names_the_weather_only_when_the_setup_has_one()
    {
        var one = new SetupExportData { Car = "C", Name = "N", Track = "T", Weathers = ["Clear"] };
        var several = new SetupExportData { Car = "C", Name = "N", Track = "T", Weathers = ["Clear", "LightRain"] };

        Assert.StartsWith("C_T_Clear_N_", CarSetupsParser.BuildExportFileName(one));
        Assert.StartsWith("C_T_N_", CarSetupsParser.BuildExportFileName(several));
    }
}
