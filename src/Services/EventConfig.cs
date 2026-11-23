using System.Text;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// The EventConfig structure of PlayerDataSaveSlot.sav, walked in one place
/// (docs/PlayerDataSaveSlot_structure.txt §3.3):
///
///   event_name  FString
///   n_contexts  int32
///   [context]:  class_path FString, nb_versions int32,
///               nb_versions × (UUID 16 B, version int32),
///               n_components int32,
///               [component]: comp_name FString, data_size int32, data_size bytes
///
/// Every config holds a RaceEventSettings context of one component; the rally
/// weekend's adds a RaceEventState context of four while a rally is in
/// progress.
///
/// Its callers read it for different reasons — one probes every offset of the
/// file looking for the list, the others know where a config starts and want
/// its components — so the walk reports a failure instead of throwing, and
/// says why. One set of plausibility bounds serves them all.
/// </summary>
internal static class EventConfig
{
    // Observed values: n_contexts is 1, or 2 with a rally in progress;
    // nb_versions is 0, 1 or 2; n_components is 1 in a settings context and 4
    // in the rally state; data_size runs from 72 B to the rally weekend's
    // 1 480 B. The bounds are the room left for future releases, not
    // measurements.
    public const int MaxContexts = 8;
    public const int MaxVersions = 8;
    public const int MaxComponents = 8;
    public const int MaxDataSize = 500_000;
    public const int MaxNameLength = 2000;

    /// <summary>The class path every context of the game starts with.</summary>
    public const string ContextPrefix = "ERaceEventSerializableContext::";

    /// <summary>The context the game adds to DefaultRally while a rally weekend is in progress.</summary>
    public const string RallyState = ContextPrefix + "RaceEventState";

    /// <summary>
    /// One component of an event config, the context it belongs to, and where
    /// its payload sits.
    /// </summary>
    public readonly record struct Component(
        string ClassPath,   // its context's
        string[] Uuids,     // its context's version UUIDs, hex, as stored
        string Name,
        int DataStart,
        int DataSize
    );

    /// <summary>
    /// Walk the event config at <paramref name="pos"/>, leaving
    /// <paramref name="pos"/> just past it. The components of every context
    /// come in file order. False — with a reason — when a field is out of
    /// bounds or runs off the buffer; <paramref name="pos"/> is then
    /// meaningless.
    /// </summary>
    public static bool TryRead(byte[] data, ref int pos, out string eventName,
                               out List<Component> components, out string reason)
    {
        eventName = "";
        components = [];
        reason = "";

        if (!TryReadFString(data, ref pos, out eventName, out reason)) return false;

        if (!TryReadCount(data, ref pos, "n_contexts", 1, MaxContexts, out int nContexts, out reason)) return false;

        for (int ci = 0; ci < nContexts; ci++)
        {
            if (!TryReadFString(data, ref pos, out string classPath, out reason)) return false;

            if (!TryReadCount(data, ref pos, "nb_versions", 0, MaxVersions, out int nbVersions, out reason)) return false;

            // nb_versions × (16-byte UUID, int32 version)
            if (pos + nbVersions * 20 > data.Length)
            {
                reason = "The context's versions run past the end of the file.";
                return false;
            }

            string[] uuids = new string[nbVersions];
            for (int v = 0; v < nbVersions; v++)
            {
                uuids[v] = Convert.ToHexString(data, pos, 16);
                pos += 20;
            }

            if (!TryReadCount(data, ref pos, "n_components", 1, MaxComponents, out int nComp, out reason)) return false;

            for (int k = 0; k < nComp; k++)
            {
                if (!TryReadFString(data, ref pos, out string compName, out reason)) return false;

                if (pos + 4 > data.Length) { reason = "data_size runs past the end of the file."; return false; }
                int dataSize = ReadInt32(data, pos); pos += 4;
                if (dataSize < 0 || dataSize > MaxDataSize || pos + dataSize > data.Length)
                {
                    reason = $"Unexpected data_size={dataSize} at offset 0x{pos - 4:X4}.";
                    return false;
                }

                components.Add(new Component(classPath, uuids, compName, pos, dataSize));
                pos += dataSize;
            }
        }

        return true;
    }

    /// <summary>An int32 counter within [<paramref name="min"/>, <paramref name="max"/>].</summary>
    private static bool TryReadCount(byte[] data, ref int pos, string field, int min, int max,
                                     out int value, out string reason)
    {
        value = 0;
        if (pos + 4 > data.Length) { reason = $"{field} runs past the end of the file."; return false; }
        value = ReadInt32(data, pos); pos += 4;
        if (value < min || value > max)
        {
            reason = $"Unexpected {field}={value} at offset 0x{pos - 4:X4}.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// An FString whose length is plausible for a name in this structure, read
    /// without decoding anything past the buffer.
    /// </summary>
    private static bool TryReadFString(byte[] data, ref int pos, out string value, out string reason)
    {
        value = "";

        if (pos + 4 > data.Length) { reason = "An FString length runs past the end of the file."; return false; }
        int len = ReadInt32(data, pos);
        if (len <= 0 || len > MaxNameLength || pos + 4 + len > data.Length)
        {
            reason = $"Invalid FString length {len} at offset 0x{pos:X4}.";
            return false;
        }

        value = Encoding.UTF8.GetString(data, pos + 4, len - 1);   // without the \0
        pos += 4 + len;
        reason = "";
        return true;
    }
}
