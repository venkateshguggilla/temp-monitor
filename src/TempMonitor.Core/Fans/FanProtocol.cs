using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TempMonitor.Fans;

/// <summary>One fan as the fan service reports it to clients.</summary>
/// <param name="Temperature">For a curve: the temperature it follows right now (hottest source).</param>
public sealed record FanStatus(string Key, string Name, string Device, float? Rpm, float? Percent,
    FanControlMode Mode, float? Target, bool HasControl, bool Stalled, string? Error, FanSetting Setting,
    float? Temperature = null);

/// <summary>Everything a client shows: profile, safety limits and every fan.</summary>
public sealed record DaemonStatus(string Profile, IReadOnlyList<string> Profiles, SafetySettings Safety,
    bool Critical, List<FanStatus> Fans, string? Note);

/// <summary>A request to the fan service (one JSON object per line on its socket).</summary>
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

/// <summary>Something that answers fan requests: the real service, or a stand-in for UI development.</summary>
public interface IFanService
{
    FanResponse Send(FanRequest request);
}

/// <summary>
/// Talks to the root fan service (temp-monitor-fans.service) over its Unix socket. Used by the
/// app and by "temp-monitor fan ..." in a terminal.
/// </summary>
public sealed class FanClient : IFanService
{
    public const string SocketPath = "/run/temp-monitor/fans.sock";

    private readonly string _socketPath;

    public FanClient(string socketPath = SocketPath) => _socketPath = socketPath;

    public FanResponse Send(FanRequest request) => Send(request, _socketPath);

    public static FanResponse Send(FanRequest request, string socketPath)
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
