using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ACRSaveManager.Services;

namespace ACRSaveManager.UI;

/// <summary>
/// Editor of one rally, offline and online alike: its rules, the opponents and
/// running order an offline weekend takes, the lobby an online one takes, then
/// its calendar stage by stage, with the values the game's menus offer (from
/// the catalog). It writes nothing itself: the Rally tab reads the rally back
/// (<see cref="Read"/>) to save or write it.
/// </summary>
public partial class RallyEditor : UserControl
{
    /// <summary>A value of a drop-down list.</summary>
    public sealed record Choice(int Value, string Name);

    /// <summary>A stage or a service park in a drop-down list.</summary>
    public sealed record Named(string Id, string Name);

    // Value standing for "the game draws it" in the weather lists.
    private const int RandomValue = -1;

    private readonly GameCatalog _catalog = GameCatalog.Default;
    private readonly Random _random = new();

    private RallyWeekend _weekend;                       // the rules, as last read from the controls; offline layout
    private LobbySettings _lobby = Rally.DefaultLobby;
    private readonly List<RallyWeekendStage> _stages = [];   // the calendar being edited
    private bool _loading;                               // the controls are being filled, not changed by the user

    /// <summary>Raised on every change the user makes.</summary>
    public event Action? Changed;

    /// <summary>Raised when the stages are replaced by those of a calendar of the game, before <see cref="Changed"/>.</summary>
    public event Action<GameCatalog.CalendarInfo>? CalendarStarted;

    /// <summary>
    /// What the ▲ and ▼ beside a number do: the step, and the range the value
    /// stays in (a clock counts in minutes). A click goes to the next multiple
    /// of the step, then commits the box as leaving it would.
    /// </summary>
    private sealed record Stepper(int Step, Func<int> Min, Func<int> Max, bool Clock, RoutedEventHandler Commit);

    private readonly Dictionary<TextBox, Stepper> _steppers;

    public RallyEditor()
    {
        InitializeComponent();
        _weekend = Rally.New("", _catalog, _random).Weekend;
        _steppers = new()
        {
            [TxtOpponents] = new(1, () => 1, () => RallyWeekendEdit.MaxOpponents, false, Rules_Changed),
            [TxtSkill] = new(5, () => (int)Math.Round(RallyWeekendEdit.MinSkill * 100), () => (int)Math.Round(RallyWeekendEdit.MaxSkill * 100), false, Rules_Changed),
            [TxtPosition] = new(1, () => 1, () => OpponentsShown() + 1, false, Rules_Changed),
            [TxtMaxPlayers] = new(1, () => RallyWeekendEdit.MinPlayers, () => RallyWeekendEdit.MaxPlayers, false, Rules_Changed),
            [TxtStart] = new(30, () => 0, () => (23 * 60) + 30, true, Stage_Changed),
            [TxtWetness] = new(5, () => 0, () => 100, false, Stage_Changed),
            [TxtSnow] = new(5, () => 0, () => 100, false, Stage_Changed),
        };
        _loading = true;
        FillLists();
        _loading = false;
    }

    /// <summary>Shows <paramref name="rally"/> in the controls.</summary>
    public void Load(Rally rally)
    {
        _weekend = rally.Weekend;
        _lobby = rally.Lobby;
        _stages.Clear();
        _stages.AddRange(rally.Weekend.Stages);

        _loading = true;
        ShowRules();
        ShowCalendar();
        _loading = false;
        RefreshStages(0);
    }

    /// <summary>
    /// The rally the controls hold, under <paramref name="name"/>, and what
    /// would stop the game from taking it: fields that do not read, then the
    /// game's own rules.
    /// </summary>
    public Rally Read(string name, out List<string> problems)
    {
        var bad = new List<string>();
        CommitStage(bad);
        bad.AddRange(ReadRules());
        var rally = new Rally(name, _weekend with { Stages = [.. _stages] }, _lobby);
        problems = [.. bad, .. rally.Problems(_catalog)];
        return rally;
    }

    // ── Filling the controls ──────────────────────────────────────────────────

    private List<Choice> Choices(string property) =>
        [.. _catalog.OptionValues(property).Select(v => new Choice(v.Value, v.Name))];

    private string Label(string property, string fallback) => _catalog.OptionName(property) ?? fallback;

