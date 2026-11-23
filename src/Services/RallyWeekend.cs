using System.IO;
using System.Text;

namespace ACRSaveManager.Services;

/// <summary>
/// The weather options of a stage, or of the stage to be driven next: the
/// game's FWeatherOptions, 42 bytes in the save (docs §3.3). Enumerations are
/// kept as the numbers the game stores; the catalog names them.
/// </summary>
public sealed record WeatherSettings(
    byte Weather,               // EWeatherType: 0 clear … 10 blizzard
    float WeatherRoll,          // the game's random draw for it
    bool RandomWeather,         // the game picks the weather itself
    byte TargetWeather,         // the forecast, EWeatherType
    float TargetRoll,
    bool RandomTarget,
    byte TargetProbability,     // ETargetWeatherProbabilityOption
    byte Persistency,           // EWeatherPersistencyOption
    bool DynamicWeather,
    float StartTime,            // seconds since midnight
    int Seed,
    float Wetness,              // starting road wetness, 0..1
    float Snow,                 // starting snow level, 0..1
    byte TimeSpeed,             // EWeatherSpeed
    byte Grip);                 // EGripLevel

/// <summary>One stage of a Rally Weekend's calendar.</summary>
public sealed record RallyWeekendStage(
    int Day,                    // 0 = day 1
    string Stage,               // the game's stage id
    WeatherSettings Weather,
    WeatherSettings DefaultWeather,   // what the menu's reset goes back to
    string Zone);               // the zone before the stage: a service park, or NoZone

/// <summary>The opponents of an offline Rally Weekend (the game's FAIOptions).</summary>
public sealed record AiSettings(
    int Opponents,
    float Skill,                // 0.5..1.2
    byte Consistency,           // EAIConsistency
    byte Spread,                // EAISpread
    float RaceCraft,
    byte CarSelection,          // EAICarClassSelection
    byte[] CustomSlots);        // EAICustomCarSelection per slot: no menu fills it in 0.7

/// <summary>The running order of an offline Rally Weekend.</summary>
public sealed record RunningOrderSettings(byte Type, int Position, float Roll);   // Type: 0 random, 1 custom

/// <summary>The lobby of an online Rally Weekend.</summary>
public sealed record LobbySettings(int MaxPlayers, byte CarSelection);   // ELobbyCarSelectionOption

