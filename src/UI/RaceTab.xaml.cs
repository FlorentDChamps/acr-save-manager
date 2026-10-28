using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ACRSaveManager.Services;
using Microsoft.Win32;

namespace ACRSaveManager.UI;

/// <summary>
/// Extract-and-restore panel for the player save's race configurations: every event
/// config, then the stage, car and weather selection past them
/// (<see cref="EventList"/>). No single block names a stage or a car, so they
/// travel together in one .race file, and the user ticks which of its parts to
/// write.
/// </summary>
public partial class RaceTab : UserControl
{
    private const string Extension = ".race";
    private const string RaceFilter = "Race files (*.race)|*.race|All files (*.*)|*.*";

    private string _savePath = string.Empty;   // the player save
    private string _racePath = string.Empty;   // the exported .race file

    // One instance: System.Text.Json caches its reflection behind these, and a
    // fresh one on every export throws that away (CA1869).
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Tag of the checkbox standing for the selection past the list.</summary>
    private static readonly object SelectionTag = new();

    public RaceTab() => InitializeComponent();

    // ── Host wiring ───────────────────────────────────────────────────────────

    /// <summary>Message for the window's status bar.</summary>
    public event Action<string>? StatusChanged;

    public string SavePath => _savePath;

    /// <summary>Shows the save to work on, picked by the window at startup.</summary>
    public void ShowSavePath(string path)
    {
        _savePath = path;
        TxtSavePath.Text = path;
        LoadSaveVersion();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // File selection
    // ══════════════════════════════════════════════════════════════════════════

    private void BtnBrowseSave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select player data save file",
            Filter = "Save files (*.sav)|*.sav|All files (*.*)|*.*",
            InitialDirectory = !string.IsNullOrEmpty(_savePath) && File.Exists(_savePath)
                                   ? Path.GetDirectoryName(_savePath)!
                                   : string.Empty,
        };

        if (dlg.ShowDialog() != true) return;

