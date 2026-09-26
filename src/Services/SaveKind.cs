namespace ACRSaveManager.Services;

/// <summary>
/// One of the two save files the game keeps side by side in SaveGames. Their
/// backups share that folder and the .bak extension, so a restore has to tell
/// them apart by content: both are GVAS, and each names its single property
/// after itself.
/// </summary>
public sealed record SaveKind(
    string Title,       // dialog wording, e.g. "player save"
    string FileName)    // the game's own name for the file
{
    public static readonly SaveKind PlayerData =
        new("player save", "PlayerDataSaveSlot.sav");

    public static readonly SaveKind CarSetups =
        new("car setups save", "CarSetupsDataSaveSlot.sav");

    /// <summary>Which save <paramref name="data"/> is, or null when it is neither.</summary>
    public static SaveKind? Identify(byte[] data) =>
        SaveFileParser.IsPlayerSave(data) ? PlayerData
        : CarSetupsParser.IsSetupsSave(data) ? CarSetups
        : null;
}
