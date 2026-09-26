using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ACRSaveManager.Services;

/// <summary>Provenance of an exported .race file.</summary>
public record RaceFileMeta(
    string Engine,          // UE version of the save it came from, e.g. "5.6.1"
    int CustomVersions,     // size of the GVAS custom-version table (109 in 0.4, 117 in 0.6)
    string Exported,        // ISO-8601 UTC
    string Tool             // ACR Save Manager version that wrote the file
);

/// <summary>
/// Container of a .race file: the event config list and the selection past it
/// (<see cref="EventList"/>), as they sit in the save.
///
/// Those bytes record no version of any kind — unlike a setup entry, which
/// carries the game build that wrote it — so writing them into a save from
/// another release is otherwise a blind operation. They go behind a small
/// header that states where they came from:
///
///   magic      6 B   "ACRSM\0"
///   container  2 B   uint16 format version of this header
///   meta_len   4 B   int32 length of the JSON metadata
///   crc32      4 B   uint32 checksum of the content alone
///   meta       var   UTF-8 JSON (<see cref="RaceFileMeta"/>)
///   content    var   the event list and its selection, untouched
///
/// Containers 1 and 2 held one event block (.weekend, .timeattack, .practice);
/// they are refused with a message that says so rather than read.
/// </summary>
public static class RaceFile
{
    public const ushort ContainerVersion = 3;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACRSM\0");
    private const int HeaderSize = 16;              // magic + container + meta_len + crc32
    private const int MaxMetaLen = 64 * 1024;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    /// <summary>Builds the file bytes for <paramref name="content"/>.</summary>
    public static byte[] Wrap(byte[] content, RaceFileMeta meta)
    {
        byte[] metaBytes = JsonSerializer.SerializeToUtf8Bytes(meta, Opts);
        byte[] file = new byte[HeaderSize + metaBytes.Length + content.Length];

        Magic.CopyTo(file, 0);
        BitConverter.GetBytes(ContainerVersion).CopyTo(file, 6);
        BitConverter.GetBytes(metaBytes.Length).CopyTo(file, 8);
        BitConverter.GetBytes(Crc32(content)).CopyTo(file, 12);
        metaBytes.CopyTo(file, HeaderSize);
        content.CopyTo(file, HeaderSize + metaBytes.Length);

        return file;
    }

    /// <summary>
    /// Splits a .race file into its content and its metadata — null when the
    /// metadata cannot be read, which is no reason to reject the content.
    /// Throws <see cref="InvalidDataException"/> for anything but an intact
    /// container of this version.
    /// </summary>
    public static (byte[] Content, RaceFileMeta? Meta) Read(byte[] file)
    {
        if (file.Length < HeaderSize || !Bytes.StartsWith(file, Magic))
            throw new InvalidDataException("This is not a file exported by ACR Save Manager.");

        ushort container = BitConverter.ToUInt16(file, 6);
        if (container > ContainerVersion)
        {
            throw new InvalidDataException(
                $"This file uses container format {container}; this build reads " +
                $"up to {ContainerVersion}. Update ACR Save Manager.");
        }
        if (container < ContainerVersion)
        {
            throw new InvalidDataException(
                "This file holds a single event block, exported by an older ACR Save " +
                "Manager (.weekend, .timeattack or .practice). Only .race files are read now.");
        }

        int metaLen = BitConverter.ToInt32(file, 8);
        if (metaLen < 0 || metaLen > MaxMetaLen || HeaderSize + metaLen > file.Length)
            throw new InvalidDataException($"Corrupt container header: metadata length {metaLen}.");

        uint storedCrc = BitConverter.ToUInt32(file, 12);
        byte[] content = file[(HeaderSize + metaLen)..];
        if (Crc32(content) != storedCrc)
        {
            throw new InvalidDataException(
                "The exported content does not match its checksum — the file is damaged.");
        }

        return (content, ParseMeta(file.AsSpan(HeaderSize, metaLen)));
    }

    private static RaceFileMeta? ParseMeta(ReadOnlySpan<byte> json)
    {
        try
        {
            if (JsonNode.Parse(json.ToArray()) is not JsonObject o) return null;

            string Str(string key) => o[key]?.GetValue<string>() ?? "";

            return new RaceFileMeta(
                Str("Engine"),
                o["CustomVersions"]?.GetValue<int>() ?? 0,
                Str("Exported"),
                Str("Tool"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    // ── CRC-32 (IEEE 802.3, the zip/png polynomial) ───────────────────────────

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
