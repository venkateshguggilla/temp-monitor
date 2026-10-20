# Background monitor: history and overheat alerts (Linux)

`temp-monitor-background.service` is a systemd **user** service. It runs `TempMonitor --monitor` while you're logged in, whether or not the Temp Monitor window is open.

Once a second it:
- records every temperature, usage figure and fan speed;
- checks for overheating;
- checks for stalled fans. It does this every 5 seconds, using the fan service.

## Alerts

Every component has its own "hot" temperature: CPU 95 °C, GPU 90 °C, drives 70 °C, RAM 80 °C.

| Alert | When | Notification |
|---|---|---|
| Warning ("CPU is running hot") | at or above hot for 30 s | normal |
| Critical ("CPU is overheating") | 5 °C above hot for 5 s | urgent: stays until dismissed |
| Fan stopped | a fan the fan service drives reads 0 RPM while it should spin | urgent |
| Back to normal / fan spinning again | 5 °C below hot / the fan turns again | logged only |

The same alert repeats at most every 10 minutes. Getting worse (warning → critical) always notifies at once. Every alert is also written to the event log in the history.

## History

Stored in `~/.local/state/temp-monitor/history.db` (SQLite). Nothing leaves the computer.

| Tier | Detail | Kept |
|---|---|---|
| raw | every second | 2 hours |
| minute | average, minimum, maximum per minute | 30 days (setting) |
| event | alerts | 30 days (setting) |

Samples are written every 10 seconds in one transaction, which keeps disk writes low.

### In the app

Open the history with **History** in the toolbar, or by clicking a card's 2-minute graph, which shows that component's history first. The window has:
- **Ranges:** 5 min, 1 hour, 24 hours, 7 days or 30 days. Short ranges refresh every 2 seconds.
- **The chart:** a min–max band with the average line. For temperatures there's a dashed line at the component's "hot" level. Hover to read exact values. Gaps mean nothing was recorded, for example while the computer was off.
- **Compare with:** draws a second series on its own right-hand axis, for example CPU temperature against fan speed.
- **Summary:** min, average and max for the range, and how long the component was at or above hot. That time is counted from the finest data kept (seconds for the last 2 hours, minutes beyond), so a short hot spell still shows on a 30-day chart.
- **Alerts in this range**, newest first.
- **Export CSV…** saves the range in full detail.
- **Clear history…** deletes everything recorded, after asking first.

## Settings

`~/.config/temp-monitor/monitor.json` (all optional):

```json
{
  "RetentionDays": 30,
  "Alerts": {
    "Enabled": true,
    "WarningAfterSeconds": 30,
    "CriticalMargin": 5,
    "CriticalAfterSeconds": 5,
    "RepeatMinutes": 10,
    "ClearMargin": 5
  }
}
```

Restart the service after editing:

```
systemctl --user restart temp-monitor-background
```

With `"Enabled": false`, events are still logged but no notifications are shown.

## Service

```
systemctl --user status temp-monitor-background
journalctl --user -u temp-monitor-background
```

The unit is in `packaging/linux/temp-monitor-background.service`. RudramOS enables it for every user with `systemctl --global enable`.
