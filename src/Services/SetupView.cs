using System.Globalization;
using System.Text.RegularExpressions;

namespace ACRSaveManager.Services;

/// <summary>How a row holds its values.</summary>
public enum SetupViewKind
{
    /// <summary>One value per setup.</summary>
    Value,
    /// <summary>A value per wheel: front left, front right, rear left, rear right.</summary>
    Corners,
    /// <summary>A value per axle: front, rear.</summary>
    Axles,
}

/// <summary>One wheel's or one axle's value, and whether the setups differ on it.</summary>
public sealed record SetupViewPart(string Text, bool Differs);

/// <summary>
/// What a row holds for one setup: <see cref="Text"/>, in one line, and for
/// a row of wheels or axles the <see cref="Parts"/> it is made of, in order.
/// </summary>
public sealed record SetupViewCell(string Text, IReadOnlyList<SetupViewPart> Parts)
{
    /// <summary>
    /// In a row of wheels, the front wheels hold the same value in every setup
    /// shown: the first part stands for both, and the second is not shown.
    /// </summary>
    public bool FrontPaired { get; init; }

    /// <summary>The same for the rear wheels, the third part and the fourth.</summary>
    public bool RearPaired { get; init; }
}

/// <summary>
/// One line of the setup viewer: a field or a parameter, and its value in
/// each setup shown. <see cref="Differs"/> when those values are not all the
/// same — never for a single setup.
/// </summary>
public sealed record SetupViewRow(string Section, string Label, IReadOnlyList<SetupViewCell> Cells, bool Differs)
{
    /// <summary>The section of the fields every setup has, above its parameters.</summary>
    public const string SetupSection = "Info";

    public SetupViewKind Kind { get; init; } = SetupViewKind.Value;

    /// <summary>Where in its section the row sits ("Centre" for a differential), or "".</summary>
    public string Subsection { get; init; } = "";

    /// <summary>Each setup's value, in one line.</summary>
    public IReadOnlyList<string> Values => [.. Cells.Select(c => c.Text)];

    /// <summary>The setup's own name, kept even when only differences are shown.</summary>
    public bool IsName => Section == SetupSection && Label == "Name";

    /// <summary>The whole row stands out; a row of parts marks the parts that differ instead.</summary>
    public bool HighlightsRow => Differs && Kind == SetupViewKind.Value;
}

/// <summary>
/// The lines of the setup viewer, for one setup or several of a car side by
/// side: what each setup is for, then its parameters, grouped by the first
/// part of their key and spelled out for reading.
/// </summary>
public static partial class SetupView
{
    /// <summary>The value of a parameter one setup lacks and another has.</summary>
    public const string Missing = "—";

    /// <summary>
    /// The sections in the order they are shown, by the first part of the keys.
    /// One not listed comes after those listed, ahead of Other.
    /// </summary>
    private static readonly string[] SectionOrder =
        ["Gearbox", "Suspensions", "Dampers", "Axles", "Differentials", "Brakes", "Wheels", "Other"];

    // the place of a value in a Corners or Axles row
    private static readonly string[] Wheels = ["FrontLeft", "FrontRight", "RearLeft", "RearRight"];
    private static readonly string[] Axle = ["Front", "Rear"];

    // its parameters differ from one position to the next (the centre one has
    // its own), so it is split by position instead
    private const string Differentials = "Differentials";

