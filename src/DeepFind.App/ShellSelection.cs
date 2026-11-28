using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;

namespace DeepFind.App;

/// <summary>Open the parent folder and select the exact filesystem item using Windows Shell.</summary>
internal static class ShellSelection
{
    // The shell accepts S_OK even when the Windows Explorer view has not yet
    // selected its item. Allow a bounded wait for that view, then apply its
    // documented selection method to the exact resolved file.
    private const int SelectionFlags = 1 | 4 | 8 | 16; // select, deselect others, ensure visible, focus

    public static Task RevealAsync(string path)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Reveal(path);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "DeepFind Explorer selection" };
        // Shell automation runs on a background STA rather than blocking WPF.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Reveal(string path)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2));
        IntPtr item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out item, 0, out _));
            if (item == IntPtr.Zero)
                throw new InvalidOperationException("Windows could not resolve the selected file.");
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
            EnsureExactSelection(path);
        }
        finally
        {
            if (item != IntPtr.Zero)
                Marshal.FreeCoTaskMem(item);
            CoUninitialize();
        }
    }

    private static void EnsureExactSelection(string path)
    {
        var expected = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(expected) ?? throw new InvalidOperationException("File has no parent folder.");
        var type = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)
            ?? throw new InvalidOperationException("Windows Explorer automation is unavailable.");
        object shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Explorer could not be resolved.");
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (TrySelectInMatchingView(shell, parent, expected))
                    return;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Windows Explorer did not select the requested item.");
        }
        finally { Release(shell); }
    }

    private static bool TrySelectInMatchingView(object shell, string parent, string path)
    {
        object? windows = null;
        try
        {
            windows = ((dynamic)shell).Windows();
            int count = ((dynamic)windows).Count;
            for (var i = 0; i < count; i++)
            {
                object? window = null, document = null, folder = null, item = null;
                try
                {
                    window = ((dynamic)windows).Item(i);
                    if (window is null)
                        continue;
                    string location = ((dynamic)window).LocationURL;
                    if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || !uri.IsFile ||
                        !SamePath(uri.LocalPath, parent))
                        continue;

                    document = ((dynamic)window).Document;
                    if (document is null)
                        continue;
                    if (HasExactSelection(document, path))
                        return true;

                    folder = ((dynamic)document).Folder;
                    item = ((dynamic)folder).ParseName(Path.GetFileName(path));
                    if (item is null || !SamePath((string)((dynamic)item).Path, path))
                        continue;

                    ((dynamic)document).SelectItem(item, SelectionFlags);
                    if (HasExactSelection(document, path))
                        return true;
                }
                catch (Exception error) when (error is COMException or RuntimeBinderException or ArgumentException)
                {
                    // Explorer can replace a view while navigating. Resolve it
                    // again on the next bounded attempt.
                }
                finally
                {
                    Release(item);
                    Release(folder);
                    Release(document);
                    Release(window);
                }
            }
        }
        catch (Exception error) when (error is COMException or RuntimeBinderException)
        {
            // The ShellWindows collection can be transiently unavailable.
        }
        finally { Release(windows); }
        return false;
    }

    private static bool HasExactSelection(object document, string path)
    {
        object? selected = null, item = null;
        try
        {
            selected = ((dynamic)document).SelectedItems();
            if (selected is null || (int)((dynamic)selected).Count != 1)
                return false;
            item = ((dynamic)selected).Item(0);
            return item is not null && SamePath((string)((dynamic)item).Path, path);
        }
        finally
        {
            Release(item);
            Release(selected);
        }
    }

    private static bool SamePath(string actual, string expected) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase);

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
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
