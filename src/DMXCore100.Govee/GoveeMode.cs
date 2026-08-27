using DMXCore.PluginSdk;

namespace DMXCore100.Govee;

/// <summary>
/// One output protocol of the plugin: its DMX channel layout (also the
/// personality of the fixture profile it suggests) and how a channel slice
/// becomes a <see cref="GoveeUpdate"/>. Every Govee LAN device is
/// color-capable, so unlike WiZ there is no per-device kind filtering —
/// the white modes are conveniences on the same hardware.
/// </summary>
internal sealed record GoveeMode(
    string ProtocolId,
    string ProfileCode,
    string Personality,
    string DisplayName,
    IReadOnlyList<PluginFixtureFunction> Channels)
{
    public static readonly GoveeMode Rgb = new(
        GoveePlugin.ColorProtocolId,
        GoveePlugin.ColorProfileCode,
        "RGB",
        "Govee Color RGB",
        [PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue]);

    /// <summary>Intensity + ColorTemperature in kelvin mode (0 = 2000 K warm, 255 = 9000 K cool).</summary>
    public static readonly GoveeMode TunableWhite = new(
        GoveePlugin.WhiteCtProtocolId,
        GoveePlugin.WhiteProfileCode,
        "Dimmer+CT",
        "Govee Tunable White (Dimmer + CT)",
        [PluginFixtureFunction.Intensity, PluginFixtureFunction.ColorTemperature]);

    /// <summary>Intensity only: brightness moves, the color stays as-is.</summary>
    public static readonly GoveeMode Dimmer = new(
        GoveePlugin.WhiteProtocolId,
        GoveePlugin.WhiteProfileCode,
        "Dimmer",
        "Govee Dimmer",
        [PluginFixtureFunction.Intensity]);

    /// <summary>
    /// Every mode, in the order the protocols are registered and the profile
    /// personalities are listed.
    /// </summary>
    public static readonly IReadOnlyList<GoveeMode> All =
    [
        Rgb,
        TunableWhite,
        Dimmer,
    ];

    public int ChannelCount => Channels.Count;

    /// <summary>
    /// Convert one channel slice (at least <see cref="ChannelCount"/> bytes)
    /// to the update to send.
    /// </summary>
    public GoveeUpdate ToUpdate(ReadOnlySpan<byte> channels)
    {
        if (ReferenceEquals(this, Rgb))
        {
            return GoveeColor.Color(Read(channels, 0), Read(channels, 1), Read(channels, 2));
        }

        if (ReferenceEquals(this, TunableWhite))
        {
            return GoveeColor.White(Read(channels, 0), Read(channels, 1));
        }

        return GoveeColor.Dimmer(Read(channels, 0));
    }

    private static double Read(ReadOnlySpan<byte> channels, int index) =>
        (index < channels.Length ? channels[index] : 0) / 255.0;
}
