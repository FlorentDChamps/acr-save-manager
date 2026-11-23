using System.Globalization;
using System.IO;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// A rally's first stage written into the single-stage modes: Single Stage
/// offline and online, Free Practice, and the stage selection past the event
/// list (docs §3.4) they all read. The game keeps there the last stage used,
/// with its weather type, start time and time speed, and a single-stage menu
/// copies those three into its own settings when it opens: so the selection
/// and the modes' settings are written together, to agree.
///
/// Each mode takes the rally's rules and the first stage's whole weather, laid
/// out as its own settings: the opponents and running order offline, the
/// lobby online, the penalty alone in Free Practice, whose free-drive flag
/// stays. The car of the selection stays too.
/// </summary>
public static class SingleStageEdit
{
    public const string OfflineEvent = "DefaultSingleStage";
    public const string OnlineEvent = "DefaultOnlineSingleStage";
    public const string PracticeEvent = "DefaultFreePractice";
    public const string PracticeComponent = "RaceEventRaceSettingsFreePracticeComponent";

    // Free Practice (game 0.7): the rules, the weather and hardcore every mode
    // starts with (0x43 bytes), then the penalty (u8) and bFreeDrive (4-byte flag).
    private const int PracticeSize = 0x48;
    private const int SharedPrefix = 0x43;
    private const int PracticePenalty = 0x43;
    private const int WeatherAt = 0x15;

    public const string PresetPath = "/Script/acr.WeatherOptions/Preset";
    public const string StartPath = "/Script/acr.WeatherOptions/StartingTime";
    public const string SpeedPath = "/Script/acr.WeatherOptions/TimeSpeed";

    /// <summary>
    /// A save with the single-stage modes rewritten: the events written, in file
    /// order, each one's new component data, and the new selection.
    /// </summary>
    public sealed record StageWrite(
        byte[] Data,
        IReadOnlyList<string> Events,
        IReadOnlyDictionary<string, byte[]> Components,
        byte[] Selection,
        int OldSizeField,
        int NewSizeField);

    /// <summary>
    /// The save with <paramref name="rally"/>'s first stage in its Single
    /// Stage, Online Single Stage and Free Practice, those of them it holds,
    /// and in the stage selection. Throws <see cref="InvalidDataException"/>
    /// when a mode's settings are not laid out as expected.
    /// </summary>
    public static StageWrite Replace(byte[] save, Rally rally, GameCatalog catalog)
    {
        if (rally.Weekend.Stages.Count == 0) throw new InvalidDataException("The rally holds no stage.");
        var list = EventList.Find(save);
        var selection = list.Selection
            ?? throw new InvalidDataException("The save holds no stage selection past its event list.");

        var splices = new List<(int Start, int End, byte[] Bytes)>();
        var components = new Dictionary<string, byte[]>();
        foreach (var ev in list.Events)
        {
            string? componentName = ev.Name switch
            {
                OfflineEvent or OnlineEvent => RallyWeekend.SingleStageComponent,
                PracticeEvent => PracticeComponent,
                _ => null,
            };
            if (componentName is null) continue;

            var component = Component(save, ev, componentName);
            byte[] current = save[component.DataStart..(component.DataStart + component.DataSize)];
            byte[] bytes = ev.Name == PracticeEvent ? Practice(current, rally, catalog) : Single(current, ev.Name, rally, catalog);
            if (bytes.Length != current.Length)
                throw new InvalidDataException($"{ev.Name} would change size ({current.Length} → {bytes.Length} bytes).");

            splices.Add((component.DataStart, component.DataStart + component.DataSize, bytes));
            components[ev.Name] = bytes;
        }

        byte[] newSelection = Selection(selection, rally, catalog);
        splices.Add((selection.Start, selection.End, newSelection));

        int sizeField = SaveFileParser.FindSizeField1Offset(save);
        if (sizeField + 4 > list.Start)
            throw new InvalidDataException($"size_field_1 at 0x{sizeField:X4} does not precede the event list.");

        int delta = newSelection.Length - (selection.End - selection.Start);
        byte[] result = new byte[save.Length + delta];
        int src = 0, dst = 0;
        foreach (var (start, end, bytes) in splices.OrderBy(s => s.Start))
        {
            Buffer.BlockCopy(save, src, result, dst, start - src);
            dst += start - src;
            Buffer.BlockCopy(bytes, 0, result, dst, bytes.Length);
            dst += bytes.Length;
            src = end;
        }
        Buffer.BlockCopy(save, src, result, dst, save.Length - src);

        int oldSizeField = ReadInt32(save, sizeField);
        WriteInt32(result, sizeField, oldSizeField + delta);

        var written = list.Events.Select(e => e.Name).Where(components.ContainsKey).ToList();
        return new StageWrite(result, written, components, newSelection, oldSizeField, oldSizeField + delta);
    }

