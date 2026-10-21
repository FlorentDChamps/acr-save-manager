// One setup entry of CarSetupsDataSaveSlot.sav, the game's save of setups, as
// ACR Save Manager and ACR Overlay both read and write it. This file is the
// same, byte for byte, in the two: it is changed in the save manager first,
// then copied to the overlay, whose CI fails when its copy has drifted.
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ACRSetups;

/// <summary>
/// The setup entry: the single walk of its fields, the check an entry must
/// pass before it is trusted, and the strings it is made of. The layout is
/// the one of the save manager's docs/CarSetupsDataSaveSlot_structure.txt.
/// </summary>
internal static class SetupEntryFormat
{
    // "CarSetupsSaveGameData\0" — FString content (length=22, includes null)
    private static readonly byte[] CarSetupsSaveGameDataStr =
        Encoding.UTF8.GetBytes("CarSetupsSaveGameData\0");

    /// <summary>
    /// The entry_marker int32 that opens every entry, followed by the
    /// marker_pad byte (§4.3).
    /// </summary>
    public const int EntryMarker = 511;

    /// <summary>
    /// True when <paramref name="data"/> holds the setups' blob, the property
    /// named CarSetupsSaveGameData.
    /// </summary>
    public static bool HasSetupsBlob(byte[] data) =>
        data.AsSpan().IndexOf(CarSetupsSaveGameDataStr) >= 0;

    /// <summary>
    /// Where the setups' blob starts: just past the FString that names it.
    /// Throws <see cref="InvalidDataException"/> for a file that has none.
    /// </summary>
    public static int FindBlobStart(byte[] data)
    {
        int idx = data.AsSpan().IndexOf(CarSetupsSaveGameDataStr);
        if (idx < 0)
            throw new InvalidDataException("'CarSetupsSaveGameData' not found in file.");
        // idx = start of FString content; blob follows immediately after
        return idx + CarSetupsSaveGameDataStr.Length;
    }

    /// <summary>
    /// Check that <paramref name="entry"/> is one whole setup entry — what an
    /// exported .setup file holds — before any field is read from it: the
    /// entry marker opens it, and the walk of its fields ends exactly on its
    /// last byte.  A truncated or padded file would otherwise surface as an
    /// index error from whichever field happens to run off the end.  It must
    /// also name its car, which is what an import files it under.
    /// </summary>
    public static void ValidateEntry(byte[] entry)
    {
        if (entry.Length < 5 || ReadInt32(entry, 0) != EntryMarker || entry[4] != 0x00)
        {
            throw new InvalidDataException(
                "This is not a setup file: it does not open with a setup entry marker.");
        }

        int end;
        try { end = WalkEntry(entry, 0).End; }
        catch (ArgumentException)
        {
            // a field ran off the end before the walk could finish
            end = int.MaxValue;
        }
        catch (InvalidDataException ex)
        {
            // a length or a count no setup has: say which, not that it is short
            throw new InvalidDataException($"The setup file is damaged: {ex.Message}");
        }

        if (end > entry.Length)
        {
            throw new InvalidDataException(
                "The setup file is truncated or damaged: its fields run past the end " +
                $"of its {entry.Length} bytes.");
        }

        if (end < entry.Length)
        {
            throw new InvalidDataException(
                $"The setup file is damaged: its entry ends at byte {end}, but the file " +
                $"is {entry.Length} bytes long.");
        }

        // The car name picks the car block the setup goes into: without one,
        // the import would create a block for no car at all.
        if (WalkEntry(entry, 0).CarName.Length == 0)
        {
            throw new InvalidDataException(
                "The setup file does not name the car it belongs to, so it cannot be " +
                "imported.");
        }
    }

    /// <summary>
    /// Every field of the setup entry at <paramref name="pos"/>, and the
    /// offset just past it.  This is the only walk of an entry: every reader
    /// of one goes through it, so a field the format adds — the author, empty
    /// before game 0.5 — cannot be accounted for in one place and forgotten in
    /// another.  Fills <paramref name="parameters"/> with the key/value pairs
    /// when asked.
    /// </summary>
    public static EntryFields WalkEntry(byte[] data, int pos, Dictionary<string, string>? parameters = null)
    {
        pos += 5; // entry_marker(4) + marker_pad(1)

        string setupName = ReadFString(data, ref pos);
        string author = ReadFString(data, ref pos);   // empty before game 0.5

        uint tsLo = (uint)ReadInt32(data, pos);
        uint tsHi = (uint)ReadInt32(data, pos + 4);
        long ticks = ((long)tsHi << 32) | tsLo;
        // The game stamps an entry with the player's own clock, not UTC: read
        // as UTC and shown converted, a setup saved at 15:00 appeared as 17:00.
        // (The other timestamps met in these saves are UTC; these are not.)
        // Kind stays Unspecified: the file names no time zone, and a .setup is
        // made to be sent to players in other ones.
        var ts = IsValidTicks(ticks) ? new DateTime(ticks) : DateTime.MinValue;
        pos += 8;

        string gameVersion = ReadFString(data, ref pos);
        string carName = ReadFString(data, ref pos);   // same car as the block
        string track = ReadFString(data, ref pos);

        // Conditions block (§4.5): three lists of strings, each an int32 count
        // and that many FStrings. Most entries keep zero or one of each, but a
        // setup kept for several weathers lists them all.
        string[] surfaces = ReadStringList(data, ref pos, "surface");
        string[] weathers = ReadStringList(data, ref pos, "weather");
        string[] tags = ReadStringList(data, ref pos, "tag");

        int kvCount = ReadInt32(data, pos);
        if (kvCount < 0 || kvCount > MaxParameters)
        {
            throw new InvalidDataException(
                $"Implausible setup parameter count {kvCount} at 0x{pos:X4}.");
        }
        pos += 4;
        for (int k = 0; k < kvCount; k++)
        {
            string key = ReadFString(data, ref pos);
            string value = ReadFString(data, ref pos);
            parameters?[key] = value;
        }

        return new EntryFields(
            setupName, author, ts, gameVersion, carName, track, surfaces, weathers, tags, pos);
    }

