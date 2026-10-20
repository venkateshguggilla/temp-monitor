# Fan control (Linux)

Temp Monitor can run fans itself instead of leaving them to the BIOS, the Apple SMC or the GPU driver. Fan speeds can only be changed by root, so a small system service does the work: `temp-monitor-fans.service` runs `TempMonitor --fan-daemon`. The app and the `temp-monitor fan` command talk to it.

A fresh install changes nothing. The default profile is **Auto**, which leaves every fan to firmware.

## Profiles

| Profile | What it does |
|---|---|
| Auto | Firmware controls every fan (default) |
| Silent / Balanced / Performance | Each controllable fan follows a curve. GPU fans follow the GPU temperature. Case, CPU and laptop fans follow whichever of the CPU and GPU is hotter |
| Custom | Each fan has its own setting: Auto, Fixed % or Curve |

Changing one fan's setting switches to Custom. Every other fan keeps doing what the previous profile had it do.

## In the app

When the fan service is running, the **Fans** card adds:
- **A profile picker** in its header.
- **An Edit button on each fan you can control**, which opens the fan's settings:
  - its name;
  - **Auto (firmware)**, **Fixed speed** or **Curve**;
  - for Curve: a graph you drag to set the speeds, the temperatures it follows (CPU, GPU, drives, RAM) and the hysteresis;
  - **Identify**, which runs the fan at full speed for 5 seconds so you can tell which one it is.
- **Safety limits…**, for the minimum speed and the critical CPU and GPU temperatures. Lowering a limit brings up the administrator prompt.

Problems show in red on the fan's row: a stalled fan, a missing temperature, or a speed that couldn't be set. A critical temperature shows at the bottom of the card. Without the service (Windows, or the service stopped) the card is read-only.

For UI development on Windows, a dev build (`-p:AsInvoker=true`) started with `TEMPMONITOR_FAKE_FANS=1` uses a stand-in service with a desktop's fans.

## From a terminal

```
temp-monitor fan                         # status: fans, speeds, settings
temp-monitor fan profile balanced
temp-monitor fan fixed 2 60              # fan 2 at 60 %
temp-monitor fan curve 1 40:30 60:50 80:100 --source cpu:*,gpu:* --hysteresis 3
temp-monitor fan auto 2                  # fan 2 back to firmware
temp-monitor fan identify 1              # fan 1 at full speed for 5 s, to find which one it is
temp-monitor fan rename 1 "CPU cooler"
temp-monitor fan safety floor=25 cpu=90  # minimum speed, critical temperatures
```

Fan numbers come from `temp-monitor fan status`. Curve sources are reading keys (`cpu:0`, `gpu:card1`, `disk:nvme0n1`) or a prefix with `*`. When several sources are given, the hottest one wins. Hysteresis is how far the temperature must drop before the fan slows down again, so it doesn't keep speeding up and slowing down.

## Safety

These rules apply whatever the settings say:
- **Floor:** no controlled fan runs below the minimum speed (default 20 %).
- **Critical temperature:** when a CPU reaches 95 °C or a GPU reaches 90 °C (both can be changed), every controlled fan runs at 100 %.
- **Missing sensor:** if a curve's temperature can't be read, that fan runs at 100 %.
- **Stall alert:** if a fan reads 0 RPM for 8 seconds while it is set to at least 25 %, it is reported as stalled.
- **Handing back:** before the service takes a fan over, it records what firmware was doing, for example the Super I/O's "Smart Fan" mode. Stopping the service puts every fan back exactly as it was. If the service crashes or is killed, systemd runs `TempMonitor --fan-restore`, which does the same from `/run/temp-monitor/fan-restore.json`.
- Firmware sometimes quietly takes a fan back, so the speed is rewritten every 10 seconds.

## Permissions (polkit)

| Action | Who |
|---|---|
| Read status | anyone |
| Profiles, fan modes, curves, names, identify (`…tempmonitor.fan-control`) | the logged-in user, no password |
| Lower a safety limit: smaller floor or higher critical temperature (`…tempmonitor.fan-safety`) | administrator password |

The service checks every request with polkit. It identifies the caller from the socket's kernel-verified peer credentials (pid, uid).

## Supported hardware

| Fans | How |
|---|---|
| Motherboard headers (Nuvoton `nct6775`/`nct6683`/`nct6687`, ITE `it87`, Fintek, Winbond, SMSC) | hwmon `pwmN` + `pwmN_enable` |
| AMD GPUs (`amdgpu`, older `radeon`), `nouveau` | hwmon `pwm1` + `pwm1_enable` |
| NVIDIA (proprietary driver 520+) | NVML `nvmlDeviceSetFanSpeed_v2`, back to automatic with `nvmlDeviceSetDefaultFanSpeed_v2` |
| Intel Macs (`applesmc`) | `fanN_manual` + `fanN_output`, in RPM between the SMC's min and max |
| ThinkPad, Dell | hwmon `pwm1` where the driver allows it (ThinkPad needs `thinkpad_acpi fan_control=1`) |

Fans with no control interface are shown as "read-only". Newer AMD cards (RX 7000) use a different fan interface and are read-only for now.

## Files

| File | What |
|---|---|
| `/usr/lib/systemd/system/temp-monitor-fans.service` | the service (`packaging/linux/`) |
| `/usr/share/polkit-1/actions/com.github.venkateshguggilla.tempmonitor.policy` | permissions (`packaging/linux/`) |
| `/etc/temp-monitor/fans.json` | settings: profile, per-fan settings, names, safety |
| `/run/temp-monitor/fans.sock` | the socket clients connect to (one JSON object per line) |
| `/run/temp-monitor/fan-restore.json` | what firmware was doing, while the service controls fans |
