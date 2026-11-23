using System.IO;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// Changing a Rally Weekend and writing it back into the player save: the
/// rules the game's menus keep (hardcore, one location, a service park to start
/// each day…), a calendar of the game's to start from, and the write itself,
/// checked byte for byte before anything reaches the disk.
/// </summary>
public static class RallyWeekendEdit
{
    /// <summary>The game's limits for a Rally Weekend calendar (DT_RaceEventTypes).</summary>
    public const int MaxDays = 4;

    /// <summary>
    /// The stages a day takes in the game's menu. The game's own calendars may
    /// hold more (Alsace, 2 days: 5 on day 1), and so may a rally
    /// (<see cref="MostStagesInADay"/>).
    /// </summary>
    public const int MaxStagesPerDay = 4;

    /// <summary>The game's limit for the opponents of a rally (DT_Disciplines).</summary>
    public const int MaxOpponents = 50;

    public const int MinPlayers = 2;
    public const int MaxPlayers = 16;
    public const float MinSkill = 0.5f;
    public const float MaxSkill = 1.2f;

    /// <summary>The zone of a stage no service park comes before.</summary>
    public const string NoZone = "NoZone";

    // What hardcore mode forces, as the menus do: both damage settings on and the realistic penalty.
    private const byte RealisticPenalty = 2;

    // ── Rules ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The weekend as the game would store it: what hardcore mode forces, the
    /// weather of the stage to be driven next taken from the first stage, and
    /// the calendar marked as changed unless it is still one of the game's.
    /// </summary>
    public static RallyWeekend Normalize(RallyWeekend w, GameCatalog catalog)
    {
        var result = w with
        {
            DamagesEnabled = w.HardCore || w.DamagesEnabled,
            GraphicalDamages = w.HardCore || w.GraphicalDamages,
            Penalty = w.HardCore ? RealisticPenalty : w.Penalty,
            NextWeather = w.Stages.Count > 0 ? w.Stages[0].Weather : w.NextWeather,
        };
        return result with { IsCustom = !IsGameCalendar(result, catalog) };
    }

    /// <summary>
    /// The most stages a day of a rally holds: what the menu takes, or what
    /// the game's own calendars hold, if more.
    /// </summary>
    public static int MostStagesInADay(GameCatalog catalog) =>
        catalog.RallyLocations.SelectMany(l => catalog.CalendarsIn(l.Id)).SelectMany(c => c.Days)
            .Select(d => d.Count).Append(MaxStagesPerDay).Max();

