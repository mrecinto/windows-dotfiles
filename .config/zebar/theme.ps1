param([switch]$Toggle)

$ErrorActionPreference = 'Stop'
$personalize = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$light = (Get-ItemProperty -LiteralPath $personalize).AppsUseLightTheme -ne 0

if ($Toggle) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ThemeNotification {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);
}
'@
    $light = -not $light
    foreach ($name in 'AppsUseLightTheme', 'SystemUsesLightTheme') {
        Set-ItemProperty -LiteralPath $personalize -Name $name -Value ([int]$light) -Type DWord
    }

    $result = [UIntPtr]::Zero
    # WM_SETTINGCHANGE (0x001A) to HWND_BROADCAST (0xFFFF).
    [void][ThemeNotification]::SendMessageTimeout(
        [IntPtr]0xFFFF, 0x001A, [IntPtr]::Zero, 'ImmersiveColorSet', 2, 100, [ref]$result)
}

if ($light) { 'light' } else { 'dark' }
