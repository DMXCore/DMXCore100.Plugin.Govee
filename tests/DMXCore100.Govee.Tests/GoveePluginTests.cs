using System.Net;
using System.Text.Json;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;

namespace DMXCore100.Govee.Tests;

[TestClass]
public class GoveePluginTests
{
    private readonly List<GoveePlugin> plugins = [];

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (GoveePlugin plugin in this.plugins)
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }

        this.plugins.Clear();
    }

    private async Task<(GoveePlugin Plugin, TestPluginHost Host, List<(IPEndPoint Endpoint, byte[] Packet)> Sent)> CreateInitializedAsync(
        IReadOnlyList<GoveeDevice>? discovered = null)
    {
        var sent = new List<(IPEndPoint, byte[])>();
        GoveeDevice[] devices = discovered?.ToArray() ??
        [
            new GoveeDevice("1F:80:C5:32:32:36:72:4E", "192.168.1.30") { Sku = "H618A" },
        ];

        var plugin = new GoveePlugin(
            (_, _) => Task.FromResult<IReadOnlyList<GoveeDevice>>(devices),
            (endpoint, packet, _) =>
            {
                sent.Add((endpoint, packet.ToArray()));
                return ValueTask.CompletedTask;
            });
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        return (plugin, host, sent);
    }

    [TestMethod]
    public void Info_ComesFromTheProjectFile()
    {
        var plugin = new GoveePlugin();

        Assert.AreEqual("govee", plugin.Info.Id);
        Assert.AreEqual("Govee", plugin.Info.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(plugin.Info.Version));
        // The declared floor is what the manifest carries; a newer SDK at
        // build time must not raise it
        Assert.AreEqual(new Version(1, 13), Version.Parse(PluginBuildInfo.MinSdkVersion));
    }

    [TestMethod]
    public async Task Initialize_RegistersProtocolsAndProfiles()
    {
        var (_, host, _) = await CreateInitializedAsync();

        Assert.IsTrue(host.OutputProtocols.ContainsKey(GoveePlugin.ColorProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(GoveePlugin.WhiteCtProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(GoveePlugin.WhiteProtocolId));
        // Plus the two realtime protocols, on by default (GoveeRealtimeTests)
        Assert.AreEqual(5, host.OutputProtocols.Count);
        Assert.IsTrue(host.FixtureProfiles.ContainsKey(GoveePlugin.ColorProfileCode));
        Assert.IsTrue(host.FixtureProfiles.ContainsKey(GoveePlugin.WhiteProfileCode));
        Assert.AreEqual(true, host.ConnectionState);
        Assert.AreEqual("Govee output ready", host.ConnectionDetail);

        OutputProtocolDescriptor color = host.OutputProtocols[GoveePlugin.ColorProtocolId].Descriptor;
        Assert.AreEqual(GoveePlugin.PortType, color.PortType);
        Assert.AreEqual("Govee", color.PortTypeDisplayName);
        Assert.AreEqual(GoveeConstants.MaxUpdatesPerSecond, color.MaxUpdatesPerSecond);
        foreach (var registered in host.OutputProtocols.Values)
        {
            Assert.AreEqual(
                TimeSpan.FromMilliseconds(GoveeConstants.RefreshIntervalMs),
                registered.Descriptor.RefreshInterval,
                $"{registered.Descriptor.Id} must ask the host for an idle refresh");
        }

        Assert.IsTrue(color.SupportsDestinationDiscovery);
        Assert.AreEqual(GoveePlugin.ColorProfileCode, color.SuggestedProfileCode);
        Assert.AreEqual("Govee Color RGB", color.DisplayName);
        Assert.AreEqual("RGB", color.SuggestedPersonality);
        Assert.AreEqual("Dimmer+CT", host.OutputProtocols[GoveePlugin.WhiteCtProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual("Dimmer", host.OutputProtocols[GoveePlugin.WhiteProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual(GoveePlugin.WhiteProfileCode, host.OutputProtocols[GoveePlugin.WhiteCtProtocolId].Descriptor.SuggestedProfileCode);

        PluginFixtureProfileDescriptor colorProfile = host.FixtureProfiles[GoveePlugin.ColorProfileCode];
        Assert.AreEqual("Govee", colorProfile.Manufacturer);
        CollectionAssert.AreEqual(
            new[] { "RGB" },
            colorProfile.Personalities.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue },
            colorProfile.Personalities[0].Channels.ToArray());

        PluginFixtureProfileDescriptor whiteProfile = host.FixtureProfiles[GoveePlugin.WhiteProfileCode];
        CollectionAssert.AreEqual(
            new[] { "Dimmer+CT", "Dimmer" },
            whiteProfile.Personalities.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { PluginFixtureFunction.Intensity, PluginFixtureFunction.ColorTemperature },
            whiteProfile.Personalities[0].Channels.ToArray());
        CollectionAssert.AreEqual(
            new[] { PluginFixtureFunction.Intensity },
            whiteProfile.Personalities[1].Channels.ToArray());
    }

    [TestMethod]
    public async Task Initialize_EveryProtocolHasAMatchingPersonality()
    {
        var (_, host, _) = await CreateInitializedAsync();
        PluginOutputMappingConfig config = Mapping("192.168.1.30");

        foreach (GoveeMode mode in GoveeMode.All)
        {
            OutputProtocolDescriptor descriptor = host.OutputProtocols[mode.ProtocolId].Descriptor;
            PluginFixtureProfileDescriptor profile = host.FixtureProfiles[descriptor.SuggestedProfileCode!];
            PluginFixturePersonality? personality = profile.Personalities
                .SingleOrDefault(p => p.Name == descriptor.SuggestedPersonality);
            Assert.IsNotNull(personality, mode.ProtocolId);

            // The personality footprint and the protocol channel count must
            // agree or the patch and the mapping drift apart
            Assert.AreEqual(
                personality.Channels.Count,
                host.OutputProtocols[mode.ProtocolId].Protocol.GetChannelCount(config),
                mode.ProtocolId);
        }
    }

    [TestMethod]
    public async Task GetChannelCount_MatchesPersonality()
    {
        var (_, host, _) = await CreateInitializedAsync();
        PluginOutputMappingConfig config = Mapping("192.168.1.30");

        Assert.AreEqual(3, Protocol(host, GoveePlugin.ColorProtocolId).GetChannelCount(config));
        Assert.AreEqual(2, Protocol(host, GoveePlugin.WhiteCtProtocolId).GetChannelCount(config));
        Assert.AreEqual(1, Protocol(host, GoveePlugin.WhiteProtocolId).GetChannelCount(config));
    }

    [TestMethod]
    public async Task SendRgb_WritesTurnBrightnessColorToPort4003()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            GoveePlugin.ColorProtocolId,
            Mapping("192.168.1.30"),
            [255, 0, 0]);

        Assert.IsTrue(ok);
        Assert.AreEqual(3, sent.Count);
        Assert.IsTrue(sent.All(item => item.Endpoint.Port == GoveeConstants.CommandPort));
        Assert.IsTrue(sent.All(item => item.Endpoint.Address.ToString() == "192.168.1.30"));

        Assert.AreEqual(1, Data(sent[0].Packet, "turn").GetProperty("value").GetInt32());
        Assert.AreEqual(100, Data(sent[1].Packet, "brightness").GetProperty("value").GetInt32());
        JsonElement color = Data(sent[2].Packet, "colorwc");
        Assert.AreEqual(255, color.GetProperty("color").GetProperty("r").GetInt32());
        Assert.AreEqual(0, color.GetProperty("color").GetProperty("g").GetInt32());
        Assert.AreEqual(0, color.GetProperty("color").GetProperty("b").GetInt32());
        Assert.AreEqual(0, color.GetProperty("colorTemInKelvin").GetInt32());
    }

    [TestMethod]
    public async Task SendRgb_BlackTurnsTheDeviceOff()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            GoveePlugin.ColorProtocolId,
            Mapping("192.168.1.30"),
            [0, 0, 0]);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, sent.Count);
        Assert.AreEqual(0, Data(sent[0].Packet, "turn").GetProperty("value").GetInt32());
    }

    [TestMethod]
    public async Task SendRgb_HalfLevelGoesToBrightness()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            GoveePlugin.ColorProtocolId,
            Mapping("192.168.1.30"),
            [128, 64, 0]);

        Assert.IsTrue(ok);
        Assert.AreEqual(51, Data(sent[1].Packet, "brightness").GetProperty("value").GetInt32());
        JsonElement color = Data(sent[2].Packet, "colorwc").GetProperty("color");
        Assert.AreEqual(251, color.GetProperty("r").GetInt32());
        Assert.AreEqual(125, color.GetProperty("g").GetInt32());
        Assert.AreEqual(0, color.GetProperty("b").GetInt32());
    }

    [TestMethod]
    public async Task SendWhiteCt_SendsKelvinAndBrightness()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            GoveePlugin.WhiteCtProtocolId,
            Mapping("192.168.1.30"),
            [128, 255]);

        Assert.IsTrue(ok);
        Assert.AreEqual(3, sent.Count);
        Assert.AreEqual(51, Data(sent[1].Packet, "brightness").GetProperty("value").GetInt32());
        JsonElement color = Data(sent[2].Packet, "colorwc");
        Assert.AreEqual(GoveeConstants.KelvinMax, color.GetProperty("colorTemInKelvin").GetInt32());
        Assert.AreEqual(0, color.GetProperty("color").GetProperty("r").GetInt32());
    }

    [TestMethod]
    public async Task SendDimmer_SendsBrightnessOnly_ZeroIsOff()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        Assert.IsTrue(await host.SimulateOutputDeliveryAsync(GoveePlugin.WhiteProtocolId, Mapping("192.168.1.30"), [255]));
        Assert.IsTrue(await host.SimulateOutputDeliveryAsync(GoveePlugin.WhiteProtocolId, Mapping("192.168.1.30"), [0]));

        Assert.AreEqual(3, sent.Count);
        Assert.AreEqual(1, Data(sent[0].Packet, "turn").GetProperty("value").GetInt32());
        Assert.AreEqual(100, Data(sent[1].Packet, "brightness").GetProperty("value").GetInt32());
        Assert.AreEqual(0, Data(sent[2].Packet, "turn").GetProperty("value").GetInt32());
    }

    [TestMethod]
    public async Task Session_ResendsOnlyChangesAcrossFrames()
    {
        var (_, host, sent) = await CreateInitializedAsync();
        IPluginOutputProtocol protocol = Protocol(host, GoveePlugin.ColorProtocolId);
        IPluginOutputSession session = await protocol.OpenSessionAsync(Mapping("192.168.1.30"), CancellationToken.None);
        await using (session)
        {
            // Full state on the first frame
            Assert.IsTrue(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(3, sent.Count);

            // Same hue, lower level: no turn resend. 128/255 rounds the
            // normalized red to 251, so brightness AND colorwc go out; a
            // level whose normalization is exact (255→~50%) would send
            // brightness alone (covered in GoveeUpdateTests)
            Assert.IsTrue(await session.SendAsync(new byte[] { 128, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(5, sent.Count);
            Assert.AreEqual(51, Data(sent[3].Packet, "brightness").GetProperty("value").GetInt32());
            Assert.AreEqual(251, Data(sent[4].Packet, "colorwc").GetProperty("color").GetProperty("r").GetInt32());

            // Identical frame: nothing on the wire
            Assert.IsTrue(await session.SendAsync(new byte[] { 128, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(5, sent.Count);

            // Blackout, then back: turn 0, then the full state again
            Assert.IsTrue(await session.SendAsync(new byte[] { 0, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(6, sent.Count);
            Assert.AreEqual(0, Data(sent[5].Packet, "turn").GetProperty("value").GetInt32());
            Assert.IsTrue(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(9, sent.Count);
            Assert.AreEqual(1, Data(sent[6].Packet, "turn").GetProperty("value").GetInt32());
        }
    }

    [TestMethod]
    public async Task Send_RejectsShortSliceForEveryMode()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        foreach (GoveeMode mode in GoveeMode.All)
        {
            bool ok = await host.SimulateOutputDeliveryAsync(
                mode.ProtocolId,
                Mapping("192.168.1.30"),
                new byte[mode.ChannelCount - 1]);
            Assert.IsFalse(ok, mode.ProtocolId);
        }

        Assert.AreEqual(0, sent.Count);
    }

    [TestMethod]
    public async Task Send_ReportsFailureWhenTheSocketFails_AndResendsFullStateAfter()
    {
        int failures = 1;
        var sent = new List<byte[]>();
        var plugin = new GoveePlugin(
            (_, _) => Task.FromResult<IReadOnlyList<GoveeDevice>>([]),
            (_, packet, _) =>
            {
                if (failures > 0)
                {
                    failures--;
                    throw new System.Net.Sockets.SocketException();
                }

                sent.Add(packet.ToArray());
                return ValueTask.CompletedTask;
            });
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, GoveePlugin.ColorProtocolId);
        IPluginOutputSession session = await protocol.OpenSessionAsync(Mapping("192.168.1.30"), CancellationToken.None);
        await using (session)
        {
            Assert.IsFalse(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));

            // The failed frame left the device state unknown: the retry must
            // resend everything, not just the delta
            Assert.IsTrue(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(3, sent.Count);
        }
    }

    [TestMethod]
    public async Task OpenSession_RejectsMissingOrInvalidAddress()
    {
        var (_, host, _) = await CreateInitializedAsync();
        IPluginOutputProtocol protocol = Protocol(host, GoveePlugin.ColorProtocolId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.OpenSessionAsync(Mapping(""), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.OpenSessionAsync(Mapping("led-strip"), CancellationToken.None));
    }

    [TestMethod]
    public async Task Discover_OffersEveryDeviceOnEveryProtocol()
    {
        GoveeDevice[] devices =
        [
            new GoveeDevice("1F:80:C5:32:32:36:72:4E", "192.168.1.30") { Sku = "H618A" },
            new GoveeDevice("2A:00:11:22:33:44:55:66", "192.168.1.31"),
        ];
        var (_, host, _) = await CreateInitializedAsync(devices);

        IReadOnlyList<PluginOutputDestinationOption>? color =
            await Protocol(host, GoveePlugin.ColorProtocolId)
                .GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        IReadOnlyList<PluginOutputDestinationOption>? white =
            await Protocol(host, GoveePlugin.WhiteCtProtocolId)
                .GetDestinationOptionsAsync(refresh: false, CancellationToken.None);

        Assert.IsNotNull(color);
        CollectionAssert.AreEqual(new[] { "192.168.1.30", "192.168.1.31" }, color.Select(o => o.Value).ToArray());
        Assert.AreEqual("Govee H618A (192.168.1.30, 1F:80:C5:32:32:36:72:4E)", color[0].Label);
        Assert.AreEqual("Govee (192.168.1.31, 2A:00:11:22:33:44:55:66)", color[1].Label);

        Assert.IsNotNull(white);
        CollectionAssert.AreEqual(color.Select(o => o.Value).ToArray(), white.Select(o => o.Value).ToArray());
    }

    [TestMethod]
    public async Task Discover_UsesCacheUntilRefresh()
    {
        int calls = 0;
        GoveeDevice strip = new("1F:80:C5:32:32:36:72:4E", "192.168.1.30") { Sku = "H618A" };
        var plugin = new GoveePlugin(
            (_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<GoveeDevice>>([strip]);
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, GoveePlugin.ColorProtocolId);

        _ = await protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);
        _ = await protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        // The cache is shared by every protocol of the plugin
        _ = await Protocol(host, GoveePlugin.WhiteProtocolId).GetDestinationOptionsAsync(refresh: false, CancellationToken.None);

        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Discover_ConcurrentRefresh_SharesInFlightScan()
    {
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GoveeDevice strip = new("1F:80:C5:32:32:36:72:4E", "192.168.1.30") { Sku = "H618A" };
        var plugin = new GoveePlugin(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return (IReadOnlyList<GoveeDevice>)[strip];
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, GoveePlugin.ColorProtocolId);

        Task<IReadOnlyList<PluginOutputDestinationOption>?> first =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        await started.Task;
        Task<IReadOnlyList<PluginOutputDestinationOption>?> second =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        release.SetResult();

        IReadOnlyList<PluginOutputDestinationOption>?[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, results[0]!.Count);
        Assert.AreEqual(1, results[1]!.Count);
        Assert.AreEqual("192.168.1.30", results[0]![0].Value);
    }

    [TestMethod]
    public async Task Discover_InitiatorCancel_DoesNotFailOtherRefresh()
    {
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken scanToken = CancellationToken.None;
        GoveeDevice strip = new("1F:80:C5:32:32:36:72:4E", "192.168.1.30") { Sku = "H618A" };
        var plugin = new GoveePlugin(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                scanToken = cancellationToken;
                started.TrySetResult();
                await release.Task;
                return (IReadOnlyList<GoveeDevice>)[strip];
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, GoveePlugin.ColorProtocolId);

        using var firstCts = new CancellationTokenSource();
        Task<IReadOnlyList<PluginOutputDestinationOption>?> first =
            protocol.GetDestinationOptionsAsync(refresh: true, firstCts.Token);
        await started.Task;
        Task<IReadOnlyList<PluginOutputDestinationOption>?> second =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        firstCts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => first);
        Assert.IsFalse(scanToken.IsCancellationRequested);

        release.SetResult();
        IReadOnlyList<PluginOutputDestinationOption>? options = await second;

        Assert.AreEqual(1, calls);
        Assert.IsNotNull(options);
        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("192.168.1.30", options[0].Value);
    }

    [TestMethod]
    public async Task Discover_ScanFailure_SurfacesAndNextRefreshRetries()
    {
        int calls = 0;
        var plugin = new GoveePlugin(
            (_, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<IReadOnlyList<GoveeDevice>>(new InvalidOperationException("no network"))
                    : Task.FromResult<IReadOnlyList<GoveeDevice>>([new GoveeDevice("AA:BB", "192.168.1.30")]);
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, GoveePlugin.ColorProtocolId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None));
        IReadOnlyList<PluginOutputDestinationOption>? options =
            await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.IsNotNull(options);
        Assert.AreEqual(1, options.Count);
    }

    [TestMethod]
    public async Task Shutdown_UnregistersEverything()
    {
        var (plugin, host, _) = await CreateInitializedAsync();

        await plugin.ShutdownAsync(CancellationToken.None);

        Assert.AreEqual(0, host.OutputProtocols.Count);
        Assert.AreEqual(0, host.FixtureProfiles.Count);
    }

    private static IPluginOutputProtocol Protocol(TestPluginHost host, string id) =>
        host.OutputProtocols[id].Protocol;

    private static PluginOutputMappingConfig Mapping(string ip) =>
        new()
        {
            DestinationAddress = ip,
            ChannelOffset = 0,
            UniverseId = 1,
        };

    private static JsonElement Data(byte[] datagram, string expectedCmd)
    {
        using JsonDocument document = JsonDocument.Parse(datagram);
        JsonElement message = document.RootElement.GetProperty("msg");
        Assert.AreEqual(expectedCmd, message.GetProperty("cmd").GetString());
        return message.GetProperty("data").Clone();
    }
}
