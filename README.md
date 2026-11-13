# ReoBridge

[![build](https://github.com/ipdialup/ReoBridge/actions/workflows/build.yml/badge.svg)](https://github.com/ipdialup/ReoBridge/actions/workflows/build.yml)

ReoBridge makes Reolink cameras that only speak Reolink's own protocol (the battery and
Wi-Fi "solo" models such as the E1, Argus and Lumus) show up as ordinary **RTSP** streams. You can
then record them in Blue Iris, Frigate, Home Assistant, VLC, ffmpeg or any NVR.

```
Reolink camera  --(Baichuan, TCP 9000)-->  ReoBridge  --(RTSP 8554)-->  Blue Iris / Frigate / VLC
```

It is a single self-contained program for Windows (as a service) or Linux (systemd). There
is no runtime to install.

## Why another one?

[Neolink](https://github.com/QuantumEntangledAndy/neolink) did this first, and its protocol
research made this project possible. With some cameras, though, Neolink would connect and
then serve nothing. ReoBridge was written from scratch to be dependable for an always-on NVR:

- **Full AES media.** Newer firmware encrypts only the start of each media packet. ReoBridge
  decrypts that part and keeps the rest as is, so frames arrive whole.
- **Keeps going.** It reconnects with backoff, restarts a stream that stalls, and keeps
  serving the last known stream description across reconnects. Viewers see a short gap
  instead of an error.
- **Starts cleanly.** New viewers always start on a keyframe. A slow viewer drops frames
  for itself without affecting anyone else.
- **Standard RTSP.**
  - Transport over TCP (interleaved) or UDP.
  - Digest or Basic authentication.
  - H.264 and H.265 video, with AAC audio.
- **Status page.** `http://host:8555/` shows each camera, its frame rate, bitrate and
  viewers. `/status` returns the same as JSON.
- **ONVIF and pan/tilt.** Each camera can have its own ONVIF service, so an NVR finds its
  streams and drives pan/tilt and presets. Mounting can be inverted, and each press can be
  limited to a fixed step.
- **Several cameras.** One ReoBridge serves as many cameras as you list.
- **Tray app for Windows.** It shows at a glance whether every camera is streaming, and it can
  edit the cameras and restart the service.
- **Saves the camera's link.** The main stream can stay on all the time. The sub stream is
  pulled only while someone is watching it.

## Stream addresses

| Path | Stream |
|---|---|
| `rtsp://host:8554/<Name>/main` | Main stream (also answers to `/<Name>` and `/<Name>/mainStream`) |
| `rtsp://host:8554/<Name>/sub`  | Sub stream (also answers to `/<Name>/subStream`) |

These are the same paths Neolink uses, so an existing NVR setup keeps working.

## Install on Windows

1. Download the `win-x64` zip from [Releases](https://github.com/ipdialup/ReoBridge/releases) and unzip it, or build it yourself:
   `dotnet publish src/ReoBridge -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish`
2. In an elevated PowerShell, run `scripts\install-windows.ps1 -Source .\publish`.
3. Edit `C:\ProgramData\ReoBridge\reobridge.json` (see below), then run the script again.

Logs go to `C:\ProgramData\ReoBridge\logs`.

## Install on Linux

1. Download the `linux-x64` or `linux-arm64` archive from [Releases](https://github.com/ipdialup/ReoBridge/releases), or publish it yourself.
2. Copy the output to `/opt/reobridge`.
3. Put your config at `/etc/reobridge.json`.
4. Create a systemd unit:

   ```ini
   [Service]
   Type=notify
   ExecStart=/opt/reobridge/ReoBridge --config /etc/reobridge.json
   Restart=always
   ```

## Configuration

```json
{
  "ReoBridge": {
    "Rtsp": { "Port": 8554, "Users": [ { "Name": "viewer", "Password": "change-me" } ] },
    "StatusPort": 8555,
    "Cameras": [
      { "Name": "Indoor", "Host": "192.168.1.50", "Username": "admin", "Password": "camera-password",
        "MainStream": "always", "SubStream": "ondemand", "OnvifPort": 8999 },
      { "Name": "Garden", "Host": "192.168.1.51", "Username": "admin", "Password": "camera-password",
        "OnvifPort": 9001, "InvertTilt": true, "PtzStepMs": 300 }
    ]
  }
}
```

- **Users:** the logins for RTSP clients. Leave it empty to allow anyone on your network.
- **Host:** give the camera a fixed address, for example a DHCP reservation. ReoBridge
  connects to it directly on port 9000; it does not use Reolink's cloud.
- **MainStream / SubStream:** each takes `always`, `ondemand` or `off`.
- **Cameras:** add as many as you like. Each needs its own `Name`, which becomes its stream path.
- **OnvifPort:** turns on an ONVIF service for that camera, so an NVR can find its streams
  and drive pan/tilt and presets. Each camera needs its own port; `0` or missing means off.
- **InvertPan / InvertTilt:** flip left/right or up/down for a camera mounted another way up.
- **PtzSpeed:** the fastest pan/tilt speed sent to the camera, 1–64. `0` lets the NVR choose.
- **PtzStepMs:** how far one press moves. Every move stops by itself after this many
  milliseconds, so a click on the NVR's arrow nudges the camera by a fixed step. `0` keeps
  moving until the NVR says stop.

## ONVIF and pan/tilt

With `OnvifPort` set, point your NVR's ONVIF settings at `host:<OnvifPort>`, using the same
login as RTSP. It answers device, media and PTZ requests: continuous and relative moves, stop,
and the camera's own presets.

In Blue Iris, set the camera's ONVIF port, then pick **ONVIF** as the PTZ type in the PTZ
settings; it does not switch that on by itself. Each PTZ request is logged, which helps when
tuning `PtzSpeed` and `PtzStepMs`.

## Tray app (Windows)

`ReoBridge.Tray.exe` sits in the notification area and shows whether every camera is
streaming: green, amber or red, with a pop-up when something changes. Double-click it for a
status window listing each camera and its streams. On the machine running the service, it can
also edit the cameras, restart the service and open the logs. To watch another machine, use
**Change server…**.

Build it with
`dotnet publish src/ReoBridge.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish-tray`.

The config is read from these places, in order:
1. `appsettings.json` next to the program.
2. The file named by `--config` or by the `REOBRIDGE_CONFIG` environment variable.
3. On Windows only: `%ProgramData%\ReoBridge\reobridge.json`.

## Testing a camera

```
set REOBRIDGE_PASSWORD=camera-password
ReoBridge probe --host 192.168.1.50 --seconds 20 --out test.h264
```

This logs in and reports the encryption, codec, resolution, frame rate and audio. It can
also save the raw video to a file, which you can play with `ffplay test.h264`.

## Status

Tested with a Reolink E1 (Wi-Fi, firmware using full AES), recording in Blue Iris.

Planned:
- Motion events
- Battery cameras' sleep and wake
- UID / relay connections

Reports from other models are very welcome.

## Support

ReoBridge is free and always will be. If it saves you time or money, you can buy me a coffee
towards keeping it going:

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/andyr82756)

Bug reports and reports from other hardware help just as much.

## Credits and licence

The Baichuan protocol details come from the published research in the Neolink project. No
Neolink code is used.

ReoBridge is MIT licensed. Reolink is a trademark of its owner. This project is not affiliated
with or endorsed by Reolink.
