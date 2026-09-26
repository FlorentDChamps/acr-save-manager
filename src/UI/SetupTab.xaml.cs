using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ACRSaveManager.Services;
using Microsoft.Win32;

namespace ACRSaveManager.UI;

/// <summary>
/// The car setups panel: what CarSetupsDataSaveSlot.sav holds, and the export,
/// import, rename and remove that act on it.
///
/// It owns the save it works on, as the Race tab owns the player save, and
/// reports what it did through
/// <see cref="StatusChanged"/>.
/// </summary>
public partial class SetupTab : UserControl
{
    private string _setupPath = string.Empty;   // the car setups save
    private string _importPath = string.Empty;   // the .setup file waiting to be imported
    private readonly ObservableCollection<SetupEntry> _setups = [];

    // One instance: System.Text.Json caches its reflection behind it, and a fresh
    // one on every export throws that away (CA1869).
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public SetupTab()
    {
        InitializeComponent();
        GridSetups.ItemsSource = _setups;
    }

    // ── Host wiring ───────────────────────────────────────────────────────────

    /// <summary>Message for the window's status bar.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The save this tab works on, for the window to remember.</summary>
    public string SetupPath => _setupPath;

    /// <summary>Whether an import backs the save up first, likewise remembered.</summary>
    public bool BackupOnImport => ChkBackupOnImport.IsChecked == true;

    /// <summary>Shows <paramref name="setupPath"/>, when there is one to show.</summary>
    public void Configure(string setupPath, bool backupOnImport)
    {
        ChkBackupOnImport.IsChecked = backupOnImport;
        if (setupPath.Length == 0) return;

        _setupPath = setupPath;
        TxtSetupPath.Text = setupPath;
        LoadSetups();
    }

    private void Status(string message) => StatusChanged?.Invoke(message);

    // ══════════════════════════════════════════════════════════════════════════
    // Setup tab — File selection & reload
    // ══════════════════════════════════════════════════════════════════════════

