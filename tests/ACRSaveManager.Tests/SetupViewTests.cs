using ACRSaveManager.Services;

namespace ACRSaveManager.Tests;

/// <summary>The lines of the setup viewer. Every value is made up.</summary>
public class SetupViewTests
{
    private static SetupExportData Setup(string name, params (string Key, string Value)[] parameters) => new()
    {
        Car = "TestCarA",
        Name = name,
        Author = "Tester",
        Track = "SomeStageNorth",
        Surfaces = ["Gravel"],
        Weathers = ["Clear", "LightRain"],
        Tags = ["SingleStage_Tag"],
        GameVersion = "0.6.0.1",
        Timestamp = new DateTime(2026, 3, 4, 5, 6, 7),
        Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
    };

    [Theory]
    [InlineData("36.000000", "36")]
    [InlineData("0.530000", "0.53")]
    [InlineData("-0.000200", "-0.0002")]
    [InlineData("-0.000000", "0")]
    [InlineData("28000.000000", "28000")]
    [InlineData("(Gears:27//8)", "27:8 (3.375)")]
    [InlineData("(Gears:13//31*28//41)", "13:31 × 28:41 (0.286)")]
    [InlineData("(Gears:27//0)", "27//0")]
    [InlineData("(LSDRampAngles:45_60)", "45° / 60°")]
    [InlineData("(Pads:Type19_Medium)", "Type19 Medium")]
    [InlineData("(GearsSets:TestCar2000Set0)", "1")]
    [InlineData("(GearsSets:TestCar2000Set1)", "2")]
    [InlineData("(GearsSets:TestCarSet12)", "13")]
    [InlineData("(GearsSets:TestCarLong)", "TestCarLong")]
    [InlineData("(MasterCylindersSets:TestCar_Set1)", "TestCar Set1")]
    [InlineData("Tarmac", "Tarmac")]
    [InlineData("", "")]
    public void Values_read_as_numbers_ratios_and_names(string raw, string shown) =>
        Assert.Equal(shown, SetupView.FormatValue(raw));

    [Theory]
    [InlineData("Wheels.FrontLeft.TyrePressure", "Front Left · Tyre Pressure")]
    [InlineData("Brakes.BrakesMain.FrontBias", "Front Bias")]
    [InlineData("Axles.Front.ARBStiffness", "Front · ARB Stiffness")]
    [InlineData("Other.Electronic.ABS", "Electronic · ABS")]
    [InlineData("Single", "Single")]
    public void A_label_is_the_key_but_its_section(string key, string label) =>
        Assert.Equal(label, SetupView.LabelOf(key));

    [Fact]
    public void One_setup_gives_its_fields_then_its_parameters_by_section_and_nothing_differs()
    {
        var rows = SetupView.Rows([Setup("Alpha",
            ("Other.Lights.AdditionalLights", "1.000000"),
            ("Brakes.BrakesMain.FrontBias", "0.530000"),
            ("Other.Electronic.EngineMap", "2.000000"))]);

        Assert.Equal(["Name", "Author", "Stage", "Surface", "Weather", "Game mode", "Build", "Date"],
            rows.Where(r => r.Section == SetupViewRow.SetupSection).Select(r => r.Label));
        Assert.Equal(["Alpha", "Tester", "Some Stage North", "Gravel", "Clear, Light Rain", "Single Stage", "0.6.0.1", "2026-03-04 05:06"],
            rows.Take(8).Select(r => r.Values.Single()));

        // grouped by section, Brakes ahead of Other; in a section, the file's order
        Assert.Equal([("Brakes", "Front Bias"), ("Other", "Lights · Additional Lights"), ("Other", "Electronic · Engine Map")],
            rows.Skip(8).Select(r => (r.Section, r.Label)));
        Assert.Equal(["0.53", "1", "2"], rows.Skip(8).Select(r => r.Values.Single()));
        Assert.All(rows, r => Assert.Equal(SetupViewKind.Value, r.Kind));
        Assert.DoesNotContain(rows, r => r.Differs);
    }

