using System.IO;
using System.Text;

namespace ACRSaveManager.Services;

/// <summary>
/// The rallies kept apart from the save: one JSON file each
/// (<see cref="RallyFile"/>) in a folder, named after the rally.
/// </summary>
public static class RallyLibrary
{
    public const string Extension = ".json";

    /// <summary>A file of the library: its rally, or why it does not read.</summary>
    public sealed record Entry(string Path, Rally? Rally, string? Error)
    {
        public string Name => Rally?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    /// <summary>The rallies of <paramref name="folder"/>, by name; none when it does not exist.</summary>
    public static IReadOnlyList<Entry> List(string folder, GameCatalog catalog)
    {
        if (!Directory.Exists(folder)) return [];

        var entries = new List<Entry>();
        foreach (string path in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            try { entries.Add(new Entry(path, RallyFile.Parse(File.ReadAllText(path, Encoding.UTF8), catalog), null)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                entries.Add(new Entry(path, null, ex.Message));
            }
        }
        return [.. entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // What a Windows file name cannot hold, whatever system the code runs on.
    private const string NotInFileName = "<>:\"/\\|?*";

    // The names Windows keeps for its devices, whatever the extension after them.
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// The file a rally of that name goes to: the name, less what a file name
    /// cannot hold, and kept off the names Windows keeps for its devices.
    /// </summary>
    public static string PathFor(string folder, string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim()) sb.Append(c < 32 || NotInFileName.Contains(c) ? '_' : c);
        string file = sb.ToString().Trim().TrimEnd('.');
        if (file.Length == 0) file = "Rally";
        string stem = file.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) file = "_" + file;
        return Path.Combine(folder, file + Extension);
    }

    /// <summary>
    /// A name no other file of the folder has: <paramref name="name"/>, else
    /// "name (2)", "name (3)"… The rally's own file, <paramref name="own"/>,
    /// does not count as another.
    /// </summary>
    public static string FreeName(string folder, string name, string? own = null)
    {
        string candidate = name;
        for (int n = 2; Taken(PathFor(folder, candidate)); n++) candidate = $"{name} ({n})";
        return candidate;

        bool Taken(string path) => File.Exists(path) && !SamePath(path, own);
    }

    /// <summary>True when both name the same file, case aside as on Windows.</summary>
    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes <paramref name="rally"/> to its file, and removes
    /// <paramref name="replacing"/> with <paramref name="remove"/> (deleted when
    /// none is given) when it was the rally's file under another name (a
    /// rename). Returns the file written.
    /// </summary>
    public static string Save(string folder, Rally rally, GameCatalog catalog, string? replacing = null,
                              Action<string>? remove = null)
    {
        Directory.CreateDirectory(folder);
        string path = PathFor(folder, rally.Name);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, RallyFile.ToJson(rally, catalog), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);

        if (replacing is not null && File.Exists(replacing) && !SamePath(replacing, path))
            (remove ?? File.Delete)(replacing);
        return path;
    }
}
