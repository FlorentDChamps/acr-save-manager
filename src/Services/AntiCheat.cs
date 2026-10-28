using System.IO;

namespace ACRSaveManager.Services;

/// <summary>
/// The folders an anti-cheat sits in beside a game, and the look for them that
/// keeps this tool from writing to the saves of a game one watches: editing them
/// could pass for cheating there. None ships with Assetto Corsa Rally today. The
/// check is the one ACR Overlay makes before reading the game's memory.
/// </summary>
public static class AntiCheat
{
    /// <summary>What Easy Anti-Cheat and BattlEye install beside a game.</summary>
    public static readonly string[] Folders = ["EasyAntiCheat", "BattlEye"];

    /// <summary>
    /// The anti-cheat folder beside <paramref name="exePath"/> or up to three
    /// levels above it, or null. From the game's exe,
    /// acr\Binaries\Win64\acr-Win64-Shipping.exe, the third level is the install
    /// folder, where such a folder usually goes. The exe itself need not exist: a
    /// folder of the game laid out another way is still looked at.
    /// </summary>
    public static string? FolderBeside(string exePath)
    {
        string? folder = Path.GetDirectoryName(exePath);
        for (int level = 0; level < 4 && !string.IsNullOrEmpty(folder); level++)
        {
            foreach (string name in Folders)
            {
                string candidate = Path.Combine(folder, name);
                if (Directory.Exists(candidate)) return candidate;
            }
            folder = Path.GetDirectoryName(folder);
        }
        return null;
    }
}
