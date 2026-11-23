using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ACRSaveManager.Services;

/// <summary>
/// The names the game shows, from the community catalog embedded in the app
/// (assets/catalog.json, CC BY 4.0): locations, stages, Rally Weekend
/// calendars, service parks and the values of the race options. English, as
/// the rest of the app. A look-up the catalog cannot answer comes back as
/// null, and the caller shows the game's own id instead.
/// </summary>
public sealed class GameCatalog
{
    private const string ResourceName = "ACRSaveManager.catalog.json";

    private static readonly Lazy<GameCatalog> Embedded = new(Load);

    /// <summary>A stage as the catalog describes it. <paramref name="Id"/> is the catalog's, lowercase.</summary>
    public sealed record StageInfo(string Id, string Name, string? ShortName, double? LengthKm, string? Location);

    public sealed record LocationInfo(string Id, string Name);

    /// <summary>A car as the catalog names it. <paramref name="Id"/> is the catalog's, lowercase.</summary>
    public sealed record CarInfo(string Id, string Name);

    /// <summary>A service park a Rally Weekend's calendar can hold.</summary>
    public sealed record ZoneInfo(string Id, string Name, int Minutes);

    /// <summary>One stage of one of the game's calendars.</summary>
    public sealed record CalendarStage(string Stage, string? ServiceZone, int StartSeconds);

    /// <summary>One of the game's Rally Weekend calendars, its days in order.</summary>
    public sealed record CalendarInfo(string Id, string Name, string Location,
                                      IReadOnlyList<IReadOnlyList<CalendarStage>> Days);

    /// <summary>
    /// One value of an option: the number the game stores, the menu's name for
    /// it, and the catalog's id (the game's own enumerator, e.g. "WT_CLEAR").
    /// </summary>
    public sealed record OptionValueInfo(int Value, string Name, string Id);

    private readonly List<LocationInfo> _locations = [];
    private readonly List<CarInfo> _carList = [];
    private readonly Dictionary<string, CarInfo> _cars = [];                        // by id and alias
    private readonly List<StageInfo> _stageList = [];
    private readonly Dictionary<string, StageInfo> _stages = [];                    // by id and alias
    private readonly List<CalendarInfo> _calendars = [];
    private readonly List<ZoneInfo> _zones = [];
    private readonly Dictionary<string, string> _optionNames = new(StringComparer.Ordinal);   // by property
    private readonly Dictionary<string, List<OptionValueInfo>> _optionValues = new(StringComparer.Ordinal);

    /// <summary>The catalog embedded in the app.</summary>
    public static GameCatalog Default => Embedded.Value;

    /// <summary>The game version the embedded catalog matches, e.g. "0.7.0".</summary>
    public string GameVersion { get; private set; } = "";

    public static GameCatalog Parse(string json)
    {
        var catalog = new GameCatalog();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        catalog.GameVersion = Text(root, "gameVersion") ?? "";

        foreach (var location in Items(root, "locations"))
            if (Text(location, "id") is { } id) catalog._locations.Add(new LocationInfo(id, Label(location)));

        var tracks = new Dictionary<string, string>();
        foreach (var track in Items(root, "tracks"))
            if (Text(track, "id") is { } id && Text(track, "location") is { } location) tracks[id] = location;

        foreach (var stage in Items(root, "stages"))
        {
            if (Text(stage, "id") is not { } id) continue;
            var info = new StageInfo(
                id,
                Label(stage),
                Text(stage, "shortName"),
                stage.TryGetProperty("lengthKm", out var length) && length.ValueKind == JsonValueKind.Number ? length.GetDouble() : null,
                Text(stage, "track") is { } track && tracks.TryGetValue(track, out string? loc) ? loc : null);
            catalog._stageList.Add(info);
            foreach (string key in Keys(stage)) catalog._stages.TryAdd(key, info);
        }

        foreach (var car in Items(root, "cars"))
        {
            if (Text(car, "id") is not { } id) continue;
            var info = new CarInfo(id, Label(car));
            catalog._carList.Add(info);
            foreach (string key in Keys(car)) catalog._cars.TryAdd(Key(key), info);
        }

        foreach (var zone in Items(root, "serviceZones"))
        {
            if (Text(zone, "id") is { } id)
            {
                int minutes = zone.TryGetProperty("minutes", out var m) && m.TryGetInt32(out int n) ? n : 0;
                catalog._zones.Add(new ZoneInfo(id, Label(zone), minutes));
            }
        }

        foreach (var calendar in Items(root, "rallyCalendars"))
        {
            if (Text(calendar, "id") is not { } id || Text(calendar, "location") is not { } location) continue;
            var days = Items(calendar, "days").Select(day => (IReadOnlyList<CalendarStage>)
                [.. Items(day, "stages").Select(run => new CalendarStage(
                    Text(run, "stage") ?? "",
                    Text(run, "serviceZone"),
                    Seconds(Text(run, "startTime"))))]).ToList();
            catalog._calendars.Add(new CalendarInfo(id, Label(calendar), location, days));
        }

        foreach (var option in Items(root, "options"))
        {
            if (Text(option, "property") is not { } property) continue;
            catalog._optionNames[property] = Label(option);
            var values = new List<OptionValueInfo>();
            foreach (var value in Items(option, "values"))
            {
                if (value.TryGetProperty("value", out var v) && v.TryGetInt32(out int number))
                    values.Add(new OptionValueInfo(number, Label(value), Text(value, "id") ?? number.ToString(CultureInfo.InvariantCulture)));
            }
            catalog._optionValues[property] = values;
        }

        return catalog;
    }