    private void FillLists()
    {
        CmbConsistency.ItemsSource = Choices(RallyOption.Consistency);
        CmbSpread.ItemsSource = Choices(RallyOption.Spread);
        CmbAiCars.ItemsSource = Choices(RallyOption.AiCars);
        CmbOrder.ItemsSource = Choices(RallyOption.OrderType);
        CmbLobbyCars.ItemsSource = Choices(RallyOption.LobbyCars);
        CmbPenalty.ItemsSource = Choices(RallyOption.Penalty);
        CmbRespawn.ItemsSource = Choices(RallyOption.Respawn);
        CmbCollisions.ItemsSource = Choices(RallyOption.Collisions);
        CmbWear.ItemsSource = Choices(RallyOption.Wear);
        CmbFailures.ItemsSource = Choices(RallyOption.Failures);

        List<Choice> weathers = [new(RandomValue, "Random"), .. Choices(RallyOption.Weather)];
        CmbWeather.ItemsSource = weathers;
        CmbTarget.ItemsSource = weathers;
        CmbProbability.ItemsSource = Choices(RallyOption.Probability);
        CmbPersistency.ItemsSource = Choices(RallyOption.Persistency);
        CmbSpeed.ItemsSource = Choices(RallyOption.Speed);
        CmbGrip.ItemsSource = Choices(RallyOption.Grip);
        CmbZone.ItemsSource = new List<Named>
        {
            new(RallyWeekendEdit.NoZone, "None"),
        }.Concat(_catalog.Zones.Select(z => new Named(z.Id, $"{z.Name}, {z.Minutes} min"))).ToList();

        LblConsistency.Text = Label(RallyOption.Consistency, "Opponents consistency");
        LblSpread.Text = Label(RallyOption.Spread, "Opponents skill spread");
        LblAiCars.Text = Label(RallyOption.AiCars, "Opponents vehicles");
        LblOrder.Text = Label(RallyOption.OrderType, "Running order");
        LblLobbyCars.Text = Label(RallyOption.LobbyCars, "Cars available");
        LblPenalty.Text = Label(RallyOption.Penalty, "Penalty");
        LblRespawn.Text = Label(RallyOption.Respawn, "Manual respawn");
        LblCollisions.Text = Label(RallyOption.Collisions, "Damage intensity");
        LblWear.Text = Label(RallyOption.Wear, "Wear intensity");
        LblFailures.Text = Label(RallyOption.Failures, "Mechanical failures");
        ChkHardCore.Content = Label(RallyOption.HardCore, "Hardcore mode");
        ChkDamages.Content = Label(RallyOption.Damages, "Car damage");
        ChkGraphical.Content = Label(RallyOption.Graphical, "Graphical damage");
        LblWeather.Text = Label(RallyOption.Weather, "Starting weather");
        LblTarget.Text = Label(RallyOption.Target, "Weather forecast");
        LblProbability.Text = Label(RallyOption.Probability, "Weather forecast probability");
        LblPersistency.Text = Label(RallyOption.Persistency, "Weather persistence");
        ChkDynamic.Content = Label(RallyOption.Dynamic, "Dynamic weather");
        LblSpeed.Text = Label(RallyOption.Speed, "Time speed");
        LblGrip.Text = Label(RallyOption.Grip, "Starting grip level");

        CmbLocation.ItemsSource = _catalog.RallyLocations;
    }

    private void ShowRules()
    {
        var w = _weekend;
        var ai = w.Ai ?? Rally.DefaultAi;
        var order = w.RunningOrder ?? Rally.DefaultRunningOrder;

        TxtOpponents.Text = ai.Opponents.ToString(CultureInfo.InvariantCulture);
        TxtSkill.Text = RallyFields.Percent(ai.Skill);
        CmbConsistency.SelectedValue = (int)ai.Consistency;
        CmbSpread.SelectedValue = (int)ai.Spread;
        CmbAiCars.SelectedValue = (int)ai.CarSelection;
        CmbOrder.SelectedValue = (int)order.Type;
        TxtPosition.Text = order.Position.ToString(CultureInfo.InvariantCulture);
        TxtMaxPlayers.Text = _lobby.MaxPlayers.ToString(CultureInfo.InvariantCulture);
        CmbLobbyCars.SelectedValue = (int)_lobby.CarSelection;

        CmbPenalty.SelectedValue = (int)w.Penalty;
        CmbRespawn.SelectedValue = (int)w.ManualRespawn;
        CmbCollisions.SelectedValue = (int)w.Collisions;
        CmbWear.SelectedValue = (int)w.Wear;
        CmbFailures.SelectedValue = (int)w.MechanicalFailures;
        ChkHardCore.IsChecked = w.HardCore;
        ChkDamages.IsChecked = w.DamagesEnabled;
        ChkGraphical.IsChecked = w.GraphicalDamages;
        LockHardCore();
    }

