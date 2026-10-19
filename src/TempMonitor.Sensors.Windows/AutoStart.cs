using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace TempMonitor;

/// <summary>
/// Starts the app at sign-in through a Task Scheduler task with "run with highest privileges".
/// The app needs admin rights, and Windows won't auto-start elevated apps from the Run key or
/// the Startup folder; a scheduled task starts it elevated with no UAC prompt.
/// Creating or deleting the task requires the app to be running as administrator.
/// </summary>
public static class AutoStart
{
    private const string TaskName = "TempMonitor";
    public const string MinimizedArg = "--minimized";

    private static string ExePath => Environment.ProcessPath!;

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>If the app folder moved since the task was created, point the task at the new path.</summary>
    public static void RepairPathIfMoved()
    {
        if (Schtasks($"/Query /TN \"{TaskName}\" /XML", out var xml) != 0) return;
        if (!xml.Contains(SecurityElement.Escape(ExePath), StringComparison.OrdinalIgnoreCase))
            Enable();
    }

    public static void Enable()
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Temp Monitor when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(ExePath)}</Command>
                  <Arguments>{MinimizedArg}</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(ExePath)!)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), $"TempMonitor-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode); // schtasks /XML expects UTF-16
            if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out var output) != 0)
                throw new InvalidOperationException(output.Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable()
    {
        if (Schtasks($"/Delete /TN \"{TaskName}\" /F", out var output) != 0 && IsEnabled())
            throw new InvalidOperationException(output.Trim());
    }

    private static int Schtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        output = stdout.Result + stderr.Result;
        return p.ExitCode;
    }
}
