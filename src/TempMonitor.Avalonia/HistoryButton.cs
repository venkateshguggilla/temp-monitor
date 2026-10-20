using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TempMonitor.History;

namespace TempMonitor.App;

/// <summary>
/// Toolbar "History" button. Shown where the background monitor records history (Linux), or
/// when TEMPMONITOR_HISTORY_DB points at a history file (development).
/// </summary>
public sealed class HistoryButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public HistoryButton()
    {
        Content = "History";
        IsVisible = Available;
        ToolTip.SetTip(this, "Temperatures, usage and fan speeds over the last 5 minutes to 30 days, and past alerts");
    }

    public static bool Available => HistoryPaths.HistoryDb != null;

    protected override void OnClick()
    {
        base.OnClick();
        Open(this, null);
    }

    /// <summary>Opens the history window, showing <paramref name="metric"/> first if given.</summary>
    public static void Open(Visual from, string? metric)
    {
        if (!Available || TopLevel.GetTopLevel(from) is not Window owner) return;
        new HistoryWindow(metric).Show(owner);
    }
}