    /// <summary>
    /// An id spelt as the game spells them: ASCII letters and digits only.
    /// The catalog would know "alsac_es2…" or "Alsacés2…" all the same, the
    /// game would not.
    /// </summary>
    public static bool IsGameId(string id) => id.Length > 0 && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// What would stop the game from taking the weekend, in words for the
    /// player; empty when there is nothing. Every value is checked against
    /// what the menus offer, for a rally file may hold anything.
    /// </summary>
    public static IReadOnlyList<string> Problems(RallyWeekend w, GameCatalog catalog)
    {
        var problems = new List<string>();

        if (w.Stages.Count == 0)
        {
            problems.Add("The calendar holds no stage.");
            return problems;
        }

        // the negated form: NaN fails every comparison
        static bool Within(float value, float min, float max) => value >= min && value <= max;
        void Option(string property, byte value, string where)
        {
            var values = catalog.OptionValues(property);
            if (values.Count > 0 && values.All(v => v.Value != value))
                problems.Add($"{where}{catalog.OptionName(property) ?? property}: {value} is not one of the game's values.");
        }

        // Days: from day 1, none skipped, in order.
        int lastDay = 0;
        for (int i = 0; i < w.Stages.Count; i++)
        {
            int day = w.Stages[i].Day;
            if (i == 0 && day != 0) problems.Add("The calendar starts on another day than day 1.");
            if (day < lastDay) problems.Add($"Stage {i + 1} goes back to day {day + 1}.");
            if (day > lastDay + 1) problems.Add($"Day {lastDay + 2} has no stage.");
            lastDay = Math.Max(lastDay, day);
        }
        if (lastDay + 1 > MaxDays) problems.Add($"A rally lasts {MaxDays} days at most.");

        int most = MostStagesInADay(catalog);
        foreach (var day in w.Stages.GroupBy(s => s.Day))
        {
            var stages = day.ToList();
            if (stages.Count > most) problems.Add($"Day {day.Key + 1} holds {stages.Count} stages: {most} at most.");
            if (string.Equals(stages[0].Zone, NoZone, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Day {day.Key + 1} does not start with a service park.");
            for (int i = 1; i < stages.Count; i++)
            {
                if (stages[i].Weather.StartTime < stages[i - 1].Weather.StartTime)
                    problems.Add($"On day {day.Key + 1}, {StageName(stages[i], catalog)} starts before the stage ahead of it.");
            }
        }

        // One location, the calendar's.
        string? location = catalog.CalendarInfoOf(w.Calendar)?.Location;
        if (location is null || !IsGameId(w.Calendar)) problems.Add($"The calendar {w.Calendar} is not one of the game's.");
        for (int i = 0; i < w.Stages.Count; i++)
        {
            var stage = w.Stages[i];
            var info = catalog.Stage(stage.Stage);
            if (info is null || !IsGameId(stage.Stage)) problems.Add($"The stage {stage.Stage} is not known.");
            else if (location is not null && info.Location != location)
                problems.Add($"{info.Name} is not in {catalog.Location(location)?.Name ?? location}.");

            if (!string.Equals(stage.Zone, NoZone, StringComparison.OrdinalIgnoreCase) &&
                (catalog.Zone(stage.Zone) is null || !IsGameId(stage.Zone)))
            {
                problems.Add($"The service park {stage.Zone} is not known.");
            }

            var x = stage.Weather;
            string where = $"SS{i + 1}, ";
            if (!(x.StartTime >= 0 && x.StartTime < 86400))
                problems.Add($"{StageName(stage, catalog)} starts outside the day.");
            if (!Within(x.Wetness, 0, 1)) problems.Add($"{where}the road wetness runs from 0 to 100 %.");
            if (!Within(x.Snow, 0, 1)) problems.Add($"{where}the snow level runs from 0 to 100 %.");
            if (!Within(x.WeatherRoll, 0, 1) || !Within(x.TargetRoll, 0, 1))
                problems.Add($"{where}the weather's random draws run from 0 to 1.");
            if (x.Seed < 0) problems.Add($"{where}the weather's seed is negative.");
            Option(RallyOption.Weather, x.Weather, where);
            Option(RallyOption.Target, x.TargetWeather, where);
            Option(RallyOption.Probability, x.TargetProbability, where);
            Option(RallyOption.Persistency, x.Persistency, where);
            Option(RallyOption.Speed, x.TimeSpeed, where);
            Option(RallyOption.Grip, x.Grip, where);
        }

        // The settings, within what the menus offer.
        if (w.Ai is { } ai)
        {
            if (ai.Opponents is < 1 or > MaxOpponents) problems.Add($"A rally has 1 to {MaxOpponents} opponents.");
            if (!Within(ai.Skill, MinSkill, MaxSkill)) problems.Add("The opponents' skill runs from 50 to 120.");
            if (!Within(ai.RaceCraft, 0, 1)) problems.Add("The opponents' racecraft runs from 0 to 1.");
            Option(RallyOption.Consistency, ai.Consistency, "");
            Option(RallyOption.Spread, ai.Spread, "");
            Option(RallyOption.AiCars, ai.CarSelection, "");
        }
        if (w.RunningOrder is { } order)
        {
            Option(RallyOption.OrderType, order.Type, "");
            if (!Within(order.Roll, 0, 1)) problems.Add("The running order's random draw runs from 0 to 1.");
            if (order.Type == 1 && w.Ai is { } field && (order.Position < 1 || order.Position > field.Opponents + 1))
                problems.Add($"The start position runs from 1 to {field.Opponents + 1}.");
        }
        if (w.Lobby is { } lobby)
        {
            if (lobby.MaxPlayers is < MinPlayers or > MaxPlayers) problems.Add($"A lobby holds {MinPlayers} to {MaxPlayers} players.");
            Option(RallyOption.LobbyCars, lobby.CarSelection, "");
        }
        Option(RallyOption.Penalty, w.Penalty, "");
        Option(RallyOption.Respawn, w.ManualRespawn, "");
        Option(RallyOption.Collisions, w.Collisions, "");
        Option(RallyOption.Wear, w.Wear, "");
        Option(RallyOption.Failures, w.MechanicalFailures, "");

        return problems;
    }

    private static string StageName(RallyWeekendStage stage, GameCatalog catalog) =>
        catalog.Stage(stage.Stage)?.Name ?? stage.Stage;

    /// <summary>
    /// True while the calendar is still the game's own: the same stages in the
    /// same days, service parks and start times, and the weather left as the
    /// game sets it.
    /// </summary>
    public static bool IsGameCalendar(RallyWeekend w, GameCatalog catalog)
    {
        if (catalog.CalendarInfoOf(w.Calendar) is not { } calendar) return false;

        var expected = calendar.Days.SelectMany((stages, day) => stages.Select(s => (Day: day, s))).ToList();
        if (expected.Count != w.Stages.Count) return false;
        for (int i = 0; i < expected.Count; i++)
        {
            var (day, want) = expected[i];
            var have = w.Stages[i];
            if (have.Day != day || GameCatalog.Key(have.Stage) != want.Stage) return false;
            if (!string.Equals(have.Zone, want.ServiceZone ?? NoZone, StringComparison.OrdinalIgnoreCase)) return false;
            if (!IsPlainWeather(have.Weather, want.StartSeconds)) return false;
        }
        return true;
    }

    /// <summary>The weather the game's calendars set: clear, real time, optimal grip, nothing random.</summary>
    private static bool IsPlainWeather(WeatherSettings s, int startSeconds) =>
        s.Weather == 0 && !s.RandomWeather && s.TargetWeather == 0 && !s.RandomTarget
        && s.TargetProbability == 1 && s.Persistency == 2 && !s.DynamicWeather
        && Math.Abs(s.StartTime - startSeconds) < 30 && s.Wetness == 0 && s.Snow == 0
        && s.TimeSpeed == 1 && s.Grip == 4;

    /// <summary>
    /// The weather the game's calendars give a stage starting at
    /// <paramref name="startSeconds"/>, with fresh random draws.
    /// </summary>
    public static WeatherSettings PlainWeather(int startSeconds, Random random) =>
        new(Weather: 0, WeatherRoll: random.NextSingle(), RandomWeather: false,
            TargetWeather: 0, TargetRoll: random.NextSingle(), RandomTarget: false,
            TargetProbability: 1, Persistency: 2, DynamicWeather: false,
            StartTime: startSeconds, Seed: random.Next(1, 10_000_000),
            Wetness: 0, Snow: 0, TimeSpeed: 1, Grip: 4);

    /// <summary>
    /// <paramref name="current"/> with one of the game's calendars in place of
    /// its own, as the menu's preset list loads it: the rules are kept.
    /// </summary>
    public static RallyWeekend FromCalendar(RallyWeekend current, GameCatalog.CalendarInfo calendar, Random random)
    {
        var stages = new List<RallyWeekendStage>();
        for (int day = 0; day < calendar.Days.Count; day++)
        {
            foreach (var run in calendar.Days[day])
            {
                var weather = PlainWeather(run.StartSeconds, random);
                stages.Add(new RallyWeekendStage(day, run.Stage, weather, weather, run.ServiceZone ?? NoZone));
            }
        }
        return current with { Calendar = calendar.Id, Stages = stages, CustomName = "", IsCustom = false };
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>A save with one Rally Weekend replaced, and its size field before and after.</summary>
    public sealed record WeekendWrite(byte[] Data, string Event, int OldSizeField, int NewSizeField);

    /// <summary>
    /// The save with the settings of the Rally Weekend <paramref name="eventName"/>
    /// replaced by <paramref name="weekend"/>: its component data and data_size,
    /// and size_field_1. Refused for a rally under way, and for a weekend of the
    /// other kind (an offline one into the online event, or the reverse).
    /// </summary>
    public static WeekendWrite Replace(byte[] save, string eventName, RallyWeekend weekend)
    {
        var list = EventList.Find(save);
        var ev = list.Events.FirstOrDefault(e => e.Name == eventName);
        if (ev.Name is null) throw new InvalidDataException($"The save holds no {eventName}.");
        if (ev.RallyInProgress)
        {
            throw new InvalidDataException(
                "A rally is under way in this Rally Weekend: it cannot be changed. " +
                "Finish or retire from the rally in the game first.");
        }

        var component = Settings(save, ev);
        var current = RallyWeekend.Read(save[component.DataStart..(component.DataStart + component.DataSize)]);
        if (current.Online != weekend.Online)
            throw new InvalidDataException($"{eventName} holds an {(current.Online ? "online" : "offline")} Rally Weekend.");

        byte[] data = weekend.Write();
        int sizeAt = component.DataStart - 4;
        int end = component.DataStart + component.DataSize;
        int delta = data.Length - component.DataSize;

        byte[] result = new byte[save.Length + delta];
        Buffer.BlockCopy(save, 0, result, 0, sizeAt);
        WriteInt32(result, sizeAt, data.Length);
        Buffer.BlockCopy(data, 0, result, sizeAt + 4, data.Length);
        Buffer.BlockCopy(save, end, result, sizeAt + 4 + data.Length, save.Length - end);

        int sizeField = SaveFileParser.FindSizeField1Offset(save);
        if (sizeField + 4 > list.Start)
            throw new InvalidDataException($"size_field_1 at 0x{sizeField:X4} does not precede the event list.");
        int oldSizeField = ReadInt32(save, sizeField);
        WriteInt32(result, sizeField, oldSizeField + delta);

        return new WeekendWrite(result, eventName, oldSizeField, oldSizeField + delta);
    }

    /// <summary>
    /// Re-read a written save and check it holds what was meant: size_field_1
    /// matches the file, the list holds the same events, the weekend reads back
    /// to exactly the bytes written, and every other byte is the original's.
    /// Throws <see cref="InvalidDataException"/> on the first check that fails.
    /// </summary>
    public static void Verify(byte[] original, WeekendWrite write, RallyWeekend weekend)
    {
        byte[] result = write.Data;
        SaveFileParser.VerifySizeField(result);

        var before = EventList.Find(original);
        var after = EventList.Find(result);
        if (after.Start != before.Start || !after.Events.Select(e => e.Name).SequenceEqual(before.Events.Select(e => e.Name)))
            throw new InvalidDataException("The event list does not hold the same events any more.");

        var oldEvent = before.Events.First(e => e.Name == write.Event);
        var newEvent = after.Events.First(e => e.Name == write.Event);
        var oldComponent = Settings(original, oldEvent);
        var newComponent = Settings(result, newEvent);

        byte[] written = weekend.Write();
        if (!result.AsSpan(newComponent.DataStart, newComponent.DataSize).SequenceEqual(written))
            throw new InvalidDataException("The Rally Weekend does not hold the settings it was meant to.");
        if (RallyWeekend.Read(written).Online != weekend.Online)
            throw new InvalidDataException("The Rally Weekend does not read back as written.");

        // Everything else: the original's bytes, but size_field_1 and data_size.
        int sizeField = SaveFileParser.FindSizeField1Offset(result);
        int oldSizeAt = oldComponent.DataStart - 4;
        for (int i = 0; i < oldSizeAt; i++)
        {
            if (i >= sizeField && i < sizeField + 4) continue;
            if (result[i] != original[i])
                throw new InvalidDataException($"Byte 0x{i:X4}, ahead of the Rally Weekend, changed.");
        }
        int oldEnd = oldComponent.DataStart + oldComponent.DataSize;
        int newEnd = newComponent.DataStart + newComponent.DataSize;
        if (!result.AsSpan(newEnd).SequenceEqual(original.AsSpan(oldEnd)))
            throw new InvalidDataException("The data past the Rally Weekend changed.");
    }

    /// <summary>One weekend of a <see cref="RallyWrite"/>: the save before it, the write, and the weekend written.</summary>
    public sealed record RallyWriteStep(byte[] Before, WeekendWrite Write, RallyWeekend Weekend);

    /// <summary>A rally written into one or more of the save's weekends, one after the other.</summary>
    public sealed record RallyWrite(byte[] Data, IReadOnlyList<RallyWriteStep> Steps);

    /// <summary>
    /// The save with <paramref name="rally"/> in place of each Rally Weekend
    /// named in <paramref name="events"/> (DefaultRally, DefaultOnlineRally),
    /// each laid out as the weekend it replaces (<see cref="Rally.For"/>).
    /// </summary>
    public static RallyWrite Replace(byte[] save, Rally rally, IReadOnlyList<string> events, GameCatalog catalog)
    {
        if (events.Count == 0) throw new InvalidDataException("No Rally Weekend of the save was picked to write.");

        byte[] data = save;
        var steps = new List<RallyWriteStep>();
        foreach (string eventName in events)
        {
            var ev = EventList.Find(data).Events.FirstOrDefault(e => e.Name == eventName);
            var slot = (ev.Name is null ? null : RallyWeekend.Find(data, ev.Start))
                ?? throw new InvalidDataException(
                       $"The save holds no {eventName}: set that Rally Weekend up once in the game first.");
            var weekend = rally.For(slot, catalog);
            var write = Replace(data, eventName, weekend);
            steps.Add(new RallyWriteStep(data, write, weekend));
            data = write.Data;
        }
        return new RallyWrite(data, steps);
    }

    /// <summary>Checks every step of a <see cref="RallyWrite"/> as <see cref="Verify(byte[], WeekendWrite, RallyWeekend)"/> does.</summary>
    public static void Verify(RallyWrite write)
    {
        foreach (var step in write.Steps) Verify(step.Before, step.Write, step.Weekend);
    }

    /// <summary>The Rally Weekend settings component of an event config.</summary>
    private static EventConfig.Component Settings(byte[] save, EventList.Event ev)
    {
        int pos = ev.Start;
        if (!EventConfig.TryRead(save, ref pos, out _, out var components, out string reason))
            throw new InvalidDataException(reason);
        var component = components.FirstOrDefault(c => c.Name == RallyWeekend.ComponentName);
        return component.Name is null
            ? throw new InvalidDataException($"{ev.Name} holds no Rally Weekend settings.")
            : component;
    }
}
