using System.Text;
using System.Text.Json;

namespace DMXCore100.Govee;

/// <summary>
/// The Govee LAN API JSON datagrams: every message is
/// <c>{"msg":{"cmd":...,"data":{...}}}</c> in both directions. Commands go
/// to the device on UDP 4003; the scan probe goes to the multicast group on
/// 4001 and every reply arrives on the fixed local port 4002.
/// </summary>
internal static class GoveeMessages
{
    /// <summary>
    /// Discovery probe: the same multicast scan the Govee app sends.
    /// <c>account_topic "reserve"</c> asks the device not to include the
    /// account binding in its reply.
    /// </summary>
    public static readonly byte[] Scan = Encoding.UTF8.GetBytes(
        """{"msg":{"cmd":"scan","data":{"account_topic":"reserve"}}}""");

    public static readonly byte[] DevStatus = Encoding.UTF8.GetBytes(
        """{"msg":{"cmd":"devStatus","data":{}}}""");

    public static byte[] Turn(bool on) => Command("turn", writer =>
        writer.WriteNumber("value", on ? 1 : 0));

    /// <summary>Brightness percent, 1-100.</summary>
    public static byte[] Brightness(int percent) => Command("brightness", writer =>
        writer.WriteNumber("value", percent));

    /// <summary>
    /// Color / white command: RGB with <c>colorTemInKelvin</c> 0 renders the
    /// color; a non-zero kelvin with black RGB renders white at that
    /// temperature. Brightness is separate (<see cref="Brightness"/>).
    /// </summary>
    public static byte[] ColorWc(byte r, byte g, byte b, int kelvin) => Command("colorwc", writer =>
    {
        writer.WritePropertyName("color");
        writer.WriteStartObject();
        writer.WriteNumber("r", r);
        writer.WriteNumber("g", g);
        writer.WriteNumber("b", b);
        writer.WriteEndObject();
        writer.WriteNumber("colorTemInKelvin", kelvin);
    });

    /// <summary>
    /// Parse one reply. Returns false when the datagram is not a Govee
    /// message with a cmd and a data object.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out string cmd, out JsonElement data)
    {
        cmd = "";
        data = default;
        try
        {
            using var document = JsonDocument.Parse(datagram.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("msg", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("cmd", out JsonElement cmdElement)
                || cmdElement.ValueKind != JsonValueKind.String
                || !message.TryGetProperty("data", out JsonElement dataElement)
                || dataElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            cmd = cmdElement.GetString() ?? "";
            data = dataElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? GetString(JsonElement data, string name) =>
        data.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[] Command(string cmd, Action<Utf8JsonWriter> writeData)
    {
        using var stream = new MemoryStream(96);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("msg");
            writer.WriteStartObject();
            writer.WriteString("cmd", cmd);
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writeData(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
