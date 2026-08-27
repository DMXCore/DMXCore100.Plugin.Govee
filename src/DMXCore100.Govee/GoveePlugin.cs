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
    public const string ColorProfileCode = "GOVEE_COLOR";
    public const string WhiteProfileCode = "GOVEE_WHITE";
    public const string PortType = "GOVEE";

    private readonly List<IDisposable> registrations = [];
    private readonly GoveeDiscoverFunc? discoverOverride;
    private readonly GoveeDatagramSender? sendOverride;

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
        return Task.CompletedTask;
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

    private static OutputProtocolDescriptor Descriptor(GoveeMode mode) =>
        new()
        {
            Id = mode.ProtocolId,
            DisplayName = mode.DisplayName,
            PortType = PortType,
            PortTypeDisplayName = "Govee",
            MaxUpdatesPerSecond = GoveeConstants.MaxUpdatesPerSecond,
            SupportsDestinationDiscovery = true,
            SuggestedProfileCode = mode.ProfileCode,
            SuggestedPersonality = mode.Personality,
        };
}
