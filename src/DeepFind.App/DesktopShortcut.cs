using System.IO;
using Microsoft.Win32;

namespace DeepFind.App;

// Only the installer passes these commands. DesktopDirectory resolves redirected
// desktops. Keep explicit ownership so uninstall cannot delete another link.
internal static class DesktopShortcut
{
    private const string Key = @"Software\DeepFind\DesktopShortcut";

    public static bool IsInstallerCommand(string argument) =>
        argument is "--create-desktop-shortcut" or "--remove-desktop-shortcut" or "--refresh-desktop-shortcut";

    public static int Run(string argument)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
            using var existing = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            var ownedPath = existing?.GetValue("Path") as string;
            var ownedTarget = existing?.GetValue("Target") as string;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) return 1;
            var destination = Path.Combine(desktop, "DeepFind.lnk");
            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            dynamic shell = Activator.CreateInstance(type)!;
            bool IsOwned(string path)
            {
                if (!string.Equals(Path.GetFileName(path), "DeepFind.lnk", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(path, ownedPath, StringComparison.OrdinalIgnoreCase) || ownedTarget is null || !File.Exists(path))
                    return false;
                return string.Equals((string)shell.CreateShortcut(path).TargetPath, ownedTarget, StringComparison.OrdinalIgnoreCase);
            }

            if (argument == "--remove-desktop-shortcut")
            {
                if (ownedPath is not null && IsOwned(ownedPath))
                    File.Delete(ownedPath);
                existing?.DeleteValue("Path", throwOnMissingValue: false);
                existing?.DeleteValue("Target", throwOnMissingValue: false);
                return 0;
            }

            if (argument == "--refresh-desktop-shortcut")
            {
                // Never resurrect a shortcut the user removed or replace one they changed.
                if (ownedPath is null || !IsOwned(ownedPath)) return 0;
                destination = ownedPath;
            }
            // Never overwrite a same-named shortcut that DeepFind did not create.
            if (File.Exists(destination) && !IsOwned(destination)) return 1;
            dynamic link = shell.CreateShortcut(destination);
            link.TargetPath = executable;
            link.WorkingDirectory = Path.GetDirectoryName(executable);
            link.Description = "Search indexed files";
            link.IconLocation = executable + ",0";
            link.Save();
            using var owner = Registry.CurrentUser.CreateSubKey(Key);
            owner.SetValue("Path", destination);
            owner.SetValue("Target", executable);
            return 0;
        }
        catch (Exception)
        {
            // The app remains installed even if the optional shortcut cannot be created.
            return 1;
        }
    }
}
