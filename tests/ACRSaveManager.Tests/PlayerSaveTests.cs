using System.IO;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>SaveFileParser and the JSON export against synthetic saves.</summary>
public class PlayerSaveTests
{
    [Fact]
    public void Identifies_a_player_save_and_reads_its_engine()
    {
        byte[] data = SaveBuilder.PlayerData();
        Assert.Same(SaveKind.PlayerData, SaveKind.Identify(data));
        Assert.Equal("5.6.1", GvasHeader.ReadInfo(data).EngineVersion);
        Assert.Equal(2, GvasHeader.ReadInfo(data).CustomVersionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_the_rally_name_of_the_weekend_config(bool leaderboardFirst)
    {
        byte[] data = SaveBuilder.PlayerData(leaderboardFirst);
        var rally = EventList.Find(data).Events.Single(e => e.Name == "DefaultRally");
        Assert.Equal(SaveBuilder.RallyName, SaveFileParser.ReadRallyName(data, rally.Start));
    }

    [Fact]
    public void A_stale_size_field_1_is_caught()
    {
        byte[] data = SaveBuilder.PlayerData();
        SaveFileParser.VerifySizeField(data);
        Assert.Throws<InvalidDataException>(() => SaveFileParser.VerifySizeField([.. data, 0]));
    }

    // ── JSON export ────────────────────────────────────────────────────────────

    [Fact] // bug 5: the stage was always "(unknown)"
    public void The_export_reports_the_stage_and_car_of_the_weather_settings()
    {
        var doc = PlayerSaveJson.Export(SaveBuilder.PlayerData(), @"C:\Users\someone\Save.sav");
        var weather = doc["weather_settings"]!;
        Assert.Equal(SaveBuilder.WeatherStage, weather["stage"]!.GetValue<string>());
        Assert.Equal(SaveBuilder.WeatherCar, weather["car"]!.GetValue<string>());
        Assert.Equal(3, weather["options"]!.AsArray().Count);

        // the section sits outside every block: its stage is not the one named
        // inside the rally weekend's own blob
        Assert.NotEqual(SaveBuilder.RallyStage, weather["stage"]!.GetValue<string>());
    }

    [Fact] // the export read the section back from its StartingTime option
    public void The_weather_settings_are_read_whatever_the_order_of_their_options()
    {
        var weather = PlayerSaveJson.Export(SaveBuilder.PlayerData(presetFirst: true), "test.sav")["weather_settings"]!;

        Assert.Equal(SaveBuilder.WeatherStage, weather["stage"]!.GetValue<string>());
        Assert.Equal(SaveBuilder.WeatherCar, weather["car"]!.GetValue<string>());
        Assert.Equal(["/Script/acr.WeatherOptions/Preset", "/Script/acr.WeatherOptions/StartingTime",
                      "/Script/acr.WeatherOptions/TimeSpeed"],
                     weather["options"]!.AsArray().Select(o => o!["path"]!.GetValue<string>()));
    }

    [Fact]
    public void The_weather_settings_sit_past_the_last_event_config()
    {
        byte[] data = SaveBuilder.PlayerData();

        int listEnd = EventList.Find(data).EventsEnd;

        int options = IndexOf(data, System.Text.Encoding.UTF8.GetBytes(
            "/Script/acr.WeatherOptions/StartingTime\0"));

        // The game keeps them there, so the fixture must too: a save that hid
        // them inside a block would let a decoder read the wrong bytes and still
        // pass every other test here.
        Assert.True(options > listEnd,
            $"the weather options start at 0x{options:X4}, inside a block ending at 0x{listEnd:X4}");
    }

    /// <summary>First offset of <paramref name="needle"/>, or -1.</summary>
    private static int IndexOf(byte[] data, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= data.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && data[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }

        return -1;
    }

    [Fact]
    public void The_export_finds_every_config_and_leaderboard()
    {
        var doc = PlayerSaveJson.Export(SaveBuilder.PlayerData(), "Save.sav");
        var configs = doc["event_configs"]!.AsArray();
        Assert.Equal(["DefaultFreePractice", "DefaultTimeAttack", "DefaultSingleStage", "DefaultRally"],
                     configs.Select(c => c!["event_name"]!.GetValue<string>()));

        var boards = doc["event_leaderboards"]!.AsArray();
        Assert.Equal(3, boards.Count);
        // DefaultFreePractice 2 runs, DefaultRally 1, TestStage_PART1_F 4
        Assert.Equal(7, boards.Sum(b => b!["stages"]!.AsArray().Sum(s => s!["cars"]!.AsArray().Count)));
    }

    [Fact]
    public void The_export_records_the_file_name_but_not_the_path()
    {
        var doc = PlayerSaveJson.Export(SaveBuilder.PlayerData(), @"C:\Users\someone\AppData\Save.sav");
        string info = doc["file_info"]!.ToJsonString();
        Assert.Contains("\"file_name\":\"Save.sav\"", info);
        Assert.DoesNotContain("someone", info);
    }

    [Fact]
    public void A_non_ascii_player_name_changes_nothing_in_the_export()
    {
        var ascii = PlayerSaveJson.Export(SaveBuilder.PlayerData(), "Save.sav");
        var utf16 = PlayerSaveJson.Export(SaveBuilder.PlayerData(playerName: "Jérôme Müller"), "Save.sav");
        foreach (string key in new[] { "gvas_header", "event_configs", "event_leaderboards", "weather_settings" })
            Assert.Equal(ascii[key]!.ToJsonString(), utf16[key]!.ToJsonString());
        Assert.DoesNotContain("_error", utf16.ToJsonString());
    }
}
