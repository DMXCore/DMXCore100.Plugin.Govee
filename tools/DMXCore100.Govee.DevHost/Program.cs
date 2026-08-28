using System.Net;
using System.Net.Sockets;
using System.Text;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using DMXCore100.Govee;

// Interactive harness: F5 this project to talk to real Govee devices on the
// LAN through the output protocol, without a DMX Core 100 device. Use `r`
// to recycle Initialize/Shutdown in-process — the host cannot unload plugin
// assemblies, so this is the practical restart.

GoveePlugin plugin = new();
var host = new TestPluginHost(plugin.Info);
// The dev harness always exposes the experimental realtime protocols
host.SetSetting(GoveePlugin.RealtimeEnabledSettingKey, "true");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"=== {plugin.Info.Name} {plugin.Info.Version} dev host ===");
Console.WriteLine();

await plugin.InitializeAsync(host, cts.Token);

PrintHelp();

try
{
    bool running = true;
    while (running && !cts.IsCancellationRequested)
    {
        Console.Write("> ");
        string? input;
        try
        {
            input = (await ReadLineAsync(cts.Token))?.Trim();
        }
        catch (OperationCanceledException)
        {
            break;
        }

        if (input == null || cts.IsCancellationRequested)
        {
            break;
        }

        try
        {
            string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "discover":
                case "m":
                    IReadOnlyList<PluginOutputDestinationOption>? devices =
                        await host.OutputProtocols[GoveePlugin.ColorProtocolId].Protocol
                            .GetDestinationOptionsAsync(refresh: true, cts.Token);
                    if (devices == null || devices.Count == 0)
                    {
                        Console.WriteLine("  no devices found (LAN Control enabled in the Govee Home app?)");
                        break;
                    }

                    foreach (PluginOutputDestinationOption option in devices)
                    {
                        Console.WriteLine($"    {option.Value}  {option.Label}");
                    }

                    break;

                case "send":
                case "color":
                    if (parts.Length < 5
                        || !byte.TryParse(parts[2], out byte red)
                        || !byte.TryParse(parts[3], out byte green)
                        || !byte.TryParse(parts[4], out byte blue))
                    {
                        Console.WriteLine("usage: send <ip> <r> <g> <b>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        GoveePlugin.ColorProtocolId,
                        Mapping(parts[1]),
                        [red, green, blue],
                        cts.Token));
                    break;

                case "sendwhite":
                    if (parts.Length < 4
                        || !byte.TryParse(parts[2], out byte intensity)
                        || !byte.TryParse(parts[3], out byte ct))
                    {
                        Console.WriteLine("usage: sendwhite <ip> <dimmer> <ct>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        GoveePlugin.WhiteCtProtocolId,
                        Mapping(parts[1]),
                        [intensity, ct],
                        cts.Token));
                    break;

                case "senddim":
                    if (parts.Length < 3 || !byte.TryParse(parts[2], out byte dim))
                    {
                        Console.WriteLine("usage: senddim <ip> <dimmer>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        GoveePlugin.WhiteProtocolId,
                        Mapping(parts[1]),
                        [dim],
                        cts.Token));
                    break;

                case "sendmode":
                {
                    // Any protocol by id with a raw channel slice, e.g.
                    // sendmode GOVEE_WHITE_CT 192.168.1.30 200 0
                    if (parts.Length < 4 || !host.OutputProtocols.ContainsKey(parts[1].ToUpperInvariant()))
                    {
                        Console.WriteLine("usage: sendmode <protocolId> <ip> <ch1> <ch2> ... (0-255 each)");
                        Console.WriteLine($"  protocols: {string.Join(", ", host.OutputProtocols.Keys)}");
                        break;
                    }

                    byte[] slice = new byte[parts.Length - 3];
                    bool parsed = true;
                    for (int i = 0; i < slice.Length && parsed; i++)
                    {
                        parsed = byte.TryParse(parts[i + 3], out slice[i]);
                    }

                    if (!parsed)
                    {
                        Console.WriteLine("  channel values must be 0-255");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        parts[1].ToUpperInvariant(),
                        Mapping(parts[2]),
                        slice,
                        cts.Token));
                    break;
                }

                case "fade":
                {
                    // Stream a 0→255→0 red ramp through ONE session at the
                    // protocol's rate to eyeball smoothness and the
                    // brightness/colorwc split on the wire
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("usage: fade <ip> [seconds]");
                        break;
                    }

                    double seconds = parts.Length > 2 && double.TryParse(parts[2], out double s) ? s : 6;
                    int steps = (int)(seconds * GoveeConstants.MaxUpdatesPerSecond);
                    var delay = TimeSpan.FromMilliseconds(1000.0 / GoveeConstants.MaxUpdatesPerSecond);
                    IPluginOutputSession session = await host.OutputProtocols[GoveePlugin.ColorProtocolId].Protocol
                        .OpenSessionAsync(Mapping(parts[1]), cts.Token);
                    await using (session)
                    {
                        for (int i = 0; i <= steps && !cts.IsCancellationRequested; i++)
                        {
                            double phase = (double)i / steps;
                            byte level = (byte)Math.Round(255 * (phase < 0.5 ? phase * 2 : (1 - phase) * 2));
                            await session.SendAsync(new byte[] { level, 0, 0 }, cts.Token);
                            await Task.Delay(delay, cts.Token);
                        }
                    }

                    Console.WriteLine("  fade done");
                    break;
                }

                case "rt":
                {
                    // Realtime (razer mode) demo through ONE session: color
                    // snaps, a 5 Hz strobe, and a per-segment chase — all
                    // things the normal LAN commands smear with their fade
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("usage: rt <ip> [segments]");
                        break;
                    }

                    int segments = parts.Length > 2 && int.TryParse(parts[2], out int s) ? s : GoveePlugin.DefaultSegments;
                    PluginOutputMappingConfig mapping = new()
                    {
                        DestinationAddress = parts[1],
                        ChannelOffset = 0,
                        UniverseId = 1,
                        Options = new Dictionary<string, string> { [GoveePlugin.SegmentsOptionKey] = segments.ToString() },
                    };

                    Console.WriteLine("  snap test: red / blue, 1 s each");
                    IPluginOutputSession rgb = await host.OutputProtocols[GoveePlugin.RealtimeColorProtocolId].Protocol
                        .OpenSessionAsync(mapping, cts.Token);
                    await using (rgb)
                    {
                        await rgb.SendAsync(new byte[] { 255, 0, 0 }, cts.Token);
                        await Task.Delay(1000, cts.Token);
                        await rgb.SendAsync(new byte[] { 0, 0, 255 }, cts.Token);
                        await Task.Delay(1000, cts.Token);

                        Console.WriteLine("  strobe: red/black at 5 Hz for 3 s");
                        for (int i = 0; i < 15 && !cts.IsCancellationRequested; i++)
                        {
                            await rgb.SendAsync(i % 2 == 0 ? new byte[] { 255, 0, 0 } : new byte[] { 0, 0, 0 }, cts.Token);
                            await Task.Delay(200, cts.Token);
                        }
                    }

                    Console.WriteLine($"  chase: one white segment sweeping {segments} segments");
                    IPluginOutputSession px = await host.OutputProtocols[GoveePlugin.RealtimePixelProtocolId].Protocol
                        .OpenSessionAsync(mapping, cts.Token);
                    await using (px)
                    {
                        for (int step = 0; step < segments * 2 && !cts.IsCancellationRequested; step++)
                        {
                            byte[] slice = new byte[segments * 3];
                            int lit = step % segments;
                            slice[(lit * 3) + 0] = 255;
                            slice[(lit * 3) + 1] = 255;
                            slice[(lit * 3) + 2] = 255;
                            await px.SendAsync(slice, cts.Token);
                            await Task.Delay(100, cts.Token);
                        }
                    }

                    Console.WriteLine("  rt demo done (device back in normal mode)");
                    break;
                }

                case "status":
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("usage: status <ip>");
                        break;
                    }

                    Console.WriteLine($"  devStatus: {await RawAsync(parts[1], Encoding.UTF8.GetString(GoveeMessages.DevStatus), cts.Token)}");
                    break;

                case "raw":
                {
                    // Send any JSON to a device and print the reply, e.g.
                    // raw 192.168.1.30 {"msg":{"cmd":"devStatus","data":{}}}
                    if (parts.Length < 3)
                    {
                        Console.WriteLine("usage: raw <ip> <json>");
                        break;
                    }

                    string json = string.Join(' ', parts.Skip(2));
                    Console.WriteLine($"  reply: {await RawAsync(parts[1], json, cts.Token)}");
                    break;
                }

                case "r":
                {
                    GoveePlugin replacement = new();
                    await replacement.InitializeAsync(host, cts.Token);
                    using var reinitCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    reinitCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await plugin.ShutdownAsync(reinitCts.Token);
                    plugin = replacement;
                    Console.WriteLine("  plugin re-initialized in-process (assemblies stay loaded)");
                    break;
                }

                case "i":
                    Console.WriteLine($"  device: {host.DeviceInfo.ProductName} '{host.DeviceInfo.DeviceName}'");
                    Console.WriteLine($"  serial: {host.DeviceInfo.Serial}");
                    Console.WriteLine($"  version: {host.DeviceInfo.SoftwareVersion}");
                    break;

                case "d":
                    Console.WriteLine($"  protocols: {string.Join(", ", host.OutputProtocols.Keys)}");
                    Console.WriteLine($"  profiles:  {string.Join(", ", host.FixtureProfiles.Keys)}");
                    Console.WriteLine($"  connected: {host.ConnectionState} {host.ConnectionDetail}");
                    break;

                case "q":
                    running = false;
                    break;

                case "?":
                case "help":
                    PrintHelp();
                    break;

                default:
                    Console.WriteLine("unknown command, ? for help");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
        }
    }
}
finally
{
    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await plugin.ShutdownAsync(shutdownCts.Token);
    Console.WriteLine("shut down cleanly");
}

