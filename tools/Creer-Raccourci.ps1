# Cree - ou repare - le raccourci « Studio Photo » du bureau.
#
# Il existe pour une raison precise : le raccourci lance un fichier .cmd, et Windows donne
# alors au raccourci l'icone de l'invite de commandes. Le logo de l'application ne se voyait
# donc nulle part sur le bureau. Un raccourci porte SA propre icone, independamment de sa
# cible : c'est ce que ce script pose.
#
# Usage :
#   .\tools\Creer-Raccourci.ps1              cree ou met a jour le raccourci du bureau
#   .\tools\Creer-Raccourci.ps1 -Nom "..."   sous un autre nom
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.

param(
    [string]$Nom = "Studio Photo"
)

$ErrorActionPreference = 'Stop'

$racine = Split-Path -Parent $PSScriptRoot
$cible = Join-Path $racine 'tools\Lancer-Studio.cmd'
$icone = Join-Path $racine 'src\Studio.App\Assets\studio-photo.ico'

if (-not (Test-Path $cible)) { throw "Lanceur introuvable : $cible" }
if (-not (Test-Path $icone)) { throw "Icone introuvable : $icone" }

# GetFolderPath et non "$env:USERPROFILE\Bureau" : le dossier Bureau porte le nom de la
# langue de Windows, et il peut avoir ete deplace (OneDrive).
$bureau = [Environment]::GetFolderPath('Desktop')
$chemin = Join-Path $bureau "$Nom.lnk"

$shell = New-Object -ComObject WScript.Shell
$raccourci = $shell.CreateShortcut($chemin)

$raccourci.TargetPath = $cible
$raccourci.WorkingDirectory = $racine
$raccourci.Description = "Studio Photo - laboratoire photo de la boutique"

# ",0" : la premiere icone du fichier. Sans l'indice, Windows ignore la ligne.
$raccourci.IconLocation = "$icone,0"

$raccourci.Save()

Write-Host ""
Write-Host "  Raccourci ecrit : $chemin"
Write-Host "  Icone           : $icone"
Write-Host ""
Write-Host "  Si l'ancienne icone reste affichee, c'est le cache d'icones de Windows :"
Write-Host "    ie4uinit.exe -show"
Write-Host ""
