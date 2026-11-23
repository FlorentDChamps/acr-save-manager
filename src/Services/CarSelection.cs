using System.IO;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// The car of the stage selection past the event list (docs §3.4): one car for
/// every mode, the one the menus show until another is picked. Changing it
/// changes nothing else: the stage and the weather options stay.
/// </summary>
public static class CarSelection
{
    // The lists before the event list (§3.2) hold a few dozen names at most;
    // the bound only keeps a search from walking the whole file.
    private const int MaxSearch = 8 * 1024;
    private const int MaxNames = 200;

    /// <summary>
    /// The cars the save keeps a date for (docs §3.2), in its order: the
    /// list of { FString name ; int64 ticks } that comes right before the
    /// location list, itself right before the event list. Empty when it does
    /// not read so.
    /// </summary>
    public static IReadOnlyList<string> Recorded(byte[] save)
    {
        var list = EventList.Find(save);
        if (DatedListEndingAt(save, list.Start) is not { } locations) return [];
        return DatedListEndingAt(save, locations.Start)?.Names ?? [];
    }

    /// <summary>A counted list of { FString name ; int64 ticks } that ends exactly at <paramref name="end"/>.</summary>
    private static (int Start, List<string> Names)? DatedListEndingAt(byte[] data, int end)
    {
        for (int start = end - 4; start >= Math.Max(0, end - MaxSearch); start--)
        {
            int count = ReadInt32(data, start);
            if (count < 1 || count > MaxNames) continue;

            int pos = start + 4;
            var names = new List<string>(count);
            bool ok = true;
            for (int i = 0; i < count && ok; i++)
            {
                ok = TryReadIdentifier(data, ref pos, out string name) && pos + 8 <= end;
                if (ok)
                {
                    names.Add(name);
                    pos += 8;
                }
            }
            if (ok && pos == end) return (start, names);
        }
        return null;
    }

    /// <summary>A save with another car selected, and its size field before and after.</summary>
    public sealed record CarWrite(byte[] Data, string OldCar, string NewCar, int OldSizeField, int NewSizeField);

    /// <summary>
    /// The save with <paramref name="car"/> (one of the game's ids) as the
    /// selection's car. Throws <see cref="InvalidDataException"/> when the
    /// save holds no selection.
    /// </summary>
    public static CarWrite Replace(byte[] save, string car)
    {
        if (!IsIdentifier(car)) throw new InvalidDataException($"\"{car}\" is not one of the game's ids.");
        var list = EventList.Find(save);
        var selection = list.Selection
            ?? throw new InvalidDataException("The save holds no stage and car selection past its event list.");

        // the car is the second FString of the selection, right after the stage
        int carStart = selection.Start;
        if (!TryReadIdentifier(save, ref carStart, out _))
            throw new InvalidDataException("The stage of the selection does not read.");
        int carEnd = carStart;
        if (!TryReadIdentifier(save, ref carEnd, out string old) || old != selection.Car)
            throw new InvalidDataException("The car of the selection does not read.");

        byte[] bytes = new RallyWeekend.Writer().FString(car).ToArray();
        int delta = bytes.Length - (carEnd - carStart);
        byte[] result = new byte[save.Length + delta];
        Buffer.BlockCopy(save, 0, result, 0, carStart);
        Buffer.BlockCopy(bytes, 0, result, carStart, bytes.Length);
        Buffer.BlockCopy(save, carEnd, result, carStart + bytes.Length, save.Length - carEnd);

        int sizeField = SaveFileParser.FindSizeField1Offset(save);
        if (sizeField + 4 > list.Start)
            throw new InvalidDataException($"size_field_1 at 0x{sizeField:X4} does not precede the event list.");
        int oldSizeField = ReadInt32(save, sizeField);
        WriteInt32(result, sizeField, oldSizeField + delta);

        return new CarWrite(result, old, car, oldSizeField, oldSizeField + delta);
    }

    /// <summary>
    /// Re-read a written save and check it holds what was meant: size_field_1
    /// matches the file, the list and the selection read as before but for
    /// the car, and every other byte is the original's.
    /// </summary>
    public static void Verify(byte[] original, CarWrite write)
    {
        byte[] result = write.Data;
        SaveFileParser.VerifySizeField(result);

        var before = EventList.Find(original);
        var after = EventList.Find(result);
        if (after.Start != before.Start || !after.Events.Select(e => e.Name).SequenceEqual(before.Events.Select(e => e.Name)))
            throw new InvalidDataException("The event list does not hold the same events any more.");
        if (after.Selection is not { } now || before.Selection is not { } was)
            throw new InvalidDataException("The stage and car selection no longer reads past the list.");
        if (now.Car != write.NewCar)
            throw new InvalidDataException("The selection does not hold the car it was meant to.");
        if (now.Stage != was.Stage || !now.Options.SequenceEqual(was.Options))
            throw new InvalidDataException("The stage or the weather options of the selection changed.");

        // Everything else: the original's bytes, but size_field_1 and the car.
        int sizeField = SaveFileParser.FindSizeField1Offset(result);
        for (int i = 0; i < was.Start; i++)
        {
            if (i >= sizeField && i < sizeField + 4) continue;
            if (result[i] != original[i])
                throw new InvalidDataException($"Byte 0x{i:X4}, ahead of the selection, changed.");
        }
        if (!result.AsSpan(now.End).SequenceEqual(original.AsSpan(was.End)))
            throw new InvalidDataException("The data past the selection changed.");
    }
}
