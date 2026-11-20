# EKSA E910 5.8GHz Companion (`EksaTray` v1.3.0)

A lightweight, native Windows WPF system-tray companion for the **EKSA E910 5.8GHz Wireless Gaming Headset** (`VID_3206&PID_0F06`), replacing the legacy OEM `ToneTuningTool.exe` with a modern Obsidian Electric Purple UI, hardware DSP 10-band EQ, an on-demand 14 Hz Anti-Skip DAC Gate Hold, and a smart audio-only battery indicator.

## Features

- **Direct Hardware 10-Band Parametric EQ (`COL01` HID)**:
  - Sends native 10-band PEQ packets (`SetPEQ` `0x21`) and DSP activation triggers (`SetAlgorithmSwitch` `0x31`) directly to the Hongkong Nano IC 5.8GHz USB transmitter.
  - Supports 8 presets (**ZXG**, **Flat**, **Deep Bass**, **EKSA Standard**, **Classic**, **Pop**, **Jazz**, **Pack&Roll**) plus **Custom** per-band tuning (`-12 dB` to `+12 dB`).
- **On-Demand 14 Hz Anti-Skip Low-Frequency Gate (`0%` Idle Drain)**:
  - Eliminates low-frequency / quiet sound skipping (e.g., Minecraft grass footsteps) caused by the EKSA E910 hardware DAC's aggressive zero-detect noise gate.
  - Automatically plays a `-51 dBFS` (`< 0.0003 mW`) 14 Hz sub-audible pilot wave while external audio is playing, and shuts off after **15 seconds of silence** so 0% battery is drained during idle.
- **Smart Audio-Only Battery Indicator (`17h 0m` Calibrated)**:
  - Calibrated to real measured hardware runtime (**`13h 35.5m` to the `20%` low-battery beep / `17h 0m` total capacity**, `5.8857%` per hour) for the modded `803040PT` 1000mAh battery + LED delete.
  - Inspects per-session CoreAudio peak meters (excluding its own process ID and ignoring silent open sessions) so it only counts time when real audio is playing and pauses after **15 seconds of silence**.
- **7.1 Virtual Surround & Hardware Mic ENC Toggles**:
  - Instant hardware DSP switching for 7.1 Surround / Stereo and Microphone Environmental Noise Cancellation.
- **Zero-Bloat System Tray Integration**:
  - Starts minimized to the Windows 11 system tray, persists settings in `%APPDATA%\EksaCompanion\settings.json`, and supports Windows startup registration.

## Building (`x86`)

`MyHidApi.dll` is a 32-bit native library, so the project targets `x86` on .NET Framework 4.8:

```powershell
& "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" EksaCompanion.csproj /p:Configuration=Release /p:Platform=x86 /v:minimal
```

Output executable: `bin\x86\Release\EksaTray.exe`
