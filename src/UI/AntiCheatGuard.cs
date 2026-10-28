using System.IO;
using System.Security;
using ACRSaveManager.Services;
using Microsoft.Win32;

namespace ACRSaveManager.UI;

/// <summary>
/// Keeps every write out of a game an anti-cheat watches (<see cref="AntiCheat"/>).
/// The game is looked for running and in the Steam libraries, before each write
/// rather than once: an anti-cheat can arrive with any update of the game.
/// </summary>
internal static class AntiCheatGuard
{
    /// <summary>
    /// Whether a write may go ahead. When it may not, says why, and that nothing
    /// was written.
    /// </summary>
    public static bool Allows()
    {
        string? folder = Found();
        if (folder == null) return true;
        Dialogs.Error(
            $"⚠  An anti-cheat folder sits beside Assetto Corsa Rally:\n\n  {folder}\n\n" +
            "Editing the saves of a game an anti-cheat watches could pass for cheating, " +
            "so ACR Save Manager writes nothing to them while that folder is there. " +
            "Reading, viewing, exporting and backing up still work.\n\n" +
            "Nothing was written.",
            "Write refused — anti-cheat found");
        return false;
    }

    /// <summary>Status line for the window at startup, when every write would be refused.</summary>
    public static string Notice(string folder) =>
        $"⚠  Anti-cheat folder found beside the game — nothing will be written to its saves: {folder}";

    /// <summary>
    /// The anti-cheat folder found beside the game, or null: none, or no game
    /// found — one installed outside Steam is only seen while it runs.
    /// </summary>
    public static string? Found()
    {
        if (GameInstall.RunningExe() is string running && AntiCheat.FolderBeside(running) is string beside)
            return beside;

        string? steam = SteamPath();
        if (steam == null) return null;
        foreach (string exe in GameInstall.SteamExes(steam))
            if (AntiCheat.FolderBeside(exe) is string found) return found;
        return null;
    }

    /// <summary>Steam's own folder, from its registry keys; null without Steam.</summary>
    private static string? SteamPath()
    {
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (user?.GetValue("SteamPath") is string path && path.Length > 0)
                return Path.GetFullPath(path);
            // Steam is a 32-bit program: its machine-wide key sits under WOW6432Node.
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (machine?.GetValue("InstallPath") is string install && install.Length > 0)
                return Path.GetFullPath(install);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException
                                         or ArgumentException or NotSupportedException)
        {
            // a registry this user may not read, or an odd value in it: no Steam to look in
        }
        return null;
    }
}
