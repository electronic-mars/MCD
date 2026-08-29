namespace Mcd.Interop.AppBar;

/// <summary>Which edge of a monitor the dock is attached to.</summary>
/// <remarks>Values match the shell's ABE_* constants and are stored in config.json by name.</remarks>
public enum AppBarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>How the dock claims its edge.</summary>
public enum AppBarMode
{
    /// <summary>Reserves the space: maximised windows stop at the dock.</summary>
    Pinned,

    /// <summary>Reserves nothing and slides out of view until the pointer reaches the edge.</summary>
    AutoHide,

    /// <summary>
    /// Lies on the desktop. Reserves nothing, claims no edge, and stays at the
    /// bottom of the pile, so any window covers it.
    /// </summary>
    /// <remarks>
    /// The bar as a thing on the desk rather than a thing on the frame. It is
    /// there when the desktop is, and out of the way the moment anything is
    /// opened over it - which is what somebody wants who reads it between
    /// tasks rather than during them.
    /// </remarks>
    Desktop,
}
