using System.IO;
using System.Text.Json.Nodes;
using static ACRSaveManager.Services.Bytes;

namespace ACRSaveManager.Services;

/// <summary>
/// Exports PlayerDataSaveSlot.sav as a structured JSON document — the whole of
/// what this tool can read of that file, for inspection and for diffing one
/// game release against the next.
///
/// File layout:
///   GVAS header (magic, versions, custom versions, save-game class)
///   1-byte prefix + FString "None"  → empty property list
///   Custom binary blob → event configs and the other blocks of the save
///
/// The blocks are located by structural validation, not by event name: each
/// game release adds and renames events (0.6 introduced
/// DefaultOnlineSingleStage).
/// </summary>
public static class PlayerSaveJson
{
    /// <summary>Fixed-size trailer closing each inner record.</summary>
    private const int CarTrailerSize = 12;

    /// <summary>The whole document, for the save named <paramref name="filePath"/>.</summary>
    public static JsonObject Export(byte[] data, string filePath)
    {
        var doc = new JsonObject
        {
            ["file_info"] = new JsonObject
            {
                // the name alone: the full path carries the Windows account name,
                // and this document is meant to be shared
                ["file_name"] = Path.GetFileName(filePath),
                ["size_bytes"] = data.Length,
                ["parsed_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            },
        };

        try
        {
            int pos = 0;
            doc["gvas_header"] = GvasHeader.ParseHeader(data, ref pos, out _);

            // Skip 1-byte prefix + "None" FString (empty property list)
            pos++;                              // 0x00 prefix byte
            int noneLen = ReadInt32(data, pos); pos += 4 + noneLen;

            doc["custom_data_offset"] = $"0x{pos:X4}";
        }
        catch (Exception ex)
        {
            doc["_header_error"] = ex.Message;
        }

        // Parse known sections independently (failures don't abort others)
        doc["event_configs"] = SafeParse(() => ScanEventConfigs(data), "event_configs");
        doc["event_leaderboards"] = SafeParse(() => ScanEventLeaderboards(data), "event_leaderboards");
        doc["weather_settings"] = SafeParse(() => ScanWeatherSettings(data), "weather_settings");

        return doc;
    }

    // ── Event Configs (settings blocks) ───────────────────────────────────────
    //
    // Structure:
    //   n_events int32
    //   [event]:
    //     event_name FString
    //     n_components int32
    //     [component]:
    //       class_path FString  ("ERaceEventSerializableContext::RaceEventSettings")
    //       nb_uuids int32
    //       nb_uuids × 16-byte GUID
    //       (nb_uuids+1) × int32  (extra ints)
    //       comp_name FString
    //       data_size int32
    //       data_size bytes (opaque component data)
    //
    // EventList finds the list; each config is walked again here for the
    // detail the document shows.

    private static JsonArray ScanEventConfigs(byte[] d)
    {
        var arr = new JsonArray();
        foreach (var e in EventList.Find(d).Events)
        {
            int pos = e.Start;
            EventConfig.TryRead(d, ref pos, out _, out var components, out _);

            var comps = new JsonArray();
            foreach (var c in components)
            {
                var guids = new JsonArray();
                foreach (string uuid in c.Uuids) guids.Add(uuid);

                comps.Add(new JsonObject
                {
                    ["class_path"] = c.ClassPath,
                    ["guids"] = guids,
                    ["component_name"] = c.Name,
                    ["data_size"] = c.DataSize,
                    ["data_hex"] = Convert.ToHexString(d, c.DataStart, c.DataSize),
                });
            }

            arr.Add(new JsonObject
            {
                ["event_name"] = e.Name,
                ["offset"] = $"0x{e.Start:X4}",
                ["components"] = comps,
            });
        }

        return arr;
    }

    // ── Event Leaderboards ────────────────────────────────────────────────────

    private static JsonArray ScanEventLeaderboards(byte[] d)
    {
        var arr = new JsonArray();

        int pos = 0;
        while (pos + 8 <= d.Length)
        {
            if (TryReadLeaderboard(d, pos, out var block, out int blockEnd))
            {
                arr.Add(block!);
                pos = blockEnd;
            }
            else
            {
                pos++;
            }
        }

        return arr;
    }

    private static bool TryReadLeaderboard(byte[] d, int pos, out JsonObject? result, out int end)
    {
        result = null;
        end = pos;

        int blockStart = pos;

        if (!TryReadIdentifier(d, ref pos, out string evtName)) return false;
        if (pos + 4 > d.Length) return false;

        int nEntries = ReadInt32(d, pos); pos += 4;
        if (nEntries < 1 || nEntries > 5000) return false;

        var stages = new JsonArray();
        int carCount = 0;

        for (int e = 0; e < nEntries; e++)
        {
            if (!TryReadIdentifier(d, ref pos, out string stageName)) return false;
            if (pos + 4 > d.Length) return false;

            int nCars = ReadInt32(d, pos); pos += 4;
            if (nCars < 0 || nCars > 200) return false;

            var cars = new JsonArray();

            for (int c = 0; c < nCars; c++)
            {
                if (pos + 4 > d.Length) return false;
                int rank = ReadInt32(d, pos); pos += 4;
                if (rank < 0 || rank > 500) return false;

                if (!TryReadIdentifier(d, ref pos, out string carName)) return false;
                if (pos + 4 > d.Length) return false;

                int nCp = ReadInt32(d, pos); pos += 4;
                if (nCp < 0 || nCp > 50) return false;
                if (pos + nCp * 12 + CarTrailerSize > d.Length) return false;

                var cps = new JsonArray();
                for (int cp = 0; cp < nCp; cp++)
                {
                    int cpIndex = ReadInt32(d, pos);
                    float cum = BitConverter.ToSingle(d, pos + 4);
                    float sec = BitConverter.ToSingle(d, pos + 8);
                    pos += 12;

                    // Checkpoint 0 is the start line: always 0/0.
                    if (cpIndex != 0)
                    {
                        cps.Add(new JsonObject
                        {
                            ["checkpoint"] = cpIndex,
                            ["cumulative_s"] = Math.Round(cum, 3),
                            ["sector_s"] = Math.Round(sec, 3),
                        });
                    }
                }

                float penalty = BitConverter.ToSingle(d, pos);
                pos += CarTrailerSize;

                cars.Add(new JsonObject
                {
                    ["rank"] = rank,
                    ["car"] = carName,
                    ["penalty_s"] = Math.Round(penalty, 3),
                    ["checkpoints"] = cps,
                });
                carCount++;
            }

            if (pos + 8 > d.Length) return false;
            long ticks = BitConverter.ToInt64(d, pos); pos += 8;

            var stageNode = new JsonObject
            {
                ["stage"] = stageName,
                ["cars"] = cars,
            };
            if (ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                stageNode["timestamp"] = new DateTime(ticks, DateTimeKind.Utc).ToString("o");

            stages.Add(stageNode);
        }

        // A block without a single recorded run is indistinguishable from noise.
        if (carCount == 0) return false;

        result = new JsonObject
        {
            ["event_name"] = evtName,
            ["offset"] = $"0x{blockStart:X4}",
            ["entry_count"] = nEntries,
            ["stages"] = stages,
        };
        end = pos;
        return true;
    }

    // ── Weather Settings ──────────────────────────────────────────────────────
    //
    // A section of its own, sitting just past the end of the event config list
    // and belonging to no block (docs §3.4):
    //   stage FString + car FString
    //   n_options int32 (= 3)
    //   [option]: path FString + value FString
    //
    // EventList reads it with the list, as the selection the single-stage modes
    // share; the document keeps the name it has always had for it.

    private static JsonNode ScanWeatherSettings(byte[] d)
    {
        if (EventList.Find(d).Selection is not { } selection)
            return JsonValue.Create("No stage, car and options past the event config list");

        var options = new JsonArray();
        foreach (var (path, value) in selection.Options)
        {
            options.Add(new JsonObject
            {
                ["path"] = path,
                ["value"] = value,
            });
        }

        return new JsonObject
        {
            ["stage"] = selection.Stage,
            ["car"] = selection.Car,
            ["weather_option_count"] = selection.Options.Count,
            ["options"] = options,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonNode SafeParse(Func<JsonNode> fn, string sectionName)
    {
        try { return fn(); }
        catch (Exception ex)
        {
            return new JsonObject { ["_error"] = $"[{sectionName}] {ex.Message}" };
        }
    }
}
