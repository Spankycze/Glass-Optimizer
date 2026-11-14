param([switch]$ShowHover, [switch]$Capture)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SpankyTrayNativeSmoke
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)]
    public struct IDENTIFIER {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle,out RECT rect);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr handle,out uint processId);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("shell32.dll")]
    public static extern int Shell_NotifyIconGetRect(ref IDENTIFIER id,out RECT bounds);
}
'@
$icon = [SpankyTrayNativeSmoke]::FindWindow('STATIC','Spankyho Fan Agent')
if ($icon -eq [IntPtr]::Zero) { throw 'TRAY_HIDDEN_WINDOW_NOT_FOUND' }
[uint32]$pidValue=0
$tid=[SpankyTrayNativeSmoke]::GetWindowThreadProcessId($icon,[ref]$pidValue)
$process=Get-Process -Id $pidValue -ErrorAction Stop
if($process.ProcessName -ne 'SpankyhoOptimizer.RuntimeHost'){
    throw "TRAY_WRONG_OWNER_PROCESS: $($process.ProcessName)"
}
$id=[SpankyTrayNativeSmoke+IDENTIFIER]::new()
$id.cbSize=[Runtime.InteropServices.Marshal]::SizeOf($id)
$id.hWnd=$icon
$id.uID=1
$bounds=[SpankyTrayNativeSmoke+RECT]::new()
$hr=[SpankyTrayNativeSmoke]::Shell_NotifyIconGetRect([ref]$id,[ref]$bounds)
"FAN_TRAY_NATIVE hwnd=$icon process=$pidValue tid=$tid shellRectHresult=$hr rect=$($bounds.L),$($bounds.T),$($bounds.R),$($bounds.B)"
if($ShowHover){
    if(-not [SpankyTrayNativeSmoke]::PostMessage($icon,0x804D,[IntPtr]::Zero,[IntPtr]::new(0x0406))){
        throw 'TRAY_TEST_HOVER_POST_FAILED'
    }
    Start-Sleep -Milliseconds 420
     $popup=[SpankyTrayNativeSmoke]::FindWindow('STATIC','Glass Optimizer — Tray')
    $visible=$popup -ne [IntPtr]::Zero -and
       [SpankyTrayNativeSmoke]::IsWindowVisible($popup)
    if($popup -ne [IntPtr]::Zero){
        $r=[SpankyTrayNativeSmoke+RECT]::new()
        $null=[SpankyTrayNativeSmoke]::GetWindowRect($popup,[ref]$r)
        "FAN_HOVER_POPUP hwnd=$popup visible=$visible bounds=$($r.L),$($r.T),$($r.R),$($r.B)"
        if($Capture -and $visible) {
            Add-Type -AssemblyName System.Drawing
            $width=[math]::Max(1,$r.R-$r.L)
            $height=[math]::Max(1,$r.B-$r.T)
            $bmp=[System.Drawing.Bitmap]::new($width,$height)
            $g=[System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.CopyFromScreen($r.L,$r.T,0,0,$bmp.Size)
                $dest=Join-Path $PSScriptRoot '..\.ai-state\tray-popup-1383.png'
                $bmp.Save($dest,[System.Drawing.Imaging.ImageFormat]::Png)
                "FAN_HOVER_CAPTURE=$dest"
            } finally { $g.Dispose(); $bmp.Dispose() }
        }
    }
    $null=[SpankyTrayNativeSmoke]::PostMessage($icon,0x804D,
        [IntPtr]::Zero,[IntPtr]::new(0x0407))
    Start-Sleep -Milliseconds 160
    if($popup -ne [IntPtr]::Zero){
        $hidden=-not [SpankyTrayNativeSmoke]::IsWindowVisible($popup)
        "FAN_HOVER_HIDE_OK=$hidden"
        if(-not $hidden){throw 'TRAY_TEST_POPUP_HIDE_FAILED'}
    }
    if(-not $visible){throw 'TRAY_TEST_POPUP_NOT_VISIBLE'}
    'FAN_TRAY_HOVER_NATIVE_SMOKE_PASS'
}
