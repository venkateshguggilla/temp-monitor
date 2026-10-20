using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TempMonitor.Fans;

/// <summary>A request from a client (one JSON object per line on the socket).</summary>
public sealed class FanRequest
{
    /// <summary>status, set, profile, rename, identify, safety</summary>
    public string Cmd { get; set; } = "status";
    public string? Key { get; set; }
    public string? Name { get; set; }
    public FanSetting? Setting { get; set; }
    public SafetySettings? Safety { get; set; }
}

public sealed record FanResponse(bool Ok, string? Error, DaemonStatus? Status);

/// <summary>Who is asking, from the socket's peer credentials.</summary>
internal readonly record struct Peer(int Pid, int Uid);

/// <summary>Decides whether a peer may perform a polkit action.</summary>
internal interface IAuthorizer
{
    bool Check(string action, Peer peer);
}

/// <summary>
/// Asks polkit (pkcheck). The policy lets the logged-in user switch profiles and fan modes
/// without a password; lowering a safety limit needs an administrator.
/// </summary>
internal sealed class PolkitAuthorizer : IAuthorizer
{
    public bool Check(string action, Peer peer)
    {
        if (peer.Uid == 0) return true;
        var startTime = ProcessStartTime(peer.Pid);
        if (startTime == null) return false;
        try
        {
            var psi = new ProcessStartInfo("pkcheck")
            {
                ArgumentList = { "--action-id", action, "--process", $"{peer.Pid},{startTime},{peer.Uid}", "--allow-user-interaction" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                p.Kill();
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Field 22 of /proc/PID/stat, which polkit uses so a reused PID can't borrow an approval.</summary>
    private static string? ProcessStartTime(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields.Length > 19 ? fields[19] : null;
        }
        catch (IOException) { return null; }
    }
}

/// <summary>
/// The root fan service ("TempMonitor --fan-daemon", run by systemd). Runs the control loop once
/// a second and answers clients on a Unix socket. Any client may read the status; changes are
/// checked with polkit. On exit every fan goes back to firmware control.
/// </summary>
internal sealed class FanDaemon
{
    public const string SocketPath = "/run/temp-monitor/fans.sock";
    public const string ConfigPath = "/etc/temp-monitor/fans.json";
    public const string RestorePath = "/run/temp-monitor/fan-restore.json";
    public const string ControlAction = "com.github.venkateshguggilla.tempmonitor.fan-control";
    public const string SafetyAction = "com.github.venkateshguggilla.tempmonitor.fan-safety";

    private static readonly TimeSpan IdentifyFor = TimeSpan.FromSeconds(5);

    private readonly FanController _controller;
    private readonly IAuthorizer _auth;
    private readonly object _lock = new();

    public FanDaemon(FanController controller, IAuthorizer auth)
    {
        _controller = controller;
        _auth = auth;
    }

    // ---------------------------------------------------------------- Requests

    /// <summary>Handles one request. Authorization runs outside the lock: a password prompt can take a while.</summary>
    public FanResponse Handle(FanRequest req, Peer peer)
    {
        var action = req.Cmd switch
        {
            "status" => null,
            "safety" when req.Safety != null && CurrentSafety().IsLoweredBy(req.Safety) => SafetyAction,
            "set" or "profile" or "rename" or "identify" or "safety" => ControlAction,
            _ => "",
        };
        if (action == "") return new FanResponse(false, $"Unknown command '{req.Cmd}'", null);
        if (action != null && !_auth.Check(action, peer))
            return new FanResponse(false, action == SafetyAction
                ? "Lowering a fan safety limit needs administrator approval"
                : "Not authorized to change fan settings", null);

        lock (_lock)
        {
            var config = _controller.Config;
            var known = _controller.LastStatus?.Fans.Select(f => f.Key).ToHashSet() ?? new HashSet<string>();
            switch (req.Cmd)
            {
                case "status":
                    break;

                case "set":
                    if (req.Key == null || req.Setting == null) return new FanResponse(false, "set needs a fan key and a setting", null);
                    if (!known.Contains(req.Key)) return new FanResponse(false, $"No fan '{req.Key}'", null);
                    if (Validate(req.Setting) is { } bad) return new FanResponse(false, bad, null);
                    if (config.Profile != FanConfig.CustomProfile)
                    {
                        // Keep every other fan doing what the old profile had it do
                        foreach (var k in known) config.Custom[k] = config.Effective(k).Clone();
                        config.Profile = FanConfig.CustomProfile;
                    }
                    config.Custom[req.Key] = req.Setting;
                    _controller.SaveConfig();
                    break;

                case "profile":
                    if (req.Name == null || !FanConfig.Profiles.Contains(req.Name))
                        return new FanResponse(false, "Profiles: " + string.Join(", ", FanConfig.Profiles), null);
                    config.Profile = req.Name;
                    _controller.SaveConfig();
                    break;

                case "rename":
                    if (req.Key == null || !known.Contains(req.Key)) return new FanResponse(false, $"No fan '{req.Key}'", null);
                    var name = req.Name?.Trim();
                    if (string.IsNullOrEmpty(name)) config.Names.Remove(req.Key);
                    else if (name.Length > 40) return new FanResponse(false, "Names can be at most 40 characters", null);
                    else config.Names[req.Key] = name;
                    _controller.SaveConfig();
                    break;

                case "identify":
                    if (req.Key == null || !known.Contains(req.Key)) return new FanResponse(false, $"No fan '{req.Key}'", null);
                    _controller.Identify(req.Key, IdentifyFor);
                    break;

                case "safety":
                    if (req.Safety == null) return new FanResponse(false, "safety needs new limits", null);
                    var s = req.Safety;
                    if (s.FloorPercent is < 0 or > 100 || s.CriticalCpu is < 50 or > 110 || s.CriticalGpu is < 50 or > 110)
                        return new FanResponse(false, "Floor must be 0–100 %, critical temperatures 50–110 °C", null);
                    config.Safety = s.Clone();
                    _controller.SaveConfig();
                    break;
            }
            return new FanResponse(true, null, _controller.Tick());
        }
    }

    private SafetySettings CurrentSafety()
    {
        lock (_lock) return _controller.Config.Safety.Clone();
    }

    private static string? Validate(FanSetting s)
    {
        if (s.Percent is < 0 or > 100) return "Percent must be 0–100";
        if (s.Mode == FanControlMode.Curve)
        {
            if (s.Points.Count < 2) return "A curve needs at least 2 points";
            if (s.Points.Any(p => p.Percent is < 0 or > 100 || p.Temp is < 0 or > 120)) return "Curve points must be 0–120 °C and 0–100 %";
            if (s.Sources.Count == 0) return "A curve needs a temperature source";
        }
        if (s.Hysteresis is < 0 or > 20) return "Hysteresis must be 0–20 °C";
        return null;
    }

    // ---------------------------------------------------------------- Service

    /// <summary>Runs until <paramref name="stop"/>; always hands the fans back on the way out.</summary>
    public void Run(CancellationToken stop, Action<string> log)
    {
        _controller.RecoverFromCrash();
        Directory.CreateDirectory(Path.GetDirectoryName(SocketPath)!);
        File.Delete(SocketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        // Anyone may connect (reading status is harmless); every change is checked with polkit
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                                             UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        listener.Listen(16);
        new Thread(() => AcceptLoop(listener, stop, log)) { IsBackground = true, Name = "Fan clients" }.Start();
        log("fan control running; profile " + _controller.Config.Profile);

        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    lock (_lock) _controller.Tick();
                }
                catch (Exception e)
                {
                    log("control step failed: " + e.Message);
                }
                stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            lock (_lock) _controller.ReleaseAll();
            try { File.Delete(SocketPath); } catch (IOException) { }
            log("stopped; fans handed back to firmware");
        }
    }

    private void AcceptLoop(Socket listener, CancellationToken stop, Action<string> log)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try { client = listener.Accept(); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            new Thread(() => Serve(client, log)) { IsBackground = true, Name = "Fan client" }.Start();
        }
    }

    private void Serve(Socket client, Action<string> log)
    {
        using (client)
        using (var stream = new NetworkStream(client, ownsSocket: false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
        {
            var peer = PeerOf(client);
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    FanResponse resp;
                    try
                    {
                        var req = JsonSerializer.Deserialize<FanRequest>(line, FanConfig.Json) ?? new FanRequest();
                        resp = Handle(req, peer);
                        if (req.Cmd != "status") log($"uid {peer.Uid}: {req.Cmd} {req.Key ?? req.Name} -> {(resp.Ok ? "ok" : resp.Error)}");
                    }
                    catch (JsonException e)
                    {
                        resp = new FanResponse(false, "Bad request: " + e.Message, null);
                    }
                    writer.WriteLine(JsonSerializer.Serialize(resp, FanConfig.Compact));
                }
            }
            catch (IOException) { /* client went away */ }
        }
    }

    /// <summary>SO_PEERCRED: the connecting process's pid and uid, vouched for by the kernel.</summary>
    private static Peer PeerOf(Socket s)
    {
        Span<byte> cred = stackalloc byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        try
        {
            s.GetRawSocketOption(1 /* SOL_SOCKET */, 17 /* SO_PEERCRED */, cred);
            return new Peer(BitConverter.ToInt32(cred[..4]), BitConverter.ToInt32(cred[4..8]));
        }
        catch (SocketException)
        {
            return new Peer(-1, -1); // unknown: polkit will refuse
        }
    }
}

/// <summary>Talks to the fan service: used by the app and by "temp-monitor fan ..." in a terminal.</summary>
public static class FanClient
{
    public static FanResponse Send(FanRequest request, string socketPath = FanDaemon.SocketPath)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var stream = new NetworkStream(s);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(stream, Encoding.UTF8);
            writer.WriteLine(JsonSerializer.Serialize(request, FanConfig.Compact));
            var line = reader.ReadLine();
            return line == null
                ? new FanResponse(false, "The fan service closed the connection", null)
                : JsonSerializer.Deserialize<FanResponse>(line, FanConfig.Json) ?? new FanResponse(false, "Empty answer", null);
        }
        catch (SocketException)
        {
            return new FanResponse(false, "The fan service isn't running (systemctl status temp-monitor-fans)", null);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new FanResponse(false, "Fan service error: " + e.Message, null);
        }
    }
}
