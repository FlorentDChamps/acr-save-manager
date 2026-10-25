using System.IO;
using System.Text.Json;

namespace ACRSaveManager.UI;

/// <summary>
/// Whole-UI state saved next to the exe as a plain JSON .config, so the theme
/// and the window layout survive a restart. Best-effort: a missing or corrupt
/// file just yields defaults, and a write failure is swallowed.
/// </summary>
public sealed class AppSettings
{
    public bool Dark { get; set; }
    public bool HasTheme { get; set; }   // false on first run => follow the OS
    // Latest update version dismissed with the "Discard" button. A different
    // release version is still offered normally on the next startup.
    public string DismissedUpdateVersion { get; set; } = "";

    // main window bounds (null on first run => the XAML defaults are used)
    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double? WinWidth { get; set; }
    public double? WinHeight { get; set; }
    public bool WinMaximized { get; set; }

    // last files opened from the pickers, so a session resumes where it stopped
    public string SetupPath { get; set; } = "";
    public string SavePath { get; set; } = "";
    public bool BackupOnImport { get; set; } = true;
    // backups kept per save, older ones sent to the Recycle Bin; 0 keeps all
    public int BackupsKept { get; set; } = 10;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>ACRSaveManager.config, alongside the exe (app base dir, which is the
    /// real exe folder even for a single-file publish).</summary>
    public static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "ACRSaveManager.config");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch { /* corrupt / unreadable -> defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); }
        catch { /* read-only dir / locked -> skip, non-fatal */ }
    }
}
