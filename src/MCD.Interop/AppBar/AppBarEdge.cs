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
}
