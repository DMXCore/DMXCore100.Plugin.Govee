using DMXCore.PluginSdk;

namespace DMXCore100.Govee;

/// <summary>
/// Govee output plugin: registers color and white output protocols plus the
/// matching fixture profiles so Govee lights can be mapped on the Outputs
/// page and patched like any other fixture. Updates are JSON datagrams
/// (turn / brightness / colorwc) to UDP 4003 per the Govee LAN API; the
/// host rate-limits, dedupes, and coalesces latest-wins.
/// </summary>
public class GoveePlugin : IPlugin
{
    public const string ColorProtocolId = "GOVEE_COLOR";
    public const string WhiteCtProtocolId = "GOVEE_WHITE_CT";
    public const string WhiteProtocolId = "GOVEE_WHITE";
    public const string RealtimeColorProtocolId = "GOVEE_RT_COLOR";
    public const string RealtimePixelProtocolId = "GOVEE_RT_PIXEL";
    public const string ColorProfileCode = "GOVEE_COLOR";
    public const string WhiteProfileCode = "GOVEE_WHITE";
    public const string PortType = "GOVEE";
    public const string RealtimeEnabledSettingKey = "realtime-enabled";
    public const string SegmentsOptionKey = "segments";
    public const int DefaultSegments = 15;

    /// <summary>
    /// Razer-mode streaming rate: one datagram per frame, and the H618A
    /// accepts datagrams 5 ms apart (measured) with a chase still fluid at
    /// 60 Hz — 40 matches the engine's default output frequency with margin.
    /// </summary>
    public const int RealtimeMaxUpdatesPerSecond = 40;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(GoveeConstants.RefreshIntervalMs);

    private readonly List<IDisposable> registrations = [];
    private readonly List<IDisposable> realtimeRegistrations = [];
    private readonly object realtimeGate = new();
    private readonly GoveeDiscoverFunc? discoverOverride;
    private readonly GoveeDatagramSender? sendOverride;
    private IPluginHost? host;
    private GoveeDiscovery? discovery;

    public GoveePlugin()
        : this(null, null)
    {
    }

    internal GoveePlugin(GoveeDiscoverFunc? discoverOverride, GoveeDatagramSender? sendOverride)
    {
        this.discoverOverride = discoverOverride;
        this.sendOverride = sendOverride;
        Info = new()
        {
            // Id/Name/Version come from the csproj (PluginId,
            // PluginDisplayName, Version) via the SDK-generated
            // PluginBuildInfo, always in sync with the generated manifest.json
            Id = PluginBuildInfo.Id,
            Name = PluginBuildInfo.Name,
            Version = PluginBuildInfo.Version,
            Description = "Drives Govee WiFi lights from DMX over the Govee LAN API (LAN Control must be enabled per device in the Govee Home app).",
            Settings =
            [
                new()
                {
                    Key = RealtimeEnabledSettingKey,
                    Label = "Realtime protocols",
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    Description = "The Realtime output protocols stream colors over the "
                        + "reverse-engineered razer/DreamView mode: instant changes with no firmware "
                        + "fade, and per-segment control on RGBIC devices — the recommended way to "
                        + "drive Govee from cues and effects. Not part of Govee's documented LAN "
                        + "API (verified on the H618A); turn this off if your device ignores them "
                        + "or misbehaves and use the standard protocols instead.",
                },
            ],
        };
    }

    public PluginInfo Info { get; }

    public Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        var discovery = new GoveeDiscovery(this.discoverOverride);

        // One profile personality per protocol, same name, so the fixture
        // editor can prefill the personality from a mapping
        this.registrations.Add(host.Outputs.RegisterFixtureProfile(Profile(
            ColorProfileCode,
            "Color Light",
            [GoveeMode.Rgb])));
        this.registrations.Add(host.Outputs.RegisterFixtureProfile(Profile(
            WhiteProfileCode,
            "White Light",
            [GoveeMode.TunableWhite, GoveeMode.Dimmer])));

