using System.IO;
using System.Windows;
using System.Windows.Controls;
using ACRSaveManager.Services;
using Microsoft.Win32;

namespace ACRSaveManager.UI;

/// <summary>
/// The Rally tab: the save's two Rally Weekends and the rallies of the
/// library (a folder of JSON files next to the app) on the left, the rally
/// picked in an editor that holds every option, offline and online alike, on
/// the right. A rally is saved to the library, or written into one or both of
/// the save's weekends, each laid out as the weekend it replaces.
/// </summary>
public partial class RallyTab : UserControl
{
    private const string OfflineEvent = "DefaultRally";
    private const string OnlineEvent = "DefaultOnlineRally";

    // One instance: System.Text.Json caches its reflection behind these, and a
    // fresh one on every export throws that away (CA1869).
    private static readonly System.Text.Json.JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>A rally in one of the lists on the left.</summary>
    public sealed record RallyItem(string Title, string Detail, string? EventName = null, string? Path = null,
                                   RallyWeekend? Weekend = null, Rally? Rally = null, string? Problem = null);

    /// <summary>A car of the list: <paramref name="Id"/> is what goes into the save.</summary>
    public sealed record CarChoice(string Id, string Name);

    private enum Source { None, Save, Library, New }

    private readonly GameCatalog _catalog = GameCatalog.Default;
    private readonly Random _random = new();
    private readonly string _library = Path.Combine(AppContext.BaseDirectory, "rallies");

    private string _savePath = string.Empty;
    private readonly Dictionary<string, RallyWeekend> _saveWeekends = [];   // by event name, none under way
    private bool _saveHasSelection;                                         // the stage selection the single-stage modes read
    private string? _saveCar;                                               // the selection's car

    private Source _source;
    private string? _sourceEvent;      // Source.Save: the weekend shown
    private string? _sourcePath;       // Source.Library: its file
    private string? _cleanJson;        // the rally as last loaded or saved; null when never saved
    private bool _quiet;               // lists and controls being filled, not changed by the user

    public RallyTab()
    {
        InitializeComponent();
        Editor.Changed += RefreshProblems;
        Editor.CalendarStarted += TakeCalendarName;
        IsVisibleChanged += (_, e) =>
        {
            // the game may have rewritten the save meanwhile
            if (e.NewValue is true) RefreshSave();
        };
        RefreshLibrary();
        ShowNothing();
    }

    // ── Host wiring ───────────────────────────────────────────────────────────

    /// <summary>Message for the window's status bar.</summary>
    public event Action<string>? StatusChanged;

    public string SavePath => _savePath;

    /// <summary>Shows the save to work on, picked by the window at startup or here.</summary>
    public void ShowSavePath(string path)
    {
        _savePath = path;
        TxtSavePath.Text = path;
        LoadSaveVersion();
        RefreshSave();
        if (_source == Source.None && ListSave.Items.OfType<RallyItem>().FirstOrDefault(i => i.Weekend is not null) is { } first)
            ListSave.SelectedItem = first;
    }

    private void Status(string message) => StatusChanged?.Invoke(message);

    // ══════════════════════════════════════════════════════════════════════════
    // The save
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

