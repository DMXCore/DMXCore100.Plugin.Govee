using System.Text;

namespace DMXCore100.Govee;

/// <summary>
/// The reverse-engineered razer/DreamView streaming mode ("ptReal"): the
/// transport Govee's own desktop app uses for music and video sync. A
/// <c>razer</c> JSON command carries a base64 BLE-style packet
/// (<c>0xBB</c>, big-endian payload length, subcommand, payload, XOR
/// checksum). Once enabled, <c>0xB0</c> packets stream per-segment RGB that
/// the device renders instantly — no firmware fade — bypassing the normal
/// colorwc/brightness pipeline. Not part of the documented LAN API; verified
/// on the H618A only.
/// </summary>
internal static class GoveeRazer
{
    /// <summary>Enter razer mode: subcommand 0xB1, payload 0x01.</summary>
    public static readonly byte[] Enable = Wrap([0xBB, 0x00, 0x01, 0xB1, 0x01, 0x0A]);

    /// <summary>Leave razer mode and return to normal rendering.</summary>
    public static readonly byte[] Disable = Wrap([0xBB, 0x00, 0x01, 0xB1, 0x00, 0x0B]);

    /// <summary>
    /// One streamed frame: the same color on every segment.
    /// </summary>
    public static byte[] Solid(byte r, byte g, byte b, int segments)
    {
        byte[] rgb = new byte[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            rgb[(i * 3) + 0] = r;
            rgb[(i * 3) + 1] = g;
            rgb[(i * 3) + 2] = b;
        }

        return Pixels(rgb, segments);
    }

    /// <summary>
    /// One streamed frame from a per-segment RGB slice (3 bytes per
    /// segment, at least <paramref name="segments"/> × 3 long).
    /// </summary>
    public static byte[] Pixels(ReadOnlySpan<byte> channels, int segments)
    {
        // Packet: BB <len hi> <len lo> B0 <gradient off = 01> <count> RGB… <xor>
        int payload = 2 + (segments * 3);
        byte[] packet = new byte[4 + payload + 1];
        packet[0] = 0xBB;
        packet[1] = (byte)(payload >> 8);
        packet[2] = (byte)(payload & 0xFF);
        packet[3] = 0xB0;
        packet[4] = 0x01;
        packet[5] = (byte)segments;
        channels[..(segments * 3)].CopyTo(packet.AsSpan(6));
        byte checksum = 0;
        for (int i = 0; i < packet.Length - 1; i++)
        {
            checksum ^= packet[i];
        }

        packet[^1] = checksum;
        return Wrap(packet);
    }

    /// <summary>
    /// Clamp a mapping's segment count to what one packet (and one universe
    /// slice: 170 × 3 = 510 channels) can carry.
    /// </summary>
    public static int ClampSegments(int segments) => Math.Clamp(segments, 1, 170);

    private static byte[] Wrap(byte[] packet) =>
        Encoding.UTF8.GetBytes(
            """{"msg":{"cmd":"razer","data":{"pt":""" + '"' + Convert.ToBase64String(packet) + '"' + "}}}");
}
