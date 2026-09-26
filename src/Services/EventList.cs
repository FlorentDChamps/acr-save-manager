using System.IO;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// The event config list of the player save (docs §3.3) and the selection
/// that follows it (§3.4): the stage, the car and the weather options the Free
/// Practice menu shows, and most likely the other single-stage modes too. No
/// event block names a stage or a car — Free Practice's own block holds
/// numbers only — so a copy of one block cannot carry them; the list and its
/// selection can, and they sit back to back.
///
/// Those bytes, from the n_events counter to the end of the selection, are
/// what a .race file holds, so one reader serves the save and the file.
/// </summary>
public sealed class EventList
{
    // Observed: 4 events in 0.4, 5 in 0.6, and 3 weather options in both. The
    // bounds are the room left for future releases, not measurements.
    private const int MinEvents = 2;
    private const int MaxEvents = 64;
    private const int MaxOptions = 10;

    /// <summary>One event config, and where its bytes sit.</summary>
    public readonly record struct Event(string Name, int Start, int End);

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
    /// The list a .race file holds: it has to start on the first byte and
    /// end, selection included, on the last one.
    /// </summary>
    public static EventList ReadFile(byte[] file)
    {
        if (!TryRead(file, 0, out var list))
            throw new InvalidDataException("This file does not hold an event list.");

        if (list!.End != file.Length)
        {
            throw new InvalidDataException(
                $"The race file is damaged: its list ends at byte {list.End}, but the " +
                $"file holds {file.Length} bytes.");
        }

        return list;
    }

    /// <summary>
    /// The list whose counter sits at <paramref name="pos"/>, or false. Each
    /// config is walked with the block bounds, and its names have to read like
    /// the identifiers the game gives them: a chance byte pattern passes the
    /// walk alone far more easily.
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
            foreach (var c in components)
                if (c.ClassPath.Length < 8 || !IsIdentifier(c.Name)) return false;

