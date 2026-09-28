using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.Govee;

/// <summary>
/// A realtime (razer-mode) output protocol: every frame is ONE streamed
/// packet the device renders instantly, bypassing the firmware fade of the
/// documented LAN commands. <c>Pixel</c> false drives the whole device as
/// one RGB zone; true exposes 3 channels per segment. The segment count
/// comes from the mapping's <c>segments</c> field.
/// </summary>
internal sealed class GoveeRealtimeProtocol : IPluginOutputProtocol
{
    private readonly bool pixel;
    private readonly GoveeDiscovery discovery;
    private readonly GoveeDatagramSender? sender;

    public GoveeRealtimeProtocol(bool pixel, GoveeDiscovery discovery, GoveeDatagramSender? sender = null)
    {
        this.pixel = pixel;
        this.discovery = discovery;
        this.sender = sender;
    }

    public static int Segments(PluginOutputMappingConfig config)
    {
        return GoveeRazer.ClampSegments(
            config.Options.TryGetValue(GoveePlugin.SegmentsOptionKey, out string? raw)
            && int.TryParse(raw, out int segments)
                ? segments
                : GoveePlugin.DefaultSegments);
    }

    public int GetChannelCount(PluginOutputMappingConfig config) =>
        this.pixel ? Segments(config) * 3 : 3;

    public Task<IPluginOutputSession> OpenSessionAsync(
        PluginOutputMappingConfig config,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = GoveeMapping.RequireEndpoint(config);
        return Task.FromResult<IPluginOutputSession>(
            new GoveeRealtimeSession(this.pixel, Segments(config), endpoint, this.sender));
    }

    public async Task<IReadOnlyList<PluginOutputDestinationOption>?> GetDestinationOptionsAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GoveeDevice> devices = await this.discovery.GetDevicesAsync(refresh, cancellationToken);
        return devices
            .Select(static device => new PluginOutputDestinationOption(
                device.Ip,
                GoveeDevice.DestinationLabel(device))
            {
                // Prefill the Segments mapping field for models we know
                Options = GoveeDevice.KnownSegments(device.Sku) is int segments
                    ? new Dictionary<string, string> { [GoveePlugin.SegmentsOptionKey] = segments.ToString() }
                    : null,
            })
            .ToArray();
    }
}

internal sealed class GoveeRealtimeSession : IPluginOutputSession
{
    /// <summary>
    /// Re-send the razer enable before the next frame after this much idle
    /// time — the device can drop back to normal mode on its own.
    /// </summary>
    private static readonly TimeSpan ReArmAfter = TimeSpan.FromSeconds(10);

    private readonly bool pixel;
    private readonly int segments;
    private readonly IPEndPoint endpoint;
    private readonly GoveeSessionIo io;
    private readonly TimeSpan refreshInterval;
    private long lastSentAt;
    private long lastArmedAt;
    private byte[]? lastFrame;
    private bool armed;

    /// <param name="refreshInterval">
    /// How old the last razer enable must be before an unchanged frame (the
    /// host's idle refresh) re-arms; defaults to
    /// <see cref="GoveeConstants.RefreshIntervalMs"/>.
    /// </param>
    public GoveeRealtimeSession(
        bool pixel,
        int segments,
        IPEndPoint endpoint,
        GoveeDatagramSender? sender,
        TimeSpan? refreshInterval = null)
    {
        this.pixel = pixel;
        this.segments = segments;
        this.endpoint = endpoint;
        this.io = new GoveeSessionIo(endpoint, sender);
        this.refreshInterval = refreshInterval ?? TimeSpan.FromMilliseconds(GoveeConstants.RefreshIntervalMs);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        if (channelValues.Length < (this.pixel ? this.segments * 3 : 3))
        {
            return false;
        }

        ReadOnlySpan<byte> channels = channelValues.Span;
        byte[] frame = this.pixel
            ? GoveeRazer.Pixels(channels, this.segments)
            : GoveeRazer.Solid(channels[0], channels[1], channels[2], this.segments);
        long now = Environment.TickCount64;

        // An unchanged frame is the host's idle refresh. The Govee app (or a
        // scene) drops the device out of razer mode without telling us, and
        // frames are ignored until it is re-armed, so a refresh re-arms once
        // the last enable is old enough. Active streaming never pays the gap.
        bool refresh = this.lastFrame != null && frame.AsSpan().SequenceEqual(this.lastFrame);

        try
        {
            if (!this.armed
                || now - this.lastSentAt > ReArmAfter.TotalMilliseconds
                || (refresh && now - this.lastArmedAt >= this.refreshInterval.TotalMilliseconds))
            {
                await this.io.Send(this.endpoint, GoveeRazer.Enable, cancellationToken);
                // The device drops a datagram arriving back-to-back with the
                // previous one (see GoveeConstants.InterCommandGapMs)
                await Task.Delay(GoveeConstants.InterCommandGapMs, cancellationToken);
                this.armed = true;
                this.lastArmedAt = now;
            }

            await this.io.Send(this.endpoint, frame, cancellationToken);
            this.lastSentAt = Environment.TickCount64;
            this.lastFrame = frame;
            return true;
        }
        catch (SocketException)
        {
            this.armed = false;
            return false;
        }
        catch (ObjectDisposedException)
        {
            this.armed = false;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.armed)
        {
            // Best effort: hand the device back to normal rendering
            try
            {
                await this.io.Send(this.endpoint, GoveeRazer.Disable, CancellationToken.None);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await this.io.DisposeAsync();
    }
}