    /// <summary>A Single Stage's settings: the rally's, laid out as that mode's are, the first stage's weather.</summary>
    private static byte[] Single(byte[] current, string eventName, Rally rally, GameCatalog catalog)
    {
        var slot = RallyWeekend.ReadSingleStage(current);
        if (slot.Online != (eventName == OnlineEvent))
            throw new InvalidDataException($"{eventName} does not hold the settings of an {(slot.Online ? "online" : "offline")} mode.");
        return rally.For(slot, catalog).WriteSingleStage();
    }

    /// <summary>
    /// Free Practice's settings: the rally's rules, the first stage's weather
    /// and the penalty; its tyre and fuel options and free-drive flag stay.
    /// </summary>
    private static byte[] Practice(byte[] current, Rally rally, GameCatalog catalog)
    {
        if (current.Length != PracticeSize)
            throw new InvalidDataException($"The Free Practice settings hold {current.Length} bytes, {PracticeSize} were expected.");
        foreach (int at in new[] { 0x00, 0x06, 0x0A, 0x0E, 0x3F, 0x44 })
        {
            if (ReadInt32(current, at) is not (0 or 1))
                throw new InvalidDataException($"The Free Practice settings hold an unexpected flag at byte {at}.");
        }

        var slot = rally.Weekend with
        {
            TyresConsumption = ReadInt32(current, 0x00) == 1,
            TyresDamage = current[0x04],
            FuelConsumption = ReadInt32(current, 0x06) == 1,
        };
        var settings = rally.For(slot, catalog);
        byte[] shared = settings.WriteSingleStage();

        byte[] result = (byte[])current.Clone();
        Buffer.BlockCopy(shared, 0, result, 0, SharedPrefix);
        result[PracticePenalty] = settings.Penalty;
        return result;
    }

    /// <summary>
    /// The selection past the list with the rally's first stage, and its
    /// weather type, start time and time speed in place of the options the
    /// game keeps; the car and any other option stay.
    /// </summary>
    private static byte[] Selection(EventList.CurrentSelection selection, Rally rally, GameCatalog catalog)
    {
        var first = rally.Weekend.Stages[0];
        var weather = first.Weather;
        string preset = catalog.OptionValueId(RallyOption.Weather, weather.Weather)
            ?? throw new InvalidDataException($"Weather type {weather.Weather} has no name in the catalog.");
        string speed = catalog.OptionValueId(RallyOption.Speed, weather.TimeSpeed)
            ?? throw new InvalidDataException($"Time speed {weather.TimeSpeed} has no name in the catalog.");

        var values = new Dictionary<string, string>
        {
            [StartPath] = string.Create(CultureInfo.InvariantCulture, $"(TimeSeconds={weather.StartTime:F6})"),
            [PresetPath] = string.Create(CultureInfo.InvariantCulture,
                $"(WeatherType={preset},RandomUniform={weather.WeatherRoll:F6},bRandom={(weather.RandomWeather ? "True" : "False")})"),
            [SpeedPath] = speed,
        };

        // the options as the save orders them, then those it lacks (a new player's save has none)
        var options = selection.Options.Select(o => (o.Path, Value: values.GetValueOrDefault(o.Path, o.Value))).ToList();
        foreach (var (path, value) in values)
            if (options.All(o => o.Path != path)) options.Add((path, value));

        var w = new RallyWeekend.Writer();
        w.FString(first.Stage).FString(selection.Car).Int32(options.Count);
        foreach (var (path, value) in options) w.FString(path).FString(value);
        return w.ToArray();
    }

