using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.Govee;

/// <summary>
/// One Govee output protocol: a channel slice laid out per its
/// <see cref="GoveeMode"/> becomes turn / brightness / colorwc UDP datagrams
/// to port 4003. The host rate-limits, dedupes, and coalesces latest-wins.
/// </summary>
internal sealed class GoveeProtocol : IPluginOutputProtocol
{
    private readonly GoveeMode mode;
    private readonly GoveeDiscovery discovery;
    private readonly GoveeDatagramSender? sender;

    public GoveeProtocol(GoveeMode mode, GoveeDiscovery discovery, GoveeDatagramSender? sender = null)
    {
        this.mode = mode;
        this.discovery = discovery;
        this.sender = sender;
    }

    public int GetChannelCount(PluginOutputMappingConfig config) => this.mode.ChannelCount;

    public Task<IPluginOutputSession> OpenSessionAsync(
        PluginOutputMappingConfig config,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = GoveeMapping.RequireEndpoint(config);
        return Task.FromResult<IPluginOutputSession>(new GoveeSession(this.mode, endpoint, this.sender));
    }

    public async Task<IReadOnlyList<PluginOutputDestinationOption>?> GetDestinationOptionsAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        // Every Govee LAN device is color-capable, so every protocol offers
        // every discovered device
        IReadOnlyList<GoveeDevice> devices = await this.discovery.GetDevicesAsync(refresh, cancellationToken);
        return devices
            .Select(static device => new PluginOutputDestinationOption(
                device.Ip,
                GoveeDevice.DestinationLabel(device)))
            .ToArray();
    }
}

internal sealed class GoveeSession : IPluginOutputSession
{
    private readonly GoveeMode mode;
    private readonly IPEndPoint endpoint;
    private readonly GoveeSessionIo io;
    private GoveeUpdate? lastSent;

    public GoveeSession(GoveeMode mode, IPEndPoint endpoint, GoveeDatagramSender? sender)
    {
        this.mode = mode;
        this.endpoint = endpoint;
        this.io = new GoveeSessionIo(endpoint, sender);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        if (channelValues.Length < this.mode.ChannelCount)
        {
            return false;
        }

        GoveeUpdate update = this.mode.ToUpdate(channelValues.Span);

        try
        {
            foreach (byte[] datagram in update.DatagramsSince(this.lastSent))
            {
                await this.io.Send(this.endpoint, datagram, cancellationToken);
            }

            this.lastSent = update;
            return true;
        }
        catch (SocketException)
        {
            // Part of the sequence may have gone out; forget the state so the
            // next frame resends everything
            this.lastSent = null;
            return false;
        }
        catch (ObjectDisposedException)
        {
            this.lastSent = null;
            return false;
        }
    }

    public ValueTask DisposeAsync() => this.io.DisposeAsync();
}
