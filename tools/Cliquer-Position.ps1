# Clique a une position donnee, relative a la fenetre d un processus.
#
# Utile pour les logiciels dont les boutons ne sont pas exposes a l automatisation
# d interface. A n employer qu en connaissant l ecran : capturer avant, cliquer,
# recapturer pour verifier.
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.
param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [string]$Processus = 'Studio.App',
    [switch]$Absolu
)

$ErrorActionPreference = 'Stop'

if (-not ([System.Management.Automation.PSTypeName]'Win32Souris').Type) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct POSRECT { public int Left, Top, Right, Bottom; }
public static class Win32Souris {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out POSRECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public const uint GAUCHE_BAS = 0x0002, GAUCHE_HAUT = 0x0004;
}
"@
}

$p = Get-Process -Name $Processus -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "Application '$Processus' introuvable." }

# amener la fenetre au premier plan : un clic synthetique atteint ce qui est visible
try { (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null } catch { }
[void][Win32Souris]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 600

$cible = @{ X = $X; Y = $Y }
if (-not $Absolu) {
    $r = New-Object POSRECT
    [void][Win32Souris]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    $cible.X = $r.Left + $X
    $cible.Y = $r.Top + $Y
}

[void][Win32Souris]::SetCursorPos($cible.X, $cible.Y)
Start-Sleep -Milliseconds 150
[Win32Souris]::mouse_event([Win32Souris]::GAUCHE_BAS, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[Win32Souris]::mouse_event([Win32Souris]::GAUCHE_HAUT, 0, 0, 0, [IntPtr]::Zero)

"Clic en ($($cible.X), $($cible.Y))"
