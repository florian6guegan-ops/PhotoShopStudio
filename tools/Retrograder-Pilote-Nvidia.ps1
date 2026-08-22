# Retrograde le pilote NVIDIA d'un poste vers une version anterieure.
#
# POURQUOI CE SCRIPT EXISTE. Studio.App a ete emporte deux fois a Creteil par
# nvd3dumx.dll (0xc0000005, les 13 et 21/08/2026) - le pilote Direct3D de NVIDIA, tue par
# DirectML pendant un detourage. Le meme logiciel, le meme modele et la meme charge ne
# plantent jamais a Maisons-Alfort, qui tourne en 582.41 la ou Creteil est en 591.86. Le
# module qui plante est ce pilote, et la seule machine qui plante est celle qui l'a.
#
# CE N'EST PAS UNE CERTITUDE, c'est l'hypothese la mieux etayee. Le script est donc fait
# pour etre ANNULABLE : il note la version de depart, et l'on peut toujours remonter.
#
# Usage :
#   .\Retrograder-Pilote-Nvidia.ps1 -Verifier        n'installe RIEN, dit ce qui serait fait
#   .\Retrograder-Pilote-Nvidia.ps1 -Installer       installe pour de bon
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.

param(
    [string]$Paquet = "C:\PilotesNvidia\582.66-geforce-whql.exe",
    [switch]$Verifier,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'

function Etat {
    $smi = "C:\Windows\System32\nvidia-smi.exe"
    if (Test-Path $smi) {
        $l = & $smi --query-gpu=name,driver_version,memory.total --format=csv,noheader
        "  Carte  : $l"
    }
    Get-CimInstance Win32_VideoController |
        Where-Object { $_.Name -match "NVIDIA" } |
        ForEach-Object { "  Pilote : $($_.DriverVersion)  ($($_.Name))" }
}

Write-Host ""
Write-Host "  ETAT ACTUEL" -ForegroundColor Cyan
Etat

# --- Les verifications, TOUTES faites avant de toucher a quoi que ce soit -----------

$soucis = @()

if (-not (Test-Path $Paquet)) { $soucis += "Paquet introuvable : $Paquet" }
else {
    $mo = (Get-Item $Paquet).Length / 1MB
    if ($mo -lt 500) { $soucis += ("Paquet suspect : {0:N0} Mo, on en attend ~870" -f $mo) }
    else { Write-Host ("  Paquet : {0:N0} Mo" -f $mo) }
}

$libre = (Get-PSDrive C).Free / 1GB
if ($libre -lt 5) { $soucis += ("Moins de 5 Go libres sur C: ({0:N1})" -f $libre) }

# ⚠ LE POSTE DOIT ETRE AU REPOS. L'installation coupe l'affichage, redemarre le pilote et
# peut fermer une session distante : un tirage en cours serait perdu, et le minilab peut
# rester avec un travail a moitie envoye.
$genants = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "Studio\.App|Studio\.Identite|Studio\.De100Host|FitEng" }
if ($genants) {
    $soucis += "A FERMER D'ABORD : " + (($genants | Select-Object -ExpandProperty Name -Unique) -join ", ")
}

# Parsec n'empeche pas l'installation, mais la session distante SAUTERA : il faut le savoir
# avant, et garder SSH comme filet - lui ne depend pas de la carte graphique.
if (Get-Process parsecd -ErrorAction SilentlyContinue) {
    Write-Host "  ⚠ Parsec tourne : la session distante coupera pendant l'installation." -ForegroundColor Yellow
    Write-Host "    SSH, lui, survivra - c'est par la qu'on reprendra la main." -ForegroundColor Yellow
}

Write-Host ""
if ($soucis.Count -gt 0) {
    Write-Host "  RIEN N'A ETE FAIT :" -ForegroundColor Red
    $soucis | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  Toutes les verifications passent." -ForegroundColor Green

if (-not $Installer) {
    Write-Host ""
    Write-Host "  Mode verification : rien n'a ete installe." -ForegroundColor Cyan
    Write-Host "  Relancer avec -Installer pour le faire."
    exit 0
}

# --- L'installation ----------------------------------------------------------------

# La version de depart est notee AVANT, sur le disque : c'est elle qu'on redemandera a
# NVIDIA si le retrogradage ne change rien, et un ecran noir n'est pas le moment de la
# chercher.
$trace = Join-Path (Split-Path $Paquet) "version-avant-retrogradage.txt"
(Etat) -join "`r`n" | Set-Content $trace -Encoding UTF8
Write-Host "  Version de depart notee dans $trace"

Write-Host ""
Write-Host "  Installation en cours - l'ecran va s'eteindre plusieurs fois." -ForegroundColor Yellow

# -s installe sans interface, -clean repart d'une installation neuve (c'est le point :
# on ne veut pas d'un reste de la 591.86), -noreboot pour choisir NOUS-MEMES le moment.
$p = Start-Process -FilePath $Paquet -ArgumentList "-s", "-clean", "-noreboot" -Wait -PassThru
Write-Host "  Programme d'installation termine (code $($p.ExitCode))."

Write-Host ""
Write-Host "  ETAT APRES" -ForegroundColor Cyan
Etat

Write-Host ""
Write-Host "  ⚠ REDEMARRER LE POSTE pour que le pilote soit reellement en service." -ForegroundColor Yellow
Write-Host "    Puis rouvrir Studio et faire un detourage pour verifier."
