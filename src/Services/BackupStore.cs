using System.IO;
using System.Text.RegularExpressions;

namespace ACRSaveManager.Services;

/// <summary>What <see cref="BackupStore.Write"/> left: the backup, and whether it is new.</summary>
public readonly record struct BackupWritten(string Path, bool Created);

/// <summary>
/// Where the backups of a save go, and which of them are this tool's.
///
/// They sit in a folder of their own next to the save, named as the tool
/// writes them: {SaveName}_{yyyyMMdd_HHmmss}.bak. Only files of exactly that
/// name are counted, moved or rotated out: one the player renamed, to keep
/// it, is never touched.
/// </summary>
public static partial class BackupStore
{
    public const string FolderName = "ACRSaveManager Backups";

    /// <summary>The backup folder of <paramref name="savePath"/>, beside it.</summary>
    public static string FolderFor(string savePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(savePath))!, FolderName);

    /// <summary>Whether <paramref name="file"/> is named as a backup of <paramref name="savePath"/>.</summary>
    public static bool IsBackupOf(string file, string savePath) =>
        Path.GetFileName(file) is var name
        && name.StartsWith(Path.GetFileNameWithoutExtension(savePath) + "_", StringComparison.OrdinalIgnoreCase)
        && name.Length == Path.GetFileNameWithoutExtension(savePath).Length + 20
        && Stamp().IsMatch(name);

    // the 20 characters after the save name: _yyyyMMdd_HHmmss.bak
    [GeneratedRegex(@"_[0-9]{8}_[0-9]{6}\.bak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();

    /// <summary>The backups of <paramref name="savePath"/> in <paramref name="folder"/>, newest first.</summary>
    public static List<string> List(string folder, string savePath) =>
        !Directory.Exists(folder) ? [] :
        [.. Directory.EnumerateFiles(folder, "*.bak")
            .Where(f => IsBackupOf(f, savePath))
            // the stamp is the only part that differs, and it sorts as it reads
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The backups past the newest <paramref name="keep"/>, which the rotation
    /// removes. None when <paramref name="keep"/> is 0 or less: keep them all.
    /// </summary>
    public static IEnumerable<string> Surplus(IReadOnlyList<string> newestFirst, int keep) =>
        keep <= 0 ? [] : newestFirst.Skip(keep);

    /// <summary>
    /// Back <paramref name="contents"/> up as a copy of <paramref name="savePath"/>,
    /// then hand every backup past the newest <paramref name="keep"/> to
    /// <paramref name="discard"/> — but <paramref name="spare"/>, the backup a
    /// restore is reading from.
    ///
    /// No file is written when the newest backup already holds these very
    /// bytes: that backup is returned, with Created false. Backups written
    /// next to the save, where this tool used to put them, move into the
    /// folder first, so that they count too.
    /// </summary>
    public static BackupWritten Write(string savePath, byte[] contents, int keep,
                                      Action<string> discard, string? spare = null)
    {
        string folder = FolderFor(savePath);
        Directory.CreateDirectory(folder);
        MoveIn(savePath, folder);

        var existing = List(folder, savePath);
        if (existing.Count > 0 && SameBytes(existing[0], contents))
            return new BackupWritten(existing[0], false);

        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(savePath)}_{ts}.bak");
        File.WriteAllBytes(path, contents);

        foreach (string old in Surplus(List(folder, savePath), keep))
        {
            bool spared = spare is not null && string.Equals(
                Path.GetFullPath(old), Path.GetFullPath(spare), StringComparison.OrdinalIgnoreCase);
            if (spared) continue;
            // The backup is written: one old file left over is only clutter,
            // and the next write tries again.
            try { discard(old); }
            catch (Exception) { }
        }
        return new BackupWritten(path, true);
    }

    /// <summary>Backups left beside the save by earlier versions, moved into the folder.</summary>
    private static void MoveIn(string savePath, string folder)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(savePath))!;
        foreach (string file in Directory.EnumerateFiles(dir, "*.bak").Where(f => IsBackupOf(f, savePath)).ToList())
        {
            string target = Path.Combine(folder, Path.GetFileName(file));
            if (File.Exists(target)) continue;   // never overwrite a backup
            try { File.Move(file, target); }
            catch (IOException) { }               // left where it is, as before
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool SameBytes(string file, byte[] contents)
    {
        try
        {
            return new FileInfo(file).Length == contents.Length
                && File.ReadAllBytes(file).AsSpan().SequenceEqual(contents);
        }
        catch (IOException) { return false; }    // unreadable: write a fresh one
        catch (UnauthorizedAccessException) { return false; }
    }
}