    /// <summary>
    /// The rows for <paramref name="setups"/>, one value per setup in their order.
    /// Sections come in <see cref="SectionOrder"/>; within one, parameters come
    /// in the order of the first setup's file, then any the others add.
    /// A parameter set per wheel ("Wheels.FrontLeft.Camber" and its three
    /// others) is one Corners row; one set per axle ("Axles.Front.…" and
    /// "Axles.Rear.…") is one Axles row. Differentials are split instead into
    /// subsections by position.
    ///
    /// A row of wheels shows no more detail than it needs, the same way in
    /// every setup so the columns line up: an axle whose left and right wheels
    /// match in every setup shows one value for both; when both axles do, the
    /// row is an Axles row; when front and rear match too, a single value. A
    /// parameter set per axle keeps its two values, front and rear, always.
    /// </summary>
    public static List<SetupViewRow> Rows(IReadOnlyList<SetupExportData> setups)
    {
        var rows = new List<SetupViewRow>
        {
            Info("Name", setups, s => s.Name),
            Info("Author", setups, s => s.Author),
            Info("Stage", setups, s => SetupShareJson.FriendlyName(s.Track)),
            Info("Surface", setups, s => List(s.Surfaces)),
            Info("Weather", setups, s => List(s.Weathers)),
            Info("Game mode", setups, s => List(s.Tags.Select(t => t.EndsWith("_Tag", StringComparison.Ordinal) ? t[..^4] : t))),
            Info("Build", setups, s => s.GameVersion),
            Info("Date", setups, s => s.Timestamp == DateTime.MinValue ? "" : s.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        };

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in setups.SelectMany(s => s.Parameters.Keys))
        {
            if (seen.Add(key)) keys.Add(key);
        }

        // one block per section; OrderBy is stable, so unlisted sections keep
        // the order they first come in
        foreach (var section in keys.GroupBy(SectionOf).OrderBy(g => Rank(g.Key)))
        {
            string name = SetupShareJson.FriendlyName(section.Key);
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in section)
            {
                if (done.Contains(key)) continue;
                string[] part = key.Split('.', 3);
                string? position = part.Length == 3 ? part[1] : null;

                if (position is not null && section.Key == Differentials)
                {
                    rows.Add(Single(name, SetupShareJson.FriendlyName(part[2]), key, setups) with
                    {
                        Subsection = SetupShareJson.FriendlyName(position),
                    });
                }
                else if (position is not null && (Wheels.Contains(position) || Axle.Contains(position)))
                {
                    bool wheels = Wheels.Contains(position);
                    string[] keysOf = [.. (wheels ? Wheels : Axle).Select(w => $"{part[0]}.{w}.{part[2]}")];
                    done.UnionWith(keysOf);
                    var values = setups.Select(s => keysOf.Select(k => ValueOf(s, k)).ToArray()).ToList();
                    string label = SetupShareJson.FriendlyName(part[2]);
                    rows.Add(wheels ? PerWheel(name, label, values) : PerAxle(name, label, values));
                }
                else
                {
                    rows.Add(Single(name, LabelOf(key), key, setups));
                }
            }
        }
        return rows;
    }

    private static SetupViewRow Info(string label, IReadOnlyList<SetupExportData> setups, Func<SetupExportData, string> value)
    {
        var values = setups.Select(value).ToList();
        return new SetupViewRow(SetupViewRow.SetupSection, label, [.. values.Select(Cell)], Differ(values));
    }

    private static SetupViewCell Cell(string text) => new(text, []);

    private static string ValueOf(SetupExportData setup, string key) =>
        setup.Parameters.TryGetValue(key, out string? v) ? FormatValue(v) : Missing;

    private static SetupViewRow Single(string section, string label, string key, IReadOnlyList<SetupExportData> setups)
    {
        var values = setups.Select(s => ValueOf(s, key)).ToList();
        return new SetupViewRow(section, label, [.. values.Select(Cell)], Differ(values));
    }

    /// <summary>
    /// The row of a parameter set per wheel, from each setup's four values in
    /// <see cref="Wheels"/> order: an Axles row, or less, when each axle's left
    /// and right match in every setup; else a Corners row, an axle that does
    /// shown once.
    /// </summary>
    private static SetupViewRow PerWheel(string section, string label, List<string[]> values)
    {
        bool front = values.All(v => v[0] == v[1]), rear = values.All(v => v[2] == v[3]);
        if (front && rear)
        {
            var axles = values.Select(v => new[] { v[0], v[2] }).ToList();
            if (axles.All(v => v[0] == v[1]))
            {
                var one = axles.Select(v => v[0]).ToList();
                return new SetupViewRow(section, label, [.. one.Select(Cell)], Differ(one));
            }
            return PerAxle(section, label, axles);
        }

        bool[] differs = PartsDiffer(values);
        var cells = values.Select(v => new SetupViewCell(
            string.Join(" · ",
                front ? $"F {v[0]}" : $"FL {v[0]} · FR {v[1]}",
                rear ? $"R {v[2]}" : $"RL {v[2]} · RR {v[3]}"),
            [.. v.Select((x, p) => new SetupViewPart(x, differs[p]))])
        {
            FrontPaired = front,
            RearPaired = rear,
        });
        return new SetupViewRow(section, label, [.. cells], differs.Any(d => d)) { Kind = SetupViewKind.Corners };
    }

    /// <summary>The row of a parameter set per axle, from each setup's front and rear values.</summary>
    private static SetupViewRow PerAxle(string section, string label, List<string[]> values)
    {
        bool[] differs = PartsDiffer(values);
        var cells = values.Select(v => new SetupViewCell(
            $"F {v[0]} · R {v[1]}",
            [.. v.Select((x, p) => new SetupViewPart(x, differs[p]))]));
        return new SetupViewRow(section, label, [.. cells], differs.Any(d => d)) { Kind = SetupViewKind.Axles };
    }

    // for each position, whether the setups set it apart
    private static bool[] PartsDiffer(List<string[]> values) =>
        [.. values[0].Select((_, p) => Differ([.. values.Select(v => v[p])]))];

    private static string List(IEnumerable<string> items) => string.Join(", ", items.Select(SetupShareJson.FriendlyName));

    private static bool Differ(List<string> values) => values.Distinct(StringComparer.Ordinal).Skip(1).Any();

    private static int Rank(string section)
    {
        int i = Array.IndexOf(SectionOrder, section);
        // between the listed ones and Other, the last of them
        return i >= 0 ? 2 * i : 2 * SectionOrder.Length - 3;
    }

    // "Wheels.FrontLeft.TyrePressure": Wheels
    private static string SectionOf(string key) => key.Split('.')[0];

    /// <summary>
    /// The key but its section, spaced into words: "Wheels.FrontLeft.TyrePressure"
    /// reads "Front Left · Tyre Pressure". A part named after its section plus
    /// "Main" ("Brakes.BrakesMain.FrontBias") says nothing and is left out.
    /// </summary>
    public static string LabelOf(string key)
    {
        string[] parts = key.Split('.');
        if (parts.Length < 2) return SetupShareJson.FriendlyName(key);
        var rest = parts.Skip(1).Where((p, i) => !(i == 0 && parts.Length > 2 && p == parts[0] + "Main"));
        return string.Join(" · ", rest.Select(SetupShareJson.FriendlyName));
    }

    /// <summary>
    /// A stored value as it reads best. Numbers lose their trailing zeros
    /// ("36.000000" is 36); a gear pair "(Gears:27//8)" reads "27:8 (3.375)",
    /// with the overall ratio; ramp angles "(LSDRampAngles:45_60)" read
    /// "45° / 60°"; a gear set "(GearsSets:SomeCarSet1)" reads as the game
    /// numbers it, from 1: "2"; any other "(Kind:Value)" reads its value,
    /// underscores as spaces. Anything else is shown as stored.
    /// </summary>
    public static string FormatValue(string raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return Number(number);

        var m = Tagged().Match(raw);
        if (!m.Success) return raw;
        string kind = m.Groups[1].Value, body = m.Groups[2].Value;

        if (kind == "Gears" && Gears(body) is string gears) return gears;
        if (kind == "LSDRampAngles" && body.Split('_') is [var a, var b]) return $"{a}° / {b}°";
        if (kind == "GearsSets" && SetNumber().Match(body) is { Success: true } set
            && int.TryParse(set.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            return (n + 1).ToString(CultureInfo.InvariantCulture);
        }
        return body.Replace('_', ' ');
    }

    // "27//8" or "13//31*28//41": pairs of teeth, multiplied
    private static string? Gears(string body)
    {
        var pairs = new List<string>();
        double ratio = 1;
        int driven = 0, driving = 1;
        foreach (string pair in body.Split('*'))
        {
            string[] t = pair.Split("//");
            bool teeth = t.Length == 2
                && int.TryParse(t[0], NumberStyles.None, CultureInfo.InvariantCulture, out driven)
                && int.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out driving)
                && driving != 0;
            if (!teeth) return null;
            pairs.Add($"{driven}:{driving}");
            ratio *= (double)driven / driving;
        }
        return $"{string.Join(" × ", pairs)} ({ratio.ToString("0.###", CultureInfo.InvariantCulture)})";
    }

    private static string Number(double n) =>
        n.ToString("0.######", CultureInfo.InvariantCulture) is var s && s == "-0" ? "0" : s;

    [GeneratedRegex(@"^\(([A-Za-z]+):(.*)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Tagged();

    // the set's index at the end of its id: "SomeCarSet0" is the first
    [GeneratedRegex(@"Set([0-9]{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex SetNumber();
}
