# DMX Core 100 — Govee Plugin

Drives **Govee** WiFi lights — LED strips, neon ropes, panels, and lamps
that support Govee's **LAN API** — from DMX Core fixture or playback data
over the local network (JSON over UDP). No cloud account or hub is needed
at show time; each device only needs **LAN Control** switched on once in
the Govee Home app.

## Setup

Only Govee models on the official [LAN API supported list](https://app-h5.govee.com/user-manual/wlan-guide)
work (e.g. the H618x/H619x strip series, H61A0 neon ropes, H6061/H6066
panels); the ordinary Govee A19 bulbs do **not** speak the LAN API. For
each device:

1. Add it to the WiFi with the Govee Home app.
2. In the device's settings, switch on **LAN Control** (the toggle can take
   a few minutes and a firmware update to appear).
3. Give it a **static DHCP lease** so the mapping's IP does not move.

Devices and the DMX Core must be on the same LAN. Discovery multicasts to
239.255.255.250:4001 and the replies come back on UDP port 4002; commands
go to the device on UDP 4003.

On the Core's **Outputs** page, add an output of type **Govee**:

1. **Protocol** — pick the layout (table below).
2. **Destination Address** — the device's IP. Use **Discover**, or type a
   known address. Discover lists the model, e.g.
   `Govee H618A (192.168.1.30, 1F:80:C5:32:32:36:72:4E)`.
3. **Start Channel** — DMX start address of that device's channels.

In the fixture editor, patch devices with the plugin's **Govee / Color
Light** or **Govee / White Light** profile; their personalities match the
protocols one-to-one, and the Mapped Device selector prefills from an
existing Govee mapping. Presets, cues, effects, and Fixture Control then
drive the light through the normal lighting pipeline.

| Protocol | Profile / personality | Channels | Notes |
|---|---|---|---|
| `GOVEE_COLOR` | Color Light / **RGB** | R, G, B | The whole device shows one color (the LAN API has no per-segment control) |
| `GOVEE_WHITE_CT` | White Light / **Dimmer+CT** | Intensity, ColorTemperature | Kelvin mode: CT 0 = 2000 K (warm), 255 = 9000 K (cool); the device clamps to its own range |
| `GOVEE_WHITE` | White Light / **Dimmer** | Intensity | Brightness only; the device keeps its current color |
| `GOVEE_RT_COLOR` | Color Light / **RGB** | R, G, B | **Realtime** — instant, no firmware fade; recommended for cues and effects; see below |
| `GOVEE_RT_PIXEL` | — | R, G, B × segments | **Realtime** — per-segment RGBIC control; see below |

The Core rate-limits each mapping to 10 updates/second and coalesces
latest-wins. Govee splits power, brightness, and color over separate
commands (`turn`, `brightness`, `colorwc`), so the plugin tracks what it
last sent and emits only the commands that changed — a color fade at a
steady hue is one `brightness` datagram per update. When a frame does need
several commands, the plugin paces them 15 ms apart: the device firmware
processes one datagram at a time and silently drops one that arrives
back-to-back with the previous (measured on an H618A — 0 ms loses the
second command, 5 ms already works). All channels at zero sends `turn 0`
(device off).

**Brightness:** Govee renders `colorwc` as the color and a separate
`brightness` percentage (1-100) as intensity. The plugin puts the level of
the brightest channel into `brightness` and normalizes the color channels
toward 255, keeping the best color resolution across the whole fade.

## Realtime protocols

The Realtime protocols — **the recommended way to drive Govee from cues,
effects, and anything with timing** — stream colors over the
reverse-engineered **razer/DreamView** mode, the transport Govee's own
desktop app uses for music and video sync:

- `GOVEE_RT_COLOR` — the whole device as one RGB zone, rendered
  **instantly**: snaps, strobes, fast chases, and hard blackouts all work,
  with none of the firmware fade of the standard commands.
- `GOVEE_RT_PIXEL` — 3 channels per segment for **per-segment (RGBIC)
  control**, which the official LAN API does not offer at all.