        ShowSavePath(dlg.FileName);
        Status($"File selected: {Path.GetFileName(_savePath)}");
    }

    private void BtnBrowseRace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select race file",
            Filter = RaceFilter,
            InitialDirectory = !string.IsNullOrEmpty(_racePath) && File.Exists(_racePath)
                                   ? Path.GetDirectoryName(_racePath)!
                                   : string.Empty,
        };

        if (dlg.ShowDialog() != true) return;

        SetRacePath(dlg.FileName);
        Status($"Race file: {Path.GetFileName(_racePath)}");
    }

    private void BtnOpenSaveDir_Click(object sender, RoutedEventArgs e)
    {
        string folder = !string.IsNullOrEmpty(_savePath) && File.Exists(_savePath)
            ? Path.GetDirectoryName(_savePath)!
            : string.Empty;

        if (!string.IsNullOrEmpty(folder))
            System.Diagnostics.Process.Start("explorer.exe", folder);
    }

    private void BtnOpenRaceDir_Click(object sender, RoutedEventArgs e)
    {
        // With no file picked yet, the folder Read & extract saves into by default.
        string folder = !string.IsNullOrEmpty(_racePath) && File.Exists(_racePath)
            ? Path.GetDirectoryName(_racePath)!
            : AppContext.BaseDirectory;

        System.Diagnostics.Process.Start("explorer.exe", folder);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Read / Write / Backup / Restore
    // ══════════════════════════════════════════════════════════════════════════

    private void BtnRead_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;

        try
        {
            byte[] data = File.ReadAllBytes(_savePath);
            var list = EventList.Find(data);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var dlg = new SaveFileDialog
            {
                Title = "Export race configurations",
                Filter = RaceFilter,
                FileName = $"Race_{ts}{Extension}",
                InitialDirectory = AppContext.BaseDirectory,
            };

            if (dlg.ShowDialog() != true) return;

            // The list records no version, so stamp the save's own build on the
            // export: Write can then tell when it is about to write race
            // settings that came from another release.
            var info = GvasHeader.ReadInfo(data);
            var meta = new RaceFileMeta(
                info.EngineVersion,
                info.CustomVersionCount,
                DateTime.UtcNow.ToString("o"),
                AppInfo.Version);

            File.WriteAllBytes(dlg.FileName, RaceFile.Wrap(data[list.Start..list.End], meta));

            SetRacePath(dlg.FileName);

            Status(
                $"✓  Race configurations extracted: {list.End - list.Start} bytes " +
                $"(0x{list.Start:X4}–0x{list.End:X4}), UE {info.EngineVersion} → {dlg.FileName}"
            );
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Read error"); }
    }

    private void BtnExportPlayerJson_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;

        try
        {
            byte[] data = File.ReadAllBytes(_savePath);
            var json = PlayerSaveJson.Export(data, _savePath);

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string def = $"{Path.GetFileNameWithoutExtension(_savePath)}_{ts}.json";

            var dlg = new SaveFileDialog
            {
                Title = "Export player save as JSON",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = def,
                InitialDirectory = Path.GetDirectoryName(_savePath)!,
            };
            if (dlg.ShowDialog() != true) return;

            string jsonText = json.ToJsonString(IndentedJson);
            File.WriteAllText(dlg.FileName, jsonText, Encoding.UTF8);

            Status($"✓  JSON exported: {Path.GetFileName(dlg.FileName)}  ({data.Length} bytes parsed)");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Export error"); }
    }

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;

        try
        {
            Status(Backups.Made(Backups.Take(_savePath, File.ReadAllBytes(_savePath))));
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Backup error"); }
    }

    private void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;

        try
        {
            string? done = Backups.Restore(_savePath, SaveKind.PlayerData);
            if (done is null) return;

            LoadSaveVersion();   // a backup may come from an older game build
            Status(done);
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Restore error"); }
    }

    private void BtnWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;
        if (!ValidateRaceFile()) return;
        if (!AntiCheatGuard.Allows()) return;

        try
        {
            byte[] original = File.ReadAllBytes(_savePath);

            // A damaged or outdated file, or bytes lost on the way, would
            // otherwise only fail the integrity check, blamed on the save format.
            byte[] file;
            RaceFileMeta? meta;
            EventList source;
            try
            {
                (file, meta) = RaceFile.Read(File.ReadAllBytes(_racePath));
                source = EventList.ReadFile(file);
            }
            catch (InvalidDataException ex)
            {
                Dialogs.Error($"{ex.Message}\n\nNothing was written.", "Unreadable race file");
                return;
            }

            var ticked = PartsPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).ToList();
            string[] events = [.. ticked.Select(c => c.Tag).OfType<string>()];
            bool selection = ticked.Any(c => c.Tag == SelectionTag);
            if (events.Length == 0 && !selection)
            {
                Dialogs.Warn("Tick at least one part of the file to write.", "Nothing ticked");
                return;
            }

            if (!EngineAgrees(meta, original)) return;

            var write = EventList.Replace(original, file, events, selection);
            if (write.Written.Count == 0 && !write.SelectionWritten)
            {
                Dialogs.Error("None of the ticked events is in this save.\n\nNothing was written.",
                              "Nothing to write");
                return;
            }

            string origin = meta is null ? "unknown" : $"UE {meta.Engine}";
            string written = write.Written.Count == 0
                ? "none"
                : string.Join(", ", write.Written.Select(name => PartName(file, source, name)));
            string chosen = write.SelectionWritten
                ? $"{source.Selection!.Stage}, {source.Selection.Car}"
                : "kept";
            string skipped = write.Missing.Count == 0
                ? ""
                : $"  Not in this save, skipped : {string.Join(", ", write.Missing.Select(ShortName))}\n";

            string summary =
                $"Replace the race configurations in:\n  {_savePath}\n\n" +
                $"  Origin    : {origin}\n" +
                $"  Events    : {written}\n" +
                $"  Selection : {chosen}\n" +
                skipped +
                $"\n  size_field_1 : {write.OldSizeField} → {write.NewSizeField}\n\n" +
                $"⚠  This will permanently modify the file.";

            if (!Dialogs.Confirm(summary, "Confirm write")) return;

            // Re-read the result before anything touches the disk: on failure the
            // save is left exactly as it was, and no stray .bak is created.
            if (!Dialogs.Verified(() => EventList.VerifyIntegrity(original, write, file)))
                return;

            Status($"Backup created: {Path.GetFileName(WriteBackup(original))}");

            AtomicFile.Write(_savePath, write.Data);

            Status($"✓  Race configurations written. New file size: {write.Data.Length} bytes.");

            Dialogs.Info(
                $"Write successful!\n\n" +
                $"Events: {written}\n" +
                $"Selection: {chosen}\n" +
                $"size_field_1 updated: {write.NewSizeField}\n" +
                $"Total file size: {write.Data.Length} bytes",
                "Success");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Write error"); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Race configurations from another engine build may not match this save's
    /// layout. Worth a warning, not a refusal: 0.4 and 0.6 event configs are
    /// interchangeable. False when the user backs out.
    /// </summary>
    private static bool EngineAgrees(RaceFileMeta? meta, byte[] save)
    {
        string targetEngine = GvasHeader.ReadInfo(save).EngineVersion;
        return meta is null || meta.Engine == targetEngine ||
            Dialogs.Confirm(
                $"This file was exported from a save built on UE {meta.Engine}, " +
                $"but the target save is UE {targetEngine}.\n\n" +
                "The layout may differ between game releases.\n\nWrite it anyway?",
                "Engine build mismatch");
    }

    /// <summary>"DefaultFreePractice" as the game's menus would say it.</summary>
    private static string ShortName(string eventName) =>
        eventName.StartsWith("Default", StringComparison.Ordinal) ? eventName["Default".Length..] : eventName;

    /// <summary>
    /// The short name, and for the rally weekend the rally it holds — the only
    /// event config that names its content.
    /// </summary>
    private static string PartName(byte[] data, EventList list, string eventName)
    {
        string name = ShortName(eventName);
        if (eventName != "DefaultRally") return name;

        try
        {
            int start = list.Events.First(ev => ev.Name == eventName).Start;
            return $"{name} ({SaveFileParser.ReadRallyName(data, start)})";
        }
        catch (InvalidDataException) { return name; }
        catch (ArgumentOutOfRangeException) { return name; }
    }

    /// <summary>Timestamped .bak in the backup folder beside the save. Returns its path.</summary>
    private string WriteBackup(byte[] contents) => Backups.Write(_savePath, contents);

    private void SetRacePath(string path)
    {
        _racePath = path;
        TxtRacePath.Text = path;
        LoadRaceVersion();
        LoadParts();
    }

    /// <summary>
    /// One ticked checkbox per part of the picked .race file: each event, then
    /// the selection. A file from another player can hold a weekend worth taking
    /// and a Free Practice not worth losing yours for.
    /// </summary>
    private void LoadParts()
    {
        PartsPanel.Children.Clear();
        PartsPanel.Visibility = Visibility.Collapsed;
        if (string.IsNullOrEmpty(_racePath) || !File.Exists(_racePath)) return;

        byte[] file;
        EventList list;
        try
        {
            file = RaceFile.Read(File.ReadAllBytes(_racePath)).Content;
            list = EventList.ReadFile(file);
        }
        catch { return; /* the badge and the write report an unreadable file */ }

        foreach (var ev in list.Events)
            PartsPanel.Children.Add(Part(PartName(file, list, ev.Name), ev.Name, ev.Name));
        if (list.Selection is { } s)
        {
            PartsPanel.Children.Add(Part($"Stage, car and weather: {s.Stage}, {s.Car}", SelectionTag,
                "The stage, car, start time and weather past the event list, as the Free Practice menu shows them"));
        }
        PartsPanel.Visibility = Visibility.Visible;
    }

    private static CheckBox Part(string label, object tag, string tip) => new()
    {
        Content = label,
        Tag = tag,
        ToolTip = tip,
        IsChecked = true,
        Margin = new Thickness(0, 0, 16, 6),
    };

    /// <summary>
    /// Engine build of the player save. Unlike a setup entry, the file records no
    /// game version at all — the UE version and the size of the custom-version
    /// table are the only markers that move between releases.
    /// </summary>
    private void LoadSaveVersion()
    {
        TxtSaveVersion.Text = "";
        if (string.IsNullOrEmpty(_savePath) || !File.Exists(_savePath)) return;

        try
        {
            TxtSaveVersion.Text = $"UE {GvasHeader.ReadInfo(File.ReadAllBytes(_savePath)).EngineVersion}";
        }
        catch { /* unreadable header: leave the badge empty rather than guess */ }
    }

    /// <summary>
    /// Engine build recorded in the .race file's container. A file that fails
    /// the container's own checks — damaged, or an event block from an older
    /// build — says so, since Write is going to refuse it.
    /// </summary>
    private void LoadRaceVersion()
    {
        TxtRaceVersion.Text = "";
        TxtRaceVersion.ToolTip = null;
        if (string.IsNullOrEmpty(_racePath) || !File.Exists(_racePath)) return;

        try
        {
            var (_, meta) = RaceFile.Read(File.ReadAllBytes(_racePath));
            TxtRaceVersion.Text = meta is null ? "no version" : $"UE {meta.Engine}";
        }
        catch (InvalidDataException ex)
        {
            TxtRaceVersion.Text = "unreadable";
            TxtRaceVersion.ToolTip = ex.Message;
        }
        catch { /* unreadable file: the write itself will report it */ }
    }

    private void Status(string message) => StatusChanged?.Invoke(message);

    private bool ValidateSaveFile()
    {
        if (string.IsNullOrWhiteSpace(_savePath))
        {
            Dialogs.Warn("Please select a .sav save file first.", "File required");
            return false;
        }
        if (!File.Exists(_savePath))
        {
            Dialogs.Error($"File not found:\n{_savePath}", "File not found");
            return false;
        }
        return true;
    }

    private bool ValidateRaceFile()
    {
        if (string.IsNullOrWhiteSpace(_racePath))
        {
            Dialogs.Warn("Please select a race file.", "File required");
            return false;
        }
        if (!File.Exists(_racePath))
        {
            Dialogs.Error($"Race file not found:\n{_racePath}", "File not found");
            return false;
        }
        if (new FileInfo(_racePath).Length == 0)
        {
            Dialogs.Warn("The race file is empty.", "Empty file");
            return false;
        }
        return true;
    }
}
