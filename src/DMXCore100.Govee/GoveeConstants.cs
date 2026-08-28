namespace DMXCore100.Govee;

internal static class GoveeConstants
{
    /// <summary>
    /// UDP port every Govee device with LAN Control enabled listens on for
    /// JSON commands (turn, brightness, colorwc, devStatus).
    /// </summary>
    public const int CommandPort = 4003;

    /// <summary>
    /// Multicast group + port the devices listen on for the discovery scan.
    /// </summary>
    public const string MulticastAddress = "239.255.255.250";

    public const int ScanPort = 4001;

    /// <summary>
    /// Fixed local port every Govee reply (scan, devStatus) is sent to —
    /// replies do not go back to the source port of the request.
    /// </summary>
    public const int ListenPort = 4002;

    /// <summary>
    /// Conservative streaming rate per Govee device; the LAN API has no
    /// documented limit, and community integrations throttle to this order.
    /// </summary>
    public const int MaxUpdatesPerSecond = 10;

    /// <summary>
    /// Pause between the datagrams of one frame when it needs more than one
    /// command. An H618A (WiFi fw 1.02.11) drops a datagram that arrives
    /// back-to-back with the previous one — measured: 0 ms gap loses the
    /// second command, 5 ms already works — so 15 ms leaves margin without
    /// hurting the frame budget (worst case turn+brightness+colorwc is two
    /// gaps, 30 ms of a 100 ms frame at 10 updates/s).
    /// </summary>
    public const int InterCommandGapMs = 15;

    /// <summary>
    /// Brightness percent range of the <c>brightness</c> command.
    /// </summary>
    public const int MinBrightness = 1;

    public const int MaxBrightness = 100;

    /// <summary>
    /// <c>colorTemInKelvin</c> range of the LAN API; the device clamps to
    /// its own capability inside it.
    /// </summary>
    public const int KelvinMin = 2000;

    public const int KelvinMax = 9000;

    public const int DiscoveryTimeoutMs = 2000;
}
