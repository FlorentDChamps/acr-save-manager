using System.IO;
using System.Text;
using System.Text.Json;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>The JSON companion of a .setup, against synthetic entries.</summary>
public class SetupShareJsonTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 4, 12, 0, 0, 123, TimeSpan.FromHours(2));

    /// <summary>Ticks of 2026-03-04 12:00:00, whatever the machine's time zone.</summary>
    private static readonly long Noon = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Unspecified).Ticks;

    private static readonly Setup Alpha =
        new("Alpha", "TestCarA", Author: "Someone", Track: "TestStageS2North", Surface: "Gravel",
            Ticks: Noon, Weathers: ["Clear", "LightRain"], Tags: ["TestMode_Tag"]);

    private static SetupShareSetup One(SetupShareFile file) => Assert.Single(file.Setups);

    /// <summary>The .json file of <paramref name="setup"/>, as it is written to disk.</summary>
    private static byte[] FileOf(Setup setup, int prefix = 2) =>
        [.. Encoding.UTF8.GetPreamble(),
         .. Encoding.UTF8.GetBytes(SetupShareJson.Serialize(SetupShareJson.Build(prefix, SaveBuilder.Entry(setup), At)))];

    /// <summary>The same file after <paramref name="edit"/> went through its text.</summary>
    private static byte[] Edited(Func<string, string> edit) =>
        Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(FileOf(Alpha)[3..])));

    [Fact]
    public void The_document_describes_the_entry()
    {
        byte[] entry = SaveBuilder.Entry(Alpha);
        var file = SetupShareJson.Build(2, entry, At);

        Assert.Equal("1.0", file.ExportVersion);
        Assert.Equal("Someone", file.ExportedBy);           // the in-game name, from the entry
        Assert.Equal("2026-03-04T12:00:00.1230000+02:00", file.ExportedAt);
        Assert.Equal("AC Rally", file.SourceGame);

        var s = One(file);
        Assert.Equal("TestCarA", s.CarName);
        Assert.Equal("Test Car A", s.FriendlyCarName);
        Assert.Equal("Alpha", s.UserDefinedName);
        Assert.Equal("TestStageS2North", s.StageName);
        Assert.Equal("Test Stage S2 North", s.FriendlyStage);
        Assert.Equal("0.6.0.100866", s.GameVersion);
        Assert.Equal("Gravel", s.Surface);
        Assert.Equal(["Gravel"], s.Surfaces);
        Assert.Equal("TestCarA|Gravel|TestStageS2North", s.UniqueKey);
        Assert.Equal("", s.PlayerName);
        Assert.Equal("", s.FriendlySetupName);
    }

    [Fact]
    public void The_template_is_the_entry_behind_the_int32_the_save_keeps_in_front_of_it()
    {
        byte[] data = SaveBuilder.CarSetups(Alpha, new Setup("Bravo", "TestCarA"));
        var bravo = CarSetupsParser.ParseSetups(data)[1];
        BitConverter.GetBytes(18).CopyTo(data, bravo.EntryStart - 4);   // not always 2 (§4.2)

        byte[] entry = CarSetupsParser.ExtractEntry(data, bravo);
        int prefix = CarSetupsParser.ReadEntryPrefix(data, bravo);
        byte[] template = Convert.FromBase64String(One(SetupShareJson.Build(prefix, entry, At)).TemplateDataBase64);

        Assert.Equal(18, prefix);
        Assert.Equal(18, BitConverter.ToInt32(template, 0));
        Assert.Equal(entry, template[4..]);                 // what the .setup file holds
    }

    [Fact]
    public void Properties_keep_the_order_of_the_entry_and_the_hash_covers_them_sorted()
    {
        var s = One(SetupShareJson.Build(2, SaveBuilder.Entry(Alpha), At));

        Assert.Equal(12, s.Properties.Count);
        Assert.Equal(["Param00", "Param01", "Param02"], s.Properties.Keys.Take(3));
        Assert.Equal("0.5", s.Properties["Param00"]);
        // sha256 of {"Param00":"0.5",…,"Param11":"11.5"}, computed outside this code
        Assert.Equal("fa9191301e2542ea7cfadae66881692c571c43f6d04806cce5784a1f72ce071d", s.SetupHash);
    }

    [Fact]
    public void The_hash_ignores_the_order_the_parameters_come_in()
    {
        // sha256 of {"Alpha.First":"(Gears:35//9)","Mid.Key":"2","Zeta.Last":"1.000000"}
        const string expected = "76dd8b61a006c4feb7329c80392b61d14464be7a9f5e69ecbc9e50e227b34c2c";

        Assert.Equal(expected, SetupShareJson.Hash(new Dictionary<string, string>
        {
            ["Zeta.Last"] = "1.000000",
            ["Alpha.First"] = "(Gears:35//9)",
            ["Mid.Key"] = "2",
        }));
        Assert.Equal(expected, SetupShareJson.Hash(new Dictionary<string, string>
        {
            ["Mid.Key"] = "2",
            ["Zeta.Last"] = "1.000000",
            ["Alpha.First"] = "(Gears:35//9)",
        }));
    }

    [Fact]
    public void A_setup_kept_for_no_surface_leaves_the_surface_fields_empty()
    {
        // an entry with an empty surface list, which SaveBuilder.Entry never writes
        var w = new Writer();
        w.Int32(511).Byte(0)
         .FString("Alpha").FString("").Int64(639_000_000_000_000_000)
         .FString("0.6.0.100866").FString("TestCarA").FString("TestStage")
         .FStrings([]).FStrings([]).FStrings([])
         .Int32(1).FString("Param00").FString("0.5");

        var s = One(SetupShareJson.Build(2, w.ToArray(), At));

        Assert.Equal("", s.Surface);
        Assert.Empty(s.Surfaces);
        Assert.Equal("", s.FriendlySurface);
        Assert.Equal("TestCarA||TestStage", s.UniqueKey);
    }

    [Fact]
    public void The_file_lists_its_fields_in_the_order_of_the_format()
    {
        string json = SetupShareJson.Serialize(SetupShareJson.Build(2, SaveBuilder.Entry(Alpha), At));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(
            ["ExportVersion", "ExportedBy", "ExportedAt", "SourceGame", "Setups"],
            doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            [
                "CarName", "FriendlyCarName", "Surface", "GameVersion", "Surfaces",
                "UserDefinedName", "StageName", "PlayerName", "FriendlySetupName",
                "TemplateDataBase64", "SetupHash", "Properties", "UniqueKey",
                "FriendlySurface", "SurfaceEmoji", "FriendlyStage",
                "Weathers", "Tags", "SavedAt",              // ours, after the format's own
            ],
            doc.RootElement.GetProperty("Setups")[0].EnumerateObject().Select(p => p.Name));

        // indented by two spaces, "+" and the emoji as \u escapes
        Assert.StartsWith("{" + Environment.NewLine + "  \"ExportVersion\": \"1.0\",", json);
        Assert.Contains("\"ExportedAt\": \"2026-03-04T12:00:00.1230000\\u002B02:00\"", json);
        Assert.Contains("\"SurfaceEmoji\": \"\\uD83C\\uDFCE\"", json);
    }

    [Fact]
    public void What_the_format_leaves_out_is_added_after_it()
    {
        string json = SetupShareJson.Serialize(SetupShareJson.Build(2, SaveBuilder.Entry(Alpha), At));
        var s = JsonDocument.Parse(json).RootElement.GetProperty("Setups")[0];

        Assert.Equal(["Clear", "LightRain"], s.GetProperty("Weathers").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["TestMode_Tag"], s.GetProperty("Tags").EnumerateArray().Select(e => e.GetString()));
        // the file travels to other time zones: the setup's own date claims none
        Assert.Equal("2026-03-04T12:00:00", s.GetProperty("SavedAt").GetString());
    }

    // ── reading ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_file_gives_back_the_entry_and_the_int32_in_front_of_it()
    {
        var read = Assert.Single(SetupShareJson.Read(FileOf(Alpha, prefix: 18)));

        Assert.Equal(18, read.Prefix);
        Assert.Equal(SaveBuilder.Entry(Alpha), read.Entry);
    }

    [Fact]
    public void Export_then_import_puts_the_same_bytes_into_another_save()
    {
        byte[] from = SaveBuilder.CarSetups(Alpha, new Setup("Bravo", "TestCarA"));
        var bravo = CarSetupsParser.ParseSetups(from)[1];
        BitConverter.GetBytes(18).CopyTo(from, bravo.EntryStart - 4);
        byte[] entry = CarSetupsParser.ExtractEntry(from, bravo);
        string json = SetupShareJson.Serialize(
            SetupShareJson.Build(CarSetupsParser.ReadEntryPrefix(from, bravo), entry, At));

        var read = Assert.Single(SetupShareJson.Read(Encoding.UTF8.GetBytes(json)));
        byte[] into = CarSetupsParser.InsertEntry(
            SaveBuilder.CarSetups(new Setup("Charlie", "TestCarA")), read.Entry, read.Prefix);

        CarSetupsParser.VerifyIntegrity(into, 2);
        var added = CarSetupsParser.ParseSetups(into)[1];
        Assert.Equal(entry, CarSetupsParser.ExtractEntry(into, added));
        Assert.Equal(18, CarSetupsParser.ReadEntryPrefix(into, added));   // not the block's 2
    }

    [Fact]
    public void An_entry_without_a_prefix_takes_the_one_its_car_block_opens_with()
    {
        byte[] save = SaveBuilder.CarSetups(new Setup("Charlie", "TestCarA"));
        var charlie = CarSetupsParser.ParseSetups(save)[0];
        BitConverter.GetBytes(18).CopyTo(save, charlie.EntryStart - 4);

        byte[] into = CarSetupsParser.InsertEntry(save, SaveBuilder.Entry(new Setup("Delta", "TestCarA")));
        byte[] other = CarSetupsParser.InsertEntry(save, SaveBuilder.Entry(new Setup("Delta", "TestCarB")), 18);

        Assert.Equal(18, CarSetupsParser.ReadEntryPrefix(into, CarSetupsParser.ParseSetups(into)[1]));
        Assert.Equal(18, CarSetupsParser.ReadEntryPrefix(other, CarSetupsParser.ParseSetups(other)[1]));   // a block of its own
    }

    [Fact]
    public void A_file_holding_several_setups_gives_them_all_in_its_order()
    {
        string one = SetupShareJson.Serialize(SetupShareJson.Build(2, SaveBuilder.Entry(Alpha), At));
        string two = SetupShareJson.Serialize(SetupShareJson.Build(18, SaveBuilder.Entry(new Setup("Bravo", "TestCarB")), At));
        using var a = JsonDocument.Parse(one);
        using var b = JsonDocument.Parse(two);
        string both = "{\"Setups\":[" + a.RootElement.GetProperty("Setups")[0].GetRawText() + "," +
                      b.RootElement.GetProperty("Setups")[0].GetRawText() + "]}";

        var read = SetupShareJson.Read(Encoding.UTF8.GetBytes(both));

        Assert.Equal([2, 18], read.Select(s => s.Prefix));
        Assert.Equal(["Alpha", "Bravo"], read.Select(s => CarSetupsParser.ReadEntrySetupName(s.Entry)));

        // and all of them go into one save, each behind its own int32
        byte[] into = SaveBuilder.CarSetups(new Setup("Charlie", "TestCarA"));
        foreach (var s in read) into = CarSetupsParser.InsertEntry(into, s.Entry, s.Prefix);

        CarSetupsParser.VerifyIntegrity(into, 3);
        var list = CarSetupsParser.ParseSetups(into);
        Assert.Equal(["Alpha", "Bravo", "Charlie"], list.Select(e => e.SetupName).Order());
        Assert.Equal(18, CarSetupsParser.ReadEntryPrefix(into, list.Single(e => e.SetupName == "Bravo")));
    }

    [Fact]
    public void One_unreadable_setup_refuses_the_whole_file()
    {
        string one = SetupShareJson.Serialize(SetupShareJson.Build(2, SaveBuilder.Entry(Alpha), At));
        using var a = JsonDocument.Parse(one);
        string both = "{\"Setups\":[" + a.RootElement.GetProperty("Setups")[0].GetRawText() +
                      ",{\"UserDefinedName\":\"Bravo\"}]}";

        Assert.Throws<InvalidDataException>(() => SetupShareJson.Read(Encoding.UTF8.GetBytes(both)));
    }

    [Fact]
    public void Several_setups_go_into_one_document_and_come_back_as_they_went()
    {
        byte[] alpha = SaveBuilder.Entry(Alpha), bravo = SaveBuilder.Entry(new Setup("Bravo", "TestCarB", Author: "Someone"));
        var file = SetupShareJson.Build([(2, alpha), (18, bravo)], At);

        Assert.Equal("Someone", file.ExportedBy);   // the name both carry
        Assert.Equal(["Alpha", "Bravo"], file.Setups.Select(s => s.UserDefinedName));
        Assert.Equal(SetupShareJson.Build(2, alpha, At).Setups[0].SetupHash, file.Setups[0].SetupHash);

        var read = SetupShareJson.Read(Encoding.UTF8.GetBytes(SetupShareJson.Serialize(file)));
        Assert.Equal([2, 18], read.Select(s => s.Prefix));
        Assert.Equal(alpha, read[0].Entry);
        Assert.Equal(bravo, read[1].Entry);
    }

    [Fact]
    public void Setups_of_several_players_leave_the_document_unsigned()
    {
        var file = SetupShareJson.Build(
            [(2, SaveBuilder.Entry(Alpha)), (2, SaveBuilder.Entry(new Setup("Bravo", "TestCarB", Author: "Other")))], At);

        Assert.Equal("", file.ExportedBy);

        // a setup saved before game 0.5 carries no name, and names no other player
        Assert.Equal("Someone", SetupShareJson.Build(
            [(2, SaveBuilder.Entry(Alpha)), (2, SaveBuilder.Entry(new Setup("Bravo", "TestCarB")))], At).ExportedBy);
    }

    [Fact]
    public void A_json_file_is_told_from_a_setup_entry_by_its_content()
    {
        Assert.True(SetupShareJson.IsJson(FileOf(Alpha)));                  // behind its BOM
        Assert.True(SetupShareJson.IsJson(" \r\n\t{}"u8.ToArray()));
        Assert.False(SetupShareJson.IsJson(SaveBuilder.Entry(Alpha)));
        Assert.False(SetupShareJson.IsJson([]));
    }

    [Fact]
    public void Extra_fields_and_a_missing_parameter_list_do_not_stop_the_reading()
    {
        byte[] file = Edited(json =>
        {
            using var doc = JsonDocument.Parse(json);
            string b64 = doc.RootElement.GetProperty("Setups")[0].GetProperty("TemplateDataBase64").GetString()!;
            return "{\"Unknown\":1,\"Setups\":[{\"Other\":[],\"TemplateDataBase64\":\"" + b64 + "\"}]}";
        });

        Assert.Equal(SaveBuilder.Entry(Alpha), Assert.Single(SetupShareJson.Read(file)).Entry);
    }

    [Fact]
    public void A_file_whose_listed_parameters_were_edited_is_refused()
    {
        var changed = Assert.Throws<InvalidDataException>(() => SetupShareJson.Read(
            Edited(json => json.Replace("\"Param03\": \"3.5\"", "\"Param03\": \"9.5\""))));
        Assert.Contains("Param03", changed.Message);

        Assert.Throws<InvalidDataException>(() => SetupShareJson.Read(
            Edited(json => json.Replace("\"Param03\": \"3.5\",", ""))));              // one removed
        Assert.Throws<InvalidDataException>(() => SetupShareJson.Read(
            Edited(json => json.Replace("\"Param03\":", "\"ParamXX\":"))));            // one renamed
    }

    [Fact]
    public void A_file_that_is_not_this_format_is_refused_with_a_reason()
    {
        static string Why(string json) =>
            Assert.Throws<InvalidDataException>(() => SetupShareJson.Read(Encoding.UTF8.GetBytes(json))).Message;

        Assert.Contains("not readable JSON", Why("{ \"Setups\": ["));
        Assert.Contains("no setup to import", Why("{\"Setups\":[]}"));
        Assert.Contains("no setup to import", Why("[1, 2]"));
        // the two other JSON files of this tool, named for what they are
        Assert.Contains("1.0.0", Why("{\"Car\":\"TestCarA\",\"Parameters\":{}}"));
        Assert.Contains("whole player save", Why("{\"file_info\":{},\"gvas_header\":{}}"));
        Assert.Contains("TemplateDataBase64", Why("{\"Setups\":[{\"CarName\":\"TestCarA\"}]}"));
        Assert.Contains("TemplateDataBase64", Why("{\"Setups\":[{\"TemplateDataBase64\":\"not base64!\"}]}"));
        Assert.Contains("TemplateDataBase64", Why("{\"Setups\":[{\"TemplateDataBase64\":\"AgA=\"}]}"));
    }

    [Fact]
    public void A_truncated_embedded_entry_is_refused_like_a_truncated_setup_file()
    {
        byte[] entry = SaveBuilder.Entry(Alpha);
        byte[] template = [2, 0, 0, 0, .. entry[..^7]];
        string json = "{\"Setups\":[{\"TemplateDataBase64\":\"" + Convert.ToBase64String(template) + "\"}]}";

        Assert.Throws<InvalidDataException>(() => SetupShareJson.Read(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("TestCarA", "Test Car A")]
    [InlineData("AlphaB2Charlie", "Alpha B2 Charlie")]
    [InlineData("FooXYZBar9", "Foo XYZ Bar9")]
    [InlineData("Fooi20NBar2", "Fooi20 N Bar2")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void An_id_is_spaced_out_at_its_capitals(string id, string friendly) =>
        Assert.Equal(friendly, SetupShareJson.FriendlyName(id));
}
