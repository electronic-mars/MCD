using System.Runtime.InteropServices;
using System.Text;

namespace Mcd.Interop.Machine;

/// <summary>A wireless device's charge, as Windows itself knows it.</summary>
/// <param name="Id">The device's container, which is the same for every node the device shows up as.</param>
/// <param name="Name">What Windows calls it: "Kone Air".</param>
/// <param name="Family">"mouse", "headset", "keyboard" or "other", from what else the container holds.</param>
/// <param name="Percent">The charge, 0 to 100.</param>
public readonly record struct DeviceCharge(Guid Id, string Name, string Family, int Percent);

/// <summary>
/// The charge of every Bluetooth device that reports one, without asking the
/// device and without anybody's software.
/// </summary>
/// <remarks>
/// <para>
/// Windows reads a device's standard battery service (or the hands-free
/// indicator of a classic headset) on its own and keeps the figure as a
/// property of the device node - the same one Settings shows beside the
/// device. Reading it is a property lookup: no driver, no rights, nothing
/// sent over the air.
/// </para>
/// <para>
/// What kind of thing it is comes from its neighbours. A device appears as
/// several nodes that share one container - a mouse as a Bluetooth node, a
/// HID node and a Mouse node - and the classes in that container are a more
/// honest answer than guessing from the name.
/// </para>
/// </remarks>
public static class DeviceBatteries
{
    private static readonly DevPropKey Battery = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DevPropKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey Description = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly DevPropKey ClassName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 9);
    private static readonly DevPropKey Container = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    private const uint PresentOnly = 0x100;

    /// <summary>Every present device with a charge, one entry per device.</summary>
    public static List<DeviceCharge> Read()
    {
        List<string> ids = PresentIds();
        var classes = new Dictionary<Guid, HashSet<string>>();
        var charged = new List<(string Id, Guid Container, int Percent)>();

        foreach (string id in ids)
        {
            if (GuidOf(id, Container) is not { } container)
            {
                continue;
            }

            if (Text(id, ClassName) is { } kind)
            {
                (classes.TryGetValue(container, out HashSet<string>? set)
                    ? set
                    : classes[container] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(kind);
            }

            if (Byte(id, Battery) is { } percent && percent <= 100)
            {
                charged.Add((id, container, percent));
            }
        }

        var found = new List<DeviceCharge>();

        foreach ((string id, Guid container, int percent) in charged)
        {
            if (found.Any(f => f.Id == container))
            {
                continue;
            }

            string name = Text(id, FriendlyName) ?? Text(id, Description) ?? "Device";
            string family = classes.TryGetValue(container, out HashSet<string>? set) ? FamilyOf(set) : "other";

            // A sleeping Bluetooth mouse keeps its battery node and drops its
            // Mouse node, so the present nodes alone called it "other" - and
            // the kind is part of the reading's key, so its chip lost it.
            // What the device is does not change: asked once of every node
            // Windows remembers, present or not, and kept.
            if (family == "other")
            {
                family = Remembered.TryGetValue(container, out string? known)
                    ? known
                    : Remembered[container] = FamilyOf(EverSeen(container));
            }
            else
            {
                Remembered[container] = family;
            }

            found.Add(new DeviceCharge(container, name, family, percent));
        }

        return found;
    }

    /// <summary>
    /// A mouse first: many mice also present a keyboard, for their extra
    /// buttons, and a headset presents audio endpoints.
    /// </summary>
    private static string FamilyOf(HashSet<string> classes) =>
        classes.Contains("Mouse") ? "mouse"
        : classes.Contains("AudioEndpoint") || classes.Contains("MEDIA") ? "headset"
        : classes.Contains("Keyboard") ? "keyboard"
        : "other";

    private static readonly Dictionary<Guid, string> Remembered = [];

    /// <summary>The classes of every node of one device, absent ones included.</summary>
    private static HashSet<string> EverSeen(Guid container)
    {
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in Ids(0))
        {
            if (GuidOf(id, Container, Phantom) == container && Text(id, ClassName, Phantom) is { } kind)
            {
                classes.Add(kind);
            }
        }

        return classes;
    }

    private const uint Phantom = 1;

    private static List<string> PresentIds() => Ids(PresentOnly);

    private static List<string> Ids(uint which)
    {
        if (CM_Get_Device_ID_List_SizeW(out uint length, null, which) != 0 || length == 0)
        {
            return [];
        }

        var buffer = new char[length];

        return CM_Get_Device_ID_ListW(null, buffer, length, which) == 0
            ? [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)]
            : [];
    }

    private static unsafe byte[]? Property(string id, DevPropKey key, uint locate = 0)
    {
        if (CM_Locate_DevNodeW(out uint node, id, locate) != 0)
        {
            return null;
        }

        uint size = 0;
        CM_Get_DevNode_PropertyW(node, key, out _, null, ref size, 0);

        if (size == 0)
        {
            return null;
        }

        byte[] data = new byte[size];

        fixed (byte* at = data)
        {
            return CM_Get_DevNode_PropertyW(node, key, out _, at, ref size, 0) == 0 ? data : null;
        }
    }

    private static byte? Byte(string id, DevPropKey key) =>
        Property(id, key) is { Length: > 0 } data ? data[0] : null;

    private static Guid? GuidOf(string id, DevPropKey key, uint locate = 0) =>
        Property(id, key, locate) is { Length: 16 } data ? new Guid(data) : null;

    private static string? Text(string id, DevPropKey key, uint locate = 0) =>
        Property(id, key, locate) is { Length: > 2 } data
            ? Encoding.Unicode.GetString(data).TrimEnd('\0')
            : null;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DevPropKey(Guid Fmtid, uint Pid);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern unsafe int CM_Get_DevNode_PropertyW(
        uint node, in DevPropKey key, out uint type, byte* buffer, ref uint size, uint flags);
}
