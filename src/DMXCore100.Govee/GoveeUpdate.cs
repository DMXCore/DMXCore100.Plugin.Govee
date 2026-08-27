namespace DMXCore100.Govee;

/// <summary>
/// The device state one DMX frame asks for. Unlike WiZ's single
/// <c>setPilot</c>, Govee splits power, brightness and color over separate
/// commands, so an update renders as up to three datagrams —
/// <see cref="DatagramsSince"/> emits only what changed against the state
/// the session last sent.
/// </summary>
internal sealed record GoveeUpdate
{
    public static readonly GoveeUpdate Off = new() { Power = false };

    public bool Power { get; init; } = true;

    /// <summary>Brightness percent, 1-100.</summary>
    public int? Brightness { get; init; }

    public byte? R { get; init; }

    public byte? G { get; init; }

    public byte? B { get; init; }

    /// <summary>White color temperature in kelvin (white mode).</summary>
    public int? Kelvin { get; init; }

    /// <summary>
    /// The command datagrams that move a device from
    /// <paramref name="previous"/> (null = state unknown: fresh session or
    /// after a failed send) to this state. Off is just <c>turn 0</c>;
    /// coming from off or unknown resends everything.
    /// </summary>
    public IReadOnlyList<byte[]> DatagramsSince(GoveeUpdate? previous)
    {
        if (!Power)
        {
            return previous is { Power: false } ? [] : [GoveeMessages.Turn(false)];
        }

        bool full = previous is not { Power: true };
        var datagrams = new List<byte[]>(3);
        if (full)
        {
            datagrams.Add(GoveeMessages.Turn(true));
        }

        if (Brightness is int brightness && (full || previous!.Brightness != brightness))
        {
            datagrams.Add(GoveeMessages.Brightness(brightness));
        }

        if ((R.HasValue || Kelvin.HasValue) && (full || ColorDiffers(previous!)))
        {
            datagrams.Add(GoveeMessages.ColorWc(R ?? 0, G ?? 0, B ?? 0, Kelvin ?? 0));
        }

        return datagrams;
    }

    private bool ColorDiffers(GoveeUpdate previous) =>
        previous.R != R || previous.G != G || previous.B != B || previous.Kelvin != Kelvin;
}

/// <summary>
/// Turns 0..1 channel levels into Govee updates. Govee renders
/// <c>colorwc</c> as the color and <c>brightness</c> (1-100) as overall
/// intensity, so the brightest channel sets the brightness and the color
/// channels are normalized toward 255 for the best color resolution — the
/// same scheme the WiZ plugin uses.
/// </summary>
internal static class GoveeColor
{
    /// <summary>
    /// Color update from 0..1 red, green, blue. All zero is
    /// <see cref="GoveeUpdate.Off"/>.
    /// </summary>
    public static GoveeUpdate Color(double r, double g, double b)
    {
        r = Clamp01(r);
        g = Clamp01(g);
        b = Clamp01(b);
        double level = Math.Max(r, Math.Max(g, b));
        if (level <= 0.0)
        {
            return GoveeUpdate.Off;
        }

        int brightness = BrightnessFor(level);
        // Scale so the brightest channel lands on 255 when brightness carries
        // the level exactly
        double scale = 255.0 * GoveeConstants.MaxBrightness / brightness;
        return new GoveeUpdate
        {
            R = ToByte(r * scale),
            G = ToByte(g * scale),
            B = ToByte(b * scale),
            Kelvin = 0,
            Brightness = brightness,
        };
    }

    /// <summary>
    /// Tunable-white update from a 0..1 intensity and a 0..1 color
    /// temperature (0 = warm, 1 = cool). Zero intensity is
    /// <see cref="GoveeUpdate.Off"/>.
    /// </summary>
    public static GoveeUpdate White(double intensity, double colorTemperature)
    {
        intensity = Clamp01(intensity);
        if (intensity <= 0.0)
        {
            return GoveeUpdate.Off;
        }

        return new GoveeUpdate
        {
            Kelvin = KelvinFromUnit(colorTemperature),
            Brightness = BrightnessFor(intensity),
        };
    }

    /// <summary>
    /// Dimmer-only update from a 0..1 intensity: brightness moves, the
    /// device keeps whatever color it is showing; zero is off.
    /// </summary>
    public static GoveeUpdate Dimmer(double intensity)
    {
        intensity = Clamp01(intensity);
        if (intensity <= 0.0)
        {
            return GoveeUpdate.Off;
        }

        return new GoveeUpdate { Brightness = BrightnessFor(intensity) };
    }

    /// <summary>
    /// Map a 0..1 ColorTemperature value onto Govee kelvin (warm at 0, cool at 1).
    /// </summary>
    public static int KelvinFromUnit(double t)
    {
        t = Clamp01(t);
        return (int)Math.Round(GoveeConstants.KelvinMin + (t * (GoveeConstants.KelvinMax - GoveeConstants.KelvinMin)));
    }

    /// <summary>
    /// Brightness percent for a 0..1 level: rounded up so the color scale
    /// never exceeds 255, floored at <see cref="GoveeConstants.MinBrightness"/>.
    /// </summary>
    internal static int BrightnessFor(double level)
    {
        int brightness = (int)Math.Ceiling(level * GoveeConstants.MaxBrightness);
        return Math.Clamp(brightness, GoveeConstants.MinBrightness, GoveeConstants.MaxBrightness);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || value < 0.0)
        {
            return 0.0;
        }

        return value > 1.0 ? 1.0 : value;
    }
}