    /// <summary>
    /// Fields of one setup entry, plus the offset just past it.
    /// </summary>
    public readonly record struct EntryFields(
        string SetupName,
        string Author,
        DateTime Timestamp,
        string GameVersion,
        string CarName,
        string Track,
        string[] Surfaces,
        string[] Weathers,
        string[] Tags,
        int End
    );

    // Bounds on counts read from the file, so a walk that has lost its place
    // stops on a plain error instead of looping over garbage. Measured: at most
    // 5 strings in a condition list, 56–77 setup parameters.
    private const int MaxConditions = 64;
    private const int MaxParameters = 1000;

    /// <summary>
    /// Read an int32 count and that many FStrings, moving past them.
    /// </summary>
    private static string[] ReadStringList(byte[] data, ref int pos, string field)
    {
        int count = ReadInt32(data, pos);
        if (count < 0 || count > MaxConditions)
        {
            throw new InvalidDataException(
                $"Implausible {field} count {count} at 0x{pos:X4}.");
        }
        pos += 4;

        string[] list = new string[count];
        for (int i = 0; i < count; i++) list[i] = ReadFString(data, ref pos);
        return list;
    }

    // ── Byte helpers ────────────────────────────────────────────────────────

    private static bool IsValidTicks(long ticks) =>
        ticks > 0 && ticks <= DateTime.MaxValue.Ticks;

    // Little-endian whatever the machine is, which BinaryPrimitives states
    // outright and BitConverter only assumes. Throws
    // ArgumentOutOfRangeException past the end of the buffer.
    private static int ReadInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

    // FString, as Unreal serialises it: an int32 length, then the characters
    // and a null terminator.  A pure-ASCII string takes one byte per character
    // under a positive length; any other is written in UTF-16LE under a
    // NEGATIVE length that counts 2-byte code units.  Reading a negative length
    // as a byte count walks the cursor backwards, so every length goes through
    // these helpers.
    //
    // No string of these saves comes near MaxFStringChars. A length past it,
    // in either encoding, is a walk that lost its place or a damaged file: it
    // stops the walk. Read as an empty string and stepped over, a long ASCII
    // one used to list a setup with no name at all.

    private const int MaxFStringChars = 4096;

    /// <summary>
    /// Bytes the FString at <paramref name="offset"/> takes, prefix included.
    /// </summary>
    public static int FStringSize(byte[] data, int offset)
    {
        int len = ReadInt32(data, offset);
        if (len < -MaxFStringChars || len > MaxFStringChars)
            throw new InvalidDataException($"Implausible string length {len} at 0x{offset:X4}.");
        return len >= 0 ? 4 + len : 4 - 2 * len;
    }

    /// <summary>
    /// Read the FString at <paramref name="pos"/> and move past it.
    /// </summary>
    public static string ReadFString(byte[] data, ref int pos)
    {
        string s = ReadFStringContent(data, pos);
        pos += FStringSize(data, pos);
        return s;
    }

    /// <summary>The text of the FString at <paramref name="offset"/>.</summary>
    public static string ReadFStringContent(byte[] data, int offset)
    {
        // An implausible length already yields "", so a length that would read
        // past the end of the buffer does the same rather than throwing: the
        // callers are reading fields whose position they are not yet sure of.
        if (offset < 0 || offset + 4 > data.Length) return "";
        int len = ReadInt32(data, offset);
        if (len < 0)
        {
            if (len < -MaxFStringChars || offset + 4 - 2L * len > data.Length) return "";
            return Encoding.Unicode.GetString(data, offset + 4, (-len - 1) * 2); // strip null
        }
        if (len == 0 || len > MaxFStringChars || offset + 4 + len > data.Length) return "";
        return Encoding.UTF8.GetString(data, offset + 4, len - 1); // strip null
    }

    /// <summary>
    /// Encode <paramref name="s"/> the way Unreal would.  A name such as "Été"
    /// written as UTF-8 bytes under a positive length would be read back by
    /// the game as single-byte characters, one per byte.  An empty string is a
    /// zero length and nothing else, not a lone terminator under a length of 1.
    /// </summary>
    public static byte[] EncodeFString(string s)
    {
        if (s.Length == 0) return new byte[4];
        bool ascii = s.All(c => c < 0x80);
        byte[] str = ascii ? Encoding.ASCII.GetBytes(s) : Encoding.Unicode.GetBytes(s);
        int nul = ascii ? 1 : 2;
        byte[] result = new byte[4 + str.Length + nul]; // len prefix + content + null
        BinaryPrimitives.WriteInt32LittleEndian(result, ascii ? s.Length + 1 : -(s.Length + 1));
        Buffer.BlockCopy(str, 0, result, 4, str.Length);
        return result;
    }
}
