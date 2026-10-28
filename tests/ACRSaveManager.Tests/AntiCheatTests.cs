using System.IO;
using ACRSaveManager.Services;

namespace ACRSaveManager.Tests;

/// <summary>The anti-cheat folders looked for beside the game, and where the game is looked for.</summary>
public sealed class AntiCheatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acrsm-ac-" + Guid.NewGuid().ToString("N"));

    public AntiCheatTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The game's exe under <paramref name="install"/>: its folder is created, the file is not.</summary>
    private static string Exe(string install)
    {
        string exe = Path.Combine(install, GameInstall.ShippingExe);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        return exe;
    }

    private static string Manifest(string appId, string name, string installDir) =>
        $"\"AppState\"\n{{\n    \"appid\"    \"{appId}\"\n    \"name\"    \"{name}\"\n    \"installdir\"    \"{installDir}\"\n}}\n";

    [Fact]
    public void No_anti_cheat_folder_beside_the_game_reports_nothing()
    {
        string exe = Exe(Path.Combine(_dir, "Assetto Corsa Rally"));
        File.WriteAllBytes(exe, [0]);

        Assert.Null(AntiCheat.FolderBeside(exe));
    }

    [Theory]
    [InlineData("EasyAntiCheat", "")]                       // in the install folder, three levels above the exe
    [InlineData("BattlEye", "")]
    [InlineData("EasyAntiCheat", @"acr\Binaries\Win64")]    // beside the exe
    [InlineData("BattlEye", @"acr\Binaries")]
    public void An_anti_cheat_folder_from_the_exe_up_to_the_install_folder_is_found(string name, string under)
    {
        string install = Path.Combine(_dir, "Assetto Corsa Rally");
        string exe = Exe(install);
        string folder = Path.Combine(install, under, name);
        Directory.CreateDirectory(folder);

        Assert.Equal(folder, AntiCheat.FolderBeside(exe));
    }

    [Fact]
    public void A_folder_above_the_install_folder_is_out_of_reach()
    {
        string exe = Exe(Path.Combine(_dir, "common", "Assetto Corsa Rally"));
        Directory.CreateDirectory(Path.Combine(_dir, "common", "EasyAntiCheat"));

        Assert.Null(AntiCheat.FolderBeside(exe));
    }

    [Fact]
    public void A_file_named_like_an_anti_cheat_folder_is_not_one()
    {
        string install = Path.Combine(_dir, "Assetto Corsa Rally");
        string exe = Exe(install);
        File.WriteAllBytes(Path.Combine(install, "BattlEye"), [0]);

        Assert.Null(AntiCheat.FolderBeside(exe));
    }

    [Fact]
    public void The_exe_need_not_exist_for_the_folders_of_the_game_to_be_looked_at()
    {
        string install = Path.Combine(_dir, "Assetto Corsa Rally");
        string folder = Path.Combine(install, "EasyAntiCheat");
        Directory.CreateDirectory(folder);

        Assert.Equal(folder, AntiCheat.FolderBeside(Path.Combine(install, GameInstall.ShippingExe)));
    }

    [Fact]
    public void The_library_folders_are_the_path_of_each_entry_of_the_vdf()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"        "C:\\Program Files (x86)\\Steam"
                    "label"        ""
                    "contentid"        "776953234"
                    "apps"
                    {
                        "228980"        "0"
                    }
                }
                "1"
                {
                    "path"        "D:\\SteamLibrary"
                    "label"        "Games \"D\""
                    "apps"
                    {
                        "3917090"        "49345778014"
                    }
                }
            }
            """;

        Assert.Equal<string>([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameInstall.LibraryFolders(vdf));
    }

    [Fact]
    public void The_older_vdf_layout_names_each_folder_by_number()
    {
        const string vdf = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"        "1600000000"
                "ContentStatsID"        "-1"
                "1"        "D:\\SteamLibrary"
                "2"        "E:\\Games\\Steam"
            }
            """;

        Assert.Equal<string>([@"D:\SteamLibrary", @"E:\Games\Steam"], GameInstall.LibraryFolders(vdf));
    }

    [Theory]
    [InlineData("3917090", "Assetto Corsa Rally", "Assetto Corsa Rally")]
    [InlineData("3917090", "ACR", "Assetto Corsa Rally")]              // the app id is enough
    [InlineData("1", "Assetto Corsa Rally", "Assetto Corsa Rally")]    // so is the name
    [InlineData("244210", "Assetto Corsa", null)]
    public void A_manifest_of_the_game_names_its_install_folder(string appId, string name, string? expected) =>
        Assert.Equal(expected, GameInstall.InstallDirOf(Manifest(appId, name, "Assetto Corsa Rally")));

    [Fact]
    public void The_game_is_found_once_in_each_Steam_library_holding_its_manifest()
    {
        string steam = Path.Combine(_dir, "Steam");
        string library = Path.Combine(_dir, "SteamLibrary");
        string gone = Path.Combine(_dir, "Gone");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));

        // Steam's own folder is listed in its vdf as well: it is looked at once.
        static string Entry(int index, string path) =>
            $"    \"{index}\"\n    {{\n        \"path\"        \"{path.Replace("\\", "\\\\")}\"\n    }}\n";
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n" + Entry(0, steam) + Entry(1, library) + Entry(2, gone) + "}\n");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_244210.acf"), Manifest("244210", "Assetto Corsa", "assettocorsa"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_3917090.acf"), Manifest("3917090", "Assetto Corsa Rally", "Assetto Corsa Rally"));
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_3917090.acf"), Manifest("3917090", "Assetto Corsa Rally", "Assetto Corsa Rally"));

        Assert.Equal<string>(
            [Path.Combine(steam, "steamapps", "common", "Assetto Corsa Rally", GameInstall.ShippingExe),
             Path.Combine(library, "steamapps", "common", "Assetto Corsa Rally", GameInstall.ShippingExe)],
            GameInstall.SteamExes(steam));
    }

    [Fact]
    public void Without_a_Steam_folder_nothing_is_found() =>
        Assert.Empty(GameInstall.SteamExes(Path.Combine(_dir, "Nowhere")));
}
