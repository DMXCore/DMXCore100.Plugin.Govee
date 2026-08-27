using System.Text;
using System.Text.Json;

namespace DMXCore100.Govee.Tests;

[TestClass]
public class GoveeUpdateTests
{
    [TestMethod]
    public void Commands_AreCompactUtf8Json()
    {
        Assert.AreEqual(
            """{"msg":{"cmd":"turn","data":{"value":1}}}""",
            Encoding.UTF8.GetString(GoveeMessages.Turn(true)));
        Assert.AreEqual(
            """{"msg":{"cmd":"turn","data":{"value":0}}}""",
            Encoding.UTF8.GetString(GoveeMessages.Turn(false)));
        Assert.AreEqual(
            """{"msg":{"cmd":"brightness","data":{"value":51}}}""",
            Encoding.UTF8.GetString(GoveeMessages.Brightness(51)));
        Assert.AreEqual(
            """{"msg":{"cmd":"colorwc","data":{"color":{"r":255,"g":0,"b":12},"colorTemInKelvin":0}}}""",
            Encoding.UTF8.GetString(GoveeMessages.ColorWc(255, 0, 12, 0)));
        Assert.AreEqual(
            """{"msg":{"cmd":"colorwc","data":{"color":{"r":0,"g":0,"b":0},"colorTemInKelvin":5500}}}""",
            Encoding.UTF8.GetString(GoveeMessages.ColorWc(0, 0, 0, 5500)));
    }

    [TestMethod]
    public void Color_AllZeroIsOff()
    {
        Assert.AreSame(GoveeUpdate.Off, GoveeColor.Color(0, 0, 0));
    }

    [TestMethod]
    public void Color_FullRedIsFullBrightnessFullRed()
    {
        GoveeUpdate update = GoveeColor.Color(1, 0, 0);

        Assert.IsTrue(update.Power);
        Assert.AreEqual((byte)255, update.R);
        Assert.AreEqual((byte)0, update.G);
        Assert.AreEqual((byte)0, update.B);
        Assert.AreEqual(0, update.Kelvin);
        Assert.AreEqual(100, update.Brightness);
    }

    [TestMethod]
    public void Color_HalfLevelKeepsColorAtFullAndDimsViaBrightness()
    {
        // (0.5, 0.25, 0) — an orange at half level: color normalized so red
        // hits 255, brightness carries the level
        GoveeUpdate update = GoveeColor.Color(0.5, 0.25, 0);

        Assert.AreEqual(50, update.Brightness);
        Assert.AreEqual((byte)255, update.R);
        Assert.AreEqual((byte)128, update.G);
        Assert.AreEqual((byte)0, update.B);
    }

    [TestMethod]
    public void Color_LevelRoundsBrightnessUpSoChannelsNeverOverflow()
    {
        // 0.501 → brightness 51 (not 50); red scaled 0.501 * 255 * 100 / 51 = 250
        GoveeUpdate update = GoveeColor.Color(0.501, 0, 0);

        Assert.AreEqual(51, update.Brightness);
        Assert.AreEqual((byte)250, update.R);
    }

    [TestMethod]
    public void Color_VeryLowLevelKeepsFading()
    {
        // 1/255 ≈ 0.4%: brightness floors at 1 and the channels scale down so
        // the fade keeps going instead of stepping to black
        GoveeUpdate update = GoveeColor.Color(1.0 / 255.0, 0, 0);

        Assert.AreEqual(1, update.Brightness);
        Assert.AreEqual((byte)100, update.R);
    }

    [TestMethod]
    public void Color_ClampsOutOfRangeInput()
    {
        GoveeUpdate update = GoveeColor.Color(2, -1, double.NaN);

        Assert.AreEqual(100, update.Brightness);
        Assert.AreEqual((byte)255, update.R);
        Assert.AreEqual((byte)0, update.G);
        Assert.AreEqual((byte)0, update.B);
    }

    [TestMethod]
    public void White_ZeroIsOff()
    {
        Assert.AreSame(GoveeUpdate.Off, GoveeColor.White(0, 0.5));
        Assert.AreSame(GoveeUpdate.Off, GoveeColor.Dimmer(0));
    }

