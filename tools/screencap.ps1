# Captures the player window in both themes (for docs/screenshots).
# Usage: powershell -File tools\screencap.ps1 -Exe F:\horizon-offline\bin\HorizonOffline.exe -OutDir C:\some\dir
param(
    [string]$Exe = 'F:\horizon-offline\bin\HorizonOffline.exe',
    [string]$OutDir = $env:TEMP
)
Add-Type -AssemblyName System.Drawing
$sig = @'
using System;
using System.Runtime.InteropServices;
public class W32 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -TypeDefinition $sig

foreach ($theme in @('amoled', 'light')) {
    $p = Start-Process -FilePath $Exe -ArgumentList '--theme', $theme -PassThru
    Start-Sleep -Seconds 5
    $h = $p.MainWindowHandle
    for ($i = 0; $i -lt 30 -and $h -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 500
        $p.Refresh()
        $h = $p.MainWindowHandle
    }
    if ($h -eq [IntPtr]::Zero) { Write-Host "no window ($theme)"; continue }
    $r = New-Object W32+RECT
    [void][W32]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][W32]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $out = Join-Path $OutDir ("gui-" + $theme + ".png")
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "saved $out ($w x $ht)"
    Stop-Process -Id $p.Id -Force
}