    [Fact]
    public void A_parameter_set_per_wheel_is_one_row_of_four_in_wheel_order()
    {
        var rows = SetupView.Rows([Setup("Alpha",
            ("Wheels.FrontLeft.Camber", "-3.500000"),
            ("Wheels.FrontLeft.Toe", "0.000000"),
            ("Wheels.FrontRight.Camber", "-3.000000"),
            ("Wheels.FrontRight.Toe", "0.000000"),
            ("Wheels.RearRight.Camber", "-1.000000"),
            ("Wheels.RearLeft.Camber", "-1.500000"),
            ("Wheels.Steering.FFBPlayerMultiplier", "1.000000"))]);

        var wheels = rows.Where(r => r.Section == "Wheels").ToList();
        Assert.Equal(["Camber", "Toe", "Steering · FFB Player Multiplier"], wheels.Select(r => r.Label));

        var camber = wheels[0];
        Assert.Equal(SetupViewKind.Corners, camber.Kind);
        Assert.Equal(["-3.5", "-3", "-1.5", "-1"], camber.Cells.Single().Parts.Select(p => p.Text));
        Assert.Equal("FL -3.5 · FR -3 · RL -1.5 · RR -1", camber.Values.Single());

        // a wheel without the parameter shows as missing; both rear ones
        // missing, front and rear each pair up, and the row is per axle
        Assert.Equal(SetupViewKind.Axles, wheels[1].Kind);
        Assert.Equal(["0", SetupView.Missing], wheels[1].Cells.Single().Parts.Select(p => p.Text));
        Assert.Equal(SetupViewKind.Value, wheels[2].Kind);
        Assert.False(camber.Differs);     // one setup: an asymmetry is not a difference
    }

    [Fact]
    public void A_parameter_set_per_axle_is_one_row_of_two()
    {
        var rows = SetupView.Rows([Setup("Alpha",
            ("Axles.Rear.ARBStiffness", "9000.000000"),
            ("Axles.Front.ARBStiffness", "12000.000000"))]);

        var arb = rows.Single(r => r.Section == "Axles");
        Assert.Equal(("ARB Stiffness", SetupViewKind.Axles), (arb.Label, arb.Kind));
        Assert.Equal(["12000", "9000"], arb.Cells.Single().Parts.Select(p => p.Text));
        Assert.Equal("F 12000 · R 9000", arb.Values.Single());
    }

    [Fact]
    public void Differentials_split_by_position_instead()
    {
        var rows = SetupView.Rows([Setup("Alpha",
            ("Differentials.Centre.CentreRatioToRear", "(Gears:27//8)"),
            ("Differentials.Centre.LSDPreload", "40.000000"),
            ("Differentials.Front.LSDPreload", "60.000000"),
            ("Differentials.Rear.LSDPreload", "20.000000"))]);

        Assert.Equal(
            [("Centre", "Centre Ratio To Rear"), ("Centre", "LSD Preload"), ("Front", "LSD Preload"), ("Rear", "LSD Preload")],
            rows.Where(r => r.Section == "Differentials").Select(r => (r.Subsection, r.Label)));
        Assert.All(rows, r => Assert.Equal(SetupViewKind.Value, r.Kind));
        Assert.All(rows.Where(r => r.Section != "Differentials"), r => Assert.Equal("", r.Subsection));
    }

    [Fact]
    public void Of_several_setups_only_the_wheels_that_differ_are_marked()
    {
        var rows = SetupView.Rows([
            Setup("Alpha", ("Wheels.FrontLeft.TyrePressure", "30.0"), ("Wheels.FrontRight.TyrePressure", "30.0"),
                           ("Wheels.RearLeft.TyrePressure", "28.0"), ("Wheels.RearRight.TyrePressure", "28.0")),
            Setup("Beta", ("Wheels.FrontLeft.TyrePressure", "30.0"), ("Wheels.FrontRight.TyrePressure", "31.0"),
                          ("Wheels.RearLeft.TyrePressure", "28.0"), ("Wheels.RearRight.TyrePressure", "28.0")),
        ]);

        var pressure = rows.Single(r => r.Label == "Tyre Pressure");
        Assert.True(pressure.Differs);
        Assert.False(pressure.HighlightsRow);    // the parts are marked, not the row
        Assert.All(pressure.Cells, c => Assert.Equal([false, true, false, false], c.Parts.Select(p => p.Differs)));
        Assert.Equal(["30", "31", "28", "28"], pressure.Cells[1].Parts.Select(p => p.Text));

        // the rear wheels match in both setups, the front ones not in Beta
        Assert.All(pressure.Cells, c => Assert.Equal((false, true), (c.FrontPaired, c.RearPaired)));
        Assert.Equal(["FL 30 · FR 30 · R 28", "FL 30 · FR 31 · R 28"], pressure.Values);
    }

    private static (string, string)[] PerWheel(string param, double fl, double fr, double rl, double rr) =>
    [
        ($"Wheels.FrontLeft.{param}", Raw(fl)), ($"Wheels.FrontRight.{param}", Raw(fr)),
        ($"Wheels.RearLeft.{param}", Raw(rl)), ($"Wheels.RearRight.{param}", Raw(rr)),
    ];

    // as the game writes a number, whatever the culture the tests run in
    private static string Raw(double n) => n.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_same_value_on_four_wheels_is_one_value()
    {
        var row = SetupView.Rows([Setup("Alpha", PerWheel("Toe", 0, 0, 0, 0)), Setup("Beta", PerWheel("Toe", 1, 1, 1, 1))])
            .Single(r => r.Label == "Toe");

        Assert.Equal(SetupViewKind.Value, row.Kind);
        Assert.Equal(["0", "1"], row.Values);
        Assert.True(row.HighlightsRow);
    }