    /// <summary>
    /// Re-read a written save and check it holds what was meant: size_field_1
    /// matches the file, the list holds the same events, each mode's settings
    /// and the selection read back to the bytes written and say what the rally
    /// says, and every other byte is the original's.
    /// </summary>
    public static void Verify(byte[] original, StageWrite write, Rally rally)
    {
        byte[] result = write.Data;
        SaveFileParser.VerifySizeField(result);

        var before = EventList.Find(original);
        var after = EventList.Find(result);
        if (after.Start != before.Start || !after.Events.Select(e => e.Name).SequenceEqual(before.Events.Select(e => e.Name)))
            throw new InvalidDataException("The event list does not hold the same events any more.");

        var first = rally.Weekend.Stages[0];
        for (int i = 0; i < after.Events.Count; i++)
        {
            var ev = after.Events[i];
            var old = before.Events[i];
            if (!write.Components.TryGetValue(ev.Name, out byte[]? expected))
            {
                if (!result.AsSpan(ev.Start, ev.End - ev.Start).SequenceEqual(original.AsSpan(old.Start, old.End - old.Start)))
                    throw new InvalidDataException($"{ev.Name} changed, and was not meant to.");
                continue;
            }

            string name = ev.Name == PracticeEvent ? PracticeComponent : RallyWeekend.SingleStageComponent;
            var component = Component(result, ev, name);
            if (!result.AsSpan(component.DataStart, component.DataSize).SequenceEqual(expected))
                throw new InvalidDataException($"{ev.Name} does not hold the settings it was meant to.");

            if (ev.Name != PracticeEvent) RallyWeekend.ReadSingleStage(expected);   // reads as the game's layout
            var stageWeather = new RallyWeekend.Writer();
            WriteWeatherBytes(stageWeather, first.Weather);
            if (!expected.AsSpan(WeatherAt, RallyWeekend.WeatherSize).SequenceEqual(stageWeather.ToArray()))
                throw new InvalidDataException($"{ev.Name} does not hold the first stage's weather.");
        }

        if (after.Selection is not { } now)
            throw new InvalidDataException("The stage selection no longer reads past the list.");
        if (!result.AsSpan(now.Start, now.End - now.Start).SequenceEqual(write.Selection) || now.Stage != first.Stage)
            throw new InvalidDataException("The stage selection does not hold the stage it was meant to.");
        if (before.Selection is { } kept && now.Car != kept.Car)
            throw new InvalidDataException("The car of the stage selection changed.");

        // Everything else: the original's bytes, but size_field_1.
        int sizeField = SaveFileParser.FindSizeField1Offset(result);
        int oldSelection = before.Selection!.Start;
        for (int i = 0; i < oldSelection; i++)
        {
            if (i >= sizeField && i < sizeField + 4) continue;
            if (result[i] == original[i]) continue;
            if (write.Components.Keys.Any(e => Within(original, before, e, i))) continue;
            throw new InvalidDataException($"Byte 0x{i:X4}, outside the single-stage settings, changed.");
        }
        if (!result.AsSpan(now.End).SequenceEqual(original.AsSpan(before.Selection.End)))
            throw new InvalidDataException("The data past the stage selection changed.");
    }

    /// <summary>True when <paramref name="at"/> lies in the settings data of the event <paramref name="eventName"/>.</summary>
    private static bool Within(byte[] save, EventList list, string eventName, int at)
    {
        var ev = list.Events.First(e => e.Name == eventName);
        if (at < ev.Start || at >= ev.End) return false;
        var component = Component(save, ev, eventName == PracticeEvent ? PracticeComponent : RallyWeekend.SingleStageComponent);
        return at >= component.DataStart && at < component.DataStart + component.DataSize;
    }

    private static void WriteWeatherBytes(RallyWeekend.Writer w, WeatherSettings s) =>
        w.Byte(s.Weather).Float(s.WeatherRoll).Bool(s.RandomWeather)
         .Byte(s.TargetWeather).Float(s.TargetRoll).Bool(s.RandomTarget)
         .Byte(s.TargetProbability).Byte(s.Persistency).Bool(s.DynamicWeather)
         .Float(s.StartTime).Int32(s.Seed).Float(s.Wetness).Float(s.Snow)
         .Byte(s.TimeSpeed).Byte(s.Grip);

    /// <summary>The settings component <paramref name="name"/> of an event config.</summary>
    private static EventConfig.Component Component(byte[] save, EventList.Event ev, string name)
    {
        int pos = ev.Start;
        if (!EventConfig.TryRead(save, ref pos, out _, out var components, out string reason))
            throw new InvalidDataException(reason);
        var component = components.FirstOrDefault(c => c.Name == name);
        return component.Name is null
            ? throw new InvalidDataException($"{ev.Name} holds no {name}.")
            : component;
    }
}
