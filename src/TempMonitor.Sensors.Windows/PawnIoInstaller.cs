using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace TempMonitor;

/// <summary>
/// Installs the PawnIO driver (needed for CPU temperatures) with winget, so the portable exe
/// can be set up on a new PC in one click. Falls back to the download page if winget is missing.
/// </summary>
public static class PawnIoInstaller
{
    private const string DownloadPage = "https://pawnio.eu/";

    public static bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key != null;
    }

    /// <summary>Runs the install in a visible console window and returns a message for the user.</summary>
    public static string Install()
    {
        try
        {
            // Visible window so the user sees winget's progress and any license prompt.
            using var p = Process.Start(new ProcessStartInfo("winget.exe",
                "install --id namazso.PawnIO --exact --source winget")
            {
                UseShellExecute = true,
            })!;
            p.WaitForExit();
        }
        catch (Win32Exception)
        {
            // winget isn't available (older Windows 10 without App Installer).
            Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true });
            return "winget isn't available on this PC, so the PawnIO download page was opened instead.\n\n" +
                   "Install PawnIO from there, then restart Temp Monitor.";
        }

        return IsInstalled()
            ? "PawnIO is installed. Close and reopen Temp Monitor to read CPU temperatures."
            : "PawnIO doesn't appear to be installed. Check the installer window for errors, " +
              $"or install it manually from {DownloadPage}";
    }
}
