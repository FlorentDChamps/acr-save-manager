using System.IO;
using System.Text;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// The fixed points of a GVAS player save (Unreal Engine 5 – Assetto Corsa
/// Rally): the PlayerSaveGameData property and its size_field_1, and the name
/// of the rally the weekend event config holds. The event configs themselves
/// are found and written through <see cref="EventList"/>.
/// </summary>
public static class SaveFileParser
{
    /// <summary>Content of the FString "PlayerSaveGameData\0" (length prefix excluded).</summary>
    private static readonly byte[] PlayerSaveGameDataString =
        Encoding.UTF8.GetBytes("PlayerSaveGameData\0");

    /// <summary>
    /// True when <paramref name="data"/> is a player save: a GVAS file whose
    /// property is PlayerSaveGameData.
    /// </summary>
    public static bool IsPlayerSave(byte[] data) =>
        GvasHeader.IsGvas(data) && IndexOf(data, PlayerSaveGameDataString, 0) >= 0;

    /// <summary>
    /// Name of the rally in the DefaultRally event config starting at
    /// <paramref name="blockStart"/> — first FString of the
    /// RaceEventRaceSettingsRallyWeekendComponent data blob, e.g.
    /// "AlsaceWeekendLong". No other event config names what it holds.
    /// </summary>
    public static string ReadRallyName(byte[] data, int blockStart)
    {
        int pos = blockStart;
        if (!EventConfig.TryRead(data, ref pos, out _, out var components, out string reason))
            throw new InvalidDataException(reason);

        // The rally name is the first component blob's first FString, at fixed
        // offset +96: 96 bytes of fixed fields before it (version, flags, floats).
        const int RallyNameBlobOffset = 96;
        int namePos = components[0].DataStart + RallyNameBlobOffset;

        int nameLen = ReadInt32(data, namePos);
        ValidateFStringLen(nameLen, "rally_name", namePos);
        return Encoding.UTF8.GetString(data, namePos + 4, nameLen - 1); // without the \0
    }

    /// <summary>
    /// Offset of the size_field_1 field (4 bytes LE), which sits immediately after
    /// the "PlayerSaveGameData" FString.
    /// size_field_1 = file length − (size_field_1 offset + 4)
    /// </summary>
    public static int FindSizeField1Offset(byte[] data)
    {
        int strIdx = IndexOf(data, PlayerSaveGameDataString, 0);
        if (strIdx < 0)
        {
            throw new InvalidDataException(
                "\"PlayerSaveGameData\" signature not found in the file.");
        }

        // The FString's int32 length prefix precedes the string by 4 bytes.
        int fstringStart = strIdx - 4;
        int fstringLen = ReadInt32(data, fstringStart);
        ValidateFStringLen(fstringLen, "PlayerSaveGameData", fstringStart);

        // size_field_1 follows the FString directly.
        return fstringStart + 4 + fstringLen;
    }

    /// <summary>
    /// Check size_field_1 against the file length. Throws
    /// <see cref="InvalidDataException"/> when they disagree.
    /// </summary>
    public static void VerifySizeField(byte[] data)
    {
        int sf1Offset = FindSizeField1Offset(data);
        int stored = ReadInt32(data, sf1Offset);
        int actual = data.Length - (sf1Offset + 4);
        if (stored != actual)
        {
            throw new InvalidDataException(
                $"size_field_1 is {stored}, expected {actual} for a {data.Length}-byte file.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ValidateFStringLen(int len, string fieldName, int offset)
    {
        if (len <= 0 || len > 2000)
        {
            throw new InvalidDataException(
                $"Invalid FString length for \"{fieldName}\": {len} " +
                $"at offset 0x{offset:X4}.");
        }
    }
}
