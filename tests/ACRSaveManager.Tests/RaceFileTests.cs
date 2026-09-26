using System.IO;
using System.Text;
using ACRSaveManager.Services;
using ACRSaveManager.Tests.Fixtures;

namespace ACRSaveManager.Tests;

/// <summary>The container of .race files.</summary>
public class RaceFileTests
{
    /// <summary>What a .race file wraps: the event list and its selection.</summary>
    private static byte[] Content()
    {
        byte[] data = SaveBuilder.PlayerData();
        var list = EventList.Find(data);
        return data[list.Start..list.End];
    }

    private static readonly RaceFileMeta Meta = new("5.6.1", 117, "2026-01-01T00:00:00Z", "test");

    /// <summary>A container of the given version, metadata as written.</summary>
    private static byte[] Container(ushort version, string json, byte[] content)
    {
        byte[] good = RaceFile.Wrap(content, Meta);     // for the magic and the CRC
        byte[] meta = Encoding.UTF8.GetBytes(json);
        byte[] file = new byte[16 + meta.Length + content.Length];
        Array.Copy(good, 0, file, 0, 16);
        BitConverter.GetBytes(version).CopyTo(file, 6);
        BitConverter.GetBytes(meta.Length).CopyTo(file, 8);
        meta.CopyTo(file, 16);
        content.CopyTo(file, 16 + meta.Length);
        return file;
    }

    [Fact]
    public void Wrap_and_read_round_trip()
    {
        byte[] content = Content();
        var (got, meta) = RaceFile.Read(RaceFile.Wrap(content, Meta));
        Assert.Equal(content, got);
        Assert.Equal(Meta, meta);
        EventList.ReadFile(got);
    }

    [Fact]
    public void A_damaged_file_fails_its_checksum()
    {
        byte[] file = RaceFile.Wrap(Content(), Meta);
        file[^1] ^= 0xFF;
        var ex = Assert.Throws<InvalidDataException>(() => RaceFile.Read(file));
        Assert.Contains("checksum", ex.Message);
    }

    [Fact]
    public void A_file_without_the_container_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => RaceFile.Read(Content()));
        Assert.Contains("not a file exported by ACR Save Manager", ex.Message);
        Assert.Throws<InvalidDataException>(() => RaceFile.Read([]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_single_block_file_of_an_older_build_is_refused(ushort version)
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            RaceFile.Read(Container(version, """{"Engine":"5.6.1","Event":"DefaultRally"}""", Content())));
        Assert.Contains("single event block", ex.Message);
    }

    [Fact]
    public void A_container_from_a_newer_build_is_refused()
    {
        byte[] file = RaceFile.Wrap(Content(), Meta);
        BitConverter.GetBytes((ushort)(RaceFile.ContainerVersion + 1)).CopyTo(file, 6);
        var ex = Assert.Throws<InvalidDataException>(() => RaceFile.Read(file));
        Assert.Contains("Update ACR Save Manager", ex.Message);
    }

    [Fact]
    public void Unreadable_metadata_still_yields_the_content()
    {
        byte[] content = Content();
        var (got, meta) = RaceFile.Read(Container(RaceFile.ContainerVersion, "{not json", content));
        Assert.Equal(content, got);
        Assert.Null(meta);
    }
}
