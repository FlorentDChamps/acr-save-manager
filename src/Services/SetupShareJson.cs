using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ACRSaveManager.Services;

/// <summary>One setup of a <see cref="SetupShareFile"/>, in the order it is written.</summary>
public class SetupShareSetup
{
    public string CarName { get; init; } = "";
    public string FriendlyCarName { get; init; } = "";
    public string Surface { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public IReadOnlyList<string> Surfaces { get; init; } = [];
    public string UserDefinedName { get; init; } = "";
    public string StageName { get; init; } = "";
    public string PlayerName { get; init; } = "";
    public string FriendlySetupName { get; init; } = "";
    public string TemplateDataBase64 { get; init; } = "";
    public string SetupHash { get; init; } = "";
    public Dictionary<string, string> Properties { get; init; } = [];
    public string UniqueKey { get; init; } = "";
    public string FriendlySurface { get; init; } = "";
    public string SurfaceEmoji { get; init; } = "";
    public string FriendlyStage { get; init; } = "";

    // Not part of the format: what the entry also says, kept readable. They come
    // last so that everything above reads as that tool wrote it.
    public IReadOnlyList<string> Weathers { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTime? SavedAt { get; init; }   // the player's own clock, no time zone
}

/// <summary>The JSON document written next to an exported .setup.</summary>
public class SetupShareFile
{
    public string ExportVersion { get; init; } = "";
    public string ExportedBy { get; init; } = "";
    public string ExportedAt { get; init; } = "";
    public string SourceGame { get; init; } = "";
    public IReadOnlyList<SetupShareSetup> Setups { get; init; } = [];
}

/// <summary>
/// One setup to import: its entry, as a .setup file holds it, and the int32 that
/// goes in front of it in the save (§4.2) — null for a .setup file, which does
/// not hold it.
/// </summary>
public readonly record struct SharedSetup(int? Prefix, byte[] Entry);

/// <summary>
/// The JSON companion of an exported .setup, in the setup-sharing format of
/// "AC Rally car setup manager" by Sheldor
/// (https://www.overtake.gg/downloads/ac-rally-car-setup-manager-by-sheldor.85944/),
/// so that a file written by either tool can be read by the other.
///
/// That tool publishes no description of its format: this one is read off
/// files it wrote, all of them setups kept for no surface in particular.
///
///   TemplateDataBase64   the int32 the save keeps in front of the entry
///                        (§4.2), then the entry itself — a .setup file
///   Properties           the entry's parameters, in the entry's order
///   SetupHash            SHA-256 of Properties as compact JSON, keys sorted
///   UniqueKey            CarName|Surface|StageName
///   Friendly…            readable names for the ids
///
/// PlayerName and FriendlySetupName were empty in every one of them and stay
/// so. What Surface, FriendlySurface and SurfaceEmoji become for a setup that
/// does name surfaces was not seen: the first of them is a guess, and the
/// emoji is the only one met. The friendly names come from a list of that
/// tool's own, which falls back on the bare id for a car it does not know;
/// here the id is spaced out at its capitals, which gives the same name in
/// most of the files but not all.
///
/// Only TemplateDataBase64 is read back. The rest is there to be looked at.
/// </summary>
public static class SetupShareJson
{
    private const string ExportVersion = "1.0";
    private const string SourceGame = "AC Rally";
    private const string SurfaceEmoji = "\U0001F3CE";   // racing car

    // One instance: System.Text.Json caches its reflection behind it, and a fresh
    // one on every export throws that away (CA1869). The default encoder is kept
    // on purpose: it writes "+" and the emoji as \u escapes, as that tool does.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The document for one setup: <paramref name="entry"/> as a .setup file
    /// holds it, and <paramref name="prefix"/>, the int32 in front of it in the
    /// save. It is signed with the in-game name the entry carries, never with
    /// the Windows account.
    /// </summary>
    public static SetupShareFile Build(int prefix, byte[] entry, DateTimeOffset exportedAt)
    {
        var data = CarSetupsParser.ReadEntryData(entry);
        string surface = data.Surfaces.Count > 0 ? data.Surfaces[0] : "";

        byte[] template = new byte[4 + entry.Length];
        BitConverter.GetBytes(prefix).CopyTo(template, 0);
        entry.CopyTo(template, 4);

        return new SetupShareFile
        {
            ExportVersion = ExportVersion,
            ExportedBy = data.Author,
            ExportedAt = exportedAt.ToString("O", CultureInfo.InvariantCulture),
            SourceGame = SourceGame,
            Setups =
            [
                new SetupShareSetup
                {
                    CarName = data.Car,
                    FriendlyCarName = FriendlyName(data.Car),
                    Surface = surface,
                    GameVersion = data.GameVersion,
                    Surfaces = data.Surfaces,
                    UserDefinedName = data.Name,
                    StageName = data.Track,
                    TemplateDataBase64 = Convert.ToBase64String(template),
                    SetupHash = Hash(data.Parameters),
                    Properties = data.Parameters,
                    UniqueKey = $"{data.Car}|{surface}|{data.Track}",
                    FriendlySurface = FriendlyName(surface),
                    SurfaceEmoji = SurfaceEmoji,
                    FriendlyStage = FriendlyName(data.Track),
                    Weathers = data.Weathers,
                    Tags = data.Tags,
                    SavedAt = data.Timestamp == DateTime.MinValue ? null : data.Timestamp,
                },
            ],
        };
    }

    /// <summary>The document as the text of the .json file.</summary>
    public static string Serialize(SetupShareFile file) =>
        JsonSerializer.Serialize(file, Indented);

    /// <summary>
    /// SHA-256, in lowercase hex, of <paramref name="properties"/> written as
    /// compact JSON with its keys sorted: the same parameters give the same hash
    /// whatever the setup is called and whoever saved it.
    /// </summary>
    public static string Hash(IDictionary<string, string> properties)
    {
        var sorted = new SortedDictionary<string, string>(properties, StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(sorted)));
    }

    /// <summary>
    /// An id spaced out into words: "MonteCarloS2Sisteron" becomes
    /// "Monte Carlo S2 Sisteron". A capital opens a word after a lowercase
    /// letter or a digit, and so does the last capital of a run of them
    /// ("XsaraWRCEvo" reads "Xsara WRC Evo").
    /// </summary>
    public static string FriendlyName(string id)
    {
        var sb = new StringBuilder(id.Length + 8);
        for (int i = 0; i < id.Length; i++)
        {
            if (i > 0 && char.IsAsciiLetterUpper(id[i]))
            {
                char prev = id[i - 1];
                bool afterWord = char.IsAsciiLetterLower(prev) || char.IsAsciiDigit(prev);
                bool endsAcronym = char.IsAsciiLetterUpper(prev) &&
                                   i + 1 < id.Length && char.IsAsciiLetterLower(id[i + 1]);
                if (afterWord || endsAcronym) sb.Append(' ');
            }
            sb.Append(id[i]);
        }
        return sb.ToString();
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// True when <paramref name="file"/> opens like a JSON document rather than
    /// like a setup entry, whatever its extension says.
    /// </summary>
    public static bool IsJson(byte[] file)
    {
        int i = Bytes.StartsWith(file, Utf8Bom) ? Utf8Bom.Length : 0;
        while (i < file.Length && file[i] is 0x20 or 0x09 or 0x0D or 0x0A) i++;   // JSON white space
        return i < file.Length && file[i] == (byte)'{';
    }

    /// <summary>
    /// The setups of a file in this format, each one checked as a .setup file
    /// would be. Throws <see cref="InvalidDataException"/> saying what is wrong
    /// with the file otherwise: one setup that cannot be read refuses them all.
    ///
    /// The format lists its setups, so a file may hold several. None of the
    /// files seen did: that they are simply one after the other is what the
    /// list suggests, not something observed.
    ///
    /// The entry is the setup; Properties only repeats its parameters for the
    /// reader. A file where the two disagree was edited after it was exported,
    /// by someone expecting the change to reach the game: it is refused rather
    /// than imported without it.
    /// </summary>
    public static IReadOnlyList<SharedSetup> Read(byte[] file)
    {
        int bom = Bytes.StartsWith(file, Utf8Bom) ? Utf8Bom.Length : 0;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(file.AsMemory(bom)); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"This file is not readable JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("Setups", out var setups) ||
                setups.ValueKind != JsonValueKind.Array || setups.GetArrayLength() == 0)
            {
                throw new InvalidDataException(NoSetups(doc.RootElement));
            }

            var result = new List<SharedSetup>();
            foreach (var setup in setups.EnumerateArray())
            {
                byte[] template = ReadTemplate(setup);
                byte[] entry = template[4..];
                CarSetupsParser.ValidateEntry(entry);
                CheckProperties(setup, entry);
                result.Add(new SharedSetup(BitConverter.ToInt32(template, 0), entry));
            }
            return result;
        }
    }

