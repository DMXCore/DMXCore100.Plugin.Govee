using System.Net;
using System.Text;
using System.Text.Json;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;

namespace DMXCore100.Govee.Tests;

[TestClass]
public class GoveeRealtimeTests
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

    [TestMethod]
    public void Razer_EnableDisable_AreTheKnownPackets()
    {
        Assert.AreEqual(
            """{"msg":{"cmd":"razer","data":{"pt":"uwABsQEK"}}}""",
            Encoding.UTF8.GetString(GoveeRazer.Enable));
        Assert.AreEqual(
            """{"msg":{"cmd":"razer","data":{"pt":"uwABsQAL"}}}""",
            Encoding.UTF8.GetString(GoveeRazer.Disable));
    }

    [TestMethod]
    public void Razer_Solid_BuildsAValidStreamPacket()
    {
        byte[] packet = Pt(GoveeRazer.Solid(255, 0, 12, 3));

        Assert.AreEqual(0xBB, packet[0]);
        Assert.AreEqual(2 + 9, (packet[1] << 8) | packet[2]);
        Assert.AreEqual(0xB0, packet[3]);
        Assert.AreEqual(0x01, packet[4]);
        Assert.AreEqual(3, packet[5]);
        for (int segment = 0; segment < 3; segment++)
        {
            Assert.AreEqual(255, packet[6 + (segment * 3)]);
            Assert.AreEqual(0, packet[7 + (segment * 3)]);
            Assert.AreEqual(12, packet[8 + (segment * 3)]);
        }

        AssertChecksum(packet);
    }

    [TestMethod]
    public void Razer_Pixels_CopiesTheSliceThrough()
    {
        byte[] packet = Pt(GoveeRazer.Pixels([1, 2, 3, 4, 5, 6], 2));

        Assert.AreEqual(2, packet[5]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, packet[6..12]);
        AssertChecksum(packet);
    }

    [TestMethod]
    public void Razer_ClampSegments()
    {
        Assert.AreEqual(1, GoveeRazer.ClampSegments(0));
        Assert.AreEqual(15, GoveeRazer.ClampSegments(15));
        Assert.AreEqual(170, GoveeRazer.ClampSegments(500));
    }

    [TestMethod]
    public async Task Initialize_RealtimeProtocolsPresentByDefault()
    {
        var (_, host, _) = await CreateInitializedAsync(realtimeEnabled: null);

        Assert.AreEqual(5, host.OutputProtocols.Count);
        Assert.IsTrue(host.OutputProtocols.ContainsKey(GoveePlugin.RealtimeColorProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(GoveePlugin.RealtimePixelProtocolId));
    }

    [TestMethod]
    public async Task Initialize_SettingDisablesRealtimeProtocols()
    {
        var (_, host, _) = await CreateInitializedAsync(realtimeEnabled: false);

        Assert.AreEqual(3, host.OutputProtocols.Count);
        Assert.IsFalse(host.OutputProtocols.ContainsKey(GoveePlugin.RealtimeColorProtocolId));
        Assert.IsFalse(host.OutputProtocols.ContainsKey(GoveePlugin.RealtimePixelProtocolId));
    }

    [TestMethod]
    public async Task Initialize_RealtimeDescriptors()
    {
        var (_, host, _) = await CreateInitializedAsync(realtimeEnabled: true);

        Assert.AreEqual(5, host.OutputProtocols.Count);
        OutputProtocolDescriptor color = host.OutputProtocols[GoveePlugin.RealtimeColorProtocolId].Descriptor;
        Assert.AreEqual("Govee Realtime RGB", color.DisplayName);
        Assert.AreEqual(GoveePlugin.RealtimeMaxUpdatesPerSecond, color.MaxUpdatesPerSecond);
        Assert.AreEqual(GoveePlugin.ColorProfileCode, color.SuggestedProfileCode);
        Assert.AreEqual("RGB", color.SuggestedPersonality);
        Assert.AreEqual(GoveePlugin.SegmentsOptionKey, color.MappingFields.Single().Key);

        OutputProtocolDescriptor pixel = host.OutputProtocols[GoveePlugin.RealtimePixelProtocolId].Descriptor;
        Assert.AreEqual("Govee Realtime Pixel", pixel.DisplayName);
        Assert.IsNull(pixel.SuggestedProfileCode);
        Assert.AreEqual(GoveePlugin.SegmentsOptionKey, pixel.MappingFields.Single().Key);
    }

    [TestMethod]
    public async Task SettingsChange_TogglesRealtimeProtocolsLive()
    {
        var (_, host, _) = await CreateInitializedAsync(realtimeEnabled: null);
        Assert.AreEqual(5, host.OutputProtocols.Count);

        host.SetSetting(GoveePlugin.RealtimeEnabledSettingKey, "false");
        await host.TriggerSettingsChangedAsync();
        Assert.AreEqual(3, host.OutputProtocols.Count);
        Assert.IsFalse(host.OutputProtocols.ContainsKey(GoveePlugin.RealtimeColorProtocolId));

        host.SetSetting(GoveePlugin.RealtimeEnabledSettingKey, "true");
        await host.TriggerSettingsChangedAsync();
        Assert.AreEqual(5, host.OutputProtocols.Count);
    }

    [TestMethod]
    public async Task Discover_PrefillsSegmentsForKnownModels()
    {
        var (_, host, _) = await CreateInitializedAsync(realtimeEnabled: true);
        IPluginOutputProtocol protocol = host.OutputProtocols[GoveePlugin.RealtimeColorProtocolId].Protocol;

        IReadOnlyList<PluginOutputDestinationOption>? options =
            await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.IsNotNull(options);
        Assert.AreEqual("15", options[0].Options?[GoveePlugin.SegmentsOptionKey]);
        Assert.IsNull(GoveeDevice.KnownSegments("H9999"));
    }

    [TestMethod]
    public async Task GetChannelCount_UsesTheSegmentsMappingField()
    {
        var (_, host, _) = await CreateInitializedAsync(realtimeEnabled: true);
        IPluginOutputProtocol color = host.OutputProtocols[GoveePlugin.RealtimeColorProtocolId].Protocol;
        IPluginOutputProtocol pixel = host.OutputProtocols[GoveePlugin.RealtimePixelProtocolId].Protocol;

        Assert.AreEqual(3, color.GetChannelCount(Mapping("192.168.1.30")));
        Assert.AreEqual(3, color.GetChannelCount(Mapping("192.168.1.30", segments: "10")));
        Assert.AreEqual(GoveePlugin.DefaultSegments * 3, pixel.GetChannelCount(Mapping("192.168.1.30")));
        Assert.AreEqual(30, pixel.GetChannelCount(Mapping("192.168.1.30", segments: "10")));
        Assert.AreEqual(510, pixel.GetChannelCount(Mapping("192.168.1.30", segments: "9999")));
        Assert.AreEqual(3, pixel.GetChannelCount(Mapping("192.168.1.30", segments: "0")));
    }

    [TestMethod]
    public async Task Send_ArmsOnceThenStreamsFrames_DisposeDisarms()
    {
        var (_, host, sent) = await CreateInitializedAsync(realtimeEnabled: true);
        IPluginOutputProtocol protocol = host.OutputProtocols[GoveePlugin.RealtimeColorProtocolId].Protocol;
        IPluginOutputSession session = await protocol.OpenSessionAsync(
            Mapping("192.168.1.30", segments: "2"), CancellationToken.None);
        await using (session)
        {
            Assert.IsTrue(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(2, sent.Count);
            CollectionAssert.AreEqual(GoveeRazer.Enable, sent[0].Packet);
            byte[] first = Pt(sent[1].Packet);
            Assert.AreEqual(0xB0, first[3]);
            Assert.AreEqual(2, first[5]);
            Assert.AreEqual(255, first[6]);

            // Streaming: no re-arm on the next frame
            Assert.IsTrue(await session.SendAsync(new byte[] { 0, 0, 255 }, CancellationToken.None));
            Assert.AreEqual(3, sent.Count);
            Assert.AreEqual(255, Pt(sent[2].Packet)[8]);

            // Blackout stays in razer mode: an all-zero frame, not a turn command
            Assert.IsTrue(await session.SendAsync(new byte[] { 0, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(4, sent.Count);
            byte[] black = Pt(sent[3].Packet);
            Assert.AreEqual(0xB0, black[3]);
            Assert.IsTrue(black[6..12].All(static value => value == 0));
        }

        Assert.AreEqual(5, sent.Count);
        CollectionAssert.AreEqual(GoveeRazer.Disable, sent[4].Packet);
        Assert.IsTrue(sent.All(item => item.Endpoint.Port == GoveeConstants.CommandPort));
    }

    [TestMethod]
    public async Task SendPixel_StreamsThePerSegmentSlice()
    {
        var (_, host, sent) = await CreateInitializedAsync(realtimeEnabled: true);
        IPluginOutputProtocol protocol = host.OutputProtocols[GoveePlugin.RealtimePixelProtocolId].Protocol;
        IPluginOutputSession session = await protocol.OpenSessionAsync(
            Mapping("192.168.1.30", segments: "2"), CancellationToken.None);
        await using (session)
        {
            Assert.IsTrue(await session.SendAsync(new byte[] { 1, 2, 3, 4, 5, 6 }, CancellationToken.None));
            byte[] frame = Pt(sent[1].Packet);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, frame[6..12]);

            // Short slice rejected without sending
            Assert.IsFalse(await session.SendAsync(new byte[] { 1, 2, 3 }, CancellationToken.None));
        }

        Assert.AreEqual(3, sent.Count);
    }

    [TestMethod]
    public async Task Send_SocketFailureRearmsOnTheNextFrame()
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
        host.SetSetting(GoveePlugin.RealtimeEnabledSettingKey, "true");
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = host.OutputProtocols[GoveePlugin.RealtimeColorProtocolId].Protocol;
        IPluginOutputSession session = await protocol.OpenSessionAsync(Mapping("192.168.1.30"), CancellationToken.None);
        await using (session)
        {
            Assert.IsFalse(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));

            // The enable may not have reached the device: the retry re-arms
            Assert.IsTrue(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));
            Assert.AreEqual(2, sent.Count);
            CollectionAssert.AreEqual(GoveeRazer.Enable, sent[0]);
        }
    }

    private async Task<(GoveePlugin Plugin, TestPluginHost Host, List<(IPEndPoint Endpoint, byte[] Packet)> Sent)> CreateInitializedAsync(
        bool? realtimeEnabled)
    {
        var sent = new List<(IPEndPoint, byte[])>();
        var plugin = new GoveePlugin(
            (_, _) => Task.FromResult<IReadOnlyList<GoveeDevice>>(
                [new GoveeDevice("1F:80:C5:32:32:36:72:4E", "192.168.1.30") { Sku = "H618A" }]),
            (endpoint, packet, _) =>
            {
                sent.Add((endpoint, packet.ToArray()));
                return ValueTask.CompletedTask;
            });
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        if (realtimeEnabled is bool enabled)
        {
            host.SetSetting(GoveePlugin.RealtimeEnabledSettingKey, enabled ? "true" : "false");
        }

        await plugin.InitializeAsync(host, CancellationToken.None);
        return (plugin, host, sent);
    }

    private static PluginOutputMappingConfig Mapping(string ip, string? segments = null) =>
        new()
        {
            DestinationAddress = ip,
            ChannelOffset = 0,
            UniverseId = 1,
            Options = segments == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [GoveePlugin.SegmentsOptionKey] = segments },
        };

    /// <summary>
    /// Unwrap a razer datagram: parse the JSON, base64-decode data.pt.
    /// </summary>
    private static byte[] Pt(byte[] datagram)
    {
        using JsonDocument document = JsonDocument.Parse(datagram);
        JsonElement message = document.RootElement.GetProperty("msg");
        Assert.AreEqual("razer", message.GetProperty("cmd").GetString());
        return Convert.FromBase64String(message.GetProperty("data").GetProperty("pt").GetString()!);
    }

    private static void AssertChecksum(byte[] packet)
    {
        byte checksum = 0;
        for (int i = 0; i < packet.Length - 1; i++)
        {
            checksum ^= packet[i];
        }

        Assert.AreEqual(checksum, packet[^1]);
    }
}