    /// <summary>Hardcore mode sets damage and penalties itself and locks the respawn setting, as the menu does.</summary>
    private void LockHardCore()
    {
        bool hard = ChkHardCore.IsChecked == true;
        if (hard)
        {
            ChkDamages.IsChecked = true;
            ChkGraphical.IsChecked = true;
            CmbPenalty.SelectedValue = 2;
        }
        ChkDamages.IsEnabled = !hard;
        ChkGraphical.IsEnabled = !hard;
        CmbPenalty.IsEnabled = !hard;
        CmbRespawn.IsEnabled = !hard;
        PanelPosition.IsEnabled = CmbOrder.SelectedValue is 1;
    }

    private void ShowCalendar()
    {
        string calendar = _catalog.Calendar(_weekend.Calendar) ?? _weekend.Calendar;
        int days = _stages.Count == 0 ? 0 : _stages.Max(s => s.Day) + 1;
        TxtCalendar.Text = $"Started from {calendar}: {_stages.Count} stage{(_stages.Count == 1 ? "" : "s")} " +
                           $"over {days} day{(days == 1 ? "" : "s")}";

        string? location = Location();
        CmbLocation.SelectedItem = _catalog.RallyLocations.FirstOrDefault(l => l.Id == location);
        SelectPreset();   // the location may not have changed, and its list with it
        List<Named> stages = location is null ? [] : [.. _catalog.StagesIn(location).Select(s => new Named(s.Id, StageLabel(s)))];
        CmbStage.ItemsSource = stages;
    }

    /// <summary>The location of the calendar the rally started from: every stage has to be there.</summary>
    private string? Location() => _catalog.CalendarInfoOf(_weekend.Calendar)?.Location;

    private static T? First<T>(IReadOnlyList<T> list) where T : class => list.Count > 0 ? list[0] : null;

    private static string StageLabel(GameCatalog.StageInfo s) =>
        s.LengthKm is { } km ? $"{s.Name} ({km.ToString("0.0", CultureInfo.InvariantCulture)} km)" : s.Name;

    private void RaiseChanged()
    {
        if (!_loading) Changed?.Invoke();
    }

    // ── The calendar ──────────────────────────────────────────────────────────

