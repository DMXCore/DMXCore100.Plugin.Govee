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
    private readonly TimeSpan refreshInterval;
    private GoveeUpdate? lastSent;
    private long lastFullSendAt;

    /// <param name="refreshInterval">
    /// How old the last full-state send must be before an unchanged update is
    /// answered with the full state again; defaults to
    /// <see cref="GoveeConstants.RefreshIntervalMs"/>.
    /// </param>
    public GoveeSession(
        GoveeMode mode,
        IPEndPoint endpoint,
        GoveeDatagramSender? sender,
        TimeSpan? refreshInterval = null)
    {
        this.mode = mode;
        this.endpoint = endpoint;
        this.io = new GoveeSessionIo(endpoint, sender);
        this.refreshInterval = refreshInterval ?? TimeSpan.FromMilliseconds(GoveeConstants.RefreshIntervalMs);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        if (channelValues.Length < this.mode.ChannelCount)
        {
            return false;
        }

        GoveeUpdate update = this.mode.ToUpdate(channelValues.Span);

        // Normally only what changed goes out. The host's idle refresh
        // re-delivers an unchanged look; once the last full send is old
        // enough, answer it with the full state so a device changed behind
        // our back (Govee app, scene) is put back. Time-gated because
        // distinct channel values can quantize to the same update during a
        // slow fade, and those must not each trigger three datagrams.
        GoveeUpdate? previous = this.lastSent;
        long now = Environment.TickCount64;
        if (update == previous && now - this.lastFullSendAt >= this.refreshInterval.TotalMilliseconds)
        {
            previous = null;
        }

        try
        {
            bool first = true;
            foreach (byte[] datagram in update.DatagramsSince(previous))
            {
                if (!first)
                {
                    // The device drops back-to-back datagrams (see
                    // GoveeConstants.InterCommandGapMs)
                    await Task.Delay(GoveeConstants.InterCommandGapMs, cancellationToken);
                }

                first = false;
                await this.io.Send(this.endpoint, datagram, cancellationToken);
            }

            if (previous == null)
            {
                this.lastFullSendAt = now;
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
