using System.Runtime.InteropServices;

namespace DeepFind.App;

/// <summary>Open the parent folder and select the exact filesystem item using Windows Shell.</summary>
internal static class ShellSelection
{
    public static void Reveal(string path)
    {
        // Task.Run uses a background MTA thread. Each successful initialization,
        // including S_FALSE, requires a matching CoUninitialize.
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
        IntPtr item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out item, 0, out _));
            if (item == IntPtr.Zero)
                throw new InvalidOperationException("Windows could not resolve the selected file.");
            // Zero item count means the full PIDL identifies the single item to select.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        finally
        {
            if (item != IntPtr.Zero)
                Marshal.FreeCoTaskMem(item);
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr item,
        uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr items, uint flags);
}
