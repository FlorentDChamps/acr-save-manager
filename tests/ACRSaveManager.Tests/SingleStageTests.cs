using System.IO;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>A rally's first stage in the single-stage modes and the stage selection they read (docs §3.3, §3.4).</summary>
public class SingleStageTests
{
    private static readonly GameCatalog Catalog = GameCatalog.Default;

    private static Rally Sample() => Rally.From("Testland", SaveBuilder.OfflineWeekend(), SaveBuilder.OnlineWeekend());

    private static byte[] Data(byte[] save, string eventName, string component)
    {
        var ev = EventList.Find(save).Events.Single(e => e.Name == eventName);
        int pos = ev.Start;
        Assert.True(EventConfig.TryRead(save, ref pos, out _, out var components, out _));
        var c = components.Single(x => x.Name == component);
        return save[c.DataStart..(c.DataStart + c.DataSize)];
    }

    [Fact]
    public void A_single_stage_holds_a_weekend_s_settings_without_the_calendar()
    {
        byte[] offline = SaveBuilder.SingleStage().WriteSingleStage();
        byte[] online = SaveBuilder.OnlineSingleStage().WriteSingleStage();
        Assert.Equal(96, offline.Length);                    // the sizes of game 0.7
        Assert.Equal(73, online.Length);

        var read = RallyWeekend.ReadSingleStage(offline);
        Assert.False(read.Online);
        Assert.Equal(26, read.Ai!.Opponents);
        Assert.Equal(6, read.NextWeather.Weather);
        Assert.Empty(read.Stages);
        Assert.Equal(offline, read.WriteSingleStage());

        var readOnline = RallyWeekend.ReadSingleStage(online);
        Assert.True(readOnline.Online);
        Assert.Equal(new LobbySettings(12, 2), readOnline.Lobby);
        Assert.Equal(online, readOnline.WriteSingleStage());

        Assert.Throws<InvalidDataException>(() => RallyWeekend.ReadSingleStage(SaveBuilder.OfflineWeekend().Write()));
    }

    [Fact]
    public void Writes_the_first_stage_into_every_single_stage_mode_and_the_selection()
    {
        byte[] save = SaveBuilder.PlayerData(online: true, onlineRally: true);
        var rally = Sample();
        var first = rally.Weekend.Stages[0];

        var write = SingleStageEdit.Replace(save, rally, Catalog);
        SingleStageEdit.Verify(save, write, rally);
        Assert.Equal(["DefaultFreePractice", "DefaultSingleStage", "DefaultOnlineSingleStage"], write.Events);

        var offline = RallyWeekend.ReadSingleStage(Data(write.Data, "DefaultSingleStage", RallyWeekend.SingleStageComponent));
        Assert.Equal(first.Weather, offline.NextWeather);
        Assert.Equal(rally.Weekend.Ai, offline.Ai);
        Assert.Equal(rally.Weekend.RunningOrder, offline.RunningOrder);
        Assert.Equal(rally.Weekend.Collisions, offline.Collisions);

        var online = RallyWeekend.ReadSingleStage(Data(write.Data, "DefaultOnlineSingleStage", RallyWeekend.SingleStageComponent));
        Assert.Equal(first.Weather, online.NextWeather);
        Assert.Equal(rally.Lobby, online.Lobby);

        byte[] practice = Data(write.Data, "DefaultFreePractice", SingleStageEdit.PracticeComponent);
        Assert.Equal(72, practice.Length);
        Assert.Equal(offline.WriteSingleStage()[..0x43], practice[..0x43]);   // the rules and the weather
        Assert.Equal(rally.Weekend.Penalty, practice[0x43]);
        Assert.Equal(1, BitConverter.ToInt32(practice, 0x44));                // free drive, kept

        var selection = EventList.Find(write.Data).Selection!;
        Assert.Equal(first.Stage, selection.Stage);
        Assert.Equal(SaveBuilder.WeatherCar, selection.Car);
        Assert.Equal(
        [
            (SingleStageEdit.StartPath, "(TimeSeconds=30600.000000)"),
            (SingleStageEdit.PresetPath, "(WeatherType=WT_LIGHT_FOG,RandomUniform=0.250000,bRandom=False)"),
            (SingleStageEdit.SpeedPath, "WT_SPEEDVERYFAST"),
        ], selection.Options);

        // the weekends and what follows the selection are untouched
        Assert.Equal(Data(save, "DefaultRally", RallyWeekend.ComponentName), Data(write.Data, "DefaultRally", RallyWeekend.ComponentName));
        Assert.Equal(save[EventList.Find(save).End..], write.Data[selection.End..]);
    }

    [Fact]
    public void A_new_player_s_save_gets_the_selection_s_options_it_lacked()
    {
        byte[] save = SaveBuilder.PlayerData(newPlayer: true);
        var write = SingleStageEdit.Replace(save, Sample(), Catalog);
        SingleStageEdit.Verify(save, write, Sample());

        Assert.Empty(write.Events);
        var selection = EventList.Find(write.Data).Selection!;
        Assert.Equal(SaveBuilder.RallyStage, selection.Stage);
        Assert.Equal(3, selection.Options.Count);
    }

    [Fact]
    public void Verify_catches_a_write_that_went_wrong()
    {
        byte[] save = SaveBuilder.PlayerData(online: true);
        var rally = Sample();
        var write = SingleStageEdit.Replace(save, rally, Catalog);

        byte[] tampered = (byte[])write.Data.Clone();
        tampered[^3] ^= 0xFF;                                   // the profile, past the selection
        Assert.Throws<InvalidDataException>(() => SingleStageEdit.Verify(save, write with { Data = tampered }, rally));

        var other = rally with { Weekend = rally.Weekend with { Stages = [.. rally.Weekend.Stages.Skip(1)] } };
        Assert.Throws<InvalidDataException>(() => SingleStageEdit.Verify(save, write, other));
    }
}
