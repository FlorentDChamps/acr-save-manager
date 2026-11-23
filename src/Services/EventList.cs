using System.IO;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// The event config list of the player save (docs §3.3) and the selection
/// that follows it (§3.4): the last stage the single-stage modes used, which
/// they share (Single Stage offline and online, Free Practice), the car, and
/// three weather options. No
/// event block names a stage or a car — Free Practice's own block holds
/// numbers only — so a copy of one block cannot carry them; the list and its
/// selection can, and they sit back to back.
/// </summary>
public sealed class EventList
{
    // Observed: 4 events in 0.4, 5 in 0.6, 1 in a new player's save (the list
    // holds only the modes played); 3 weather options, none until a single
    // stage has been driven. The bounds are the room left for future
    // releases, not measurements.
    private const int MinEvents = 1;
    private const int MaxEvents = 64;
    private const int MaxOptions = 10;

    /// <summary>
    /// One event config, and where its bytes sit. <paramref name="RallyInProgress"/>:
    /// it holds the state of a rally weekend under way (§3.3), besides its
    /// settings.
    /// </summary>
    public readonly record struct Event(string Name, int Start, int End, bool RallyInProgress = false);

    /// <summary>The selection past the list, and where its bytes sit.</summary>
    public sealed record CurrentSelection(
        string Stage,
        string Car,
        IReadOnlyList<(string Path, string Value)> Options,
        int Start,
        int End);

    private EventList(int start, List<Event> events, CurrentSelection? selection)
    {
        Start = start;
        Events = events;
        Selection = selection;
    }

    /// <summary>Offset of the n_events counter.</summary>
    public int Start { get; }

    public IReadOnlyList<Event> Events { get; }

    /// <summary>Null when the bytes past the list do not read as a selection.</summary>
    public CurrentSelection? Selection { get; }

    /// <summary>Offset just past the last event config.</summary>
    public int EventsEnd => Events[^1].End;

    /// <summary>Offset just past the selection, or past the list without one.</summary>
    public int End => Selection?.End ?? EventsEnd;

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The list in a player save. Its events are renamed and added from one
    /// release to the next, so it is found by its structure: the first offset
    /// where a whole list decodes.
    /// </summary>
    public static EventList Find(byte[] data)
    {
        for (int pos = 0; pos + 8 <= data.Length; pos++)
        {
            if (TryRead(data, pos, out var list)) return list!;
        }

        throw new InvalidDataException("Event config list not found in the file.");
    }

    /// <summary>
    /// The list whose counter sits at <paramref name="pos"/>, or false. Each
    /// config is walked with the block bounds, its names have to read like
    /// the identifiers the game gives them and its contexts carry the game's
    /// class path: a chance byte pattern passes the walk alone far more easily,
    /// all the more for a list of one event.
    /// </summary>
    private static bool TryRead(byte[] data, int pos, out EventList? list)
    {
        list = null;
        int start = pos;

        if (pos + 4 > data.Length) return false;
        int count = ReadInt32(data, pos); pos += 4;
        if (count < MinEvents || count > MaxEvents) return false;

        var events = new List<Event>(count);
        for (int i = 0; i < count; i++)
        {
            int eventStart = pos;
            if (!EventConfig.TryRead(data, ref pos, out string name, out var components, out _)) return false;
            if (!IsIdentifier(name)) return false;
            bool gameNames = components.All(c => c.ClassPath.StartsWith(EventConfig.ContextPrefix, StringComparison.Ordinal)
                                                 && IsIdentifier(c.Name));
            if (!gameNames) return false;

            bool inProgress = components.Any(c => c.ClassPath == EventConfig.RallyState);
            events.Add(new Event(name, eventStart, pos, inProgress));
        }

        list = new EventList(start, events, ReadSelection(data, pos));
        return true;
    }

    /// <summary>
    /// stage FString, car FString, n_options int32, then n_options pairs of
    /// FStrings (path, value), none in a new player's save. The options come in
    /// no fixed order, so none of them can anchor the section: it is read
    /// forward from the end of the list.
    /// </summary>
    private static CurrentSelection? ReadSelection(byte[] data, int pos)
    {
        int start = pos;
        if (!TryReadIdentifier(data, ref pos, out string stage)) return null;
        if (!TryReadIdentifier(data, ref pos, out string car)) return null;

        if (pos + 4 > data.Length) return null;
        int count = ReadInt32(data, pos); pos += 4;
        if (count < 0 || count > MaxOptions) return null;

        var options = new List<(string, string)>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadAsciiFString(data, ref pos, out string path)) return null;
            if (!TryReadAsciiFString(data, ref pos, out string value)) return null;
            options.Add((path, value));
        }

        return new CurrentSelection(stage, car, options, start, pos);
    }
}