/// <summary>
/// The settings of a Rally Weekend, offline (DefaultRally) or online
/// (DefaultOnlineRally): the data of a RaceEventRaceSettingsRallyWeekendComponent,
/// read field by field (docs §3.3). The two share everything but the middle:
/// offline, the opponents, the penalties and the running order; online, the
/// penalties and the lobby. <see cref="Read(byte[])"/> and <see cref="Write"/>
/// are exact inverses, which is what lets a weekend be rewritten later.
/// </summary>
public sealed record RallyWeekend(
    bool TyresConsumption,      // not in the game's menus yet (0.7)
    byte TyresDamage,           // ETyresDamageOption, not in the menus yet
    byte ManualRespawn,         // EManualRespawnOption
    bool FuelConsumption,       // not in the menus yet
    bool DamagesEnabled,
    bool GraphicalDamages,
    byte Collisions,            // ECarDamagesCollisionsOption
    byte Wear,                  // ECarDamagesWearOption
    byte MechanicalFailures,    // ECarDamagesMechanicalFailuresOption
    WeatherSettings NextWeather,   // the stage to be driven next
    bool HardCore,
    AiSettings? Ai,             // offline only
    byte Penalty,               // ERacePenaltyOption
    RunningOrderSettings? RunningOrder,   // offline only
    LobbySettings? Lobby,       // online only
    string Calendar,            // the game's calendar it started from, e.g. "SwedenWeekendLong"
    IReadOnlyList<RallyWeekendStage> Stages,
    string CustomName,
    bool IsCustom,              // the calendar was changed in the menu
    byte[] CalendarInstanceId)  // 16 bytes
{
    /// <summary>The name of the component that holds a Rally Weekend's settings.</summary>
    public const string ComponentName = "RaceEventRaceSettingsRallyWeekendComponent";

    public const int WeatherSize = 42;

    public bool Online => Lobby is not null;

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The settings in a component's data. The data says nothing of its own
    /// layout, so it is read as the offline one, then as the online one: the
    /// one that ends exactly on its last byte, its strings and flags all well
    /// formed, is the one.
    /// </summary>
    public static RallyWeekend Read(byte[] data)
    {
        string? offlineError;
        try { return Read(data, online: false); }
        catch (InvalidDataException ex) { offlineError = ex.Message; }

        try { return Read(data, online: true); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"The Rally Weekend settings are not laid out as expected (offline: {offlineError} " +
                $"Online: {ex.Message})");
        }
    }

    /// <summary>The settings in a component's data, in the given layout.</summary>
    public static RallyWeekend Read(byte[] data, bool online) => ReadFields(data, online, calendar: true);

    /// <summary>The name of the component that holds a Single Stage's settings, offline and online.</summary>
    public const string SingleStageComponent = "RaceEventRaceSettingsSingleEventComponent";

    /// <summary>
    /// The settings of a Single Stage (a SingleEventComponent): a Rally
    /// Weekend's, offline or online, without the calendar, which this
    /// component does not add (96 bytes offline, 73 online in game 0.7). The
    /// weather is <see cref="NextWeather"/>; the calendar comes back empty.
    /// </summary>
    public static RallyWeekend ReadSingleStage(byte[] data)
    {
        string? offlineError;
        try { return ReadFields(data, online: false, calendar: false); }
        catch (InvalidDataException ex) { offlineError = ex.Message; }

        try { return ReadFields(data, online: true, calendar: false); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"The Single Stage settings are not laid out as expected (offline: {offlineError} " +
                $"Online: {ex.Message})");
        }
    }

    private static RallyWeekend ReadFields(byte[] data, bool online, bool calendar)
    {
        var r = new Reader(data);

        bool tyresConsumption = r.Bool();
        byte tyresDamage = r.Byte();
        byte respawn = r.Byte();
        bool fuelConsumption = r.Bool();
        bool damages = r.Bool();
        bool graphical = r.Bool();
        byte collisions = r.Byte();
        byte wear = r.Byte();
        byte failures = r.Byte();
        var next = ReadWeather(r);
        bool hardCore = r.Bool();

        AiSettings? ai = null;
        RunningOrderSettings? order = null;
        LobbySettings? lobby = null;
        byte penalty;
        if (online)
        {
            penalty = r.Byte();
            lobby = new LobbySettings(r.Int32(), r.Byte());
        }
        else
        {
            int opponents = r.Int32();
            float skill = r.Float();
            byte consistency = r.Byte();
            byte spread = r.Byte();
            float raceCraft = r.Float();
            byte carSelection = r.Byte();
            int slots = r.Count(64);
            ai = new AiSettings(opponents, skill, consistency, spread, raceCraft, carSelection, r.Take(slots));
            penalty = r.Byte();
            order = new RunningOrderSettings(r.Byte(), r.Int32(), r.Float());
        }

        if (!calendar)
        {
            if (!r.AtEnd)
                throw new InvalidDataException($"{data.Length - r.Position} bytes are left past the settings.");
            return new RallyWeekend(tyresConsumption, tyresDamage, respawn, fuelConsumption, damages, graphical,
                                    collisions, wear, failures, next, hardCore, ai, penalty, order, lobby,
                                    "", [], "", false, new byte[16]);
        }

        string calendarId = r.Identifier();
        int count = r.Count(64);
        var stages = new List<RallyWeekendStage>(count);
        for (int i = 0; i < count; i++)
        {
            int day = r.Int32();
            string stage = r.Identifier();
            var weather = ReadWeather(r);
            var defaults = ReadWeather(r);
            stages.Add(new RallyWeekendStage(day, stage, weather, defaults, r.Identifier()));
        }
        string customName = r.FString();
        bool isCustom = r.Bool();
        byte[] instance = r.Take(16);

        if (!r.AtEnd)
            throw new InvalidDataException($"{data.Length - r.Position} bytes are left past the calendar.");

        return new RallyWeekend(tyresConsumption, tyresDamage, respawn, fuelConsumption, damages, graphical,
                                collisions, wear, failures, next, hardCore, ai, penalty, order, lobby,
                                calendarId, stages, customName, isCustom, instance);
    }

    /// <summary>
    /// The Rally Weekend settings of the event config starting at
    /// <paramref name="eventStart"/> in a save, or null when it holds none.
    /// </summary>
    public static RallyWeekend? Find(byte[] save, int eventStart)
    {
        int pos = eventStart;
        if (!EventConfig.TryRead(save, ref pos, out _, out var components, out string reason))
            throw new InvalidDataException(reason);

        // The settings context comes first, before any rally in progress.
        var component = components.FirstOrDefault(c => c.Name == ComponentName);
        return component.Name is null ? null : Read(save[component.DataStart..(component.DataStart + component.DataSize)]);
    }

    private static WeatherSettings ReadWeather(Reader r)
    {
        int start = r.Position;
        var weather = new WeatherSettings(
            r.Byte(), r.Float(), r.Bool(),
            r.Byte(), r.Float(), r.Bool(),
            r.Byte(), r.Byte(), r.Bool(),
            r.Float(), r.Int32(), r.Float(), r.Float(),
            r.Byte(), r.Byte());
        if (r.Position - start != WeatherSize) throw new InvalidOperationException("Weather block size.");
        return weather;
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>The component data, laid out as the game writes it.</summary>
    public byte[] Write() => WriteFields(calendar: true);

    /// <summary>The settings as a Single Stage holds them: all but the calendar, <see cref="NextWeather"/> its weather.</summary>
    public byte[] WriteSingleStage() => WriteFields(calendar: false);

    private byte[] WriteFields(bool calendar)
    {
        var w = new Writer();
        w.Bool(TyresConsumption).Byte(TyresDamage).Byte(ManualRespawn).Bool(FuelConsumption)
         .Bool(DamagesEnabled).Bool(GraphicalDamages)
         .Byte(Collisions).Byte(Wear).Byte(MechanicalFailures);
        WriteWeather(w, NextWeather);
        w.Bool(HardCore);

        if (Lobby is { } lobby)
        {
            w.Byte(Penalty).Int32(lobby.MaxPlayers).Byte(lobby.CarSelection);
        }
        else
        {
            var ai = Ai ?? throw new InvalidOperationException("An offline Rally Weekend has opponents settings.");
            var order = RunningOrder ?? throw new InvalidOperationException("An offline Rally Weekend has a running order.");
            w.Int32(ai.Opponents).Float(ai.Skill).Byte(ai.Consistency).Byte(ai.Spread).Float(ai.RaceCraft)
             .Byte(ai.CarSelection).Int32(ai.CustomSlots.Length).Raw(ai.CustomSlots)
             .Byte(Penalty)
             .Byte(order.Type).Int32(order.Position).Float(order.Roll);
        }
        if (!calendar) return w.ToArray();

        w.FString(Calendar).Int32(Stages.Count);
        foreach (var stage in Stages)
        {
            w.Int32(stage.Day).FString(stage.Stage);
            WriteWeather(w, stage.Weather);
            WriteWeather(w, stage.DefaultWeather);
            w.FString(stage.Zone);
        }
        if (CalendarInstanceId.Length != 16) throw new InvalidOperationException("The calendar instance id is 16 bytes.");
        w.FString(CustomName).Bool(IsCustom).Raw(CalendarInstanceId);
        return w.ToArray();
    }

    private static void WriteWeather(Writer w, WeatherSettings s) =>
        w.Byte(s.Weather).Float(s.WeatherRoll).Bool(s.RandomWeather)
         .Byte(s.TargetWeather).Float(s.TargetRoll).Bool(s.RandomTarget)
         .Byte(s.TargetProbability).Byte(s.Persistency).Bool(s.DynamicWeather)
         .Float(s.StartTime).Int32(s.Seed).Float(s.Wetness).Float(s.Snow)
         .Byte(s.TimeSpeed).Byte(s.Grip);

    // ── Byte access ───────────────────────────────────────────────────────────

    /// <summary>
    /// A forward reader that fails with <see cref="InvalidDataException"/>:
    /// past the end, on a flag other than 0 or 1, or on a string that is not
    /// one — anything a wrong layout would produce.
    /// </summary>
    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position == data.Length;

        private void Need(int n)
        {
            if (Position + n > data.Length)
                throw new InvalidDataException($"The data ends at byte {data.Length}, {n} more were expected at {Position}.");
        }

        public byte Byte() { Need(1); return data[Position++]; }

        public int Int32() { Need(4); int v = Bytes.ReadInt32(data, Position); Position += 4; return v; }

        public float Float()
        {
            Need(4);
            float v = BitConverter.Int32BitsToSingle(Bytes.ReadInt32(data, Position));
            Position += 4;
            return v;
        }

        /// <summary>A flag, stored on 4 bytes.</summary>
        public bool Bool()
        {
            int at = Position;
            int v = Int32();
            return v switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException($"Unexpected flag {v} at byte {at}."),
            };
        }

        public int Count(int max)
        {
            int at = Position;
            int v = Int32();
            if (v < 0 || v > max) throw new InvalidDataException($"Unexpected count {v} at byte {at}.");
            return v;
        }

        public byte[] Take(int n) { Need(n); byte[] v = data[Position..(Position + n)]; Position += n; return v; }

        /// <summary>Unreal's FString: UTF-8 under a positive length, UTF-16 under a negative one, null included.</summary>
        public string FString()
        {
            int at = Position;
            int len = Int32();
            if (len == 0) return "";
            if (len > 0)
            {
                if (len > 1024) throw new InvalidDataException($"Unexpected string length {len} at byte {at}.");
                Need(len);
                if (data[Position + len - 1] != 0) throw new InvalidDataException($"Unterminated string at byte {at}.");
                string s = Encoding.UTF8.GetString(data, Position, len - 1);
                Position += len;
                return s;
            }
            if (len < -1024) throw new InvalidDataException($"Unexpected string length {len} at byte {at}.");
            int size = -len * 2;
            Need(size);
            if (data[Position + size - 1] != 0 || data[Position + size - 2] != 0)
                throw new InvalidDataException($"Unterminated string at byte {at}.");
            string u = Encoding.Unicode.GetString(data, Position, size - 2);
            Position += size;
            return u;
        }

        /// <summary>An FString holding one of the game's ids: a calendar, a stage, a zone.</summary>
        public string Identifier()
        {
            int at = Position;
            string s = FString();
            if (!Bytes.IsIdentifier(s))
                throw new InvalidDataException($"Unexpected name \"{Shorten(s)}\" at byte {at}.");
            return s;
        }

        private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
    }

    /// <summary>A little-endian writer of the game's fields, flags on 4 bytes and FStrings as the game writes them.</summary>
    internal sealed class Writer
    {
        private readonly MemoryStream _out = new();

        public Writer Byte(byte v) { _out.WriteByte(v); return this; }

        public Writer Int32(int v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, v);
            _out.Write(b);
            return this;
        }

        public Writer Float(float v) => Int32(BitConverter.SingleToInt32Bits(v));

        public Writer Bool(bool v) => Int32(v ? 1 : 0);

        public Writer Raw(byte[] v) { _out.Write(v); return this; }

        /// <summary>Pure ASCII as single bytes, anything else as UTF-16 under a negative length.</summary>
        public Writer FString(string s)
        {
            if (s.Length == 0) return Int32(0);
            if (s.All(c => c < 0x80))
            {
                Int32(s.Length + 1);
                _out.Write(Encoding.ASCII.GetBytes(s));
                return Byte(0);
            }
            Int32(-(s.Length + 1));
            _out.Write(Encoding.Unicode.GetBytes(s + "\0"));
            return this;
        }

        public byte[] ToArray() => _out.ToArray();
    }
}
