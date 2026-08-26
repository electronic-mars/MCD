using System.Security.Cryptography;
using System.Text;

namespace Mcd.Core.Monitors;

/// <summary>
/// The key a monitor's configuration is filed under.
/// </summary>
/// <remarks>
/// Derived only from the monitor's device interface path. There is deliberately
/// no constructor that accepts a GDI name: PowerToys #49604 happens because
/// their lookup falls back to <c>\.\DISPLAY1</c> when the device path is
/// briefly unavailable, and a live monitor then looks like a brand new one.
/// </remarks>
public readonly record struct MonitorStableId
{
    private MonitorStableId(string value) => Value = value;

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>
    /// Sixteen hex characters of SHA-256 over the device path. The raw path is
    /// kept beside it in the registry entry, because a settings file nobody can
    /// read is a settings file nobody can fix.
    /// </summary>
    public static MonitorStableId FromDevicePath(string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);

        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(devicePath));
        return new MonitorStableId(Convert.ToHexStringLower(hash.AsSpan(0, 8)));
    }

    /// <summary>Rebuilds an id read back from config.json.</summary>
    public static MonitorStableId FromStored(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>Everything that identifies a physical panel, in order of trustworthiness.</summary>
public sealed record MonitorIdentity(
    MonitorStableId StableId,
    string DevicePath,
    string EdidKey,
    string FriendlyName,
    string GdiName);
