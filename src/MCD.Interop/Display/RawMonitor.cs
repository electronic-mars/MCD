using Windows.Win32.Foundation;

namespace Mcd.Interop.Display;

/// <summary>One monitor as GDI sees it, before identity is resolved.</summary>
/// <param name="GdiName"><c>\.\DISPLAY1</c> - volatile, correlation only.</param>
/// <param name="Bounds">Full bounds in virtual-screen physical pixels.</param>
/// <param name="WorkArea">Bounds minus whatever the shell has reserved.</param>
/// <param name="Dpi">Effective DPI; 96 means 100% scaling.</param>
/// <param name="IsPrimary">True for the one monitor whose top-left is the virtual origin.</param>
public readonly record struct RawMonitor(
    string GdiName,
    RECT Bounds,
    RECT WorkArea,
    uint Dpi,
    bool IsPrimary);