    private static GameCatalog Load()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded catalog {ResourceName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    // ── Look-ups ──────────────────────────────────────────────────────────────

    /// <summary>The locations that have a Rally Weekend calendar, in the catalog's order.</summary>
    public IReadOnlyList<LocationInfo> RallyLocations =>
        [.. _locations.Where(l => _calendars.Any(c => c.Location == l.Id))];

    public LocationInfo? Location(string id) => _locations.FirstOrDefault(l => l.Id == Key(id));

    public StageInfo? Stage(string id) => _stages.GetValueOrDefault(Key(id));

    /// <summary>The cars of the game, in the catalog's order.</summary>
    public IReadOnlyList<CarInfo> Cars => _carList;

    /// <summary>A car by the game's id or one of the catalog's, case and accents aside.</summary>
    public CarInfo? Car(string id) => _cars.GetValueOrDefault(Key(id));

    /// <summary>The stages of a location, in the catalog's order.</summary>
    public IReadOnlyList<StageInfo> StagesIn(string location) => [.. _stageList.Where(s => s.Location == location)];

    /// <summary>The name of one of the game's calendars.</summary>
    public string? Calendar(string id) => CalendarInfoOf(id)?.Name;

    public CalendarInfo? CalendarInfoOf(string id) =>
        _calendars.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The game's calendars of a location, shortest first as the catalog lists them.</summary>
    public IReadOnlyList<CalendarInfo> CalendarsIn(string location) => [.. _calendars.Where(c => c.Location == location)];

    public IReadOnlyList<ZoneInfo> Zones => _zones;

    public (string Name, int Minutes)? Zone(string id) =>
        _zones.FirstOrDefault(z => string.Equals(z.Id, id, StringComparison.OrdinalIgnoreCase)) is { } zone
            ? (zone.Name, zone.Minutes)
            : null;

    /// <summary>The name of an option, by its place among the event's settings ("AIOptions.Skill").</summary>
    public string? OptionName(string property) => _optionNames.GetValueOrDefault(property);

    /// <summary>The values of an option, as the menu lists them; empty when the catalog has none.</summary>
    public IReadOnlyList<OptionValueInfo> OptionValues(string property) =>
        _optionValues.TryGetValue(property, out var values) ? values : [];

    /// <summary>The name of an option's value, as the menu shows it.</summary>
    public string? OptionValue(string property, int value) =>
        OptionValues(property).FirstOrDefault(v => v.Value == value)?.Name;

    /// <summary>The catalog's id of an option's value, e.g. "Realistic"; null when it has none.</summary>
    public string? OptionValueId(string property, int value) =>
        OptionValues(property).FirstOrDefault(v => v.Value == value)?.Id;

    /// <summary>The number the game stores for the value <paramref name="id"/> of an option, case aside.</summary>
    public int? OptionValueOf(string property, string id) =>
        OptionValues(property).FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// The form of every id and alias of the catalog: lowercase, accents
    /// removed, letters and digits only.
    /// </summary>
    public static string Key(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(lower);
        }
        return sb.ToString();
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray()) yield return item;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The English label, or the name when the catalog gives none.</summary>
    private static string Label(JsonElement item) =>
        item.TryGetProperty("labels", out var labels) && Text(labels, "en") is { } english
            ? english
            : Text(item, "name") ?? "";

    /// <summary>"08:30" -> 30600.</summary>
    private static int Seconds(string? clock) =>
        clock is { Length: 5 } && int.TryParse(clock[..2], CultureInfo.InvariantCulture, out int h)
                               && int.TryParse(clock[3..], CultureInfo.InvariantCulture, out int m)
            ? h * 3600 + m * 60
            : 0;

    private static IEnumerable<string> Keys(JsonElement item)
    {
        if (Text(item, "id") is { } id) yield return id;
        foreach (var alias in Items(item, "aliases"))
            if (alias.ValueKind == JsonValueKind.String) yield return alias.GetString()!;
    }
}