Both take a **Segments** field on the mapping (the H618A exposes 15;
prefilled when Discover recognizes the model) — it sets the Pixel
protocol's channel count and must match the device. Updates stream at up
to 40 per second (one datagram per frame; an H618A stayed fluid at 60 Hz).
While a realtime mapping is active the device is held in streaming mode
(its app scenes don't render); it returns to normal when the output goes
inactive.

This mode is not part of Govee's documented LAN API. The packet format is
shared across Govee's WiFi RGBIC line (it is the same protocol community
projects like OpenRGB use on the H6159/H616x/H618x/H619x/H61A0 families),
but it has only been **verified on the H618A** here. It is on by default;
if your model ignores it or misbehaves, switch the **Realtime protocols**
plugin setting off and use the standard protocols — and consider opening
an issue or PR, the plugin is open source.

The standard protocols exist for everything else: models where realtime
turns out not to work, and slow ambient content (schedules, gentle washes)
where the firmware fade is actually pleasant.

Requires a Core whose plugin SDK contract is **1.6** or newer.

Verified on hardware with a Govee H618A RGBIC strip (WiFi firmware
1.02.11): discovery, every protocol with `devStatus` readback, 10 Hz fades
through the low end, and the realtime protocols (instant snaps, a 5 Hz
strobe, and a per-segment chase).

## Troubleshooting

- **No devices in Discover:** LAN Control must be enabled per device in
  the Govee Home app — devices without it do not answer the scan at all.
  Also confirm the device is on the same subnet and that the network
  passes multicast (some WiFi access points block it or isolate clients).
- **Discover fails immediately:** replies arrive on the fixed UDP port
  4002; another Govee integration bound exclusively to it (Home Assistant,
  govee2mqtt) on the same host can block the scan.
- **Discover is empty or intermittent on a Windows Core:** Windows
  Defender Firewall must allow the DMX Core executable to receive inbound
  UDP on port 4002 for the active network profile (usually Private) —
  without that rule the scan replies are silently dropped even though
  commands to the lights still work.
- **Device does not follow cues:** check the mapping's IP, that the
  fixture is patched to a Govee profile whose personality matches the
  protocol, and that the output is enabled. Command port 4003 must be
  reachable.
- **Whole strip changes color at once:** the official LAN API exposes the
  device as a single zone; per-segment (RGBIC) control needs the Realtime
  Pixel protocol.
- **Everything fades instead of snapping:** that is the Govee firmware —
  color, brightness, and power changes on the standard protocols are all
  smoothed over a fixed fade the LAN API cannot shorten (verified on an
  H618A: no command lands as a hard snap). Slow fades and gentle effects
  look great; for strobes, fast chases, and hard blackouts use the
  **Realtime** protocols instead (the default recommendation).
- **Wrong device:** destination is the IP address. Re-run Discover after a
  DHCP change, or set a static lease.
- **Plugin will not load:** the device firmware must expose SDK 1.6+.

## Development

```shell
dotnet test tests/DMXCore100.Govee.Tests
./pack.sh            # or pack.ps1 — produces artifacts/govee.dmxplugin + the .nupkg
```

```powershell
pwsh ./deploy-dev.ps1     # pack and upload to localhost:8080 (prompts for PIN)
```

The SDK is restored from nuget.org (`DMXCore.PluginSdk` 1.*). To build
against an unpublished SDK, pack `src/PluginSdk` and `src/PluginSdk.Testing`
from the Software repo into `local-feed/` (see the comment in
`nuget.config`); the `.nupkg` files are git-ignored.

Iterate with `tools/DMXCore100.Govee.DevHost` (F5 in Visual Studio) and the
unit tests — both use [`TestPluginHost`](https://www.nuget.org/packages/DMXCore.PluginSdk.Testing).
Core 2026.8+ hot-reloads an uploaded `.dmxplugin`; older firmware applies
it on the next device restart.

```text
discover
status 192.168.1.30                       # devStatus
send 192.168.1.30 255 0 0
sendwhite 192.168.1.30 200 0              # kelvin mode, warm
senddim 192.168.1.30 128
fade 192.168.1.30 6                       # red ramp at the streaming rate
rt 192.168.1.30                           # realtime demo: snaps, 5 Hz strobe, segment chase
raw 192.168.1.30 {"msg":{"cmd":"devStatus","data":{}}}
r                                         # shutdown + initialize again
d                                         # dump registered protocols / profiles
```

Every push to `main` builds, tests, packs, and publishes the package to
nuget.org (the plugin registry the devices install from) via trusted
publishing; bumping `<Version>` in the csproj is what publishes a new
release.

## License

[MIT](LICENSE)
