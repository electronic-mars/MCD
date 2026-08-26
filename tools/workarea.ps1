# Reads the live work area of the primary monitor, in physical pixels.
# Screen.PrimaryScreen caches per process, so this must run as its own process
# each time it is asked - otherwise a shrinking work area looks unchanged.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Wa {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, ref RECT c, uint d);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public static string Read() {
        SetProcessDPIAware();
        RECT r = new RECT();
        SystemParametersInfo(0x0030, 0, ref r, 0);
        return r.L + "," + r.T + "," + r.R + "," + r.B;
    }
}
"@
[Wa]::Read()
