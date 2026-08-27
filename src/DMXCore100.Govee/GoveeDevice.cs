namespace DMXCore100.Govee;

/// <summary>
/// One device found by the LAN scan. Every device that answers the scan has
/// LAN Control enabled and speaks the full command set (they are all RGBIC
/// hardware), so there is no capability filtering — the SKU is shown so the
/// user can tell their strips apart.
/// </summary>
internal sealed class GoveeDevice
{
    public GoveeDevice(string id, string ip)
    {
        Id = id;
        Ip = ip;
    }

    /// <summary>
    /// Device id as Govee reports it in the scan reply's <c>device</c>
    /// field, e.g. <c>1F:80:C5:32:32:36:72:4E</c>.
    /// </summary>
    public string Id { get; }

    public string Ip { get; set; }

    /// <summary>Model, e.g. <c>H618A</c>.</summary>
    public string Sku { get; set; } = "";

    public string WifiVersion { get; set; } = "";

    internal static string DestinationLabel(GoveeDevice device)
    {
        string name = string.IsNullOrWhiteSpace(device.Sku) ? "Govee" : $"Govee {device.Sku}";
        return $"{name} ({device.Ip}, {device.Id})";
    }
}