    [Fact]
    public void Left_matching_right_on_both_axles_makes_a_row_per_axle()
    {
        var row = SetupView.Rows([Setup("Alpha", PerWheel("Camber", -2, -2, -1, -1))]).Single(r => r.Label == "Camber");

        Assert.Equal(SetupViewKind.Axles, row.Kind);
        Assert.Equal("F -2 · R -1", row.Values.Single());
    }

    [Fact]
    public void Wheels_pair_up_only_when_they_match_in_every_setup()
    {
        var row = SetupView.Rows([
            Setup("Alpha", PerWheel("Camber", -2, -2, -1, -1)),
            Setup("Beta", PerWheel("Camber", -2, -2, -1, -1.5)),
        ]).Single(r => r.Label == "Camber");

        Assert.Equal(SetupViewKind.Corners, row.Kind);
        Assert.All(row.Cells, c => Assert.Equal((true, false), (c.FrontPaired, c.RearPaired)));
        Assert.Equal(["F -2 · RL -1 · RR -1", "F -2 · RL -1 · RR -1.5"], row.Values);
        Assert.Equal([false, false, false, true], row.Cells[0].Parts.Select(p => p.Differs));
    }

    [Fact]
    public void A_parameter_set_per_axle_keeps_both_values_even_when_they_match()
    {
        var row = SetupView.Rows([Setup("Alpha", ("Axles.Front.ARBStiffness", "9000.0"), ("Axles.Rear.ARBStiffness", "9000.0"))])
            .Single(r => r.Section == "Axles");

        Assert.Equal((SetupViewKind.Axles, "F 9000 · R 9000"), (row.Kind, row.Values.Single()));
    }

    [Fact]
    public void Sections_come_in_a_set_order_and_unknown_ones_just_before_Other()
    {
        var rows = SetupView.Rows([Setup("Alpha",
            ("Other.Lights.AdditionalLights", "1"),
            ("Wheels.FrontLeft.Camber", "-3.5"),
            ("Brakes.BrakesMain.FrontBias", "0.53"),
            ("Aero.Rear.WingAngle", "4"),
            ("Differentials.Rear.LSDPreload", "20"),
            ("Axles.Front.ARBStiffness", "12000"),
            ("Dampers.FrontLeft.SlowBump", "3500"),
            ("Ballast.Main.Mass", "10"),
            ("Suspensions.FrontLeft.SpringStiffness", "45000"),
            ("Gearbox.GearboxMain.GearsSet", "(GearsSets:TestCarSet1)"))]);

        Assert.Equal(
            ["Info", "Gearbox", "Suspensions", "Dampers", "Axles", "Differentials", "Brakes", "Wheels", "Aero", "Ballast", "Other"],
            rows.Select(r => r.Section).Distinct());
    }

    [Fact]
    public void Several_setups_line_up_and_the_rows_that_differ_say_so()
    {
        var rows = SetupView.Rows([
            Setup("Alpha", ("Brakes.BrakesMain.FrontBias", "0.530000"), ("Other.Electronic.ABS", "0.000000")),
            Setup("Beta", ("Brakes.BrakesMain.FrontBias", "0.53"), ("Other.Electronic.ABS", "1.000000")),
        ]);

        var bias = rows.Single(r => r.Label == "Front Bias");
        Assert.Equal(["0.53", "0.53"], bias.Values);
        Assert.False(bias.Differs);      // compared as shown: 0.530000 is 0.53

        var abs = rows.Single(r => r.Label == "Electronic · ABS");
        Assert.Equal(["0", "1"], abs.Values);
        Assert.True(abs.Differs);
        Assert.True(abs.HighlightsRow);

        var name = rows.Single(r => r.IsName);
        Assert.Equal(["Alpha", "Beta"], name.Values);
        Assert.True(name.Differs);
        Assert.False(rows.Single(r => r.Label == "Author").Differs);
    }

    [Fact]
    public void A_parameter_one_setup_lacks_shows_as_missing_and_differs()
    {
        var rows = SetupView.Rows([
            Setup("Alpha", ("Other.Electronic.ABS", "1")),
            Setup("Beta", ("Other.Electronic.ABS", "1"), ("Other.Lights.AdditionalLights", "1.000000"), ("Wheels.Steering.FFBPlayerMultiplier", "0.8")),
        ]);

        var lights = rows.Single(r => r.Label == "Lights · Additional Lights");
        Assert.Equal([SetupView.Missing, "1"], lights.Values);
        Assert.True(lights.Differs);

        // a key the first setup lacks still joins its section
        Assert.Equal(["Steering · FFB Player Multiplier", "Electronic · ABS", "Lights · Additional Lights"],
            rows.Skip(8).Select(r => r.Label));
    }
}
