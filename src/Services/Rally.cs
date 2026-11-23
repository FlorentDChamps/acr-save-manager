namespace ACRSaveManager.Services;

/// <summary>
/// Where each setting of a rally sits among the event's settings: the
/// catalog's <c>property</c>, which names the setting and its values.
/// </summary>
public static class RallyOption
{
    public const string Consistency = "AIOptions.Consistency";
    public const string Spread = "AIOptions.Spread";
    public const string AiCars = "AIOptions.CarClassSelection.Type";
    public const string OrderType = "RunningOrderOptions.RunningOrderType";
    public const string LobbyCars = "LobbyOptions.CarSelectionOption";
    public const string Penalty = "RacePenaltyOptions.RacePenalty";
    public const string Respawn = "RaceOptions.Respawn.ManualRespawn";
    public const string Collisions = "RaceOptions.CarDamage.Collisions";
    public const string Wear = "RaceOptions.CarDamage.Wear";
    public const string Failures = "RaceOptions.CarDamage.MechanicalFailures";
    public const string HardCore = "HardCoreOptions.bHardCore";
    public const string Damages = "RaceOptions.CarDamage.bDamagesEnabled";
    public const string Graphical = "RaceOptions.CarDamage.bGraphicalDamages";
    public const string Weather = "WeatherOptions.Preset";
    public const string Target = "WeatherOptions.TargetPreset";
    public const string Probability = "WeatherOptions.TargetWeatherProbability";
    public const string Persistency = "WeatherOptions.WeatherPersistency";
    public const string Dynamic = "WeatherOptions.bDynamicWeather";
    public const string Speed = "WeatherOptions.TimeSpeed";
    public const string Grip = "WeatherOptions.StartingGripLevel";
}

/// <summary>
/// A Rally Weekend as the Rally tab edits it and its library keeps it: every
/// option the menus offer, offline and online alike, so that one rally can be
/// written into either weekend of the save. <see cref="Weekend"/> holds the
/// rules, the opponents, the running order and the calendar, laid out as an
/// offline weekend; <see cref="Lobby"/> takes the opponents' and the running
/// order's place online.
/// </summary>
public sealed record Rally(string Name, RallyWeekend Weekend, LobbySettings Lobby)
{
    /// <summary>The opponents of a rally made from an online weekend, the save holding no offline one.</summary>
    public static readonly AiSettings DefaultAi = new(10, 0.8f, 0, 0, 0.1f, 0, []);

    public static readonly RunningOrderSettings DefaultRunningOrder = new(0, 1, 0.5f);

    /// <summary>The lobby of a rally made from an offline weekend, the save holding no online one.</summary>
    public static readonly LobbySettings DefaultLobby = new(RallyWeekendEdit.MaxPlayers, 0);

    /// <summary>
    /// A new rally: the first of the game's calendars, as its menu loads it,
    /// with damage and the realistic settings on and the default opponents
    /// and lobby.
    /// </summary>
    public static Rally New(string name, GameCatalog catalog, Random random)
    {
        var calendar = catalog.RallyLocations.Select(l => catalog.CalendarsIn(l.Id)).FirstOrDefault(c => c.Count > 0)?[0]
            ?? throw new InvalidOperationException("The catalog holds no Rally Weekend calendar.");
        var blank = new RallyWeekend(
            TyresConsumption: false, TyresDamage: 0, ManualRespawn: 1, FuelConsumption: false,
            DamagesEnabled: true, GraphicalDamages: true, Collisions: 3, Wear: 3, MechanicalFailures: 3,
            NextWeather: RallyWeekendEdit.PlainWeather(8 * 3600, random), HardCore: false,
            Ai: DefaultAi, Penalty: 1, RunningOrder: DefaultRunningOrder, Lobby: null,
            Calendar: calendar.Id, Stages: [], CustomName: "", IsCustom: false, CalendarInstanceId: new byte[16]);
        return new Rally(name, RallyWeekendEdit.FromCalendar(blank, calendar, random), DefaultLobby);
    }

    /// <summary>
    /// The rally one of the save's weekends holds. What that weekend lacks
    /// (the lobby of an offline one, the opponents and running order of an
    /// online one) comes from <paramref name="other"/>, the save's other
    /// weekend, or else from the defaults.
    /// </summary>
    public static Rally From(string name, RallyWeekend weekend, RallyWeekend? other)
    {
        var offline = weekend.Online
            ? weekend with
            {
                Ai = other?.Ai ?? DefaultAi,
                RunningOrder = other?.RunningOrder ?? DefaultRunningOrder,
                Lobby = null,
            }
            : weekend;
        return new Rally(name, offline, weekend.Lobby ?? other?.Lobby ?? DefaultLobby);
    }

    /// <summary>
    /// The weekend to write in place of <paramref name="slot"/>, one of the
    /// save's: laid out as that slot is, offline or online, and normalised as
    /// the game would store it. What no menu shows (tyres and fuel, the AI's
    /// custom car slots, the calendar's own name and instance id) stays the
    /// slot's.
    /// </summary>
    public RallyWeekend For(RallyWeekend slot, GameCatalog catalog)
    {
        var w = Weekend with
        {
            TyresConsumption = slot.TyresConsumption,
            TyresDamage = slot.TyresDamage,
            FuelConsumption = slot.FuelConsumption,
            CustomName = slot.CustomName,
            CalendarInstanceId = slot.CalendarInstanceId,
        };
        w = slot.Online
            ? w with { Ai = null, RunningOrder = null, Lobby = Lobby }
            : w with
            {
                Ai = (Weekend.Ai ?? DefaultAi) with { CustomSlots = slot.Ai?.CustomSlots ?? [] },
                RunningOrder = Weekend.RunningOrder ?? DefaultRunningOrder,
                Lobby = null,
            };
        return RallyWeekendEdit.Normalize(w, catalog);
    }

    /// <summary>What would stop the game from taking the rally, offline or online; empty when nothing.</summary>
    public IReadOnlyList<string> Problems(GameCatalog catalog) =>
        RallyWeekendEdit.Problems(Weekend with { Lobby = Lobby }, catalog);

    /// <summary>The location of the calendar the rally started from, or null when the catalog does not know it.</summary>
    public string? Location(GameCatalog catalog) => catalog.CalendarInfoOf(Weekend.Calendar)?.Location;

    public int Days => Weekend.Stages.Count == 0 ? 0 : Weekend.Stages.Max(s => s.Day) + 1;
}
