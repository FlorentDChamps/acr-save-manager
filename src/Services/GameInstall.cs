using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ACRSaveManager.Services;

/// <summary>
/// Where Assetto Corsa Rally is: running, or installed by Steam. Each answer is
/// the path of the game's exe, which <see cref="AntiCheat.FolderBeside"/> starts
/// from.
/// </summary>
public static partial class GameInstall
{
    /// <summary>The game's Steam app: its manifest is appmanifest_3917090.acf.</summary>
    public const string SteamAppId = "3917090";

    /// <summary>The game's name in Steam, for a manifest read by name.</summary>
    public const string SteamName = "Assetto Corsa Rally";

    /// <summary>The game's exe under its install folder: an Unreal project named acr.</summary>
    public static readonly string ShippingExe =
        Path.Combine("acr", "Binaries", "Win64", "acr-Win64-Shipping.exe");

    // The game itself, and the launcher that starts it; the game holds the memory.
    private static readonly string[] ProcessNames = ["acr-Win64-Shipping", "acr"];

    // A "key" "value" pair of a Valve KeyValues file, quotes escaped with a backslash.
    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""\s+""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Pair { get; }

    /// <summary>
    /// The exe of the running game, or null: not running, or not to be asked — a
    /// game run as administrator keeps its path from a process that is not.
    /// </summary>
    public static string? RunningExe()
    {
        Process? game = null;
        foreach (var process in ProcessNames.SelectMany(Process.GetProcessesByName))
        {
            try
            {
                if (game == null || process.WorkingSet64 > game.WorkingSet64)
                {
                    game?.Dispose();
                    game = process;
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (InvalidOperationException) { process.Dispose(); }   // gone since it was listed
        }
        if (game == null) return null;
        using (game)
        {
            try { return game.MainModule?.FileName; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; }
        }
    }

    /// <summary>
    /// The exe of each install of the game in the Steam libraries: Steam's own
    /// folder, <paramref name="steamPath"/>, and those its libraryfolders.vdf
    /// lists. The app manifests of each library say what is installed there and
    /// where. The exe is named whether or not it exists, so that a folder of the
    /// game laid out another way is still looked at. A library that cannot be
    /// read is passed over.
    /// </summary>
    public static IReadOnlyList<string> SteamExes(string steamPath)
    {
        List<string> libraries = [steamPath];
        string? vdf = TryRead(Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"));
        if (vdf != null) libraries.AddRange(LibraryFolders(vdf));

        List<string> exes = [];
        foreach (string library in libraries)
        {
            try
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                foreach (string manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    string? installDir = TryRead(manifest) is string acf ? InstallDirOf(acf) : null;
                    if (installDir == null) continue;
                    string exe = Path.GetFullPath(Path.Combine(steamapps, "common", installDir, ShippingExe));
                    if (!exes.Contains(exe, StringComparer.OrdinalIgnoreCase)) exes.Add(exe);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // a library on a drive that is gone, or a folder this user may not list
            }
        }
        return exes;
    }

    /// <summary>
    /// The library folders a libraryfolders.vdf lists: the "path" of each entry,
    /// or, in the older layout, the folder each numbered entry names.
    /// </summary>
    public static IReadOnlyList<string> LibraryFolders(string vdfText)
    {
        List<string> folders = [];
        foreach (Match pair in Pair.Matches(vdfText))
        {
            string key = Unescape(pair.Groups[1].Value);
            string value = Unescape(pair.Groups[2].Value);
            bool path = key == "path"
                        || (key.Length > 0 && key.All(char.IsAsciiDigit) && (value.Contains('\\') || value.Contains('/')));
            if (path && !folders.Contains(value, StringComparer.OrdinalIgnoreCase)) folders.Add(value);
        }
        return folders;
    }

    /// <summary>
    /// The folder under steamapps\common that an app manifest of the game names,
    /// or null for the manifest of another app. The game is known by its app id,
    /// or by its name should the id ever change.
    /// </summary>
    public static string? InstallDirOf(string manifestText)
    {
        string? appId = null, name = null, installDir = null;
        foreach (Match pair in Pair.Matches(manifestText))
        {
            string value = Unescape(pair.Groups[2].Value);
            switch (Unescape(pair.Groups[1].Value))
            {
                case "appid": appId ??= value; break;
                case "name": name ??= value; break;
                case "installdir": installDir ??= value; break;
            }
        }
        bool game = appId == SteamAppId || string.Equals(name, SteamName, StringComparison.OrdinalIgnoreCase);
        return game && !string.IsNullOrEmpty(installDir) ? installDir : null;
    }

    private static string Unescape(string value) => value.Replace("\\\\", "\\").Replace("\\\"", "\"");

    /// <summary>The text of <paramref name="path"/>, or null when it is not there or cannot be read.</summary>
    private static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
