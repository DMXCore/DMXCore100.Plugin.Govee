using System.Net;
using System.Text;
using DMXCore.PluginSdk.Testing;

namespace DMXCore100.Govee.Tests;

/// <summary>
/// The host's idle refresh (SDK contract 1.13) re-delivers an unchanged look;
/// the sessions answer it with the full state / a re-armed razer mode, but
/// only once the last full send or arm is older than the refresh interval.
/// </summary>
[TestClass]
public class GoveeRefreshTests
{
    private static readonly IPEndPoint Endpoint = new(IPAddress.Loopback, GoveeConstants.CommandPort);

    private static (GoveeDatagramSender Sender, List<string> Sent) Recorder()
    {
        var sent = new List<string>();
        GoveeDatagramSender sender = (_, packet, _) =>
        {
            sent.Add(Encoding.UTF8.GetString(packet.Span));
            return ValueTask.CompletedTask;
        };

        return (sender, sent);
    }

    [TestMethod]
    public async Task UnchangedColor_WithinTheInterval_SendsNothing()
    {
        var (sender, sent) = Recorder();
        await using var session = new GoveeSession(GoveeMode.Rgb, Endpoint, sender, refreshInterval: TimeSpan.FromHours(1));

        await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None);
        int afterFirst = sent.Count;
        await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None);

        Assert.AreEqual(3, afterFirst, "first frame sends turn + brightness + colorwc");
        Assert.AreEqual(afterFirst, sent.Count);
    }

    [TestMethod]
    public async Task UnchangedColor_AfterTheInterval_ResendsTheFullState()
    {
        var (sender, sent) = Recorder();
        await using var session = new GoveeSession(GoveeMode.Rgb, Endpoint, sender, refreshInterval: TimeSpan.Zero);

        await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None);
        await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None);

        Assert.AreEqual(6, sent.Count);
        CollectionAssert.AreEqual(sent.Take(3).ToList(), sent.Skip(3).ToList());
        StringAssert.Contains(sent[3], "\"turn\"");
    }

    [TestMethod]
    public async Task ChangedColor_StillSendsOnlyTheDiff()
    {
        var (sender, sent) = Recorder();
        await using var session = new GoveeSession(GoveeMode.Rgb, Endpoint, sender, refreshInterval: TimeSpan.Zero);

        await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None);
        await session.SendAsync(new byte[] { 0, 255, 0 }, CancellationToken.None);

        Assert.AreEqual(4, sent.Count);
        StringAssert.Contains(sent[3], "colorwc");
    }

    [TestMethod]
    public async Task UnchangedOff_AfterTheInterval_ResendsTurnOff()
    {
        var (sender, sent) = Recorder();
        await using var session = new GoveeSession(GoveeMode.Rgb, Endpoint, sender, refreshInterval: TimeSpan.Zero);

        await session.SendAsync(new byte[] { 0, 0, 0 }, CancellationToken.None);
        await session.SendAsync(new byte[] { 0, 0, 0 }, CancellationToken.None);

        Assert.AreEqual(2, sent.Count);
        Assert.AreEqual(sent[0], sent[1]);
        StringAssert.Contains(sent[1], "\"turn\"");
    }

    [TestMethod]
    public async Task Realtime_UnchangedFrame_ReArmsOnlyAfterTheInterval()
    {
        string enable = Encoding.UTF8.GetString(GoveeRazer.Enable);

        var (heldSender, held) = Recorder();
        await using (var session = new GoveeRealtimeSession(false, 15, Endpoint, heldSender, refreshInterval: TimeSpan.FromHours(1)))
        {
            await session.SendAsync(new byte[] { 10, 20, 30 }, CancellationToken.None);
            await session.SendAsync(new byte[] { 10, 20, 30 }, CancellationToken.None);
        }

        var (dueSender, due) = Recorder();
        await using (var session = new GoveeRealtimeSession(false, 15, Endpoint, dueSender, refreshInterval: TimeSpan.Zero))
        {
            await session.SendAsync(new byte[] { 10, 20, 30 }, CancellationToken.None);
            await session.SendAsync(new byte[] { 10, 20, 30 }, CancellationToken.None);
            await session.SendAsync(new byte[] { 90, 20, 30 }, CancellationToken.None);
        }

        // Held: enable, frame, frame (+ disable on dispose)
        Assert.AreEqual(1, held.Count(datagram => datagram == enable));
        // Due: enable, frame, enable (unchanged = refresh), frame, frame (changed: no re-arm)
        Assert.AreEqual(2, due.Count(datagram => datagram == enable));
        Assert.AreEqual(enable, due[2]);
        Assert.AreNotEqual(enable, due[4]);
    }
}
