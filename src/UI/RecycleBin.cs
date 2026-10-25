using System.IO;
using System.Runtime.InteropServices;

namespace ACRSaveManager.UI;

/// <summary>
/// Sends a file to the Recycle Bin, where Explorer would, rather than
/// deleting it for good: an old backup rotated out can still be got back.
/// </summary>
internal static partial class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    // Asks the player before deleting for good, where no Recycle Bin is (a
    // network share): a "No" leaves the file, and Send throws.
    private const ushort FofWantNukeWarning = 0x4000;

    public static void Send(string path)
    {
        // pFrom is a list of paths, each ended by a null, the list by one more:
        // the marshalled string brings the second.
        IntPtr from = Marshal.StringToHGlobalUni(Path.GetFullPath(path) + "\0");
        try
        {
            var op = new FileOp
            {
                Func = FoDelete,
                From = from,
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
            };
            int rc = SHFileOperation(ref op);
            if (rc != 0 || op.AnyOperationsAborted != 0)
                throw new IOException($"{path} was not moved to the Recycle Bin (code {rc}).");
        }
        finally { Marshal.FreeHGlobal(from); }
    }

    // SHFILEOPSTRUCTW, with the strings as pointers so the struct is blittable.
    // Natural alignment is the x64 layout, the only one the app is built for.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref FileOp op);
}