    private void BtnOpenSaveDir_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_savePath) && File.Exists(_savePath))
            System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(_savePath)!);
    }

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;
        try { Status(Backups.Made(Backups.Take(_savePath, File.ReadAllBytes(_savePath)))); }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Backup error"); }
    }

    private void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;
        try
        {
            string? done = Backups.Restore(_savePath, SaveKind.PlayerData);
            if (done is null) return;
            LoadSaveVersion();
            RefreshSave();
            Status(done);
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Restore error"); }
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile()) return;
        try
        {
            byte[] data = File.ReadAllBytes(_savePath);
            var json = PlayerSaveJson.Export(data, _savePath);
            var dlg = new SaveFileDialog
            {
                Title = "Export player save as JSON",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"{Path.GetFileNameWithoutExtension(_savePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                InitialDirectory = Path.GetDirectoryName(_savePath)!,
            };
            if (dlg.ShowDialog() != true) return;

            File.WriteAllText(dlg.FileName, json.ToJsonString(IndentedJson), System.Text.Encoding.UTF8);
            Status($"✓  JSON exported: {Path.GetFileName(dlg.FileName)}  ({data.Length} bytes parsed)");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "Export error"); }
    }

    private void LoadSaveVersion()
    {
        TxtSaveVersion.Text = "";
        if (string.IsNullOrEmpty(_savePath) || !File.Exists(_savePath)) return;
        try { TxtSaveVersion.Text = $"UE {GvasHeader.ReadInfo(File.ReadAllBytes(_savePath)).EngineVersion}"; }
        catch { /* unreadable header: leave the badge empty rather than guess */ }
    }

    /// <summary>
    /// The save's two Rally Weekends in the list, and the weekends a rally can
    /// be written into: those the save holds, with no rally under way.
    /// </summary>
    private void RefreshSave()
    {
        _saveWeekends.Clear();
        _saveHasSelection = false;
        _saveCar = null;
        IReadOnlyList<string> recordedCars = [];
        var items = new List<RallyItem>();
        string? problem = null;
        var inProgress = new HashSet<string>();
        var found = new Dictionary<string, RallyWeekend>();
        var unreadable = new Dictionary<string, string>();   // by event name, why its weekend does not read

        if (!string.IsNullOrEmpty(_savePath) && File.Exists(_savePath))
        {
            try
            {
                byte[] data = File.ReadAllBytes(_savePath);
                var list = EventList.Find(data);
                _saveHasSelection = list.Selection is not null;
                _saveCar = list.Selection?.Car;
                recordedCars = CarSelection.Recorded(data);
                foreach (var ev in list.Events)
                {
                    RallyWeekend? weekend;
                    try { weekend = RallyWeekend.Find(data, ev.Start); }
                    catch (InvalidDataException ex)
                    {
                        unreadable[ev.Name] = ex.Message;
                        continue;
                    }
                    if (weekend is null) continue;
                    found[ev.Name] = weekend;
                    if (ev.RallyInProgress) inProgress.Add(ev.Name);
                    else _saveWeekends[ev.Name] = weekend;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problem = ex.Message;
            }
        }
        else
        {
            problem = "No save picked.";
        }

        foreach (var (name, title) in new[] { (OfflineEvent, "Offline Rally Weekend"), (OnlineEvent, "Online Rally Weekend") })
        {
            if (found.TryGetValue(name, out var weekend))
            {
                string detail = Describe(weekend.Calendar, weekend.IsCustom, weekend.Stages);
                if (inProgress.Contains(name)) detail = "Rally under way · " + detail;
                items.Add(new RallyItem(title, detail, name, Weekend: weekend));
            }
            else if (unreadable.TryGetValue(name, out string? why))
            {
                items.Add(new RallyItem(title, "Does not read", name, Problem: why));
            }
            else
            {
                items.Add(new RallyItem(title, problem ?? "Not set up in the game yet", name,
                                        Problem: problem ?? "Set this Rally Weekend up once in the game first."));
            }
        }

        _quiet = true;
        ListSave.ItemsSource = items;
        ListSave.SelectedItem = _source == Source.Save ? items.FirstOrDefault(i => i.EventName == _sourceEvent) : null;
        _quiet = false;

        foreach (var (box, name) in new[] { (ChkOffline, OfflineEvent), (ChkOnline, OnlineEvent) })
        {
            bool can = _saveWeekends.ContainsKey(name);
            box.IsEnabled = can;
            if (!can) box.IsChecked = false;
            box.ToolTip = can ? null
                : inProgress.Contains(name) ? "A rally is under way in this weekend: finish or retire from it in the game first."
                : unreadable.ContainsKey(name) ? "This weekend of the save does not read."
                : "The save holds no such weekend: set it up once in the game first.";
        }
        ChkSingle.IsEnabled = _saveHasSelection;
        if (!_saveHasSelection) ChkSingle.IsChecked = false;
        FillCars(recordedCars);
    }

    /// <summary>
    /// The game's cars, by the catalog's names, the selection's own picked. A
    /// car the save names is written as the save spells it, the others under
    /// the catalog's id; a car the catalog does not know yet is listed under
    /// the save's id.
    /// </summary>
    private void FillCars(IReadOnlyList<string> recorded)
    {
        string[] known = [.. recorded, .. _saveCar is null ? [] : new[] { _saveCar }];
        var cars = _catalog.Cars
            .Select(c => new CarChoice(known.FirstOrDefault(k => _catalog.Car(k) == c) ?? c.Id, c.Name))
            .Concat(known.Where(k => _catalog.Car(k) is null).Distinct().Select(k => new CarChoice(k, k)))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        CmbCar.ItemsSource = cars;
        CmbCar.SelectedItem = _saveCar is null ? null : cars.FirstOrDefault(c => c.Id == _saveCar);
        CmbCar.IsEnabled = _saveCar is not null;
        BtnWriteCar.IsEnabled = _saveCar is not null;
    }

    private void BtnWriteCar_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSaveFile() || CmbCar.SelectedItem is not CarChoice car) return;
        if (car.Id == _saveCar)
        {
            Dialogs.Info($"The save already has the {car.Name} selected: nothing to write.", "Nothing changed");
            return;
        }
        if (!GameClosed() || !AntiCheatGuard.Allows()) return;

        try
        {
            byte[] original = File.ReadAllBytes(_savePath);
            var write = CarSelection.Replace(original, car.Id);
            string was = _catalog.Car(write.OldCar)?.Name ?? write.OldCar;
            string summary =
                $"Select the {car.Name} in:\n  {_savePath}\n\n" +
                $"  Car : {was} → {car.Name}\n" +
                $"\n  size_field_1 : {write.OldSizeField} → {write.NewSizeField}\n\n" +
                "⚠  This will permanently modify the file.";
            if (!Dialogs.Confirm(summary, "Confirm write") || !StillAsRead(original)) return;
            if (!Dialogs.Verified(() => CarSelection.Verify(original, write))) return;

            Status($"Backup created: {Path.GetFileName(Backups.Write(_savePath, original))}");
            AtomicFile.Write(_savePath, write.Data);
            RefreshSave();
            Status($"✓  Car written: {car.Name}.");
        }
        catch (InvalidDataException ex)
        {
            Dialogs.Error($"{ex.Message}\n\nNothing was written.", "Write refused");
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message, "Write error");
        }
    }

    /// <summary>
    /// False, once the player is told, when the save changed on disk since
    /// <paramref name="original"/> was read (the game started meanwhile), or
    /// the game now runs: a write would lose what it wrote.
    /// </summary>
    private bool StillAsRead(byte[] original)
    {
        if (!GameClosed()) return false;
        if (File.ReadAllBytes(_savePath).AsSpan().SequenceEqual(original)) return true;
        Dialogs.Warn("The save changed on disk while this write waited: nothing was written.\n\n" +
                     "Look at it again, then write again.", "Save changed");
        RefreshSave();
        return false;
    }

    /// <summary>False, once the player is told, while the game runs: it would overwrite the save.</summary>
    private static bool GameClosed()
    {
        if (!GameInstall.IsRunning()) return true;
        Dialogs.Warn(
            "Assetto Corsa Rally is running. It keeps its settings in memory and rewrites the save " +
            "as soon as a menu setting changes, which would undo this change.\n\n" +
            "Close the game, then write again.",
            "Close the game first");
        return false;
    }

    /// <summary>"Sweden, Long · 3 days · 9 stages", in the words of the game's menus.</summary>
    private string Describe(string calendar, bool changed, IReadOnlyList<RallyWeekendStage> stages)
    {
        int days = stages.Count == 0 ? 0 : stages.Max(s => s.Day) + 1;
        string name = _catalog.Calendar(calendar) ?? calendar;
        return $"{name}{(changed ? ", changed" : "")} · {days} day{(days == 1 ? "" : "s")} · " +
               $"{stages.Count} stage{(stages.Count == 1 ? "" : "s")}";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The library
    // ══════════════════════════════════════════════════════════════════════════

    private void RefreshLibrary()
    {
        IReadOnlyList<RallyLibrary.Entry> entries;
        string empty = "No rally saved yet: pick one of the save's, then Save to library.";
        try { entries = RallyLibrary.List(_library, _catalog); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the folder itself does not read: the rest of the tab still works
            entries = [];
            empty = $"The library's folder does not read: {ex.Message}";
        }
        var items = entries.Select(e => e.Rally is { } rally
            ? new RallyItem(rally.Name, Describe(rally.Weekend.Calendar, !RallyWeekendEdit.IsGameCalendar(rally.Weekend, _catalog),
                                                 rally.Weekend.Stages), Path: e.Path, Rally: rally)
            : new RallyItem(e.Name, "Unreadable file", Path: e.Path, Problem: e.Error)).ToList();
        TxtLibraryEmpty.Text = empty;

        _quiet = true;
        ListLibrary.ItemsSource = items;
        ListLibrary.SelectedItem = _source == Source.Library ? items.FirstOrDefault(i => SamePath(i.Path, _sourcePath)) : null;
        _quiet = false;
        TxtLibraryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool SamePath(string? a, string? b) => RallyLibrary.SamePath(a, b);

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        var rally = Rally.New(RallyLibrary.FreeName(_library, "New rally"), _catalog, _random);
        Show(rally, Source.New);
    }

    private void BtnDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_source == Source.None) return;
        var rally = Editor.Read(TxtName.Text.Trim(), out _);
        rally = rally with { Name = RallyLibrary.FreeName(_library, rally.Name.Length == 0 ? "Rally" : rally.Name) };
        Show(rally, Source.New);
        _cleanJson = null;   // a copy to save
        RefreshProblems();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_source != Source.Library || _sourcePath is null) return;
        if (!Dialogs.Confirm($"Send “{Path.GetFileName(_sourcePath)}” to the Recycle Bin?", "Delete rally")) return;

        try { RecycleBin.Send(_sourcePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(ex.Message, "Delete error");
            return;
        }
        Status($"Rally deleted: {Path.GetFileName(_sourcePath)}");
        ShowNothing();
        RefreshLibrary();
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import rallies into the library",
            Filter = "Rally files (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        var imported = new List<string>();
        var refused = new List<string>();
        foreach (string file in dlg.FileNames)
        {
            try
            {
                var rally = RallyFile.Parse(File.ReadAllText(file), _catalog);
                rally = rally with { Name = RallyLibrary.FreeName(_library, rally.Name) };
                imported.Add(RallyLibrary.Save(_library, rally, _catalog));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                refused.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        RefreshLibrary();
        if (refused.Count > 0)
            Dialogs.Warn($"Not imported:\n\n• {string.Join("\n• ", refused)}", "Import");
        if (imported.Count > 0)
            Status($"✓  {imported.Count} rall{(imported.Count == 1 ? "y" : "ies")} imported into the library.");
    }

    private void BtnOpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_library);
            System.Diagnostics.Process.Start("explorer.exe", _library);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(ex.Message, "Library");
        }
    }

    private void BtnSaveLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_source == Source.None) return;
        string name = TxtName.Text.Trim();
        if (name.Length == 0)
        {
            Dialogs.Warn("Give the rally a name first: it names its file too.", "No name");
            return;
        }

        var rally = Editor.Read(name, out var problems);
        string target = RallyLibrary.PathFor(_library, name);
        string? replacing = _source == Source.Library ? _sourcePath : null;
        if (File.Exists(target) && !SamePath(target, replacing) &&
            !Dialogs.Confirm($"The library already holds “{Path.GetFileName(target)}”. Replace it?", "Save to library"))
        {
            return;
        }
        if (replacing is not null && File.Exists(replacing) && !SamePath(target, replacing) &&
            !Dialogs.Confirm($"The rally is renamed: it is saved as “{Path.GetFileName(target)}”, and " +
                             $"“{Path.GetFileName(replacing)}” goes to the Recycle Bin.", "Save to library"))
        {
            return;
        }
        if (problems.Count > 0 &&
            !Dialogs.Confirm($"The game would not take this rally as it is:\n\n• {string.Join("\n• ", problems.Distinct())}\n\n" +
                             "Save it to the library anyway?", "Save to library"))
        {
            return;
        }

        try
        {
            string path = RallyLibrary.Save(_library, rally, _catalog, replacing, RecycleBin.Send);
            _source = Source.Library;
            _sourcePath = path;
            _sourceEvent = null;
            _cleanJson = CurrentJson();
            RefreshLibrary();
            _quiet = true;
            ListSave.SelectedItem = null;
            _quiet = false;
            ShowSource();
            Status($"✓  Rally saved: {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(ex.Message, "Save error");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The rally shown
    // ══════════════════════════════════════════════════════════════════════════

    private void ListSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet || ListSave.SelectedItem is not RallyItem item) return;
        if (item.Weekend is null || !ConfirmLeave())
        {
            if (item.Problem is { } problem && item.Weekend is null) Dialogs.Info(problem, item.Title);
            Revert(ListSave, e);
            return;
        }

        string? other = item.EventName == OfflineEvent ? OnlineEvent : OfflineEvent;
        var otherWeekend = ((IEnumerable<RallyItem>)ListSave.ItemsSource).FirstOrDefault(i => i.EventName == other)?.Weekend;
        string name = _catalog.Calendar(item.Weekend.Calendar) ?? item.Weekend.Calendar;
        Show(Rally.From(item.Weekend.IsCustom ? name + ", changed" : name, item.Weekend, otherWeekend), Source.Save, item.EventName);

        // the weekend shown is where it goes back, unless the user picks otherwise
        if (_saveWeekends.ContainsKey(item.EventName!))
        {
            ChkOffline.IsChecked = item.EventName == OfflineEvent;
            ChkOnline.IsChecked = item.EventName == OnlineEvent;
        }
    }

    private void ListLibrary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet || ListLibrary.SelectedItem is not RallyItem item) return;
        if (item.Rally is null || !ConfirmLeave())
        {
            if (item.Rally is null) Dialogs.Warn($"{Path.GetFileName(item.Path)} does not read as a rally:\n\n{item.Problem}", "Unreadable file");
            Revert(ListLibrary, e);
            return;
        }
        Show(item.Rally, Source.Library, path: item.Path);
    }

    /// <summary>Puts a list's selection back where it was, without loading anything.</summary>
    private void Revert(ListBox list, SelectionChangedEventArgs e)
    {
        _quiet = true;
        list.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
        _quiet = false;
    }

    /// <summary>Shows a rally in the editor, and where it comes from.</summary>
    private void Show(Rally rally, Source source, string? eventName = null, string? path = null)
    {
        _quiet = true;
        if (source != Source.Save) ListSave.SelectedItem = null;
        if (source != Source.Library) ListLibrary.SelectedItem = null;
        TxtName.Text = rally.Name;
        _quiet = false;

        _source = source;
        _sourceEvent = eventName;
        _sourcePath = path;
        Editor.Load(rally);
        Editor.IsEnabled = true;
        TxtName.IsEnabled = true;
        _cleanJson = CurrentJson();
        ShowSource();
        RefreshProblems();
    }

    /// <summary>Nothing picked: the editor stays empty and the buttons that act on a rally do nothing.</summary>
    private void ShowNothing()
    {
        _source = Source.None;
        _sourceEvent = null;
        _sourcePath = null;
        _cleanJson = null;
        _quiet = true;
        TxtName.Text = "";
        ListLibrary.SelectedItem = null;
        _quiet = false;
        Editor.IsEnabled = false;
        TxtName.IsEnabled = false;
        ShowSource();
        TxtProblems.Text = "";
    }

    private void ShowSource()
    {
        TxtSource.Text = _source switch
        {
            Source.Save when _sourceEvent == OnlineEvent =>
                "The save's online Rally Weekend. The opponents and the running order, which it does not hold, " +
                "come from the offline one.",
            Source.Save =>
                "The save's offline Rally Weekend. The lobby, which it does not hold, comes from the online one.",
            Source.Library => $"In the library: {Path.GetFileName(_sourcePath)}",
            Source.New => "Not saved yet.",
            _ => "Pick a rally on the left, or make a new one.",
        };
        BtnDelete.IsEnabled = _source == Source.Library;
        BtnSaveLibrary.IsEnabled = _source != Source.None;
        BtnWrite.IsEnabled = _source != Source.None;
    }

    private string CurrentJson() => RallyFile.ToJson(Editor.Read(TxtName.Text.Trim(), out _), _catalog);

    private bool IsDirty => _source != Source.None && (_cleanJson is null || CurrentJson() != _cleanJson);

    /// <summary>True when the rally shown can be left: nothing changed, or the user lets the changes go.</summary>
    private bool ConfirmLeave() =>
        !IsDirty || Dialogs.Confirm($"The changes to “{TxtName.Text.Trim()}” are not saved. Leave them?", "Changes not saved");

    /// <summary>
    /// A rally started again from a calendar of the game takes its name; one
    /// already in the library under that name, another file than this rally's,
    /// makes it take the next free one.
    /// </summary>
    private void TakeCalendarName(GameCatalog.CalendarInfo calendar)
    {
        string? own = _source == Source.Library ? _sourcePath : null;
        _quiet = true;
        TxtName.Text = RallyLibrary.FreeName(_library, calendar.Name, own);
        _quiet = false;
    }

    private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_quiet) RefreshProblems();
    }

    private void RefreshProblems()
    {
        if (_source == Source.None) return;
        Editor.Read(TxtName.Text.Trim(), out var problems);
        if (TxtName.Text.Trim().Length == 0) problems.Insert(0, "The rally has no name.");
        TxtProblems.Text = string.Join("  ", problems.Distinct());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Writing into the save
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes the rally into the weekends ticked, as every write here does:
    /// refused while the game runs or beside an anti-cheat, a summary to
    /// confirm, the result re-read before it reaches the disk, a backup first.
    /// </summary>
    private void BtnWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_source == Source.None || !ValidateSaveFile()) return;

        var targets = new List<string>();
        if (ChkOffline.IsChecked == true) targets.Add(OfflineEvent);
        if (ChkOnline.IsChecked == true) targets.Add(OnlineEvent);
        bool single = ChkSingle.IsChecked == true;
        if (targets.Count == 0 && !single)
        {
            Dialogs.Warn("Tick where to write the rally: the offline or online Rally Weekend, the Single Stage, or several.",
                         "Nothing to write into");
            return;
        }

        var rally = Editor.Read(TxtName.Text.Trim(), out var problems);
        if (problems.Count > 0)
        {
            Dialogs.Warn($"The game would not take this rally:\n\n• {string.Join("\n• ", problems.Distinct())}", "Not written");
            return;
        }
        if (!GameClosed() || !AntiCheatGuard.Allows()) return;

        try
        {
            byte[] original = File.ReadAllBytes(_savePath);

            // the weekends first, then the single-stage modes, each step on the save the one before left
            var weekends = targets.Count > 0 ? RallyWeekendEdit.Replace(original, rally, targets, _catalog) : null;
            byte[] beforeSingle = weekends?.Data ?? original;
            var stages = single ? SingleStageEdit.Replace(beforeSingle, rally, _catalog) : null;
            byte[] result = stages?.Data ?? beforeSingle;
            if (result.AsSpan().SequenceEqual(original))
            {
                Dialogs.Info("The save already holds this rally as it is: nothing to write.", "Nothing changed");
                return;
            }

            var into = new List<string>();
            if (weekends is not null)
                into.AddRange(weekends.Steps.Select(s => $"  • {(s.Weekend.Online ? "Online" : "Offline")} Rally Weekend"));
            if (stages is not null)
            {
                string modes = stages.Events.Count == 0 ? "" : string.Join(", ", stages.Events.Select(ModeName)) + ", and ";
                string stage = _catalog.Stage(rally.Weekend.Stages[0].Stage)?.Name ?? rally.Weekend.Stages[0].Stage;
                into.Add($"  • {modes}the stage selection: {stage}");
            }
            int oldSize = weekends?.Steps[0].Write.OldSizeField ?? stages!.OldSizeField;
            int newSize = stages?.NewSizeField ?? weekends!.Steps[^1].Write.NewSizeField;
            string summary =
                $"Write “{rally.Name}” into:\n{string.Join("\n", into)}\n\nof:\n  {_savePath}\n\n" +
                $"  {Describe(rally.Weekend.Calendar, !RallyWeekendEdit.IsGameCalendar(rally.Weekend, _catalog), rally.Weekend.Stages)}\n" +
                $"\n  size_field_1 : {oldSize} → {newSize}\n\n" +
                "⚠  This will permanently modify the file.";
            if (!Dialogs.Confirm(summary, "Confirm write") || !StillAsRead(original)) return;

            if (!Dialogs.Verified(() =>
            {
                if (weekends is not null) RallyWeekendEdit.Verify(weekends);
                if (stages is not null) SingleStageEdit.Verify(beforeSingle, stages, rally);
            }))
            {
                return;
            }

            Status($"Backup created: {Path.GetFileName(Backups.Write(_savePath, original))}");
            AtomicFile.Write(_savePath, result);
            if (_source == Source.Save && targets.Contains(_sourceEvent!)) _cleanJson = CurrentJson();
            RefreshSave();
            Status($"✓  Rally written. New file size: {result.Length} bytes.");
        }
        catch (InvalidDataException ex)
        {
            Dialogs.Error($"{ex.Message}\n\nNothing was written.", "Write refused");
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message, "Write error");
        }
    }

    /// <summary>The name the game's menus give a single-stage mode.</summary>
    private static string ModeName(string eventName) => eventName switch
    {
        SingleStageEdit.OfflineEvent => "Single Stage",
        SingleStageEdit.OnlineEvent => "Online Single Stage",
        SingleStageEdit.PracticeEvent => "Free Practice",
        _ => eventName,
    };

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
}
