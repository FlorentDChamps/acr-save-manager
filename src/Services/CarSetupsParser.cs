using System.IO;
using ACRSetups;
using static ACRSaveManager.Services.Bytes;
using static ACRSetups.SetupEntryFormat;

namespace ACRSaveManager.Services;

/// <summary>A setup entry parsed from CarSetupsDataSaveSlot.sav.</summary>
public record SetupEntry(
    string CarName,
    string SetupName,
    string Author,
    string Track,
    DateTime Timestamp,
    string GameVersion,
    int EntryStart,
    int EntryEnd
)
{
    /// <summary>
    /// <see cref="Timestamp"/> for display, which the game already writes in the
    /// player's own time zone.  Null for an entry whose stored ticks are
    /// unreadable, so the grid shows nothing rather than 0001-01-01.  Computed,
    /// hence outside the record's equality.
    /// </summary>
    public DateTime? DisplayTimestamp =>
        Timestamp == DateTime.MinValue ? null : Timestamp;
}

/// <summary>Full decoded content of a setup entry.</summary>
public class SetupExportData
{
    public string Car { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Track { get; init; } = "";
    // A setup can be kept for several surfaces, weathers and game modes — or
    // for none in particular, which is an empty list.
    public IReadOnlyList<string> Surfaces { get; init; } = [];
    public IReadOnlyList<string> Weathers { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string GameVersion { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = [];
}

/// <summary>
/// Parses and mutates CarSetupsDataSaveSlot.sav (GVAS / Unreal Engine 5).
/// Binary format documented in docs/CarSetupsDataSaveSlot_structure.txt.
///
/// The author FString following setup_name is empty in entries written by game
/// version 0.4 and populated from 0.5 onward.
/// </summary>
public static class CarSetupsParser
{
    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="data"/> is a setups save: a GVAS file whose
    /// property is CarSetupsSaveGameData.
    /// </summary>
    public static bool IsSetupsSave(byte[] data) =>
        GvasHeader.IsGvas(data) && HasSetupsBlob(data);

    /// <summary>
    /// Build the default export filename for a setup entry.
    /// Pattern: {Car}_{Track}[_{Weather}]_{Name}[_{Author}]_{timestamp}.setup
    /// The weather is named only when the setup is kept for exactly one: one kept
    /// for several is not a setup for any of them in particular.
    /// The file is meant to be sent to other players, so it names its author by
    /// the in-game name the entry already carries, never by the Windows account.
    /// The setup name is whatever the player typed, so a character Windows
    /// refuses in a file name — "/" or ":" say — is replaced by "_".
    /// </summary>
    public static string BuildExportFileName(SetupExportData data)
    {
        string ts = data.Timestamp.ToString("yyyyMMdd_HHmmss");   // already local
        string track = string.IsNullOrEmpty(data.Track) ? "NoTrack" : data.Track;
        string weather = data.Weathers.Count == 1 ? $"_{data.Weathers[0]}" : "";
        string author = string.IsNullOrEmpty(data.Author) ? "" : $"_{data.Author}";
        string name = $"{data.Car}_{track}{weather}_{data.Name}{author}_{ts}.setup";
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    /// <summary>Parse all setup entries from the file bytes.</summary>
    public static List<SetupEntry> ParseSetups(byte[] data) =>
        ParseSetups(data, out _);

    /// <summary>
    /// Same walk, also reporting the offset it stops at — the end of the last
    /// car block.  A file left with no setup at all has no entry to end on, so
    /// the walk itself is the only thing that can say where the blob stops.
    /// </summary>
    private static List<SetupEntry> ParseSetups(byte[] data, out int end)
    {
        int pos = FindBlobStart(data) + 4 + 4; // skip size_field + unk1
        int nCars = ReadInt32(data, pos); pos += 4;

        var entries = new List<SetupEntry>();

        for (int ci = 0; ci < nCars; ci++)
        {
            string carName = ReadFString(data, ref pos);

            int nSetups = ReadInt32(data, pos); pos += 4;
            pos += 4; // unk_field (separator value; also appears between consecutive entries)

            for (int si = 0; si < nSetups; si++)
            {
                var f = WalkEntry(data, pos);
                // the car block's spelling, not the entry's own: the game
                // re-spells the id and the two can differ (§4.2)
                entries.Add(new SetupEntry(
                    carName, f.SetupName, f.Author, f.Track, f.Timestamp, f.GameVersion, pos, f.End));
                pos = f.End;
                // Inter-entry separator: present between entries, absent after last
                if (si < nSetups - 1) pos += 4;
            }
        }

        end = pos;
        return entries;
    }

    /// <summary>
    /// Re-read a spliced buffer and check the blob invariants before it reaches
    /// the disk. A layout change this parser does not know about — a new game
    /// version moving a field, say — shows up here as a walk that no longer lands
    /// on the invariants, and the caller can abort with the file untouched.
    /// Throws <see cref="InvalidDataException"/> describing the failed check.
    /// </summary>
    public static void VerifyIntegrity(byte[] data, int expectedSetups)
    {
        int blobStart = FindBlobStart(data);

        int stored = ReadInt32(data, blobStart);
        int actual = data.Length - (blobStart + 4);
        if (stored != actual)
        {
            throw new InvalidDataException(
                $"size_field is {stored}, expected {actual} for a {data.Length}-byte file.");
        }

        var entries = ParseSetups(data, out int end);
        if (entries.Count != expectedSetups)
        {
            throw new InvalidDataException(
                $"Re-parse found {entries.Count} setup(s), expected {expectedSetups}.");
        }

        // The walk ends where the last car block does, which is EOF. A walk that
        // stops short means the entry boundaries drifted even though the counters
        // add up.
        if (end != data.Length)
        {
            throw new InvalidDataException(
                $"Walk ended at 0x{end:X4}, expected EOF at 0x{data.Length:X4}.");
        }
    }

    /// <summary>Extract the raw bytes of a single setup entry.</summary>
    public static byte[] ExtractEntry(byte[] data, SetupEntry entry)
    {
        CheckEntryAt(data, entry);
        return data[entry.EntryStart..entry.EntryEnd];
    }

    /// <summary>
    /// The int32 the save keeps in front of <paramref name="entry"/> (§4.2): the
    /// car block's separator field for its first entry, the separator between
    /// two entries for the others.  A .setup file leaves it out; the JSON
    /// companion carries it along with the entry.
    /// </summary>
    public static int ReadEntryPrefix(byte[] data, SetupEntry entry)
    {
        CheckEntryAt(data, entry);
        return ReadInt32(data, entry.EntryStart - 4);
    }

    /// <summary>
    /// Check that <paramref name="entry"/> is one whole setup entry — what an
    /// exported .setup file holds — before any field is read from it: the entry
    /// marker opens it, and the walk of its fields ends exactly on its last byte.
    /// A truncated or padded file would otherwise surface as an index error from
    /// whichever field happens to run off the end.  It must also name its car,
    /// which is what the import files it under.
    /// </summary>
    public static void ValidateEntry(byte[] entry) =>
        SetupEntryFormat.ValidateEntry(entry);

    /// <summary>Setup name of a raw entry blob, e.g. a .setup file.</summary>
    public static string ReadEntrySetupName(byte[] entry) =>
        WalkEntry(entry, 0).SetupName;

    /// <summary>
    /// Car the raw entry blob belongs to, as the entry itself spells it
    /// (used when importing a .setup file).
    /// </summary>
    public static string ReadEntryCarName(byte[] entry) =>
        WalkEntry(entry, 0).CarName;

    /// <summary>
    /// Game build that wrote a raw entry blob, e.g. "0.6.0.100866"
    /// (used to show where an imported .setup file comes from).
    /// </summary>
    public static string ReadEntryGameVersion(byte[] entry) =>
        WalkEntry(entry, 0).GameVersion;

    /// <summary>
    /// Return a copy of <paramref name="entry"/> with setup_name replaced by
    /// <paramref name="newName"/>.  All other fields are unchanged.
    /// </summary>
    public static byte[] RenameEntry(byte[] entry, string newName)
    {
        int oldSize = FStringSize(entry, 5);       // current FString, prefix included
        byte[] newFStr = EncodeFString(newName);
        return Splice(entry, 5, oldSize, newFStr); // replace old FString at offset 5
    }

    /// <summary>
    /// Decode all fields of a raw entry blob into a <see cref="SetupExportData"/>.
    /// </summary>
    public static SetupExportData ReadEntryData(byte[] entry)
    {
        var parameters = new Dictionary<string, string>();
        var f = WalkEntry(entry, 0, parameters);

        return new SetupExportData
        {
            Car = f.CarName,
            Name = f.SetupName,
            Author = f.Author,
            Track = f.Track,
            Surfaces = f.Surfaces,
            Weathers = f.Weathers,
            Tags = f.Tags,
            GameVersion = f.GameVersion,
            Timestamp = f.Timestamp,
            Parameters = parameters,
        };
    }

    /// <summary>
    /// Replace the raw bytes of <paramref name="entry"/> in-place with
    /// <paramref name="newEntryBytes"/> and update size_field.
    /// Use after <see cref="RenameEntry"/> to write the rename back to the file.
    /// </summary>
    public static byte[] ReplaceEntry(byte[] data, SetupEntry entry, byte[] newEntryBytes)
    {
        CheckEntryAt(data, entry);

        int sfOffset = FindBlobStart(data);
        int oldLen = entry.EntryEnd - entry.EntryStart;
        int delta = newEntryBytes.Length - oldLen;

        byte[] result = Splice(data, entry.EntryStart, oldLen, newEntryBytes);
        WriteInt32(result, sfOffset, ReadInt32(result, sfOffset) + delta);
        return result;
    }

    /// <summary>
    /// Remove <paramref name="entry"/> from the file.
    /// If it was the only entry for its car, the entire car block is removed.
    /// The int32 in front of it goes with it, so every entry left keeps its own,
    /// and size_field / n_cars / n_setups are updated.
    /// </summary>
    public static byte[] RemoveEntry(byte[] data, SetupEntry entry)
    {
        CheckEntryAt(data, entry);

        int blobStart = FindBlobStart(data);
        int sfOffset = blobStart;
        int pos = blobStart + 8; // skip size_field + unk1
        int nCars = ReadInt32(data, pos); pos += 4;
        int nCarsOffset = blobStart + 8;

        for (int ci = 0; ci < nCars; ci++)
        {
            int carBlockStart = pos;
            string carName = ReadFString(data, ref pos);

            int nSetupsOffset = pos;
            int nSetups = ReadInt32(data, pos); pos += 4;
            pos += 4; // separator_value / unk_field (sep before entry[0])

            if (carName != entry.CarName)
            {
                for (int si = 0; si < nSetups; si++)
                {
                    pos = WalkEntry(data, pos).End;
                    if (si < nSetups - 1) pos += 4;
                }
                continue;
            }

            // Found car block. Locate entry by EntryStart.
            for (int si = 0; si < nSetups; si++)
            {
                int entryStart = pos;
                int entryEnd = WalkEntry(data, pos).End;

                if (entryStart == entry.EntryStart)
                {
                    if (nSetups == 1)
                    {
                        // Remove entire car block (car_name…entry inclusive)
                        int removeLen = entryEnd - carBlockStart;
                        byte[] r = Splice(data, carBlockStart, removeLen, []);
                        WriteInt32(r, nCarsOffset, nCars - 1);
                        WriteInt32(r, sfOffset, ReadInt32(r, sfOffset) - removeLen);
                        return r;
                    }
                    else
                    {
                        // The entry and the int32 in front of it, which is its own
                        // (§4.2): the block header's for entry[0], whose place the
                        // next entry's then takes. Cutting the following one
                        // instead gave the next entry the removed one's value.
                        int removeStart = entryStart - 4;
                        int removeLen = entryEnd - removeStart;
                        byte[] r = Splice(data, removeStart, removeLen, []);
                        WriteInt32(r, nSetupsOffset, nSetups - 1);
                        WriteInt32(r, sfOffset, ReadInt32(r, sfOffset) - removeLen);
                        return r;
                    }
                }

                pos = entryEnd;
                if (si < nSetups - 1) pos += 4;
            }
        }

        throw new InvalidDataException($"Entry '{entry.SetupName}' not found in file.");
    }

    /// <summary>
    /// Append <paramref name="entryBytes"/> to the matching car block
    /// (or create a new car block if the car is not yet present).
    /// Returns the modified file bytes.
    ///
    /// <paramref name="prefix"/> is the int32 that goes in front of the entry
    /// (§4.2).  A .setup file does not hold it, so without one the entry takes
    /// the value its car block opens with, or 2 in a block of its own.
    /// </summary>
    public static byte[] InsertEntry(byte[] data, byte[] entryBytes, int? prefix = null)
    {
        ValidateEntry(entryBytes);

        string carName = ReadEntryCarName(entryBytes);
        int blobStart = FindBlobStart(data);
        int sfOffset = blobStart; // size_field int32 is first in blob

        // Traverse all car blocks; locate the target car (single pass)
        int pos = blobStart + 8; // skip size_field + unk1
        int nCars = ReadInt32(data, pos); pos += 4;

        int nSetupsOffset = -1;
        int unkFieldValue = 2; // separator value (default; read from target car block)
        int carBlockEnd = -1;

        for (int ci = 0; ci < nCars; ci++)
        {
            string thisCarName = ReadFString(data, ref pos);

            int nSetupsOff = pos;
            int nSetups = ReadInt32(data, pos); pos += 4;
            int unkFieldVal = ReadInt32(data, pos); pos += 4;

            for (int si = 0; si < nSetups; si++)
            {
                pos = WalkEntry(data, pos).End;
                if (si < nSetups - 1) pos += 4; // inter-entry separator
            }

            // The car id is an Unreal FName: the engine compares it without case
            // and re-spells it on every save, so the same car is written both
            // "Hyundaii20NRally2" and "HyundaiI20NRally2" from one file to the
            // next. A second block for the same car would be a file the game
            // never writes.
            if (string.Equals(thisCarName, carName, StringComparison.OrdinalIgnoreCase)
                && carBlockEnd < 0)
            {
                nSetupsOffset = nSetupsOff;
                unkFieldValue = unkFieldVal;
                carBlockEnd = pos;
            }
        }

        int allCarsEnd = pos; // used only when car not found

        if (carBlockEnd >= 0)
        {
            // Append to existing car block.
            // Must prepend the inter-entry separator before the new entry.
            byte[] separator = BitConverter.GetBytes(prefix ?? unkFieldValue);
            byte[] toInsert = Concat(separator, entryBytes);
            byte[] result = Splice(data, carBlockEnd, 0, toInsert);

            WriteInt32(result, nSetupsOffset, ReadInt32(result, nSetupsOffset) + 1);
            WriteInt32(result, sfOffset, ReadInt32(result, sfOffset) + toInsert.Length);

            return result;
        }
        else
        {
            // Build a new car block (unk_field = 2 is the default for all known cars).
            // unk_field precedes the sole entry directly (no extra separator needed).
            byte[] carNameFStr = EncodeFString(carName);
            byte[] nSetupsBuf = BitConverter.GetBytes(1);
            byte[] unkField = BitConverter.GetBytes(prefix ?? 2);
            byte[] newCarBlock = Concat(carNameFStr, nSetupsBuf, unkField, entryBytes);

            byte[] result = Splice(data, allCarsEnd, 0, newCarBlock);

            int nCarsOffset = blobStart + 8;
            WriteInt32(result, nCarsOffset, nCars + 1);
            WriteInt32(result, sfOffset, ReadInt32(result, sfOffset) + newCarBlock.Length);

            return result;
        }
    }

    // ── Internal parsing ────────────────────────────────────────────────────

    /// <summary>
    /// Check that <paramref name="entry"/> still describes the bytes it points at.
    /// Its offsets were measured on the file as it was when the list was parsed,
    /// and the game rewrites this file every time it saves: applied to bytes read
    /// later they can land on a different entry, which would then be the one
    /// removed or renamed.  The entry marker and the setup name at EntryStart
    /// tell the two apart; the walk ending exactly at EntryEnd catches the same
    /// setup rewritten in place with another length (a longer author, say),
    /// whose old bounds would cut through the file.
    /// </summary>
    private static void CheckEntryAt(byte[] data, SetupEntry entry)
    {
        if (entry.EntryStart < 0 || entry.EntryEnd <= entry.EntryStart || entry.EntryEnd > data.Length)
        {
            throw new InvalidDataException(
                $"Setup '{entry.SetupName}' spans 0x{entry.EntryStart:X4}–0x{entry.EntryEnd:X4}, " +
                $"outside this {data.Length}-byte file. Reload the list and try again.");
        }

        if (entry.EntryStart + 5 > data.Length ||
            ReadInt32(data, entry.EntryStart) != EntryMarker ||
            data[entry.EntryStart + 4] != 0x00)
        {
            throw new InvalidDataException(
                $"No setup entry starts at 0x{entry.EntryStart:X4}. The file changed " +
                "on disk since the list was loaded — reload it and try again.");
        }

        string name = ReadFStringContent(data, entry.EntryStart + 5);
        if (name != entry.SetupName)
        {
            throw new InvalidDataException(
                $"The setup at 0x{entry.EntryStart:X4} is now '{name}', not " +
                $"'{entry.SetupName}'. The file changed on disk since the list was " +
                "loaded — reload it and try again.");
        }

        int end;
        try { end = WalkEntry(data, entry.EntryStart).End; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            end = -1;   // its fields no longer walk: not the entry that was listed
        }
        if (end != entry.EntryEnd)
        {
            throw new InvalidDataException(
                $"The setup '{entry.SetupName}' no longer ends at 0x{entry.EntryEnd:X4}. " +
                "The file changed on disk since the list was loaded — reload it and try again.");
        }
    }

    /// <summary>Splice: data[..at] + insert + data[at + removeLen ..]</summary>
    private static byte[] Splice(byte[] data, int at, int removeLen, byte[] insert)
    {
        int newLen = data.Length - removeLen + insert.Length;
        byte[] result = new byte[newLen];
        Buffer.BlockCopy(data, 0, result, 0, at);
        Buffer.BlockCopy(insert, 0, result, at, insert.Length);
        Buffer.BlockCopy(data, at + removeLen, result, at + insert.Length, data.Length - at - removeLen);
        return result;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        int total = 0; foreach (byte[] a in arrays) total += a.Length;
        byte[] result = new byte[total];
        int off = 0; foreach (byte[] a in arrays) { Buffer.BlockCopy(a, 0, result, off, a.Length); off += a.Length; }
        return result;
    }

}
