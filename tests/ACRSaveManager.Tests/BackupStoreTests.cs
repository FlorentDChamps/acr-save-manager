using System.IO;
using ACRSaveManager.Services;

namespace ACRSaveManager.Tests;

/// <summary>Where backups go, and which of them the rotation removes.</summary>
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acrsm-bak-" + Guid.NewGuid().ToString("N"));
    private readonly string _save;
    private readonly string _folder;
    private readonly List<string> _discarded = [];

    public BackupStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _save = Path.Combine(_dir, "CarSetupsDataSaveSlot.sav");
        File.WriteAllBytes(_save, [1, 2, 3]);
        _folder = BackupStore.FolderFor(_save);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BackupWritten Write(byte[] contents, int keep = 10, string? spare = null) =>
        BackupStore.Write(_save, contents, keep, f => { _discarded.Add(Path.GetFileName(f)); File.Delete(f); }, spare);

    /// <summary>An old backup, at a made-up date of 2020 so it sorts before any new one.</summary>
    private string Old(int day, string? dir = null, string stem = "CarSetupsDataSaveSlot")
    {
        string path = Path.Combine(dir ?? _folder, $"{stem}_202001{day:00}_120000.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [(byte)day]);
        return path;
    }

    private string[] Names() => [.. BackupStore.List(_folder, _save).Select(Path.GetFileName)!];

    [Theory]
    [InlineData("CarSetupsDataSaveSlot_20260101_120000.bak", true)]
    [InlineData("carsetupsdatasaveslot_20260101_120000.BAK", true)]
    [InlineData("CarSetupsDataSaveSlot_20260101_120000 keep.bak", false)]
    [InlineData("CarSetupsDataSaveSlot_before-rally.bak", false)]
    [InlineData("CarSetupsDataSaveSlot_20260101_1200.bak", false)]
    [InlineData("CarSetupsDataSaveSlot_x_20260101_120000.bak", false)]
    [InlineData("PlayerDataSaveSlot_20260101_120000.bak", false)]
    [InlineData("CarSetupsDataSaveSlot_20260101_120000.bak.bak", false)]
    public void Only_the_names_this_tool_writes_are_its_backups(string name, bool expected) =>
        Assert.Equal(expected, BackupStore.IsBackupOf(name, _save));

    [Fact]
    public void A_backup_goes_to_the_folder_beside_the_save()
    {
        var bak = Write([9, 9]);

        Assert.True(bak.Created);
        Assert.Equal(Path.Combine(_dir, BackupStore.FolderName), Path.GetDirectoryName(bak.Path));
        Assert.Equal([9, 9], File.ReadAllBytes(bak.Path));
        Assert.True(BackupStore.IsBackupOf(bak.Path, _save));
    }

    [Fact]
    public void The_same_bytes_as_the_newest_backup_write_no_new_one()
    {
        var first = Write([9, 9]);
        var again = Write([9, 9]);

        Assert.False(again.Created);
        Assert.Equal(first.Path, again.Path);
        Assert.Single(Names());
    }

    [Fact]
    public void Bytes_of_an_older_backup_still_get_a_new_one()
    {
        Old(1);                       // holds [1]
        Old(2);                       // holds [2], the newest
        var bak = Write([1]);

        Assert.True(bak.Created);
        Assert.Equal(3, Names().Length);
    }

    [Fact]
    public void Past_the_count_kept_the_oldest_go()
    {
        for (int day = 1; day <= 5; day++) Old(day);

        var bak = Write([42], keep: 3);

        Assert.Equal(
            [Path.GetFileName(bak.Path), "CarSetupsDataSaveSlot_20200105_120000.bak", "CarSetupsDataSaveSlot_20200104_120000.bak"],
            Names());
        Assert.Equal(
            ["CarSetupsDataSaveSlot_20200103_120000.bak", "CarSetupsDataSaveSlot_20200102_120000.bak", "CarSetupsDataSaveSlot_20200101_120000.bak"],
            _discarded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_count_of_zero_or_less_keeps_them_all(int keep)
    {
        for (int day = 1; day <= 5; day++) Old(day);

        Write([42], keep);

        Assert.Equal(6, Names().Length);
        Assert.Empty(_discarded);
    }

    [Fact]
    public void Files_not_named_by_the_tool_are_never_counted_nor_removed()
    {
        for (int day = 1; day <= 3; day++) Old(day);
        string pinned = Path.Combine(_folder, "CarSetupsDataSaveSlot_before-rally.bak");
        File.WriteAllBytes(pinned, [7]);
        string other = Old(1, stem: "PlayerDataSaveSlot");

        Write([42], keep: 1);

        Assert.True(File.Exists(pinned));
        Assert.True(File.Exists(other));
        Assert.Single(Names());
    }

    [Fact]
    public void The_backup_a_restore_reads_from_is_spared()
    {
        for (int day = 1; day <= 3; day++) Old(day);
        string restoring = Path.Combine(_folder, "CarSetupsDataSaveSlot_20200101_120000.bak");

        Write([42], keep: 2, spare: restoring);

        Assert.True(File.Exists(restoring));
        Assert.Equal(["CarSetupsDataSaveSlot_20200102_120000.bak"], _discarded);
    }

    [Fact]
    public void Backups_beside_the_save_move_into_the_folder_and_count()
    {
        Old(1, _dir);
        Old(2, _dir);
        string pinned = Path.Combine(_dir, "CarSetupsDataSaveSlot_keep.bak");
        File.WriteAllBytes(pinned, [7]);

        Write([42], keep: 2);

        Assert.Empty(Directory.EnumerateFiles(_dir, "*_2020*.bak"));
        Assert.True(File.Exists(pinned));    // not the tool's name: left alone
        Assert.Equal(2, Names().Length);
        Assert.Equal(["CarSetupsDataSaveSlot_20200101_120000.bak"], _discarded);
    }

    [Fact]
    public void A_backup_already_in_the_folder_is_not_overwritten_by_one_beside_the_save()
    {
        Old(1);                       // [1] in the folder
        string beside = Old(1, _dir);
        File.WriteAllBytes(beside, [99]);

        Write([42]);

        Assert.Equal([1], File.ReadAllBytes(Path.Combine(_folder, Path.GetFileName(beside))));
        Assert.True(File.Exists(beside));
    }

    [Fact]
    public void A_failed_discard_leaves_the_new_backup_written()
    {
        for (int day = 1; day <= 3; day++) Old(day);

        var bak = BackupStore.Write(_save, [42], 1, _ => throw new IOException("locked"));

        Assert.True(bak.Created);
        Assert.Equal([42], File.ReadAllBytes(bak.Path));
        Assert.Equal(4, Names().Length);
    }
}