        foreach (GoveeMode mode in GoveeMode.All)
        {
            this.registrations.Add(host.Outputs.RegisterOutputProtocol(
                Descriptor(mode),
                new GoveeProtocol(mode, discovery, this.sendOverride)));
        }

        this.host = host;
        this.discovery = discovery;
        SyncRealtimeRegistrations();
        this.registrations.Add(host.Settings.OnChanged(_ =>
        {
            SyncRealtimeRegistrations();
            return Task.CompletedTask;
        }));

        host.SetConnectionState(true, "Govee output ready");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable registration in this.registrations)
        {
            registration.Dispose();
        }

        this.registrations.Clear();
        lock (this.realtimeGate)
        {
            foreach (IDisposable registration in this.realtimeRegistrations)
            {
                registration.Dispose();
            }

            this.realtimeRegistrations.Clear();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Register or unregister the realtime (razer-mode) protocols to match
    /// the enable setting. Mappings bound to them while disabled degrade to
    /// non-rendering, the same as when a plugin is stopped.
    /// </summary>
    private void SyncRealtimeRegistrations()
    {
        bool enabled = this.host!.Settings.GetBoolean(RealtimeEnabledSettingKey) ?? true;
        lock (this.realtimeGate)
        {
            if (enabled == this.realtimeRegistrations.Count > 0)
            {
                return;
            }

            if (enabled)
            {
                this.realtimeRegistrations.Add(this.host.Outputs.RegisterOutputProtocol(
                    RealtimeDescriptor(
                        RealtimeColorProtocolId,
                        "Govee Realtime RGB",
                        suggestProfile: true),
                    new GoveeRealtimeProtocol(pixel: false, this.discovery!, this.sendOverride)));
                this.realtimeRegistrations.Add(this.host.Outputs.RegisterOutputProtocol(
                    RealtimeDescriptor(
                        RealtimePixelProtocolId,
                        "Govee Realtime Pixel",
                        suggestProfile: false),
                    new GoveeRealtimeProtocol(pixel: true, this.discovery!, this.sendOverride)));
            }
            else
            {
                foreach (IDisposable registration in this.realtimeRegistrations)
                {
                    registration.Dispose();
                }

                this.realtimeRegistrations.Clear();
            }
        }
    }

    private static PluginFixtureProfileDescriptor Profile(string code, string name, IReadOnlyList<GoveeMode> modes) =>
        new()
        {
            Code = code,
            Name = name,
            Manufacturer = "Govee",
            Personalities = modes
                .Select(static mode => new PluginFixturePersonality
                {
                    Name = mode.Personality,
                    Channels = mode.Channels,
                })
                .ToArray(),
        };

    private static OutputProtocolDescriptor RealtimeDescriptor(string id, string displayName, bool suggestProfile) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            PortType = PortType,
            PortTypeDisplayName = "Govee",
            MaxUpdatesPerSecond = RealtimeMaxUpdatesPerSecond,
            RefreshInterval = RefreshInterval,
            SupportsDestinationDiscovery = true,
            SuggestedProfileCode = suggestProfile ? ColorProfileCode : null,
            SuggestedPersonality = suggestProfile ? "RGB" : null,
            MappingFields =
            [
                new()
                {
                    Key = SegmentsOptionKey,
                    Label = "Segments",
                    Type = PluginSettingType.Integer,
                    DefaultValue = "15",
                    Description = "Addressable segments of the device (H618A: 15). The Pixel protocol "
                        + "uses 3 channels per segment.",
                },
            ],
        };

    private static OutputProtocolDescriptor Descriptor(GoveeMode mode) =>
        new()
        {
            Id = mode.ProtocolId,
            DisplayName = mode.DisplayName,
            PortType = PortType,
            PortTypeDisplayName = "Govee",
            MaxUpdatesPerSecond = GoveeConstants.MaxUpdatesPerSecond,
            RefreshInterval = RefreshInterval,
            SupportsDestinationDiscovery = true,
            SuggestedProfileCode = mode.ProfileCode,
            SuggestedPersonality = mode.Personality,
        };
}
