using System.Text;

namespace ACRSaveManager.Tests.Fixtures;

/// <summary>
/// Little-endian byte writer with Unreal's FString rule: a pure-ASCII string is
/// written as single bytes under a positive length, anything else as UTF-16LE
/// under a negative length counting code units — null terminator included.
/// </summary>
internal sealed class Writer
{
    private readonly List<byte> _bytes = [];

    public int Length => _bytes.Count;

    public Writer Int32(int v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public Writer UInt16(ushort v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public Writer UInt32(uint v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public Writer Int64(long v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public Writer Float(float v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public Writer Byte(byte v) { _bytes.Add(v); return this; }
    public Writer Raw(byte[] v) { _bytes.AddRange(v); return this; }
    public Writer Zeros(int n) { _bytes.AddRange(new byte[n]); return this; }

    public Writer FString(string s)
    {
        if (s.Length == 0) return Int32(0);
        if (s.All(c => c < 0x80))
        {
            Int32(s.Length + 1);
            _bytes.AddRange(Encoding.ASCII.GetBytes(s));
            _bytes.Add(0);
        }
        else
        {
            Int32(-(s.Length + 1));
            _bytes.AddRange(Encoding.Unicode.GetBytes(s + "\0"));
        }
        return this;
    }

    /// <summary>An int32 count, then that many FStrings.</summary>
    public Writer FStrings(string[] list)
    {
        Int32(list.Length);
        foreach (string s in list) FString(s);
        return this;
    }

    public byte[] ToArray() => [.. _bytes];
}

/// <summary>One synthetic setup entry. Every value is made up.</summary>
internal sealed record Setup(
    string Name,
    string Car,
    string Author = "",
    string Track = "TestStage",
    string Surface = "Tarmac",
    long Ticks = 639_000_000_000_000_000,   // 2025-12-..., the player's own clock
    string GameVersion = "0.6.0.100866",
    int Parameters = 12,
    string[]? Weathers = null,
    string[]? Tags = null);

/// <summary>
/// Builds CarSetupsDataSaveSlot and PlayerDataSaveSlot files with the layout
/// documented in docs/, and nothing but invented content — so a test can run,
/// and be shared, without a single real save.
/// </summary>
internal static class SaveBuilder
{
    public const string RaceEventSettings = "ERaceEventSerializableContext::RaceEventSettings";

    // ── GVAS header + property preamble (docs, §1–§3) ──────────────────────────

    private static void Header(Writer w, string property)
    {
        w.Raw("GVAS"u8.ToArray())
         .Int32(3).Int32(522).Int32(1017)              // save / UE4 / UE5 package versions
         .UInt16(5).UInt16(6).UInt16(1).UInt32(0)      // engine 5.6.1, changelist 0
         .FString("++UE5+Release-5.6")
         .Int32(3).Int32(2);                           // custom version format, count
        for (int i = 0; i < 2; i++)
            w.Raw([.. Enumerable.Range(0, 16).Select(b => (byte)(b * 7 + i))]).Int32(i + 1);
        w.FString("/Script/dmengine.SaveGameSlot");

        w.Byte(0).FString("None").Int32(0)             // pad, empty property list, flags
         .Int32(0)                                     // secondary custom versions: none
         .Int32(1).FString(property);                  // property_count, property name
    }

    // ── CarSetupsDataSaveSlot ──────────────────────────────────────────────────

    /// <summary>The raw bytes of one entry — what a .setup file holds.</summary>
    public static byte[] Entry(Setup s)
    {
        var w = new Writer();
        w.Int32(511).Byte(0)                           // entry marker + pad
         .FString(s.Name)
         .FString(s.Author)
         .Int64(s.Ticks)
         .FString(s.GameVersion)
         .FString(s.Car)
         .FString(s.Track)
         .FStrings([s.Surface])                        // conditions: surfaces,
         .FStrings(s.Weathers ?? [])                   // weathers
         .FStrings(s.Tags ?? [])                       // and game mode tags
         .Int32(s.Parameters);
        for (int k = 0; k < s.Parameters; k++)
            w.FString($"Param{k:00}").FString($"{k}.5");
        return w.ToArray();
    }

    /// <summary>
    /// A setups save holding <paramref name="setups"/>, grouped into car blocks
    /// in first-seen order under the car name each block is given.
    /// </summary>
    public static byte[] CarSetups(params Setup[] setups) =>
        CarSetups(setups.GroupBy(s => s.Car).Select(g => (g.Key, g.ToArray())).ToArray());

    /// <summary>Explicit car blocks, for a block named differently from its entries.</summary>
    public static byte[] CarSetups(params (string Block, Setup[] Setups)[] cars)
    {
        var w = new Writer();
        Header(w, "CarSetupsSaveGameData");

        int sizeFieldAt = w.Length;
        w.Int32(0)                                     // size_field, patched below
         .Int32(1)                                     // unk1
         .Int32(cars.Length);
        foreach (var (block, setups) in cars)
        {
            w.FString(block).Int32(setups.Length).Int32(2);   // name, n_setups, separator
            for (int i = 0; i < setups.Length; i++)
            {
                if (i > 0) w.Int32(2);                        // inter-entry separator
                w.Raw(Entry(setups[i]));
            }
        }

        byte[] data = w.ToArray();
        BitConverter.GetBytes(data.Length - (sizeFieldAt + 4)).CopyTo(data, sizeFieldAt);
        return data;
    }

    // ── PlayerDataSaveSlot ─────────────────────────────────────────────────────

    public const string RallyName = "TestWeekendShort";
    public const string RallyStage = "TestlandS1ValleyForward";

    // The weather settings name a stage and a car of their own, on purpose
    // different from the weekend's: they are read from another part of the file.
    public const string WeatherStage = "TestlandS2RidgeReverse";
    public const string WeatherCar = "TestCarGtA";

    public const string PlayerName = "Tester";

    /// <summary>
    /// A player save with the four event configs of game 0.4, the stage, car
    /// and weather selection that follows them, and a leaderboard block per
    /// base event.
    /// </summary>
    /// <param name="leaderboardFirst">Put a leaderboard block named like an event
    /// config ahead of the config list, so anchoring by name alone would land on
    /// the wrong occurrence.</param>
    /// <param name="playerName">Free text stored in the profile section.</param>
    /// <param name="presetFirst">Write the weather Preset option ahead of
    /// StartingTime, an order the game also uses.</param>
    /// <param name="stage">Stage of the selection past the list.</param>
    /// <param name="seed">Shifts every event blob, so two saves differ in each
    /// of their events.</param>
    /// <param name="online">Add the fifth event config of game 0.6.</param>
    public static byte[] PlayerData(bool leaderboardFirst = false, string playerName = PlayerName,
                                    bool presetFirst = false, string stage = WeatherStage,
                                    int seed = 0, bool online = false)
    {
        var w = new Writer();
        Header(w, "PlayerSaveGameData");

        int sizeFieldAt = w.Length;
        w.Int32(0)                                     // size_field_1, patched below
         .Int32(87);                                   // size_field_2
        w.FString("fr").FString("French");             // a few settings, as in §3.1

        if (leaderboardFirst) Leaderboard(w, "DefaultTimeAttack", 3);

        // §3.3: counter, then the event configs
        w.Int32(online ? 5 : 4);
        EventConfig(w, "DefaultFreePractice", "FreePractice", Pattern(72, 1 + seed));
        EventConfig(w, "DefaultTimeAttack", "TimeAttack", Pattern(118, 2 + seed));
        EventConfig(w, "DefaultSingleStage", "SingleEvent", Pattern(96, 3 + seed));
        EventConfig(w, "DefaultRally", "RallyWeekend", RallyBlob(seed), uuids: 2);
        if (online) EventConfig(w, "DefaultOnlineSingleStage", "SingleEvent", Pattern(73, 4 + seed), uuids: 2);

        // §3.4: the weather settings, past the end of the config list
        var start = ("/Script/acr.WeatherOptions/StartingTime", "(TimeSeconds=30600.000000)");
        var preset = ("/Script/acr.WeatherOptions/Preset", "(WeatherType=WT_CLEAR,RandomUniform=0.5,bRandom=False)");
        var speed = ("/Script/acr.WeatherOptions/TimeSpeed", "WT_SPEEDREALISTIC");
        w.FString(stage).FString(WeatherCar).Int32(3);
        foreach (var (path, value) in presetFirst ? [preset, start, speed] : new[] { start, preset, speed })
            w.FString(path).FString(value);

        // the blocks that follow, one of them sharing a config's name
        Leaderboard(w, "DefaultFreePractice", 2);
        Leaderboard(w, "DefaultRally", 1);
        Leaderboard(w, "TestStage_PART1_F", 4);

        // the profile, the only free text in the file
        w.Int32(2).Int32(511).Byte(0).FString(playerName).Zeros(16);

        byte[] data = w.ToArray();
        BitConverter.GetBytes(data.Length - (sizeFieldAt + 4)).CopyTo(data, sizeFieldAt);
        return data;
    }

    private static byte[] Pattern(int size, int seed) =>
        [.. Enumerable.Range(0, size).Select(i => (byte)(i * 31 + seed))];

    /// <summary>
    /// The rally weekend's own blob: the name the tool reads at the fixed offset
    /// +96, then the itinerary. No weather in it — that section is written after
    /// the config list instead.
    /// </summary>
    private static byte[] RallyBlob(int seed)
    {
        var w = new Writer();
        w.Raw(Pattern(96, 9 + seed))                   // fixed fields
         .FString(RallyName)
         .Int32(1).Int32(0)                            // one stage in the itinerary
         .FString(RallyStage).Raw(Pattern(24, 5))
         .FString("ServiceParkDefault");
        return w.ToArray();
    }

    private static void EventConfig(Writer w, string name, string kind, byte[] data, int uuids = 1)
    {
        w.FString(name).Int32(1)                       // event_name, n_components
         .FString(RaceEventSettings)
         .Int32(uuids);
        for (int u = 0; u < uuids; u++) w.Raw(Pattern(16, u + name.Length));
        for (int x = 0; x <= uuids; x++) w.Int32(x);  // (nb_uuids + 1) extra ints
        w.FString($"RaceEventRaceSettings{kind}Component")
         .Int32(data.Length).Raw(data);
    }

    private static void Leaderboard(Writer w, string name, int runs)
    {
        w.FString(name).Int32(1)                       // event_name, one session
         .FString("TestlandS1ValleyForward")
         .Int32(runs);
        for (int r = 0; r < runs; r++)
        {
            w.Int32(r).FString("TestCarRally2")
             .Int32(2)                                 // two checkpoints
             .Int32(0).Float(0).Float(0)
             .Int32(1).Float(60.5f + r).Float(60.5f + r)
             .Float(0).Zeros(8);                       // penalty + reserved
        }
        w.Int64(639_000_000_000_000_000);              // session timestamp
    }
}
