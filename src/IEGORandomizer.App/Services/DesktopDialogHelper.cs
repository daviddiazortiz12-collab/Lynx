using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace IEGORandomizer.App.Services
{
    public sealed class FileBrowserEntry
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public long SizeBytes { get; init; }
    }

    public sealed class FileBrowserResponse
    {
        public string CurrentDirectory { get; init; } = string.Empty;
        public string ParentDirectory { get; init; } = string.Empty;
        public List<string> Drives { get; init; } = new();
        public List<FileBrowserEntry> Entries { get; init; } = new();
    }

    /// <summary>
    /// Provides cross-platform directory browsing plus native Windows file/folder dialogs
    /// and desktop app-window launching on the user's PC. Never touches console hardware.
    /// </summary>
    public static class DesktopDialogHelper
    {
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".fa", ".3ds", ".cci", ".cxi", ".romfs"
        };

        public static FileBrowserResponse BrowseDirectory(string requestedPath, bool directoriesOnly)
        {
            string target = requestedPath;
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                target = Directory.Exists(userHome) ? userHome : Directory.GetCurrentDirectory();
            }

            string fullDir = Path.GetFullPath(target);
            string parent = Directory.GetParent(fullDir)?.FullName ?? string.Empty;

            var drives = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    drives.Add(d.RootDirectory.FullName);
                }
            }
            catch
            {
                // Ignore drive enumeration issues
            }

            var entries = new List<FileBrowserEntry>();
            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(fullDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("."))
                        continue;

                    entries.Add(new FileBrowserEntry
                    {
                        Name = name,
                        FullPath = subDir,
                        IsDirectory = true,
                        SizeBytes = 0
                    });
                }

                if (!directoriesOnly)
                {
                    foreach (string file in Directory.EnumerateFiles(fullDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        string ext = Path.GetExtension(file);
                        if (!SupportedExtensions.Contains(ext))
                            continue;

                        var fi = new FileInfo(file);
                        entries.Add(new FileBrowserEntry
                        {
                            Name = fi.Name,
                            FullPath = fi.FullName,
                            IsDirectory = false,
                            SizeBytes = fi.Length
                        });
                    }
                }
            }
            catch
            {
                // Ignore permission-restricted subdirectories
            }

            return new FileBrowserResponse
            {
                CurrentDirectory = fullDir,
                ParentDirectory = parent,
                Drives = drives,
                Entries = entries
            };
        }

        /// <summary>
        /// Opens a native Windows File/Folder dialog when running on a Windows PC,
        /// using a TopMost owner window so the dialog always appears in front of the app window.
        /// Returns null on non-Windows platforms so the frontend seamlessly opens the built-in browser modal.
        /// </summary>
        public static string TryPickNativeWindowsDialog(string mode)
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                string script = mode == "folder"
                    ? "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::EnableVisualStyles(); $owner = New-Object System.Windows.Forms.Form -Property @{TopMost = $true; ShowInTaskbar = $false; WindowState = 'Minimized'}; $f = New-Object System.Windows.Forms.FolderBrowserDialog; $f.Description = 'Selecciona la carpeta (RomFS o carpeta de salida)'; $f.ShowNewFolderButton = $true; if ($f.ShowDialog($owner) -eq 'OK') { Write-Output $f.SelectedPath }; $owner.Dispose()"
                    : "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::EnableVisualStyles(); $owner = New-Object System.Windows.Forms.Form -Property @{TopMost = $true; ShowInTaskbar = $false; WindowState = 'Minimized'}; $f = New-Object System.Windows.Forms.OpenFileDialog; $f.Title = 'Selecciona ie_a.fa o ROM .3ds desencriptada'; $f.Filter = 'Inazuma Eleven GO (*.fa;*.3ds;*.cxi;*.romfs)|*.fa;*.3ds;*.cci;*.cxi;*.romfs|Todos los archivos (*.*)|*.*'; if ($f.ShowDialog($owner) -eq 'OK') { Write-Output $f.FileName }; $owner.Dispose()";

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -STA -WindowStyle Hidden -Command \"{script}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                if (proc == null)
                    return null;

                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(180_000);
                return string.IsNullOrWhiteSpace(output) ? null : output;
            }
            catch
            {
                return null;
            }
        }

        public static void TryOpenDesktopAppWindow(string localUrl)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Prefer Edge --app mode on Windows 10/11 for a clean standalone desktop window
                    foreach (string browserExe in new[] { "msedge", "chrome" })
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = browserExe,
                                Arguments = $"--app={localUrl} --window-size=1260,880",
                                UseShellExecute = true
                            });
                            return;
                        }
                        catch
                        {
                            // Try next candidate
                        }
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = localUrl,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // Ignore if headless environment
            }
        }
    }
}