static void Report(bool ok) => Console.WriteLine(ok ? "  sent" : "  send failed");

static async Task<string> RawAsync(string ip, string json, CancellationToken cancellationToken)
{
    // Govee replies land on the fixed local port 4002, not the source port
    // of the request, so listen there
    using var udp = new UdpClient(AddressFamily.InterNetwork);
    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    udp.Client.Bind(new IPEndPoint(IPAddress.Any, GoveeConstants.ListenPort));
    var endpoint = new IPEndPoint(IPAddress.Parse(ip), GoveeConstants.CommandPort);
    await udp.SendAsync(Encoding.UTF8.GetBytes(json), endpoint, cancellationToken);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(2));
    try
    {
        UdpReceiveResult reply = await udp.ReceiveAsync(timeout.Token);
        return Encoding.UTF8.GetString(reply.Buffer);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return "(no reply within 2 s)";
    }
}

static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
{
    Task<string?> read = Task.Run(Console.ReadLine, cancellationToken);
    return await read.WaitAsync(cancellationToken);
}

static PluginOutputMappingConfig Mapping(string ip) =>
    new()
    {
        DestinationAddress = ip,
        ChannelOffset = 0,
        UniverseId = 1,
    };

static void PrintHelp()
{
    Console.WriteLine("""
        Commands (output protocol, same path as the Core's Outputs page):
          discover                      multicast-discover Govee devices
          send <ip> r g b               GOVEE_COLOR (0-255)
          sendwhite <ip> dim ct         GOVEE_WHITE_CT (kelvin mode, ct 0=warm 255=cool)
          senddim <ip> dim              GOVEE_WHITE (brightness only)
          sendmode <proto> <ip> ch...   any protocol id with a raw slice
          fade <ip> [seconds]           red 0→255→0 ramp at 10 updates/s (one session)
          rt <ip> [segments]            realtime (razer) demo: snaps, 5 Hz strobe, segment chase
          status <ip>                   print devStatus
          raw <ip> <json>               send any JSON and print the reply
          r                             shutdown + initialize again (no assembly unload)
          i                             show device info
          d                             dump registered protocols / profiles
          q                             quit
        """);
}
