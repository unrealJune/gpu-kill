# GPUKill

A tiny Windows tray app for one-click disable / enable of an NVIDIA discrete GPU.
Built primarily for the **first-gen ASUS ROG Zephyrus G14 (2020, GA401)**, where
neither Armoury Crate nor G-Helper exposes a way to power down the dGPU — that
generation has no firmware MUX or ACPI power-gate path, so the only available
lever is toggling the device's PnP state.

GPUKill wraps that lever in a tray icon.

## Features

- **Left-click** the tray icon to toggle the dGPU off / on
- **Right-click** menu: Disable, Enable, Restart, Auto-disable on battery, Start with Windows, Open NVIDIA Control Panel
- **Live status** — green icon = enabled, red = disabled, gray = not found
- **Auto-disable on battery** (opt-in) — flips the dGPU off the moment AC unplugs
- **Start with Windows** — adds itself to `HKCU\...\Run`
- **Single elevated UAC prompt** at launch (toggling a PCI device requires admin)
- Self-contained single-file `.exe`, ~50 MB, no .NET runtime required on the host

## Why this exists

On the 2020 G14:
- **G-Helper** hides its GPU section entirely — confirmed by the author, the
  hardware doesn't support disabling the dGPU at the firmware level.
- **Armoury Crate** only offers MSHybrid / Discrete mode switches that require
  reboots and don't actually shut down the dGPU on idle.
- The community workaround is to disable the device in Device Manager, which
  works but is fiddly to do constantly.

GPUKill is just that workaround in a tray icon.

## Install

Download `GPUKill.exe` from the [latest release](../../releases/latest) and
run it. Approve the UAC prompt. A colored circle appears in your tray.

To make it persistent, right-click the icon → **Start with Windows**.

## Build from source

Requires the **.NET 10 SDK** (or .NET 8+ if you retarget the `.csproj`).

```sh
git clone https://github.com/unrealJune/gpu_kill.git
cd gpu_kill
dotnet publish -c Release -r win-x64 --self-contained
```

The single-file exe lands at:

```
bin/Release/net10.0-windows/win-x64/publish/GPUKill.exe
```

## How it works

1. WMI query `Win32_PnPEntity WHERE PNPClass = 'Display'` finds Display-class
   PnP entities, then filters for `PCI\VEN_10DE` (NVIDIA's PCI vendor ID).
2. Cached instance ID is passed to `pnputil /disable-device` /
   `/enable-device` / `/restart-device`. These ship in Windows 10/11 — no
   `devcon` dependency.
3. State is polled every 3 seconds via the same WMI query
   (`ConfigManagerErrorCode == 22` means disabled).
4. The "Auto-disable on battery" option hooks `SystemEvents.PowerModeChanged`
   and disables the dGPU on AC-unplug if it's currently enabled.
5. UAC elevation is requested once at startup via the embedded
   `app.manifest` (`requireAdministrator`).

## Limitations

- **First NVIDIA GPU only.** If you have two NVIDIA GPUs, GPUKill only touches
  the first one it finds. Multi-GPU laptops are rare; PRs welcome.
- **No true power gate.** This is the same trick you'd do manually in Device
  Manager. On older laptops where there's no firmware power-gate path, the
  card may still draw a small amount of idle power even when "disabled."
  The win is preventing the driver from spinning it up under load — which
  is the practical battery saver.
- **Windows only.** Linux users: see
  [fariquelme/g14_dgpu_acpi](https://github.com/fariquelme/g14_dgpu_acpi).
- **Tested on first-gen G14 (GA401).** Should work on any Windows laptop
  with an NVIDIA dGPU, but I can't promise it.

## Project layout

```
GPUKill.csproj      single-file WinForms project
Program.cs          entry point + single-instance mutex
TrayApp.cs          NotifyIcon, context menu, polling, settings
GpuController.cs    WMI lookup + pnputil wrappers
app.manifest        requireAdministrator
.github/workflows/  publish workflow
```

## License

MIT — see [LICENSE](LICENSE).
