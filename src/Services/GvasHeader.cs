using System.Text;
using System.Text.Json.Nodes;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// The GVAS header both save files open with (magic, engine version, the custom
/// version table, the save game class name). It is the only part of the two
/// files that shares a layout, and the only place a build marker appears.
/// </summary>
public static class GvasHeader
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The version fields of the GVAS header. The engine version and the size of
    /// the custom-version table are the only build markers a save carries — the
    /// game's own version number appears nowhere in it.
    /// </summary>
    public record GvasInfo(string EngineVersion, int Ue5Version, int CustomVersionCount, string SaveGameClass);

    /// <summary>True when <paramref name="data"/> opens with the GVAS magic — an Unreal save at all.</summary>
    public static bool IsGvas(byte[] data) =>
        data.Length >= 4 && data[0] == 'G' && data[1] == 'V' && data[2] == 'A' && data[3] == 'S';

    /// <summary>Reads the header version fields alone, for display.</summary>
    public static GvasInfo ReadInfo(byte[] data)
    {
        int pos = 0;
        ParseHeader(data, ref pos, out var info);
        return info;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The header as JSON, leaving <paramref name="pos"/> just past it. The JSON
    /// export starts here, and everything after it is save-specific.
    /// </summary>
    public static JsonObject ParseHeader(byte[] d, ref int pos, out GvasInfo info)
    {
        var h = new JsonObject();

        // Magic
        string magic = Encoding.ASCII.GetString(d, pos, 4); pos += 4;
        h["magic"] = magic;
        if (magic != "GVAS")
            throw new InvalidOperationException($"Expected GVAS magic, got '{magic}'");

        // Versions
        int sgfv = ReadInt32(d, pos); pos += 4;
        h["save_game_file_version"] = sgfv;
        h["file_version_ue4"] = ReadInt32(d, pos); pos += 4;
        if (sgfv >= 3) { h["file_version_ue5"] = ReadInt32(d, pos); pos += 4; }

        // FEngineVersion: uint16 Major, Minor, Patch + uint32 Changelist + FString Branch
        ushort major = BitConverter.ToUInt16(d, pos); pos += 2;
        ushort minor = BitConverter.ToUInt16(d, pos); pos += 2;
        ushort patch = BitConverter.ToUInt16(d, pos); pos += 2;
        uint cl = BitConverter.ToUInt32(d, pos); pos += 4;
        string? branch = ReadFString(d, ref pos);
        h["engine_version"] = $"{major}.{minor}.{patch} CL={cl} Branch={branch}";
        string engine = $"{major}.{minor}.{patch}";

        // Custom versions
        h["custom_version_format"] = ReadInt32(d, pos); pos += 4;
        int cvCount = ReadInt32(d, pos); pos += 4;
        h["custom_version_count"] = cvCount;

        var cvArr = new JsonArray();
        for (int i = 0; i < cvCount; i++)
        {
            string guid = Convert.ToHexString(d, pos, 16); pos += 16;
            int ver = ReadInt32(d, pos); pos += 4;
            cvArr.Add(new JsonObject { ["guid"] = guid, ["version"] = ver });
        }
        h["custom_versions"] = cvArr;

        // SaveGameClassName
        string saveClass = ReadFString(d, ref pos) ?? "";
        h["save_game_class"] = saveClass;

        info = new GvasInfo(engine, sgfv >= 3 ? (int)h["file_version_ue5"]! : 0, cvCount, saveClass);
        return h;
    }

    /// <summary>
    /// Reads the FString at <paramref name="pos"/> and moves past it, or returns
    /// null when its length is not one this file could hold.
    /// </summary>
    private static string? ReadFString(byte[] d, ref int pos)
    {
        if (pos + 4 > d.Length) return null;
        int len = ReadInt32(d, pos); pos += 4;
        if (len <= 0 || len > 4096 || pos + len > d.Length) return null;
        string s = Encoding.UTF8.GetString(d, pos, len - 1);
        pos += len;
        return s;
    }
}