    private void BtnBrowseSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select CarSetupsDataSaveSlot.sav",
            Filter = "Save files (*.sav)|*.sav|All files (*.*)|*.*",
            InitialDirectory = !string.IsNullOrEmpty(_setupPath) && File.Exists(_setupPath)
                                   ? Path.GetDirectoryName(_setupPath)!
                                   : string.Empty,
        };

        if (dlg.ShowDialog() != true) return;

        _setupPath = dlg.FileName;
        TxtSetupPath.Text = _setupPath;
        LoadSetups();
    }

    private void BtnOpenSetupDir_Click(object sender, RoutedEventArgs e)
    {
        string folder = !string.IsNullOrEmpty(_setupPath) && File.Exists(_setupPath)
            ? Path.GetDirectoryName(_setupPath)!
            : string.Empty;

        if (!string.IsNullOrEmpty(folder))
            System.Diagnostics.Process.Start("explorer.exe", folder);
    }

    private void BtnReloadSetups_Click(object sender, RoutedEventArgs e) => LoadSetups();

    // ══════════════════════════════════════════════════════════════════════════
    // Setup tab — Backup / Restore
    // ══════════════════════════════════════════════════════════════════════════

    private void BtnBackupSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSetupFile()) return;

        try
        {
            string bakPath = Backups.Write(_setupPath, File.ReadAllBytes(_setupPath));
            Status($"✓  Backup created: {Path.GetFileName(bakPath)}");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Backup error"); }
    }

    private void BtnRestoreSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSetupFile()) return;

        try
        {
            string? done = Backups.Restore(_setupPath, SaveKind.CarSetups);
            if (done is null) return;

            LoadSetups();
            Status(done);
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Restore error"); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Setup tab — Export / Import
    // ══════════════════════════════════════════════════════════════════════════

    private void BtnExportSetup_Click(object sender, RoutedEventArgs e)
    {
        if (GridSetups.SelectedItem is not SetupEntry entry)
        {
            Dialogs.Warn("Select a setup from the list.", "Selection required");
            return;
        }
        if (!ValidateSetupFile()) return;

        try
        {
            byte[]? data = ReadSaveIfListIsCurrent();
            if (data is null) return;

            byte[] bytes = CarSetupsParser.ExtractEntry(data, entry);
            var exportData = CarSetupsParser.ReadEntryData(bytes);

            string defaultName = CarSetupsParser.BuildExportFileName(exportData);

            var dlg = new SaveFileDialog
            {
                Title = "Export setup",
                Filter = "Setup files (*.setup)|*.setup|All files (*.*)|*.*",
                FileName = defaultName,
            };
            if (dlg.ShowDialog() != true) return;

            // Binary .setup
            File.WriteAllBytes(dlg.FileName, bytes);

            // JSON companion file (same base name, .json extension)
            string jsonPath = Path.ChangeExtension(dlg.FileName, ".json");
            string json = JsonSerializer.Serialize(exportData, IndentedJson);
            File.WriteAllText(jsonPath, json, Encoding.UTF8);

            TxtSetupFile.Text = dlg.FileName;
            Status(
                $"✓  Setup exported: {Path.GetFileName(dlg.FileName)}  ({bytes.Length} bytes)  " +
                $"+ {Path.GetFileName(jsonPath)}");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Export error"); }
    }

    private void BtnSelectImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a .setup file",
            Filter = "Setup files (*.setup)|*.setup|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        _importPath = dlg.FileName;
        TxtSetupFile.Text = dlg.FileName;
        LoadImportVersion();
        Status($"File selected: {Path.GetFileName(dlg.FileName)}");
    }

    private void BtnImportSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSetupFile()) return;

        if (string.IsNullOrEmpty(_importPath) || !File.Exists(_importPath))
        {
            Dialogs.Warn("Select a .setup file first.", "File required");
            return;
        }

        if (!Dialogs.Confirm(
                "⚠  Make sure Assetto Corsa Rally is closed before importing.\n\n" +
                "The save file may be locked or overwritten by the game while it is running.\n\n" +
                "Proceed?",
                "Game must be closed"))
        {
            return;
        }

        try
        {
            byte[] entryBytes = File.ReadAllBytes(_importPath);
            CarSetupsParser.ValidateEntry(entryBytes);   // before any field is read from it

            string carName = CarSetupsParser.ReadEntryCarName(entryBytes);
            string setupName = CarSetupsParser.ReadEntrySetupName(entryBytes);

            // The entry keeps its own spelling of the car id, but it joins the
            // block already in the save — so that is the name to show.
            string carShown = _setups.FirstOrDefault(s => SameCar(s.CarName, carName))?.CarName
                              ?? carName;

            // Same warning as for a .race file, from the build stamped in the
            // entry itself. Only the release is compared: two builds of the same
            // release differ by a number that says nothing about the layout.
            string fromBuild = CarSetupsParser.ReadEntryGameVersion(entryBytes);
            string toBuild = NewestGameVersion(_setups);
            if (fromBuild.Length > 0 && toBuild.Length > 0 &&
                Release(fromBuild) != Release(toBuild) &&
                !Dialogs.Confirm(
                    $"This setup was written by game build {fromBuild}, while the newest " +
                    $"entry in the save comes from {toBuild}.\n\n" +
                    "Setup entries have stayed compatible across releases so far.\n\nImport anyway?",
                    "Game build mismatch"))
            {
                return;
            }

            // Always prompt for name; suggest unique candidate if collision
            string defaultName = setupName;
            if (_setups.Any(s => SameCar(s.CarName, carName) && s.SetupName == defaultName))
            {
                int idx = 2;
                while (_setups.Any(s => SameCar(s.CarName, carName) && s.SetupName == defaultName))
                    defaultName = $"{setupName}_{idx++}";
            }

            string? chosenName = ShowInputDialog(
                $"Setup name for import (car: {carShown}):",
                "Import — set name",
                defaultName);

            if (string.IsNullOrWhiteSpace(chosenName)) return;
            chosenName = chosenName.Trim();

            if (_setups.Any(s => SameCar(s.CarName, carName) && s.SetupName == chosenName))
            {
                Dialogs.Warn($"Setup \"{chosenName}\" already exists for {carShown}. Choose another name.", "Name conflict");
                return;
            }

            if (chosenName != setupName)
                entryBytes = CarSetupsParser.RenameEntry(entryBytes, chosenName);

            // Read only now, after the last dialog: the game may have saved while
            // they were open, and the bytes written must be the ones on disk.
            byte[]? original = ReadSaveIfListIsCurrent();
            if (original is null) return;

            byte[] modified = CarSetupsParser.InsertEntry(original, entryBytes);

            // Re-read the result before anything touches the disk: on failure the
            // save is left exactly as it was, and no stray .bak is created.
            if (!Dialogs.Verified(() => CarSetupsParser.VerifyIntegrity(modified, _setups.Count + 1))) return;

            // Auto-backup

            string bakPath = ChkBackupOnImport.IsChecked == true
                ? Backups.Write(_setupPath, original)
                : "";

            AtomicFile.Write(_setupPath, modified);

            LoadSetups();
            string bakInfo = ChkBackupOnImport.IsChecked == true
                ? $"  Backup: {Path.GetFileName(bakPath)}"
                : "";
            Status($"✓  Setup imported for {carShown}.{bakInfo}");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Import error"); }
    }

    private void BtnRenameSetup_Click(object sender, RoutedEventArgs e)
    {
        if (GridSetups.SelectedItem is not SetupEntry entry)
        {
            Dialogs.Warn("Select a setup from the list.", "Selection required");
            return;
        }
        if (!ValidateSetupFile()) return;

        if (!Dialogs.Confirm(
                "⚠  Make sure Assetto Corsa Rally is closed before renaming.\n\n" +
                "The save file may be locked or overwritten by the game while it is running.\n\n" +
                "A backup will be created automatically.\n\n" +
                "Proceed?",
                "Game must be closed"))
        {
            return;
        }

        string? newName = ShowInputDialog(
            $"New name for \"{entry.SetupName}\" ({entry.CarName}):",
            "Rename setup",
            entry.SetupName);

        if (string.IsNullOrWhiteSpace(newName)) return;
        newName = newName.Trim();

        if (newName == entry.SetupName) return;

        if (_setups.Any(s => SameCar(s.CarName, entry.CarName) && s.SetupName == newName))
        {
            Dialogs.Warn($"Setup \"{newName}\" already exists for {entry.CarName}.", "Name conflict");
            return;
        }

        try
        {
            byte[]? data = ReadSaveIfListIsCurrent();
            if (data is null) return;

            byte[] entryBytes = CarSetupsParser.ExtractEntry(data, entry);
            byte[] renamed = CarSetupsParser.RenameEntry(entryBytes, newName);
            byte[] modified = CarSetupsParser.ReplaceEntry(data, entry, renamed);

            // Re-read the result before anything touches the disk: on failure the
            // save is left exactly as it was, and no stray .bak is created.
            if (!Dialogs.Verified(() => CarSetupsParser.VerifyIntegrity(modified, _setups.Count))) return;

            // Auto-backup: the new name rarely has the old one's byte length, so
            // this rewrites the whole file around the entry — same risk as Remove.
            string bakPath = Backups.Write(_setupPath, data);

            AtomicFile.Write(_setupPath, modified);
            LoadSetups();
            Status($"✓  Renamed \"{entry.SetupName}\" → \"{newName}\".  Backup: {Path.GetFileName(bakPath)}");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Rename error"); }
    }

    private void BtnRemoveSetup_Click(object sender, RoutedEventArgs e)
    {
        if (GridSetups.SelectedItem is not SetupEntry entry)
        {
            Dialogs.Warn("Select a setup from the list.", "Selection required");
            return;
        }
        if (!ValidateSetupFile()) return;

        if (!Dialogs.Confirm(
                $"⚠  Make sure Assetto Corsa Rally is closed.\n\n" +
                $"Remove \"{entry.SetupName}\" ({entry.CarName}) permanently from the sav?\n\n" +
                $"A backup will be created automatically.",
                "Confirm remove — game must be closed"))
        {
            return;
        }

        try
        {
            byte[]? original = ReadSaveIfListIsCurrent();
            if (original is null) return;

            byte[] modified = CarSetupsParser.RemoveEntry(original, entry);

            // Re-read the result before anything touches the disk: on failure the
            // save is left exactly as it was, and no stray .bak is created.
            if (!Dialogs.Verified(() => CarSetupsParser.VerifyIntegrity(modified, _setups.Count - 1))) return;

            // Auto-backup
            string bakPath = Backups.Write(_setupPath, original);

            AtomicFile.Write(_setupPath, modified);
            LoadSetups();
            Status($"✓  Removed \"{entry.SetupName}\" ({entry.CarName}).  Backup: {Path.GetFileName(bakPath)}");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Remove error"); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Setup helpers
    // ══════════════════════════════════════════════════════════════════════════

    private void LoadSetups()
    {
        _setups.Clear();
        TxtSetupVersion.Text = "";
        if (string.IsNullOrEmpty(_setupPath) || !File.Exists(_setupPath)) return;

        try
        {
            byte[] data = File.ReadAllBytes(_setupPath);
            var entries = CarSetupsParser.ParseSetups(data);
            foreach (var entry in entries) _setups.Add(entry);
            // File-level marker only: the game build belongs to each entry, and
            // the Build column shows it per row.
            TxtSetupVersion.Text = $"UE {GvasHeader.ReadInfo(data).EngineVersion}";
            Status($"✓  {entries.Count} setup(s) loaded from {Path.GetFileName(_setupPath)}");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Parse error"); }
    }

    /// <summary>
    /// True when two car ids name the same car. The id is an Unreal FName, which
    /// the engine compares without case and re-spells on every save: the same car
    /// appears as "Hyundaii20NRally2" in one file and "HyundaiI20NRally2" in the
    /// next. Setup names, in contrast, are the player's own and stay case
    /// sensitive.
    /// </summary>
    private static bool SameCar(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Highest game build found among the entries. Each entry keeps the build that
    /// wrote it, so a file carries a mix — the newest one is the build that last
    /// touched the save.
    /// </summary>
    private static string NewestGameVersion(IEnumerable<SetupEntry> entries)
    {
        string best = "";
        foreach (var e in entries)
        {
            if (e.GameVersion.Length > 0 && CompareVersions(e.GameVersion, best) > 0)
                best = e.GameVersion;
        }

        return best;
    }

    /// <summary>Release part of a build string: "0.6.0.100866" becomes "0.6".</summary>
    private static string Release(string build)
    {
        string[] parts = build.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : build;
    }

    /// <summary>Numeric compare of dotted builds ("0.6.0.100866"), missing parts count as 0.</summary>
    private static int CompareVersions(string a, string b)
    {
        string[] pa = a.Split('.'), pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Validation
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read the save and prove the displayed list still describes it, so that
    /// every operation works on the bytes its offsets were measured on. The game
    /// rewrites this file whenever it saves, and a row whose entry has moved
    /// since would otherwise remove or rename whatever now sits at that offset.
    /// Returns null — after reloading the list and saying nothing was written —
    /// when the file no longer matches.
    /// </summary>
    private byte[]? ReadSaveIfListIsCurrent()
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(_setupPath);
            // SetupEntry is a positional record, so this compares the offsets too.
            if (CarSetupsParser.ParseSetups(data).SequenceEqual(_setups)) return data;
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Read error"); return null; }

        LoadSetups();
        Dialogs.Warn(
            "The save file changed on disk since the list was loaded, so the list has " +
            "just been reloaded.\n\nNothing was written — check your selection and try again.",
            "File changed on disk");
        return null;
    }

    private bool ValidateSetupFile()
    {
        if (string.IsNullOrWhiteSpace(_setupPath))
        {
            Dialogs.Warn("Select a CarSetupsDataSaveSlot.sav file first.", "File required");
            return false;
        }
        if (!File.Exists(_setupPath))
        {
            Dialogs.Error($"File not found:\n{_setupPath}", "File not found");
            return false;
        }
        return true;
    }

    /// <summary>Build stamped in the .setup file waiting to be imported.</summary>
    private void LoadImportVersion()
    {
        TxtSetupFileVersion.Text = "";
        if (string.IsNullOrEmpty(_importPath) || !File.Exists(_importPath)) return;

        try { TxtSetupFileVersion.Text = CarSetupsParser.ReadEntryGameVersion(File.ReadAllBytes(_importPath)); }
        catch { /* unreadable entry: the import itself will report it */ }
    }

    /// <summary>
    /// Cap on a typed setup name. The game's own limit is unknown; this is well
    /// above any name seen in real saves and keeps the export file name, which
    /// embeds it, far from the Windows path limit.
    /// </summary>
    private const int MaxSetupNameLength = 64;

    /// <summary>
    /// Minimal WPF input dialog.  Returns trimmed text, or null if cancelled.
    /// </summary>
    private string? ShowInputDialog(string prompt, string title, string defaultValue)
    {
        var window = Window.GetWindow(this)!;

        var win = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = window,
            // Same dictionary instance as the main window: the dialog picks up the
            // flat button/textbox styles and follows a live theme switch.
            Resources = window.Resources,
            Background = (Brush)window.Resources["Brush.Window"],
            Foreground = (Brush)window.Resources["Brush.Text"],
        };

        var sp = new StackPanel { Margin = new Thickness(14) };

        sp.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var tb = new TextBox
        {
            Text = defaultValue,
            MaxLength = MaxSetupNameLength,
            Margin = new Thickness(0, 0, 0, 10),
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        sp.Children.Add(tb);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        string? result = null;

        var ok = new Button
        {
            Content = "OK",
            Width = 74,
            IsDefault = true,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };

        ok.Click += (_, _) => { result = tb.Text.Trim(); win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };

        btnPanel.Children.Add(ok);
        btnPanel.Children.Add(cancel);
        sp.Children.Add(btnPanel);

        win.Content = sp;
        tb.Loaded += (_, _) => { tb.SelectAll(); tb.Focus(); };

        win.ShowDialog();
        return result;
    }
}
