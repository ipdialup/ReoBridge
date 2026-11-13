## Downloads

- **Windows:** `ReoBridge-<version>-win-x64.zip` holds the service, the tray app (`ReoBridge.Tray.exe`), the install script and an example config.
  Unzip it, then in an elevated PowerShell run `scripts\install-windows.ps1 -Source .\publish`. Edit `C:\ProgramData\ReoBridge\reobridge.json` and run the script again.
- **Linux:** `ReoBridge-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz` (Raspberry Pi 4/5, 64-bit OS). See the README for the systemd unit.

Everything is self-contained: no .NET runtime is needed.

## What's in it

- Reolink cameras that only speak Reolink's own protocol (E1, Argus, Lumus and others), served as standard RTSP. This includes full-AES firmware.
- Each camera can have its own ONVIF service, so an NVR such as Blue Iris drives pan/tilt and presets. It also has invert options, a speed cap and a fixed step per press.
- Several cameras in one service.
- Windows tray app: health at a glance, a status window, a camera editor and a service restart.

If it is useful to you, you can [support it on Ko-fi](https://ko-fi.com/andyr82756).
