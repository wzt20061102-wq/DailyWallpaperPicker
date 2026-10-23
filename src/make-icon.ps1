Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class PickerIconNative{[DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);}
'@
$bitmap=[Drawing.Bitmap]::new(64,64)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
$background=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(21,26,32))
$front=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(125,220,200))
$white=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(244,247,250))
try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.FillRectangle($background,2,2,60,60)
    $graphics.FillRectangle($front,10,16,12,33)
    $graphics.FillRectangle($white,26,12,12,37)
    $graphics.FillRectangle($front,42,20,12,29)
    $handle=$bitmap.GetHicon()
    try {
        $icon=[Drawing.Icon]::FromHandle($handle)
        $stream=[IO.File]::Create("$PSScriptRoot\picker.ico")
        try {$icon.Save($stream)} finally {$stream.Dispose();$icon.Dispose()}
    } finally {[PickerIconNative]::DestroyIcon($handle)|Out-Null}
} finally {$white.Dispose();$front.Dispose();$background.Dispose();$graphics.Dispose();$bitmap.Dispose()}
Get-FileHash -LiteralPath "$PSScriptRoot\picker.ico" -Algorithm SHA256 | ConvertTo-Json
