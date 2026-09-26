using System.Windows;

namespace ACRSaveManager.UI;

/// <summary>
/// Message boxes shared by the window and its tabs. They all take
/// (message, title), in that order, like MessageBox.Show itself: both
/// arguments are strings, so a swapped pair compiles without a word.
/// </summary>
internal static class Dialogs
{
    public static void Error(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Yes/No question defaulting to No, for anything that writes.</summary>
    public static bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Question about an optional step ahead of a write, defaulting to Yes. True
    /// and false answer it; null means the user backed out of the whole operation.
    /// </summary>
    public static bool? Ask(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            MessageBoxResult.Yes) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    /// <summary>
    /// Runs a pre-write integrity check on the bytes about to be saved. Returns
    /// false — after telling the user nothing was written — when the re-parse
    /// disagrees with what the splice was supposed to produce.
    /// </summary>
    public static bool Verified(Action check)
    {
        try { check(); return true; }
        catch (Exception ex)
        {
            Error($"{ex.Message}\n\n" +
                "Nothing was written: the save file is untouched.\n\n" +
                "This usually means the game's save format changed. Please report it " +
                "with your game version.",
                "Write aborted — integrity check failed");
            return false;
        }
    }
}
