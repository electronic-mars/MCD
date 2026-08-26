namespace Mcd.Interop.Display;

/// <summary>
/// What the Display Configuration API knows about one active output.
/// </summary>
/// <param name="GdiName">
/// <c>\.\DISPLAY1</c>. Volatile: Windows hands the same name to a different
/// physical monitor after a topology change. Useful for logs, useless as a key -
/// that mistake is the root of PowerToys #49604.
/// </param>
/// <param name="DevicePath">
/// The device interface path, e.g.
/// <c>\?\DISPLAY#GSM5B08#5&amp;1234abcd&amp;0&amp;UID4353#{e6f07b5f-...}</c>.
/// Survives reboots and re-plugging into the same port.
/// </param>
/// <param name="FriendlyName">The name a person recognises, e.g. "LG ULTRAGEAR".</param>
/// <param name="EdidVendor">Three-letter PnP vendor code from EDID, e.g. "GSM".</param>
/// <param name="EdidProduct">EDID product code, as four hex digits.</param>
public readonly record struct DisplayTarget(
    string GdiName,
    string DevicePath,
    string FriendlyName,
    string EdidVendor,
    string EdidProduct)
{
    /// <summary>Identifies the physical panel across ports. Empty when EDID was unreadable.</summary>
    public string EdidKey =>
        EdidVendor.Length == 0 ? string.Empty : $"{EdidVendor}:{EdidProduct}";
}