            events.Add(new Event(name, eventStart, pos));
        }

        list = new EventList(start, events, ReadSelection(data, pos));
        return true;
    }

    /// <summary>
    /// stage FString, car FString, n_options int32, then n_options pairs of
    /// FStrings (path, value). The options come in no fixed order, so none of
    /// them can anchor the section: it is read forward from the end of the list.
    /// </summary>
    private static CurrentSelection? ReadSelection(byte[] data, int pos)
    {
        int start = pos;
        if (!TryReadIdentifier(data, ref pos, out string stage)) return null;
        if (!TryReadIdentifier(data, ref pos, out string car)) return null;

        if (pos + 4 > data.Length) return null;
        int count = ReadInt32(data, pos); pos += 4;
        if (count < 1 || count > MaxOptions) return null;

        var options = new List<(string, string)>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadAsciiFString(data, ref pos, out string path)) return null;
            if (!TryReadAsciiFString(data, ref pos, out string value)) return null;
            options.Add((path, value));
        }

        return new CurrentSelection(stage, car, options, start, pos);
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>A save with parts of its list replaced, and what was done.</summary>
    public readonly record struct ListWrite(
        byte[] Data,                        // the whole save, ready to be written
        IReadOnlyList<string> Written,      // events replaced, in file order
        IReadOnlyList<string> Missing,      // events asked for that the save lacks
        bool SelectionWritten,
        int OldSizeField,                   // size_field_1, before and after
        int NewSizeField
    );

    /// <summary>
    /// Replace the <paramref name="events"/> of <paramref name="save"/> — and
    /// its selection when <paramref name="selection"/> is set — with those of
    /// the .race <paramref name="file"/>, then move size_field_1 by the size
    /// difference. Events are matched by name: a file from another release can
    /// hold one the save lacks, which is reported and skipped, and the save's
    /// own events keep their order.
    /// </summary>
    public static ListWrite Replace(byte[] save, byte[] file, IReadOnlyCollection<string> events,
                                    bool selection)
    {
        var target = Find(save);
        var source = ReadFile(file);

        var splices = new List<(int Start, int End, byte[] Bytes)>();
        var written = new List<string>();
        var missing = new List<string>();

        foreach (var e in source.Events)
        {
            if (!events.Contains(e.Name)) continue;

            int i = target.IndexOf(e.Name);
            if (i < 0) { missing.Add(e.Name); continue; }

            splices.Add((target.Events[i].Start, target.Events[i].End, file[e.Start..e.End]));
            written.Add(e.Name);
        }

        if (selection)
        {
            if (source.Selection is not { } theirs)
                throw new InvalidDataException("The race file holds no stage and car selection.");
            if (target.Selection is not { } ours)
                throw new InvalidDataException("The save has no stage and car selection past its event list.");
            splices.Add((ours.Start, ours.End, file[theirs.Start..theirs.End]));
        }

        int sizeFieldOffset = SaveFileParser.FindSizeField1Offset(save);
        if (sizeFieldOffset + 4 > target.Start)
        {
            throw new InvalidDataException(
                $"size_field_1 at 0x{sizeFieldOffset:X4} does not precede the event list " +
                $"at 0x{target.Start:X4}.");
        }

        splices.Sort((a, b) => a.Start.CompareTo(b.Start));
        int delta = splices.Sum(s => s.Bytes.Length - (s.End - s.Start));

        byte[] result = new byte[save.Length + delta];
        int src = 0, dst = 0;
        foreach (var (start, end, bytes) in splices)
        {
            Buffer.BlockCopy(save, src, result, dst, start - src);
            dst += start - src;
            Buffer.BlockCopy(bytes, 0, result, dst, bytes.Length);
            dst += bytes.Length;
            src = end;
        }
        Buffer.BlockCopy(save, src, result, dst, save.Length - src);

        int oldSizeField = ReadInt32(save, sizeFieldOffset);
        WriteInt32(result, sizeFieldOffset, oldSizeField + delta);

        return new ListWrite(result, written, missing, selection, oldSizeField, oldSizeField + delta);
    }

    /// <summary>
    /// Re-read a spliced save and check it holds what <paramref name="write"/>
    /// meant: size_field_1 matches the file, the list still decodes where it was
    /// with the same events in the same order, every part carries the bytes of
    /// the file or of the original as asked, and nothing outside the list moved
    /// but size_field_1. Throws <see cref="InvalidDataException"/> on the first
    /// check that fails.
    /// </summary>
    public static void VerifyIntegrity(byte[] original, ListWrite write, byte[] file)
    {
        byte[] result = write.Data;
        SaveFileParser.VerifySizeField(result);

        var before = Find(original);
        var source = ReadFile(file);
        if (!TryRead(result, before.Start, out var after))
            throw new InvalidDataException($"The event list no longer reads at 0x{before.Start:X4}.");

        if (!after!.Events.Select(e => e.Name).SequenceEqual(before.Events.Select(e => e.Name)))
            throw new InvalidDataException("The event list does not hold the same events any more.");

        for (int i = 0; i < after.Events.Count; i++)
        {
            var e = after.Events[i];
            byte[] expected = write.Written.Contains(e.Name)
                ? source.Part(file, e.Name)
                : original[before.Events[i].Start..before.Events[i].End];
            if (!result.AsSpan(e.Start, e.End - e.Start).SequenceEqual(expected))
                throw new InvalidDataException($"{e.Name} does not hold the bytes it was meant to.");
        }

        if (before.Selection is { } kept)
        {
            if (after.Selection is not { } now)
                throw new InvalidDataException("The stage and car selection no longer reads past the list.");

            var want = write.SelectionWritten ? source.Selection! : kept;
            byte[] wantBytes = write.SelectionWritten
                ? file[want.Start..want.End]
                : original[want.Start..want.End];
            if (!result.AsSpan(now.Start, now.End - now.Start).SequenceEqual(wantBytes))
                throw new InvalidDataException("The stage and car selection does not hold the bytes it was meant to.");
        }

        // Outside the list, only size_field_1 may differ.
        int sizeField = SaveFileParser.FindSizeField1Offset(result);
        for (int i = 0; i < before.Start; i++)
        {
            if (i >= sizeField && i < sizeField + 4) continue;
            if (result[i] != original[i])
                throw new InvalidDataException($"Byte 0x{i:X4}, ahead of the event list, changed.");
        }
        if (!result.AsSpan(after.End).SequenceEqual(original.AsSpan(before.End)))
            throw new InvalidDataException("The data past the event list changed.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int IndexOf(string name)
    {
        for (int i = 0; i < Events.Count; i++)
            if (Events[i].Name == name) return i;
        return -1;
    }

    /// <summary>The bytes of the event <paramref name="name"/> of this list.</summary>
    private byte[] Part(byte[] data, string name)
    {
        var e = Events[IndexOf(name)];
        return data[e.Start..e.End];
    }
}
