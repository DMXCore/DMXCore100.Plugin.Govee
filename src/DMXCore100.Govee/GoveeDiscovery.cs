using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace DMXCore100.Govee;

internal delegate Task<IReadOnlyList<GoveeDevice>> GoveeDiscoverFunc(bool refresh, CancellationToken cancellationToken);

internal delegate ValueTask GoveeDatagramSender(
    IPEndPoint endpoint,
    ReadOnlyMemory<byte> packet,
    CancellationToken cancellationToken);

/// <summary>
/// One-shot Govee LAN discovery: multicast a <c>scan</c> probe to
/// 239.255.255.250:4001 on every interface and collect the replies, which
/// the devices send to the fixed local UDP port 4002 (not the source port
/// of the probe). Destination value is the device IP. Concurrent callers
/// share one scan; the last result is cached until the next refresh.
/// </summary>
internal sealed class GoveeDiscovery
{
    private readonly GoveeDiscoverFunc? discoverOverride;
    private readonly object gate = new();
    private IReadOnlyList<GoveeDevice>? cached;
    private Task<IReadOnlyList<GoveeDevice>>? inFlight;

    public GoveeDiscovery(GoveeDiscoverFunc? discoverOverride = null)
    {
        this.discoverOverride = discoverOverride;
    }

    public async Task<IReadOnlyList<GoveeDevice>> GetDevicesAsync(bool refresh, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IReadOnlyList<GoveeDevice>>? owner = null;
        Task<IReadOnlyList<GoveeDevice>> pending;
        lock (this.gate)
        {
            if (!refresh && this.cached != null)
            {
                return this.cached;
            }

            if (this.inFlight != null)
            {
                pending = this.inFlight;
            }
            else
            {
                owner = new TaskCompletionSource<IReadOnlyList<GoveeDevice>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                this.inFlight = owner.Task;
                pending = owner.Task;
            }
        }

        if (owner != null)
        {
            _ = this.RunScanAsync(owner, refresh);
        }

        return await pending.WaitAsync(cancellationToken);
    }

    private async Task RunScanAsync(TaskCompletionSource<IReadOnlyList<GoveeDevice>> owner, bool refresh)
    {
        // The scan runs on its own token: the caller that started it may
        // cancel, but the other callers sharing the scan still want the result
        using var lifetime = new CancellationTokenSource();
        try
        {
            IReadOnlyList<GoveeDevice> devices = this.discoverOverride != null
                ? await this.discoverOverride(refresh, lifetime.Token)
                : await MulticastScanAsync(
                    TimeSpan.FromMilliseconds(GoveeConstants.DiscoveryTimeoutMs),
                    lifetime.Token);
            lock (this.gate)
            {
                this.cached = devices;
            }

            owner.TrySetResult(devices);
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
        }
        finally
        {
            lock (this.gate)
            {
                this.inFlight = null;
            }
        }
    }

    internal static async Task<IReadOnlyList<GoveeDevice>> MulticastScanAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var devices = new Dictionary<string, GoveeDevice>(StringComparer.OrdinalIgnoreCase);
        using UdpClient udp = CreateListener();
        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task listen = ListenAsync(udp, devices, listenCts.Token);

        var group = new IPEndPoint(IPAddress.Parse(GoveeConstants.MulticastAddress), GoveeConstants.ScanPort);
        foreach (IPAddress source in DiscoveryInterfaceAddresses())
        {
            Send(udp, GoveeMessages.Scan, group, source);
        }

        // And once on the OS default multicast route, for interfaces the
        // enumeration missed
        Send(udp, GoveeMessages.Scan, group, null);

        try
        {
            await Task.Delay(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopListen(listenCts, listen);
            throw;
        }

        await StopListen(listenCts, listen);

        lock (devices)
        {
            return devices.Values
                .OrderBy(static device => device.Ip, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static async Task ListenAsync(
        UdpClient udp,
        Dictionary<string, GoveeDevice> devices,
        CancellationToken cancellationToken)
    {
        int consecutiveErrors = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // Windows reports ICMP port-unreachable of an earlier send as
                // a receive failure; keep listening for the real replies, but
                // give up on a socket that keeps failing
                if (++consecutiveErrors >= 10)
                {
                    break;
                }

                continue;
            }

            consecutiveErrors = 0;
            HandleReply(result.Buffer, result.RemoteEndPoint.Address.ToString(), devices);
        }
    }

    internal static void HandleReply(byte[] datagram, string ip, Dictionary<string, GoveeDevice> devices)
    {
        if (!GoveeMessages.TryParse(datagram, out string cmd, out JsonElement data) || cmd != "scan")
        {
            return;
        }

        string? id = GoveeMessages.GetString(data, "device");
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (devices)
        {
            if (!devices.TryGetValue(id, out GoveeDevice? device))
            {
                device = new GoveeDevice(id, ip);
                devices[id] = device;
            }

            // The reply carries the device's own idea of its IP; the UDP
            // source address is what actually reached us, prefer it
            device.Ip = ip;
            string? sku = GoveeMessages.GetString(data, "sku");
            if (!string.IsNullOrWhiteSpace(sku))
            {
                device.Sku = sku;
            }

            string? firmware = GoveeMessages.GetString(data, "wifiVersionSoft");
            if (!string.IsNullOrWhiteSpace(firmware))
            {
                device.WifiVersion = firmware;
            }
        }
    }

    /// <summary>
    /// The IPv4 addresses to multicast the scan from, one per up,
    /// multicast-capable interface, so multi-homed hosts probe every LAN.
    /// </summary>
    internal static IReadOnlyList<IPAddress> DiscoveryInterfaceAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.SupportsMulticast)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses.Distinct().ToArray();
    }

    /// <summary>
    /// The listener must own local port 4002 — Govee devices reply there
    /// regardless of where the probe came from. ReuseAddress lets the scan
    /// coexist with another Govee integration on the same host.
    /// </summary>
    private static UdpClient CreateListener()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, GoveeConstants.ListenPort));
        GoveeSessionIo.IgnoreConnectionReset(client);
        return client;
    }

    private static void Send(UdpClient udp, byte[] packet, IPEndPoint group, IPAddress? interfaceAddress)
    {
        try
        {
            if (interfaceAddress != null)
            {
                udp.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    interfaceAddress.GetAddressBytes());
            }

            udp.Send(packet, packet.Length, group);
        }
        catch (SocketException)
        {
            // An interface that refuses multicast must not abort the scan on
            // the others
        }
    }

    private static async Task StopListen(CancellationTokenSource listenCts, Task listen)
    {
        await listenCts.CancelAsync();
        try
        {
            await listen;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
