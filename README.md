# ReoBridge

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
- **Saves the camera's link.** The main stream can stay on all the time. The sub stream is
  pulled only while someone is watching it.

## Stream addresses

| Path | Stream |
|---|---|
| `rtsp://host:8554/<Name>/main` | Main stream (also answers to `/<Name>` and `/<Name>/mainStream`) |
| `rtsp://host:8554/<Name>/sub`  | Sub stream (also answers to `/<Name>/subStream`) |

These are the same paths Neolink uses, so an existing NVR setup keeps working.

## Install on Windows

1. Download a release, or build it yourself:
   `dotnet publish src/ReoBridge -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish`
2. In an elevated PowerShell, run `scripts\install-windows.ps1 -Source .\publish`.
3. Edit `C:\ProgramData\ReoBridge\reobridge.json` (see below), then run the script again.

Logs go to `C:\ProgramData\ReoBridge\logs`.

## Install on Linux

1. Publish for `linux-x64` or `linux-arm64`.
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
        "MainStream": "always", "SubStream": "ondemand" }
    ]
  }
}
```

- **Users:** the logins for RTSP clients. Leave it empty to allow anyone on your network.
- **Host:** give the camera a fixed address, for example a DHCP reservation. ReoBridge
  connects to it directly on port 9000; it does not use Reolink's cloud.
- **MainStream / SubStream:** each takes `always`, `ondemand` or `off`.

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
- ONVIF device and PTZ service
- Motion events
- Battery cameras' sleep and wake
- UID / relay connections

Reports from other models are very welcome.

## Credits and licence

The Baichuan protocol details come from the published research in the Neolink project. No
Neolink code is used.

ReoBridge is MIT licensed. Reolink is a trademark of its owner. This project is not affiliated
with or endorsed by Reolink.
