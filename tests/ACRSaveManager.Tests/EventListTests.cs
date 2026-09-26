using System.IO;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>EventList: the whole event config list and its selection, as a .race file carries them.</summary>
public class EventListTests
{
    private static readonly string[] AllEvents =
        ["DefaultFreePractice", "DefaultTimeAttack", "DefaultSingleStage", "DefaultRally", "DefaultOnlineSingleStage"];

    private const string OtherStage = "TestlandS3CoastFullForward";

    /// <summary>What a .race file holds: the list and its selection, straight from the save.</summary>
    private static byte[] FileOf(byte[] save)
    {
        var list = EventList.Find(save);
        return save[list.Start..list.End];
    }

    private static byte[] Part(byte[] data, EventList list, string name)
    {
        var e = list.Events.Single(x => x.Name == name);
        return data[e.Start..e.End];
    }

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
    public void An_extracted_file_reads_back_whole()
    {
        byte[] file = FileOf(SaveBuilder.PlayerData(online: true));
        var list = EventList.ReadFile(file);

        Assert.Equal(0, list.Start);
        Assert.Equal(file.Length, list.End);
        Assert.Equal(AllEvents, list.Events.Select(e => e.Name));
    }

    [Fact]
    public void Writing_a_save_its_own_list_reproduces_it()
    {
        byte[] save = SaveBuilder.PlayerData();
        byte[] file = FileOf(save);
        var write = EventList.Replace(save, file, AllEvents, selection: true);

        Assert.Equal(save, write.Data);
        EventList.VerifyIntegrity(save, write, file);
    }

    [Fact]
    public void Another_save_list_replaces_every_event_and_the_selection()
    {
        byte[] save = SaveBuilder.PlayerData();
        byte[] other = SaveBuilder.PlayerData(stage: OtherStage, seed: 5);
        byte[] file = FileOf(other);

        var write = EventList.Replace(save, file, AllEvents, selection: true);
        EventList.VerifyIntegrity(save, write, file);

        var before = EventList.Find(save);
        var after = EventList.Find(write.Data);
        var theirs = EventList.Find(other);
        foreach (var e in after.Events)
            Assert.Equal(Part(other, theirs, e.Name), Part(write.Data, after, e.Name));
        Assert.Equal(OtherStage, after.Selection!.Stage);
        Assert.Equal(AllEvents[..4], write.Written);
        Assert.Empty(write.Missing);

        // a longer stage name: the file grows and size_field_1 follows it
        int grown = OtherStage.Length - SaveBuilder.WeatherStage.Length;
        Assert.Equal(save.Length + grown, write.Data.Length);
        Assert.Equal(write.OldSizeField + grown, write.NewSizeField);
        SaveFileParser.VerifySizeField(write.Data);

        // the leaderboards and the profile past the list do not move
        Assert.Equal(save[before.End..], write.Data[after.End..]);
    }

    [Fact]
    public void Only_the_ticked_parts_are_written()
    {
        byte[] save = SaveBuilder.PlayerData();
        byte[] other = SaveBuilder.PlayerData(stage: OtherStage, seed: 5);
        byte[] file = FileOf(other);

        var write = EventList.Replace(save, file, ["DefaultFreePractice"], selection: false);
        EventList.VerifyIntegrity(save, write, file);

        var mine = EventList.Find(save);
        var theirs = EventList.Find(other);
        var after = EventList.Find(write.Data);
        Assert.Equal(Part(other, theirs, "DefaultFreePractice"), Part(write.Data, after, "DefaultFreePractice"));
        foreach (string name in AllEvents[1..4])
            Assert.Equal(Part(save, mine, name), Part(write.Data, after, name));
        Assert.Equal(SaveBuilder.WeatherStage, after.Selection!.Stage);
        Assert.False(write.SelectionWritten);
    }

    [Fact]
    public void The_selection_can_be_written_alone()
    {
        byte[] save = SaveBuilder.PlayerData();
        byte[] file = FileOf(SaveBuilder.PlayerData(stage: OtherStage, seed: 5));

        var write = EventList.Replace(save, file, [], selection: true);
        EventList.VerifyIntegrity(save, write, file);

        var mine = EventList.Find(save);
        var after = EventList.Find(write.Data);
        Assert.Equal(OtherStage, after.Selection!.Stage);
        foreach (string name in AllEvents[..4])
            Assert.Equal(Part(save, mine, name), Part(write.Data, after, name));
    }

    [Fact]
    public void An_event_the_save_lacks_is_reported_and_skipped()
    {
        byte[] save = SaveBuilder.PlayerData();                       // four events, as in 0.4
        byte[] file = FileOf(SaveBuilder.PlayerData(seed: 5, online: true));

        var write = EventList.Replace(save, file, AllEvents, selection: true);
        EventList.VerifyIntegrity(save, write, file);

        Assert.Equal(["DefaultOnlineSingleStage"], write.Missing);
        Assert.Equal(AllEvents[..4], write.Written);
        Assert.Equal(AllEvents[..4], EventList.Find(write.Data).Events.Select(e => e.Name));
    }

    [Fact]
    public void A_file_that_is_not_an_event_list_is_refused()
    {
        // a single event config: what a .weekend file used to hold
        byte[] save = SaveBuilder.PlayerData();
        var rally = EventList.Find(save).Events.Single(e => e.Name == "DefaultRally");
        byte[] weekend = save[rally.Start..rally.End];

        var ex = Assert.Throws<InvalidDataException>(() => EventList.ReadFile(weekend));
        Assert.Contains("does not hold an event list", ex.Message);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(10)]
    public void A_truncated_or_padded_file_is_refused(int change)
    {
        byte[] file = FileOf(SaveBuilder.PlayerData());
        byte[] damaged = change < 0 ? file[..^-change] : [.. file, .. new byte[change]];

        Assert.Throws<InvalidDataException>(() => EventList.ReadFile(damaged));
        Assert.Throws<InvalidDataException>(() =>
            EventList.Replace(SaveBuilder.PlayerData(), damaged, AllEvents, selection: true));
    }

    [Fact]
    public void A_damaged_splice_is_caught()
    {
        byte[] save = SaveBuilder.PlayerData();
        byte[] file = FileOf(SaveBuilder.PlayerData(stage: OtherStage, seed: 5));
        var write = EventList.Replace(save, file, ["DefaultRally"], selection: false);

        // one byte of the written event, then one byte of an event left alone
        var after = EventList.Find(write.Data);
        foreach (string name in new[] { "DefaultRally", "DefaultTimeAttack" })
        {
            byte[] bad = (byte[])write.Data.Clone();
            bad[after.Events.Single(e => e.Name == name).End - 1] ^= 0xFF;
            Assert.Throws<InvalidDataException>(() =>
                EventList.VerifyIntegrity(save, write with { Data = bad }, file));
        }
    }

    [Fact]
    public void A_stale_size_field_1_is_caught()
    {
        byte[] save = SaveBuilder.PlayerData();
        byte[] file = FileOf(SaveBuilder.PlayerData(stage: OtherStage, seed: 5));
        var write = EventList.Replace(save, file, AllEvents, selection: true);

        BitConverter.GetBytes(write.OldSizeField).CopyTo(write.Data, SaveFileParser.FindSizeField1Offset(write.Data));
        Assert.Throws<InvalidDataException>(() => EventList.VerifyIntegrity(save, write, file));
    }
}