    private void CmbLocation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var calendars = CmbLocation.SelectedItem is GameCatalog.LocationInfo location
            ? _catalog.CalendarsIn(location.Id)
            : [];
        CmbPreset.ItemsSource = calendars;
        SelectPreset();
    }

    /// <summary>The calendar the rally started from in the preset list, or the location's first.</summary>
    private void SelectPreset()
    {
        var calendars = CmbPreset.ItemsSource as IReadOnlyList<GameCatalog.CalendarInfo> ?? [];
        CmbPreset.SelectedItem = calendars.FirstOrDefault(c => c.Id == _weekend.Calendar) ?? First(calendars);
    }

    private void BtnLoadPreset_Click(object sender, RoutedEventArgs e)
    {
        if (CmbPreset.SelectedItem is not GameCatalog.CalendarInfo calendar) return;
        if (!Dialogs.Confirm($"Replace the {_stages.Count} stages with those of {calendar.Name}?\n\nThe rules are kept.",
                             "Start from a calendar of the game"))
        {
            return;
        }

        ReadRules();
        _weekend = RallyWeekendEdit.FromCalendar(_weekend with { Stages = [.. _stages] }, calendar, _random);
        _stages.Clear();
        _stages.AddRange(_weekend.Stages);
        _loading = true;
        ShowCalendar();
        _loading = false;
        RefreshStages(0);
        CalendarStarted?.Invoke(calendar);
        RaiseChanged();
    }

    // ── The ▲ and ▼ beside a number ───────────────────────────────────────────

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        // ▲ and ▼ stand on top of each other, right of their box in its DockPanel
        if (sender is not Button { Tag: string tag, Parent: StackPanel { Parent: DockPanel panel } } ||
            panel.Children.OfType<TextBox>().FirstOrDefault() is not { } box ||
            !_steppers.TryGetValue(box, out var stepper))
        {
            return;
        }

        int direction = int.Parse(tag, CultureInfo.InvariantCulture);
        int? value = stepper.Clock
            ? RallyFields.ParseClock(box.Text) is { } seconds ? (int)Math.Round(seconds / 60) : null
            : int.TryParse(box.Text.Trim().TrimEnd('%').Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
        if (value is not { } now) return;   // the box says what is wrong with it once committed

        // at the end of the range, or past it already: nothing to do
        if (RallyFields.Step(now, stepper.Step, stepper.Min(), stepper.Max(), direction) is not { } next) return;

        box.Text = stepper.Clock
            ? TimeSpan.FromMinutes(next).ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : next.ToString(CultureInfo.InvariantCulture);
        stepper.Commit(box, e);
    }

    /// <summary>The number of opponents the box shows, or the most there can be when it does not read.</summary>
    private int OpponentsShown() =>
        int.TryParse(TxtOpponents.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : RallyWeekendEdit.MaxOpponents;

    private void RefreshStages(int select)
    {
        bool wasLoading = _loading;
        _loading = true;
        ListStages.ItemsSource = _stages.Select((s, i) =>
        {
            string name = _catalog.Stage(s.Stage)?.Name ?? s.Stage;
            string service = string.Equals(s.Zone, RallyWeekendEdit.NoZone, StringComparison.OrdinalIgnoreCase) ? "" : "  · service park before";
            return $"Day {s.Day + 1}  ·  {RallyFields.Clock(s.Weather.StartTime)}  ·  SS{i + 1} {name}{service}";
        }).ToList();
        ListStages.SelectedIndex = _stages.Count == 0 ? -1 : Math.Clamp(select, 0, _stages.Count - 1);
        ShowStage();
        _loading = wasLoading;
    }

    private void ListStages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        ShowStage();
        _loading = false;
    }

    private int Selected => ListStages.SelectedIndex;

    /// <summary>The picked stage in the controls on the right.</summary>
    private void ShowStage()
    {
        PanelStage.IsEnabled = Selected >= 0;
        if (Selected < 0) return;

        var s = _stages[Selected];
        var w = s.Weather;
        var known = _catalog.Stage(s.Stage);
        CmbStage.SelectedValue = known?.Id;
        CmbZone.SelectedValue = ShownZone(s.Zone);
        TxtStart.Text = RallyFields.Clock(w.StartTime);
        CmbWeather.SelectedValue = w.RandomWeather ? RandomValue : w.Weather;
        CmbTarget.SelectedValue = w.RandomTarget ? RandomValue : w.TargetWeather;
        CmbProbability.SelectedValue = (int)w.TargetProbability;
        CmbPersistency.SelectedValue = (int)w.Persistency;
        ChkDynamic.IsChecked = w.DynamicWeather;
        CmbSpeed.SelectedValue = (int)w.TimeSpeed;
        CmbGrip.SelectedValue = (int)w.Grip;
        TxtWetness.Text = RallyFields.Percent(w.Wetness);
        TxtSnow.Text = RallyFields.Percent(w.Snow);
        LockForecast();
    }

    /// <summary>
    /// The service park the list shows for <paramref name="zone"/>: the
    /// catalog's spelling of it, or None for one the catalog does not know.
    /// </summary>
    private string ShownZone(string zone) =>
        _catalog.Zones.FirstOrDefault(z => string.Equals(z.Id, zone, StringComparison.OrdinalIgnoreCase))?.Id
        ?? RallyWeekendEdit.NoZone;

    private void LockForecast()
    {
        bool dynamic = ChkDynamic.IsChecked == true;
        CmbTarget.IsEnabled = dynamic;
        CmbProbability.IsEnabled = dynamic;
        CmbPersistency.IsEnabled = dynamic;
    }

    private void Stage_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Selected < 0) return;
        LockForecast();
        if (CommitStage(null))
        {
            // Once the click that may have moved the focus here is through: a
            // list refilled under it would swallow the stage it picks.
            _ = Dispatcher.InvokeAsync(() => RefreshStages(Selected), System.Windows.Threading.DispatcherPriority.Background);
        }
        RaiseChanged();
    }

    /// <summary>
    /// The picked stage, from the controls on the right. True when it changed.
    /// A box left as <see cref="ShowStage"/> filled it keeps the stage's own
    /// value, not the one its text reads as (a start off the minute, a road
    /// 19.999999 % wet); one that does not read keeps it too, and says so in
    /// <paramref name="bad"/>.
    /// </summary>
    private bool CommitStage(List<string>? bad)
    {
        if (Selected < 0) return false;

        var s = _stages[Selected];
        var w = s.Weather;
        string where = $"SS{Selected + 1}";

        float Field(TextBox box, string shown, float own, Func<string, float?> parse, string what)
        {
            if (box.Text.Trim() == shown) return own;
            if (parse(box.Text) is { } value) return value;
            bad?.Add($"{where}: {what}");
            return own;
        }

        int weather = CmbWeather.SelectedValue as int? ?? w.Weather;
        int target = CmbTarget.SelectedValue as int? ?? w.TargetWeather;
        var weatherNow = w with
        {
            RandomWeather = weather == RandomValue,
            Weather = weather == RandomValue ? w.Weather : (byte)weather,
            RandomTarget = target == RandomValue,
            TargetWeather = target == RandomValue ? w.TargetWeather : (byte)target,
            TargetProbability = (byte)(CmbProbability.SelectedValue as int? ?? w.TargetProbability),
            Persistency = (byte)(CmbPersistency.SelectedValue as int? ?? w.Persistency),
            DynamicWeather = ChkDynamic.IsChecked == true,
            TimeSpeed = (byte)(CmbSpeed.SelectedValue as int? ?? w.TimeSpeed),
            Grip = (byte)(CmbGrip.SelectedValue as int? ?? w.Grip),
            StartTime = Field(TxtStart, RallyFields.Clock(w.StartTime), w.StartTime, RallyFields.ParseClock,
                              $"the start time \"{TxtStart.Text.Trim()}\" is not a time of day (HH:MM)."),
            Wetness = Field(TxtWetness, RallyFields.Percent(w.Wetness), w.Wetness, RallyFields.ParsePercent,
                            $"the road wetness \"{TxtWetness.Text.Trim()}\" is not a whole number from 0 to 100."),
            Snow = Field(TxtSnow, RallyFields.Percent(w.Snow), w.Snow, RallyFields.ParsePercent,
                         $"the snow level \"{TxtSnow.Text.Trim()}\" is not a whole number from 0 to 100."),
        };

        // A stage or a service park picked from the list takes the catalog's
        // id; one left as shown keeps the game's own, known or not.
        string stage = CmbStage.SelectedValue as string is { } id && _catalog.Stage(s.Stage)?.Id != id ? id : s.Stage;
        string zone = CmbZone.SelectedValue as string is { } picked && picked != ShownZone(s.Zone) ? picked : s.Zone;
        var now = s with { Stage = stage, Zone = zone, Weather = weatherNow };
        if (now == s) return false;
        _stages[Selected] = now;
        return true;
    }

    private void BtnAddStage_Click(object sender, RoutedEventArgs e)
    {
        if (Location() is not { } location || First(_catalog.StagesIn(location)) is not { } first) return;

        int at = Selected < 0 ? _stages.Count - 1 : Selected;
        int day = at < 0 ? 0 : _stages[at].Day;
        if (_stages.Count(s => s.Day == day) >= RallyWeekendEdit.MaxStagesPerDay)
        {
            Dialogs.Warn($"A day holds {RallyWeekendEdit.MaxStagesPerDay} stages at most in the game's menu.", "Day full");
            return;
        }

        float start = at < 0 ? 8 * 3600 : Math.Min(_stages[at].Weather.StartTime + 3600, 23 * 3600);
        var weather = RallyWeekendEdit.PlainWeather((int)start, _random);
        string zone = at < 0 ? First(_catalog.Zones)?.Id ?? RallyWeekendEdit.NoZone : RallyWeekendEdit.NoZone;
        _stages.Insert(at + 1, new RallyWeekendStage(day, first.Id, weather, weather, zone));
        RefreshStages(at + 1);
        RaiseChanged();
    }

    private void BtnAddDay_Click(object sender, RoutedEventArgs e)
    {
        if (Location() is not { } location || First(_catalog.StagesIn(location)) is not { } first) return;

        int day = _stages.Count == 0 ? 0 : _stages.Max(s => s.Day) + 1;
        if (day >= RallyWeekendEdit.MaxDays)
        {
            Dialogs.Warn($"A rally lasts {RallyWeekendEdit.MaxDays} days at most.", "No more days");
            return;
        }

        var weather = RallyWeekendEdit.PlainWeather(8 * 3600, _random);
        string zone = First(_catalog.Zones)?.Id ?? RallyWeekendEdit.NoZone;
        _stages.Add(new RallyWeekendStage(day, first.Id, weather, weather, zone));
        RefreshStages(_stages.Count - 1);
        RaiseChanged();
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected < 0) return;
        int at = Selected;
        var removed = _stages[at];
        int day = removed.Day;
        bool firstOfDay = at == 0 || _stages[at - 1].Day != day;
        _stages.RemoveAt(at);

        // The day's first stage takes the service park it started with.
        if (firstOfDay && at < _stages.Count && _stages[at].Day == day &&
            string.Equals(_stages[at].Zone, RallyWeekendEdit.NoZone, StringComparison.OrdinalIgnoreCase))
        {
            _stages[at] = _stages[at] with { Zone = removed.Zone };
        }

        CloseDay(day);
        RefreshStages(at);
        RaiseChanged();
    }

    /// <summary>A day left with no stage goes, and the days after it come one earlier.</summary>
    private void CloseDay(int day)
    {
        if (_stages.Any(s => s.Day == day)) return;
        for (int i = 0; i < _stages.Count; i++)
            if (_stages[i].Day > day) _stages[i] = _stages[i] with { Day = _stages[i].Day - 1 };
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void BtnDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    /// <summary>
    /// Swaps the picked stage with its neighbour on the same day, the start
    /// times staying where they were; at the edge of a day, moves it to the
    /// neighbouring day instead, the day it leaves going if left empty.
    /// </summary>
    private void Move(int step)
    {
        int at = Selected;
        int to = at + step;
        if (at < 0 || to < 0 || to >= _stages.Count) return;

        var a = _stages[at];
        var b = _stages[to];
        if (a.Day == b.Day)
        {
            _stages[at] = b with { Weather = b.Weather with { StartTime = a.Weather.StartTime } };
            _stages[to] = a with { Weather = a.Weather with { StartTime = b.Weather.StartTime } };
            RefreshStages(to);
        }
        else
        {
            _stages[at] = a with { Day = b.Day };
            CloseDay(a.Day);
            RefreshStages(at);
        }
        RaiseChanged();
    }

    // ── The rules ─────────────────────────────────────────────────────────────

    private void Rules_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        LockHardCore();
        _loading = false;
        RaiseChanged();
    }

    /// <summary>
    /// The rules, from the controls, into <see cref="_weekend"/> and
    /// <see cref="_lobby"/>. Returns the fields that do not read as a number,
    /// for the list of problems.
    /// </summary>
    private List<string> ReadRules()
    {
        var bad = new List<string>();
        var w = _weekend;
        var ai = w.Ai ?? Rally.DefaultAi;
        var order = w.RunningOrder ?? Rally.DefaultRunningOrder;

        int Number(TextBox box, int fallback, string what)
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            bad.Add($"{what} is not a whole number.");
            return fallback;
        }
        byte Pick(ComboBox box, byte fallback) => box.SelectedValue is int v ? (byte)v : fallback;

        _lobby = _lobby with
        {
            MaxPlayers = Number(TxtMaxPlayers, _lobby.MaxPlayers, "The number of players"),
            CarSelection = Pick(CmbLobbyCars, _lobby.CarSelection),
        };
        _weekend = w with
        {
            Ai = ai with
            {
                Opponents = Number(TxtOpponents, ai.Opponents, "The number of opponents"),
                // as shown, the skill stays the save's own (0.79999995, not 0.8)
                Skill = TxtSkill.Text.Trim() == RallyFields.Percent(ai.Skill)
                    ? ai.Skill
                    : Number(TxtSkill, (int)Math.Round(ai.Skill * 100), "The opponents' skill") / 100f,
                Consistency = Pick(CmbConsistency, ai.Consistency),
                Spread = Pick(CmbSpread, ai.Spread),
                CarSelection = Pick(CmbAiCars, ai.CarSelection),
            },
            RunningOrder = order with
            {
                Type = Pick(CmbOrder, order.Type),
                Position = Number(TxtPosition, order.Position, "The start position"),
            },
            Lobby = null,
            Penalty = Pick(CmbPenalty, w.Penalty),
            ManualRespawn = Pick(CmbRespawn, w.ManualRespawn),
            Collisions = Pick(CmbCollisions, w.Collisions),
            Wear = Pick(CmbWear, w.Wear),
            MechanicalFailures = Pick(CmbFailures, w.MechanicalFailures),
            HardCore = ChkHardCore.IsChecked == true,
            DamagesEnabled = ChkDamages.IsChecked == true,
            GraphicalDamages = ChkGraphical.IsChecked == true,
        };
        return bad;
    }
}
