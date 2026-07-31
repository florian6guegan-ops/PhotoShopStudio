# Capture la fenetre de Studio Photo dans un PNG.
#
# Sert a regarder l interface reellement rendue : le XAML decrit une intention, pas
# un resultat. Un bouton peut etre present dans le code et illisible a l ecran.
#
# On passe par PrintWindow, qui demande a la fenetre de se dessiner elle-meme : elle
# est capturee meme si une autre fenetre la recouvre, et on evite deux ecueils -
# Windows refuse le passage au premier plan a un processus en arriere-plan, et
# restaurer une fenetre maximisee la reduit.
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.
param(
    [string]$Sortie = "$env:TEMP\studio-capture.png",
    [string]$Processus = 'Studio.App',
    [int]$AttenteSecondes = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ([System.Management.Automation.PSTypeName]'Win32Fenetre').Type) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class Win32Fenetre {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@
}

if ($AttenteSecondes -gt 0) { Start-Sleep -Seconds $AttenteSecondes }

$p = Get-Process -Name $Processus -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "Fenetre introuvable pour le processus '$Processus'. L application est-elle lancee ?" }

$r = New-Object RECT
[void][Win32Fenetre]::GetWindowRect($p.MainWindowHandle, [ref]$r)
$largeur = $r.Right - $r.Left
$hauteur = $r.Bottom - $r.Top
if ($largeur -le 0 -or $hauteur -le 0) { throw "Dimensions de fenetre invalides." }

$bmp = New-Object System.Drawing.Bitmap($largeur, $hauteur)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
try {
    # 2 = PW_RENDERFULLCONTENT, indispensable pour les fenetres WPF
    [void][Win32Fenetre]::PrintWindow($p.MainWindowHandle, $hdc, 2)
}
finally {
    $g.ReleaseHdc($hdc)
    $g.Dispose()
}

$bmp.Save($Sortie, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

"Capture : $Sortie (${largeur}x${hauteur})"
