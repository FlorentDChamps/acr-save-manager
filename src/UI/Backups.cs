using System.IO;
using ACRSaveManager.Services;
using Microsoft.Win32;

namespace ACRSaveManager.UI;

/// <summary>Backup and restore of a save file, shared by the Setup and Race tabs.</summary>
internal static class Backups
{
    /// <summary>
    /// How many backups of each save are kept; older ones go to the Recycle
    /// Bin. 0 or less keeps them all. From the .config.
    /// </summary>
    public static int Keep { get; set; } = 10;

    /// <summary>
    /// Timestamped .bak in the backup folder beside <paramref name="savePath"/>,
    /// unless the newest one already holds these bytes. Returns the path of the
    /// backup that does.
    /// </summary>
    public static string Write(string savePath, byte[] contents) => Take(savePath, contents).Path;

    /// <summary>As <see cref="Write"/>, saying whether a new file was written.</summary>
    public static BackupWritten Take(string savePath, byte[] contents, string? spare = null) =>
        BackupStore.Write(savePath, contents, Keep, RecycleBin.Send, spare);

    /// <summary>Status line of a Backup button.</summary>
    public static string Made(BackupWritten bak) => bak.Created
        ? $"✓  Backup created: {Path.GetFileName(bak.Path)}"
        : $"✓  Already backed up, unchanged since: {Path.GetFileName(bak.Path)}";

    /// <summary>
    /// Lets the user pick a .bak and puts it back over <paramref name="savePath"/>.
    /// Returns the status line, or null when nothing was written — the user backed
    /// out, the pick was refused, or an anti-cheat sits beside the game.
    ///
    /// Both saves keep their backups in the same folder under the same extension,
    /// so the pick is checked against <paramref name="kind"/>: a setups backup
    /// restored over the player save would cost the player their profile. The
    /// save being replaced is deliberately not checked — it may be the very file
    /// such a mix-up damaged, and restoring over it is the repair.
    /// </summary>
    public static string? Restore(string savePath, SaveKind kind)
    {
        // A restore writes the save like any other operation: refused on the same grounds.
        if (!AntiCheatGuard.Allows()) return null;

        string folder = BackupStore.FolderFor(savePath);
        var dlg = new OpenFileDialog
        {
            Title = "Select a backup (.bak)",
            Filter = "Backup files (*.bak)|*.bak|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(folder) ? folder : Path.GetDirectoryName(savePath)!,
        };
        if (dlg.ShowDialog() != true) return null;

        string bakPath = dlg.FileName;
        byte[] backup = File.ReadAllBytes(bakPath);

        var found = SaveKind.Identify(backup);
        if (found != kind)
        {
            string what = found is null
                ? "neither a player save nor a car setups save"
                : $"a {found.Title} ({found.FileName})";
            Dialogs.Error(
                $"The selected file is {what}, but this tab restores the {kind.Title} " +
                $"({kind.FileName}).\n\nNothing was written.",
                "Wrong backup file");
            return null;
        }

        // Asked, not imposed: the usual reason to restore is a save that is
        // already broken, and a .bak of that is only clutter. But the file also
        // holds whatever the game wrote since the backup was taken.
        bool? backupFirst = Dialogs.Ask(
            $"Back up the current {kind.Title} before restoring?\n\n" +
            $"  {savePath}\n\n" +
            "It is about to be overwritten, along with anything the game saved in it " +
            "since the backup you picked.",
            "Restore — back up first?");
        if (backupFirst is null) return null;

        // Last gate, and the only one where "No" means stop — same as every other write.
        if (!Dialogs.Confirm(
                "⚠  Make sure Assetto Corsa Rally is closed before restoring.\n\n" +
                "The save file may be locked or overwritten by the game while it is running.\n\n" +
                $"Restore:\n  {savePath}\n\n" +
                $"from:\n  {bakPath}\n\n" +
                (backupFirst == true
                    ? "The current .sav will be backed up, then overwritten."
                    : "⚠  The current .sav will be overwritten, with NO backup.") +
                "\n\nProceed?",
                "Confirm restore — game must be closed"))
        {
            return null;
        }

        // the backup being restored is spared by the rotation this backup runs
        string bakInfo = backupFirst == true
            ? $"  Backup: {Path.GetFileName(Take(savePath, File.ReadAllBytes(savePath), bakPath).Path)}"
            : "";

        // The bytes that were checked are the bytes written, rather than a second
        // read of the .bak by File.Copy — which would also hand the save the
        // backup's old timestamp, unlike every other write this tool makes.
        AtomicFile.Write(savePath, backup);

        return $"✓  Restored from {Path.GetFileName(bakPath)}.{bakInfo}";
    }
}