    [TestMethod]
    public void White_SendsKelvinAndBrightnessOnly()
    {
        GoveeUpdate warm = GoveeColor.White(1, 0);
        GoveeUpdate cool = GoveeColor.White(0.5, 1);
        GoveeUpdate mid = GoveeColor.White(0.3, 0.5);

        Assert.AreEqual(GoveeConstants.KelvinMin, warm.Kelvin);
        Assert.AreEqual(100, warm.Brightness);
        Assert.IsNull(warm.R);
        Assert.AreEqual(GoveeConstants.KelvinMax, cool.Kelvin);
        Assert.AreEqual(50, cool.Brightness);
        Assert.AreEqual(5500, mid.Kelvin);
        Assert.AreEqual(30, mid.Brightness);
    }

    [TestMethod]
    public void Dimmer_SendsBrightnessOnly()
    {
        GoveeUpdate update = GoveeColor.Dimmer(0.01);

        Assert.AreEqual(1, update.Brightness);
        Assert.IsNull(update.Kelvin);
        Assert.IsNull(update.R);
    }

    [TestMethod]
    public void BrightnessFor_CeilsAndClamps()
    {
        Assert.AreEqual(GoveeConstants.MinBrightness, GoveeColor.BrightnessFor(0.001));
        Assert.AreEqual(10, GoveeColor.BrightnessFor(0.1));
        Assert.AreEqual(11, GoveeColor.BrightnessFor(0.101));
        Assert.AreEqual(100, GoveeColor.BrightnessFor(1));
        Assert.AreEqual(100, GoveeColor.BrightnessFor(5));
    }

    [TestMethod]
    public void DatagramsSince_UnknownStateSendsEverything()
    {
        IReadOnlyList<byte[]> datagrams = GoveeColor.Color(1, 0, 0).DatagramsSince(null);

        CollectionAssert.AreEqual(
            new[] { "turn", "brightness", "colorwc" },
            datagrams.Select(Cmd).ToArray());
    }

    [TestMethod]
    public void DatagramsSince_SendsOnlyWhatChanged()
    {
        GoveeUpdate red = GoveeColor.Color(1, 0, 0);
        GoveeUpdate green = GoveeColor.Color(0, 1, 0);
        GoveeUpdate dimRed = GoveeColor.Color(0.5, 0, 0);

        // Same color, same brightness: nothing to send
        Assert.AreEqual(0, GoveeColor.Color(1, 0, 0).DatagramsSince(red).Count);
        // Hue change at the same level: colorwc only
        CollectionAssert.AreEqual(new[] { "colorwc" }, green.DatagramsSince(red).Select(Cmd).ToArray());
        // Level change of the same hue: brightness only (color stays normalized at 255)
        CollectionAssert.AreEqual(new[] { "brightness" }, dimRed.DatagramsSince(red).Select(Cmd).ToArray());
    }

    [TestMethod]
    public void DatagramsSince_OffAndBackOn()
    {
        GoveeUpdate red = GoveeColor.Color(1, 0, 0);

        // On → off is just turn 0, repeated off sends nothing
        IReadOnlyList<byte[]> off = GoveeUpdate.Off.DatagramsSince(red);
        CollectionAssert.AreEqual(new[] { "turn" }, off.Select(Cmd).ToArray());
        Assert.AreEqual(0, GoveeUpdate.Off.DatagramsSince(GoveeUpdate.Off).Count);

        // Coming out of off resends the full state
        CollectionAssert.AreEqual(
            new[] { "turn", "brightness", "colorwc" },
            red.DatagramsSince(GoveeUpdate.Off).Select(Cmd).ToArray());
    }

    [TestMethod]
    public void DatagramsSince_DimmerModeNeverSendsColor()
    {
        GoveeUpdate half = GoveeColor.Dimmer(0.5);

        CollectionAssert.AreEqual(
            new[] { "turn", "brightness" },
            half.DatagramsSince(null).Select(Cmd).ToArray());
        CollectionAssert.AreEqual(
            new[] { "brightness" },
            GoveeColor.Dimmer(1).DatagramsSince(half).Select(Cmd).ToArray());
    }

    private static string Cmd(byte[] datagram)
    {
        using JsonDocument document = JsonDocument.Parse(datagram);
        return document.RootElement.GetProperty("msg").GetProperty("cmd").GetString() ?? "";
    }
}