    /// <summary>
    /// Why a document without a "Setups" list cannot be imported — naming the
    /// two other JSON files of this tool, which a player may pick by mistake.
    /// </summary>
    private static string NoSetups(JsonElement root)
    {
        const string none = "This JSON file holds no setup to import: it has no \"Setups\" list.";
        if (root.ValueKind != JsonValueKind.Object) return none;

        if (root.TryGetProperty("Parameters", out _) && root.TryGetProperty("Car", out _))
        {
            return none + " A .json written by ACR Save Manager 1.0.0 only describes " +
                   "the .setup exported next to it: import that one.";
        }

        if (root.TryGetProperty("gvas_header", out _))
        {
            return none + " It is the readable export of a whole player save, which " +
                   "holds no setup: setups live in CarSetupsDataSaveSlot.sav.";
        }

        return none;
    }

    private static byte[] ReadTemplate(JsonElement setup)
    {
        if (setup.ValueKind != JsonValueKind.Object ||
            !setup.TryGetProperty("TemplateDataBase64", out var b64) ||
            b64.ValueKind != JsonValueKind.String ||
            !b64.TryGetBytesFromBase64(out byte[]? template) || template.Length < 4)
        {
            throw new InvalidDataException(
                "A setup of this JSON file does not embed its entry (TemplateDataBase64), " +
                "so there is nothing to import.");
        }
        return template;
    }

    private static void CheckProperties(JsonElement setup, byte[] entry)
    {
        if (!setup.TryGetProperty("Properties", out var listed) ||
            listed.ValueKind != JsonValueKind.Object)
        {
            return;   // nothing listed, nothing to disagree with
        }

        var embedded = CarSetupsParser.ReadEntryData(entry).Parameters;
        int count = 0;
        foreach (var p in listed.EnumerateObject())
        {
            count++;
            if (p.Value.ValueKind != JsonValueKind.String ||
                !embedded.TryGetValue(p.Name, out string? value) || value != p.Value.GetString())
            {
                throw Edited($"\"{p.Name}\"");
            }
        }
        if (count != embedded.Count) throw Edited($"{count} listed, {embedded.Count} embedded");
    }

    private static InvalidDataException Edited(string where) => new(
        $"The parameters this JSON file lists do not match the setup it embeds ({where}): " +
        "it was edited after it was exported. Only the embedded setup reaches the game, " +
        "so the file is refused rather than imported without the change.");
}
