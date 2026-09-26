using System.IO;

namespace ACRSaveManager.UI;

/// <summary>Overwrites a save without ever leaving it half-written.</summary>
internal static class AtomicFile
{
    /// <summary>
    /// Replace <paramref name="path"/> with <paramref name="contents"/>. The bytes go
    /// to a temporary file in the same folder, flushed to disk, which then takes
    /// the save's place in a single rename. Writing over the save directly, a
    /// crash, a full disk or a power cut mid-write leaves a truncated file the
    /// game refuses; this way the folder holds either the old save or the new one.
    /// </summary>
    public static void Write(string path, byte[] contents)
    {
        string tmp = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(contents);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
        }
        finally
        {
            // Gone after a successful replace; only a failure leaves it behind.
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best effort: the original error is the one to report */ }
        }
    }
}
