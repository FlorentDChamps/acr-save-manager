using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ACRSaveManager.Updates;
using Microsoft.Win32;

namespace ACRSaveManager.UI;

public partial class MainWindow : Window
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly AppSettings _settings = AppSettings.Load();
    private AvailableUpdate? _availableUpdate;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        Title = "ACR Save Manager " + AppInfo.Version;   // so bug reports carry a version

        RestoreWindowBounds();   // before the window is shown => no reposition flicker
        // set before the window exists, the state raises no StateChanged
        SyncMaximizeGlyph();
        // saved theme if any, else follow the OS light/dark setting on first run
        Backups.Keep = _settings.BackupsKept;

        ApplyTheme(_settings.HasTheme ? _settings.Dark : OsPrefersDark());

        // Race tab — the save used last, else the game's own
        TabRace.StatusChanged += SetStatus;
        string sav = Pick(_settings.SavePath, "PlayerDataSaveSlot.sav");
        if (sav.Length > 0) TabRace.ShowSavePath(sav);

        // Setup tab — same rule for the setups save
        TabSetup.StatusChanged += SetStatus;
        TabSetup.Configure(Pick(_settings.SetupPath, "CarSetupsDataSaveSlot.sav"),
                           _settings.BackupOnImport);

        Closing += (_, _) => SaveSettings();

        // once the window is shown: the update prompt needs it as its owner
        Loaded += (_, _) => _ = CheckForUpdateAsync();
    }

    /// <summary>Remembered path if it still exists, else the game's default save.</summary>
    private static string Pick(string remembered, string defaultName)
    {
        if (remembered.Length > 0 && File.Exists(remembered)) return remembered;
        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "acr", "Saved", "SaveGames", defaultName);
        return File.Exists(fallback) ? fallback : "";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Theme
    // ══════════════════════════════════════════════════════════════════════════

    private bool _dark;
    private bool _syncingTheme;   // guards the toggle<->ApplyTheme round-trip

    private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingTheme) return;
        _settings.HasTheme = true;
        ApplyTheme(ThemeToggle.IsChecked == true);
        SaveSettings();
    }

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        var palette = dark ? DarkPalette : LightPalette;
        foreach (var kv in palette)
            Resources[kv.Key] = new SolidColorBrush(kv.Value);
        if (ThemeToggle != null && ThemeToggle.IsChecked != dark)
        {
            _syncingTheme = true;         // avoid re-entering via Checked/Unchecked
            ThemeToggle.IsChecked = dark;
            _syncingTheme = false;
        }
        ThemeLabel?.Text = dark ? "Dark mode" : "Light mode";
    }

    private static bool OsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* registry unreadable — fall back to light */ }
        return false;
    }

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        ["Brush.Window"] = C(0xF5, 0xF6, 0xF8),
        ["Brush.Card"] = C(0xFF, 0xFF, 0xFF),
        ["Brush.Input"] = C(0xFF, 0xFF, 0xFF),
        ["Brush.Border"] = C(0xD0, 0xD7, 0xDE),
        ["Brush.BorderHover"] = C(0xBF, 0xC7, 0xCF),
        ["Brush.WindowOutline"] = C(0x57, 0x60, 0x6A),
        ["Brush.Text"] = C(0x1F, 0x23, 0x28),
        ["Brush.ButtonText"] = C(0x24, 0x29, 0x2F),
        ["Brush.ButtonBg"] = C(0xFF, 0xFF, 0xFF),
        ["Brush.ButtonHover"] = C(0xF3, 0xF4, 0xF6),
        ["Brush.ButtonPressed"] = C(0xE7, 0xE9, 0xEC),
        ["Brush.Muted"] = C(0x65, 0x6D, 0x76),
        ["Brush.Faint"] = C(0x8C, 0x95, 0x9F),
        ["Brush.Accent"] = C(0x1A, 0x7F, 0x37),   // update notice
        ["Brush.Danger"] = C(0xB4, 0x23, 0x18),
        ["Brush.SwitchOff"] = C(0xCD, 0xD3, 0xDA),
        ["Brush.SwitchOn"] = C(0x65, 0x6D, 0x76),   // grey, no colour accent
        ["Brush.SwitchThumb"] = C(0xFF, 0xFF, 0xFF),
        ["Brush.ScrollTrack"] = C(0xEC, 0xEE, 0xF1),
        ["Brush.ScrollThumb"] = C(0xC4, 0xCB, 0xD3),
        ["Brush.ScrollThumbHover"] = C(0xAE, 0xB6, 0xBF),
        ["Brush.RowAlt"] = C(0xF6, 0xF8, 0xFA),
        ["Brush.Selection"] = C(0xDD, 0xE7, 0xF2),
    };

    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        ["Brush.Window"] = C(0x0E, 0x11, 0x16),
        ["Brush.Card"] = C(0x16, 0x1B, 0x22),
        ["Brush.Input"] = C(0x0D, 0x11, 0x17),
        ["Brush.Border"] = C(0x30, 0x36, 0x3D),
        ["Brush.BorderHover"] = C(0x44, 0x4C, 0x56),
        ["Brush.WindowOutline"] = C(0x30, 0x36, 0x3D),
        ["Brush.Text"] = C(0xE6, 0xED, 0xF3),
        ["Brush.ButtonText"] = C(0xE6, 0xED, 0xF3),
        ["Brush.ButtonBg"] = C(0x21, 0x26, 0x2D),
        ["Brush.ButtonHover"] = C(0x2A, 0x31, 0x39),
        ["Brush.ButtonPressed"] = C(0x30, 0x36, 0x3D),
        ["Brush.Muted"] = C(0x8B, 0x94, 0x9E),
        ["Brush.Faint"] = C(0x6E, 0x76, 0x81),
        ["Brush.Accent"] = C(0x3F, 0xB9, 0x50),
        ["Brush.Danger"] = C(0xE5, 0x48, 0x4D),
        ["Brush.SwitchOff"] = C(0x3A, 0x42, 0x4C),
        ["Brush.SwitchOn"] = C(0x8B, 0x94, 0x9E),   // grey, no colour accent
        ["Brush.SwitchThumb"] = C(0xE6, 0xED, 0xF3),
        ["Brush.ScrollTrack"] = C(0x16, 0x1B, 0x22),
        ["Brush.ScrollThumb"] = C(0x3A, 0x42, 0x4C),
        ["Brush.ScrollThumbHover"] = C(0x4C, 0x55, 0x61),
        ["Brush.RowAlt"] = C(0x1B, 0x21, 0x2A),
        ["Brush.Selection"] = C(0x2C, 0x35, 0x42),
    };

    // ══════════════════════════════════════════════════════════════════════════
    // Update notice
    // ══════════════════════════════════════════════════════════════════════════

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update == null || !IsLoaded) return;
            _availableUpdate = update;
            UpdateAvailableBtn.Visibility = Visibility.Visible;
            if (update.Version != _settings.DismissedUpdateVersion)
                ShowAvailableUpdateDialog();
        }
        catch
        {
            // An update check is optional: startup remains silent and unaffected
            // when offline, rate-limited, or when GitHub is temporarily unavailable.
        }
    }

    private void UpdateAvailableBtn_Click(object sender, RoutedEventArgs e) => ShowAvailableUpdateDialog();

    private void ShowAvailableUpdateDialog()
    {
        var update = _availableUpdate;
        if (update == null) return;
        switch (UpdateAvailableDialog.Show(this, update))
        {
            case UpdateDialogResult.Download:
                OpenUrl(update.DownloadUrl.AbsoluteUri);
                break;
            case UpdateDialogResult.Discard:
                _settings.DismissedUpdateVersion = update.Version;
                SaveSettings();
                break;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Open URL error: " + ex.Message); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Window chrome (WindowStyle=None + WindowChrome: the caption is ours)
    // ══════════════════════════════════════════════════════════════════════════

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // Segoe MDL2 Assets, private-use range: spelled out rather than pasted, so
    // the source stays readable in an editor without that font.
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        SyncMaximizeGlyph();
    }

    private void SyncMaximizeGlyph()
        => MaximizeBtn?.Content = WindowState == WindowState.Maximized
                ? RestoreGlyph
                : MaximizeGlyph;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    /// <summary>
    /// Without this, a WindowChrome window maximises over the taskbar: clamp the
    /// maximised size and position to the monitor's work area. The message is
    /// left unhandled so WPF's own processing still runs after this: it fills in
    /// the minimum tracking size from MinWidth / MinHeight, and marking it handled
    /// let the window shrink to any size, its content clipped.
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;

        nint monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = info.WorkArea.Left - info.MonitorArea.Left;
        minMax.MaxPosition.Y = info.WorkArea.Top - info.MonitorArea.Top;
        minMax.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        minMax.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
        Marshal.StructureToPtr(minMax, lParam, true);
        return IntPtr.Zero;
    }

    // Source-generated P/Invoke (SYSLIB1054): the marshalling stub is written at
    // compile time instead of at startup. Both signatures are blittable — handles,
    // ints and a struct of ints — so nothing is marshalled but the BOOL return.
    // "private" is spelled out against the habit of the rest of the file: a
    // partial method that returns something has to carry an accessibility.
    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    // The entry point is spelled out: user32 exports GetMonitorInfoA and
    // GetMonitorInfoW, never a bare GetMonitorInfo. DllImport with CharSet.Auto
    // used to try the suffixes; LibraryImport takes the name as given.
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInt Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt MonitorArea, WorkArea;
        public uint Flags;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Settings
    // ══════════════════════════════════════════════════════════════════════════

    private void RestoreWindowBounds()
    {
        var s = _settings;
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;

        // The config is a hand-editable file: a size larger than all screens together
        // only comes from a damaged one, and past Int32 WPF throws while the window
        // is built — the app would not start at all. Such a size is ignored, and the
        // window opens at its default size.
        if (s.WinWidth is double w && w > 200 && w <= vw) Width = w;
        if (s.WinHeight is double h && h > 200 && h <= vh) Height = h;
        if (s.WinLeft is double l && s.WinTop is double t)
        {
            if (l < vx + vw - 40 && l + Width > vx + 40 && t >= vy - 1 && t < vy + vh - 40)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t;
            }
        }
        if (s.WinMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveSettings()
    {
        _settings.Dark = _dark;
        _settings.BackupOnImport = TabSetup.BackupOnImport;
        _settings.SetupPath = TabSetup.SetupPath;
        _settings.SavePath = TabRace.SavePath;
        _settings.WinMaximized = WindowState == WindowState.Maximized;
        // RestoreBounds holds the pre-maximise rectangle, so restoring a window
        // closed while maximised puts it back at its normal size.
        var r = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
        {
            _settings.WinLeft = r.Left;
            _settings.WinTop = r.Top;
            _settings.WinWidth = r.Width;
            _settings.WinHeight = r.Height;
        }
        _settings.Save();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ══════════════════════════════════════════════════════════════════════════

    private void SetStatus(string message) =>
        TxtStatus.Text = message;

}
