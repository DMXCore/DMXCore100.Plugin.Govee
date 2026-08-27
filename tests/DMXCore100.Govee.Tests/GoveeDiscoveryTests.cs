using System.Net;
using System.Text;
using System.Text.Json;

namespace DMXCore100.Govee.Tests;

[TestClass]
public class GoveeDiscoveryTests
{
    [TestMethod]
    public void Scan_IsTheProbeTheGoveeAppSends()
    {
        string json = Encoding.UTF8.GetString(GoveeMessages.Scan);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement message = document.RootElement.GetProperty("msg");
        Assert.AreEqual("scan", message.GetProperty("cmd").GetString());
        Assert.AreEqual("reserve", message.GetProperty("data").GetProperty("account_topic").GetString());
    }

    [TestMethod]
    public void TryParse_ReadsCmdAndData()
    {
        byte[] reply = Encoding.UTF8.GetBytes(
            """{"msg":{"cmd":"scan","data":{"ip":"192.168.1.30","device":"1F:80:C5:32:32:36:72:4E","sku":"H618A","bleVersionHard":"3.01.01","bleVersionSoft":"1.03.01","wifiVersionHard":"1.00.10","wifiVersionSoft":"1.02.11"}}}""");

        Assert.IsTrue(GoveeMessages.TryParse(reply, out string cmd, out JsonElement data));
        Assert.AreEqual("scan", cmd);
        Assert.AreEqual("H618A", GoveeMessages.GetString(data, "sku"));
        Assert.AreEqual("1F:80:C5:32:32:36:72:4E", GoveeMessages.GetString(data, "device"));
    }

    [TestMethod]
    public void TryParse_RejectsGarbage()
    {
        Assert.IsFalse(GoveeMessages.TryParse(Encoding.UTF8.GetBytes("""{"cmd":"scan","data":{}}"""), out _, out _));
        Assert.IsFalse(GoveeMessages.TryParse(Encoding.UTF8.GetBytes("""{"msg":{"cmd":"scan"}}"""), out _, out _));
        Assert.IsFalse(GoveeMessages.TryParse(Encoding.UTF8.GetBytes("not json"), out _, out _));
        Assert.IsFalse(GoveeMessages.TryParse(Encoding.UTF8.GetBytes("[1,2]"), out _, out _));
        Assert.IsFalse(GoveeMessages.TryParse([], out _, out _));
    }

    [TestMethod]
    public void HandleReply_BuildsTheDevice()
    {
        var devices = new Dictionary<string, GoveeDevice>(StringComparer.OrdinalIgnoreCase);

        GoveeDiscovery.HandleReply(
            Encoding.UTF8.GetBytes(
                """{"msg":{"cmd":"scan","data":{"ip":"192.168.1.30","device":"1F:80:C5:32:32:36:72:4E","sku":"H618A","wifiVersionSoft":"1.02.11"}}}"""),
            "192.168.1.30",
            devices);

        Assert.AreEqual(1, devices.Count);
        GoveeDevice device = devices["1F:80:C5:32:32:36:72:4E"];
        Assert.AreEqual("192.168.1.30", device.Ip);
        Assert.AreEqual("H618A", device.Sku);
        Assert.AreEqual("1.02.11", device.WifiVersion);
        Assert.AreEqual(
            "Govee H618A (192.168.1.30, 1F:80:C5:32:32:36:72:4E)",
            GoveeDevice.DestinationLabel(device));
    }

    [TestMethod]
    public void HandleReply_SourceAddressWinsOverReportedIp()
    {
        var devices = new Dictionary<string, GoveeDevice>(StringComparer.OrdinalIgnoreCase);

        GoveeDiscovery.HandleReply(
            Encoding.UTF8.GetBytes(
                """{"msg":{"cmd":"scan","data":{"ip":"10.0.0.99","device":"AA:BB","sku":"H618A"}}}"""),
            "192.168.1.30",
            devices);

        Assert.AreEqual("192.168.1.30", devices["AA:BB"].Ip);
    }

    [TestMethod]
    public void HandleReply_IgnoresOtherCommandsAndMissingDevice()
    {
        var devices = new Dictionary<string, GoveeDevice>(StringComparer.OrdinalIgnoreCase);

        GoveeDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"msg":{"cmd":"devStatus","data":{"onOff":1,"brightness":100}}}"""),
            "192.168.1.30",
            devices);
        GoveeDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"msg":{"cmd":"scan","data":{"ip":"192.168.1.31","sku":"H618A"}}}"""),
            "192.168.1.31",
            devices);

        Assert.AreEqual(0, devices.Count);
    }

    [TestMethod]
    public void HandleReply_SecondScanUpdatesInPlace()
    {
        var devices = new Dictionary<string, GoveeDevice>(StringComparer.OrdinalIgnoreCase);
        GoveeDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"msg":{"cmd":"scan","data":{"device":"AA:BB","sku":"H618A"}}}"""),
            "192.168.1.30",
            devices);

        GoveeDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"msg":{"cmd":"scan","data":{"device":"AA:BB","sku":"H618A"}}}"""),
            "192.168.1.40",
            devices);

        Assert.AreEqual(1, devices.Count);
        Assert.AreEqual("192.168.1.40", devices["AA:BB"].Ip);
    }

    [TestMethod]
    public void DestinationLabel_WithoutSku()
    {
        Assert.AreEqual(
            "Govee (192.168.1.30, AA:BB)",
            GoveeDevice.DestinationLabel(new GoveeDevice("AA:BB", "192.168.1.30")));
    }

    [TestMethod]
    public void DiscoveryInterfaceAddresses_AreDistinctIpv4()
    {
        IReadOnlyList<IPAddress> addresses = GoveeDiscovery.DiscoveryInterfaceAddresses();

        Assert.AreEqual(addresses.Count, addresses.Distinct().Count());
        Assert.IsFalse(addresses.Any(static address =>
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork));
    }
}
