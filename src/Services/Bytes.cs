using System.Buffers.Binary;
using System.Text;

namespace ACRSaveManager.Services;

/// <summary>
/// The byte-level reads every parser here needs. The save files are
/// little-endian whatever the machine is, which is what BinaryPrimitives
/// states outright and BitConverter only assumes.
/// </summary>
internal static class Bytes
{
    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> past the end of the
    /// buffer, as the callers that probe uncertain offsets expect.
    /// </summary>
    public static int ReadInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

    public static void WriteInt32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);

    /// <summary>
    /// Offset of the first <paramref name="needle"/> at or after
    /// <paramref name="from"/>, or -1.
    /// </summary>
    public static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        if (from < 0) from = 0;
        if (from > haystack.Length) return -1;

        int found = haystack.AsSpan(from).IndexOf(needle);
        return found < 0 ? -1 : from + found;
    }

    /// <summary>True when <paramref name="data"/> starts with <paramref name="prefix"/>.</summary>
    public static bool StartsWith(byte[] data, byte[] prefix) =>
        data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    /// <summary>
    /// Reads a null-terminated, printable-ASCII FString.  Returns false without
    /// moving <paramref name="pos"/> when the bytes are not a plausible string,
    /// which is how the section scanners reject candidate offsets.
    /// </summary>
    public static bool TryReadAsciiFString(byte[] d, ref int pos, out string value)
    {
        value = "";
        if (pos < 0 || pos + 4 > d.Length) return false;

        int len = ReadInt32(d, pos);
        if (len <= 1 || len > 256 || pos + 4 + len > d.Length) return false;
        if (d[pos + 4 + len - 1] != 0) return false;

        for (int i = 0; i < len - 1; i++)
        {
            byte b = d[pos + 4 + i];
            if (b < 32 || b > 126) return false;
        }

        value = Encoding.ASCII.GetString(d, pos + 4, len - 1);
        pos += 4 + len;
        return true;
    }

    /// <summary>
    /// A name of the shape the player save gives its events, stages and cars:
    /// 3 to 64 characters, starting with a letter, letters digits and
    /// underscores only. What tells a real list from a chance byte pattern.
    /// </summary>
    public static bool IsIdentifier(string value)
    {
        if (value.Length < 3 || value.Length > 64 || !char.IsLetter(value[0])) return false;
        foreach (char c in value)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>Reads an FString constrained to an event / stage / car identifier.</summary>
    public static bool TryReadIdentifier(byte[] d, ref int pos, out string value)
    {
        int start = pos;
        if (!TryReadAsciiFString(d, ref pos, out value)) return false;

        if (!IsIdentifier(value))
        {
            pos = start;
            value = "";
            return false;
        }

        return true;
    }
}
