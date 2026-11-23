using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ACRSaveManager.Services;

/// <summary>
/// A <see cref="Rally"/> as a JSON file of the library: readable, and made to
/// be shared. Values are the catalog's ids (the game's own enumerators, e.g.
/// "Realistic", "WT_LIGHT_RAIN"), stages, service parks and calendars the
/// game's ids. A number is accepted wherever an id is, for a value the
/// catalog does not know yet.
///
/// The file holds what the menus show, and the random draws behind them
/// (weather rolls, seeds) so that a rally comes back as it was. What no menu
/// shows stays the save's when the rally is written (<see cref="Rally.For"/>);
/// each stage's reset weather is the plain weather at its start time.
/// </summary>
public static class RallyFile
{
    public const string Format = "acr-rally";
    public const int Version = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ── Writing ───────────────────────────────────────────────────────────────

    public static string ToJson(Rally rally, GameCatalog catalog)
    {
        var w = rally.Weekend;
        var ai = w.Ai ?? Rally.DefaultAi;
        var order = w.RunningOrder ?? Rally.DefaultRunningOrder;

        var days = new JsonArray();
        foreach (var day in w.Stages.GroupBy(s => s.Day).OrderBy(g => g.Key))
        {
            var stages = new JsonArray();
            foreach (var s in day)
            {
                var stage = new JsonObject { ["stage"] = s.Stage };
                if (!string.Equals(s.Zone, RallyWeekendEdit.NoZone, StringComparison.OrdinalIgnoreCase))
                    stage["servicePark"] = s.Zone;
                stage["start"] = Clock(s.Weather.StartTime);
                stage["weather"] = WeatherJson(s.Weather, catalog);
                stages.Add(stage);
            }
            days.Add(new JsonObject { ["stages"] = stages });
        }

        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["name"] = rally.Name,
            ["game"] = catalog.GameVersion,
            ["calendar"] = w.Calendar,
            ["rules"] = new JsonObject
            {
                ["hardcore"] = w.HardCore,
                ["damage"] = w.DamagesEnabled,
                ["graphicalDamage"] = w.GraphicalDamages,
                ["collisions"] = Value(catalog, RallyOption.Collisions, w.Collisions),
                ["wear"] = Value(catalog, RallyOption.Wear, w.Wear),
                ["mechanicalFailures"] = Value(catalog, RallyOption.Failures, w.MechanicalFailures),
                ["respawn"] = Value(catalog, RallyOption.Respawn, w.ManualRespawn),
                ["penalty"] = Value(catalog, RallyOption.Penalty, w.Penalty),
            },
            ["offline"] = new JsonObject
            {
                ["opponents"] = ai.Opponents,
                ["skill"] = Number(ai.Skill),
                ["consistency"] = Value(catalog, RallyOption.Consistency, ai.Consistency),
                ["spread"] = Value(catalog, RallyOption.Spread, ai.Spread),
                ["raceCraft"] = Number(ai.RaceCraft),
                ["opponentCars"] = Value(catalog, RallyOption.AiCars, ai.CarSelection),
                ["runningOrder"] = Value(catalog, RallyOption.OrderType, order.Type),
                ["startPosition"] = order.Position,
                ["runningOrderRoll"] = Number(order.Roll),
            },
            ["online"] = new JsonObject
            {
                ["maxPlayers"] = rally.Lobby.MaxPlayers,
                ["cars"] = Value(catalog, RallyOption.LobbyCars, rally.Lobby.CarSelection),
            },
            ["days"] = days,
        };
        return root.ToJsonString(Indented);
    }

    private static JsonObject WeatherJson(WeatherSettings s, GameCatalog catalog) => new()
    {
        ["preset"] = Value(catalog, RallyOption.Weather, s.Weather),
        ["randomPreset"] = s.RandomWeather,
        ["presetRoll"] = Number(s.WeatherRoll),
        ["dynamic"] = s.DynamicWeather,
        ["forecast"] = Value(catalog, RallyOption.Target, s.TargetWeather),
        ["randomForecast"] = s.RandomTarget,
        ["forecastRoll"] = Number(s.TargetRoll),
        ["forecastProbability"] = Value(catalog, RallyOption.Probability, s.TargetProbability),
        ["persistency"] = Value(catalog, RallyOption.Persistency, s.Persistency),
        ["timeSpeed"] = Value(catalog, RallyOption.Speed, s.TimeSpeed),
        ["grip"] = Value(catalog, RallyOption.Grip, s.Grip),
        ["wetness"] = Number(s.Wetness),
        ["snow"] = Number(s.Snow),
        ["seed"] = s.Seed,
    };

    /// <summary>The catalog's id of a value, or the number itself when the catalog has none.</summary>
    private static JsonValue Value(GameCatalog catalog, string property, byte value) =>
        catalog.OptionValueId(property, value) is { } id ? JsonValue.Create(id) : JsonValue.Create((int)value);

    /// <summary>
    /// A float in its shortest form that reads back to the same float: 0.9, not
    /// 0.899999976. JSON has no number for NaN and the infinities: they go as
    /// the text "NaN", "Infinity", "-Infinity", which <see cref="Fields.Float"/>
    /// reads back.
    /// </summary>
    private static JsonValue Number(float value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return float.IsFinite(value)
            ? JsonValue.Create(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
            : JsonValue.Create(text);
    }

    /// <summary>"08:30", or "08:30:15" for a start off the minute; within the day.</summary>
    public static string Clock(float seconds)
    {
        int total = Math.Clamp((int)Math.Round(seconds), 0, (24 * 3600) - 1);
        var t = TimeSpan.FromSeconds(total);
        return t.Seconds == 0
            ? t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The rally in a library file. Throws <see cref="InvalidDataException"/>
    /// naming the first field that is missing or does not read; the calendar
    /// itself is not checked here (<see cref="Rally.Problems"/> is).
    /// </summary>
    public static Rally Parse(string json, GameCatalog catalog)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }); }
        catch (JsonException ex) { throw new InvalidDataException($"Not a JSON file: {ex.Message}"); }
        if (root is not JsonObject top) throw new InvalidDataException("Not a rally file.");

        var r = new Fields(top, "", catalog);
        if (r.Text("format") != Format) throw new InvalidDataException("Not a rally file of ACR Save Manager.");
        int version = r.Int("version");
        if (version > Version)
            throw new InvalidDataException($"This rally file is of a newer format ({version}): update ACR Save Manager to read it.");

        var rules = r.Object("rules");
        var offline = r.Object("offline");
        var online = r.Object("online");

        var stages = new List<RallyWeekendStage>();
        var days = r.Array("days");
        for (int day = 0; day < days.Count; day++)
        {
            var dayFields = days[day];
            var runs = dayFields.Array("stages");
            if (runs.Count == 0) throw new InvalidDataException($"{dayFields.Path}stages: a day holds one stage at least.");
            foreach (var run in runs)
            {
                float start = run.Clock("start");
                var weather = ReadWeather(run.Object("weather"), start);
                stages.Add(new RallyWeekendStage(day, run.Identifier("stage"), weather, PlainWeather(weather),
                                                 run.OptionalIdentifier("servicePark") ?? RallyWeekendEdit.NoZone));
            }
        }

        var ai = new AiSettings(
            offline.Int("opponents"),
            offline.Float("skill"),
            offline.Option("consistency", RallyOption.Consistency),
            offline.Option("spread", RallyOption.Spread),
            offline.Float("raceCraft"),
            offline.Option("opponentCars", RallyOption.AiCars),
            []);
        var order = new RunningOrderSettings(
            offline.Option("runningOrder", RallyOption.OrderType),
            offline.Int("startPosition"),
            offline.Float("runningOrderRoll"));
        var lobby = new LobbySettings(online.Int("maxPlayers"), online.Option("cars", RallyOption.LobbyCars));

        var firstWeather = stages.Count > 0 ? stages[0].Weather : PlainWeather(null);
        var weekend = new RallyWeekend(
            TyresConsumption: false, TyresDamage: 0, ManualRespawn: rules.Option("respawn", RallyOption.Respawn),
            FuelConsumption: false,
            DamagesEnabled: rules.Bool("damage"),
            GraphicalDamages: rules.Bool("graphicalDamage"),
            Collisions: rules.Option("collisions", RallyOption.Collisions),
            Wear: rules.Option("wear", RallyOption.Wear),
            MechanicalFailures: rules.Option("mechanicalFailures", RallyOption.Failures),
            NextWeather: firstWeather,
            HardCore: rules.Bool("hardcore"),
            Ai: ai,
            Penalty: rules.Option("penalty", RallyOption.Penalty),
            RunningOrder: order,
            Lobby: null,
            Calendar: r.Identifier("calendar"),
            Stages: stages,
            CustomName: "",
            IsCustom: false,
            CalendarInstanceId: new byte[16]);

        string name = r.Text("name").Trim();
        if (name.Length == 0) throw new InvalidDataException("name: the rally has no name.");
        return new Rally(name, weekend, lobby);
    }

    private static WeatherSettings ReadWeather(Fields f, float start) => new(
        Weather: f.Option("preset", RallyOption.Weather),
        WeatherRoll: f.Float("presetRoll"),
        RandomWeather: f.Bool("randomPreset"),
        TargetWeather: f.Option("forecast", RallyOption.Target),
        TargetRoll: f.Float("forecastRoll"),
        RandomTarget: f.Bool("randomForecast"),
        TargetProbability: f.Option("forecastProbability", RallyOption.Probability),
        Persistency: f.Option("persistency", RallyOption.Persistency),
        DynamicWeather: f.Bool("dynamic"),
        StartTime: start,
        Seed: f.Int("seed"),
        Wetness: f.Float("wetness"),
        Snow: f.Float("snow"),
        TimeSpeed: f.Option("timeSpeed", RallyOption.Speed),
        Grip: f.Option("grip", RallyOption.Grip));

    /// <summary>
    /// The weather the menu's reset goes back to: the game's calendars' plain
    /// weather at the stage's start, with the stage's own draws.
    /// </summary>
    private static WeatherSettings PlainWeather(WeatherSettings? s) => new(
        Weather: 0, WeatherRoll: s?.WeatherRoll ?? 0.5f, RandomWeather: false,
        TargetWeather: 0, TargetRoll: s?.TargetRoll ?? 0.5f, RandomTarget: false,
        TargetProbability: 1, Persistency: 2, DynamicWeather: false,
        StartTime: s?.StartTime ?? 8 * 3600, Seed: s?.Seed ?? 1,
        Wetness: 0, Snow: 0, TimeSpeed: 1, Grip: 4);

    /// <summary>The fields of one JSON object, read with errors that say where.</summary>
    private sealed class Fields(JsonObject node, string path, GameCatalog catalog)
    {
        public string Path => path;

        private JsonNode Need(string name) =>
            node[name] ?? throw new InvalidDataException($"{path}{name} is missing.");

        private InvalidDataException Bad(string name, string what) =>
            new($"{path}{name}: {what}.");

        public JsonValue Scalar(string name) =>
            Need(name) as JsonValue ?? throw Bad(name, "a single value was expected");

        public string Text(string name) =>
            Scalar(name).TryGetValue(out string? s) ? s : throw Bad(name, "text was expected");

        public string Identifier(string name)
        {
            string s = Text(name);
            return Bytes.IsIdentifier(s) ? s : throw Bad(name, $"\"{s}\" is not one of the game's ids");
        }

        public string? OptionalIdentifier(string name) => node[name] is null ? null : Identifier(name);

        public bool Bool(string name) =>
            Scalar(name).TryGetValue(out bool b) ? b : throw Bad(name, "true or false was expected");

        public int Int(string name) =>
            Scalar(name).TryGetValue(out int v) ? v : throw Bad(name, "a whole number was expected");

        public float Float(string name)
        {
            var value = Scalar(name);
            if (value.GetValueKind() == JsonValueKind.String &&
                value.GetValue<string>() is "NaN" or "Infinity" or "-Infinity")
            {
                return float.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture);
            }
            if (value.GetValueKind() != JsonValueKind.Number) throw Bad(name, "a number was expected");
            // the text, not a double: the float written comes back bit for bit
            return float.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>A value of an option: the catalog's id, or the number the game stores.</summary>
        public byte Option(string name, string property)
        {
            var value = Scalar(name);
            int? number = value.GetValueKind() switch
            {
                JsonValueKind.Number when value.TryGetValue(out int n) => n,
                JsonValueKind.String => catalog.OptionValueOf(property, value.GetValue<string>()),
                _ => null,
            };
            return number is >= 0 and <= byte.MaxValue
                ? (byte)number.Value
                : throw Bad(name, $"{value.ToJsonString()} is not a value of this option");
        }

        public float Clock(string name)
        {
            string s = Text(name);
            return TimeSpan.TryParseExact(s, [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"],
                                          CultureInfo.InvariantCulture, out var t) && t.TotalHours < 24
                ? (float)t.TotalSeconds
                : throw Bad(name, $"\"{s}\" is not a time of day (HH:MM)");
        }

        public Fields Object(string name) =>
            Need(name) is JsonObject o ? new Fields(o, $"{path}{name}.", catalog) : throw Bad(name, "an object was expected");

        public List<Fields> Array(string name)
        {
            if (Need(name) is not JsonArray list) throw Bad(name, "a list was expected");
            var items = new List<Fields>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                items.Add(list[i] is JsonObject o
                    ? new Fields(o, $"{path}{name}[{i}].", catalog)
                    : throw Bad($"{name}[{i}]", "an object was expected"));
            }
            return items;
        }
    }
}
