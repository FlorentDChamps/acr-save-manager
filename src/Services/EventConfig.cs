using System.Text;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// The EventConfig structure of PlayerDataSaveSlot.sav, walked in one place
/// (docs/PlayerDataSaveSlot_structure.txt §3.3):
///
///   event_name  FString
///   n_components int32
///   [component]: class_path FString, nb_uuids int32, nb_uuids × 16 B,
///                (nb_uuids + 1) × int32, comp_name FString,
///                data_size int32, data_size bytes
///
/// Its callers read it for different reasons — one probes every offset of the
/// file looking for the list, the others know where a config starts and want
/// its components — so the walk reports a failure instead of throwing, and
/// says why. One set of plausibility bounds serves them all.
/// </summary>
internal static class EventConfig
{
    // Observed values: n_components is 1 in every block of both saves; nb_uuids
    // is 1 or 2; data_size runs from 72 B to the rally weekend's 1 480 B. The
    // bounds are the room left for future releases, not measurements.
    public const int MaxComponents = 8;
    public const int MaxUuids = 8;
    public const int MaxDataSize = 500_000;
    public const int MaxNameLength = 2000;

    /// <summary>One component of an event config, and where its payload sits.</summary>
    public readonly record struct Component(
        string ClassPath,
        string[] Uuids,     // hex, as stored
        string Name,
        int DataStart,
        int DataSize
    );

    /// <summary>
    /// Walk the event config at <paramref name="pos"/>, leaving
    /// <paramref name="pos"/> just past it. False — with a reason — when a field
    /// is out of bounds or runs off the buffer; <paramref name="pos"/> is then
    /// meaningless.
    /// </summary>
    public static bool TryRead(byte[] data, ref int pos, out string eventName,
                               out List<Component> components, out string reason)
    {
        eventName = "";
        components = [];
        reason = "";

        if (!TryReadFString(data, ref pos, out eventName, out reason)) return false;

        if (pos + 4 > data.Length) { reason = "n_components runs past the end of the file."; return false; }
        int nComp = ReadInt32(data, pos); pos += 4;
        if (nComp < 1 || nComp > MaxComponents)
        {
            reason = $"Unexpected n_components={nComp} at offset 0x{pos - 4:X4}.";
            return false;
        }

        for (int ci = 0; ci < nComp; ci++)
        {
            if (!TryReadFString(data, ref pos, out string classPath, out reason)) return false;

            if (pos + 4 > data.Length) { reason = "nb_uuids runs past the end of the file."; return false; }
            int nbUuids = ReadInt32(data, pos); pos += 4;
            if (nbUuids < 0 || nbUuids > MaxUuids)
            {
                reason = $"Unexpected nb_uuids={nbUuids} at offset 0x{pos - 4:X4}.";
                return false;
            }

            // nb_uuids × 16-byte UUID, then (nb_uuids + 1) extra int32s
            if (pos + nbUuids * 16 + (nbUuids + 1) * 4 > data.Length)
            {
                reason = "The component's UUIDs run past the end of the file.";
                return false;
            }

            string[] uuids = new string[nbUuids];
            for (int g = 0; g < nbUuids; g++)
            {
                uuids[g] = Convert.ToHexString(data, pos, 16);
                pos += 16;
            }
            pos += (nbUuids + 1) * 4;

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
