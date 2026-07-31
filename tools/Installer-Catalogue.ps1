# Installe le catalogue repris de DiLand dans le dossier de donnees de l'application.
#
# Le depot contient catalog\products.diland.json (la reference, versionnee) ; l'application
# lit D:\PhotoStudioData\catalog\products.json. Ce script fait le pont, en conservant les
# produits deja configures qui n'existent pas dans le catalogue DiLand - typiquement la
# planche d'identite, qui porte un DEVMODE capture et un profil ICC.
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.

param(
    [string]$Source = "$PSScriptRoot\..\catalog\products.diland.json",
    [string]$DataRoot = 'D:\PhotoStudioData'
)

$ErrorActionPreference = 'Stop'

$cible = Join-Path $DataRoot 'catalog\products.json'
if (-not (Test-Path $Source)) { throw "Catalogue source introuvable : $Source" }
if (-not (Test-Path (Split-Path $cible))) { throw "Dossier de donnees introuvable : $(Split-Path $cible)" }

$nouveaux = Get-Content $Source -Raw | ConvertFrom-Json
$codesNouveaux = @{}
foreach ($p in $nouveaux) { $codesNouveaux[$p.Code] = $true }

$conserves = @()
if (Test-Path $cible) {
    # sauvegarde horodatee : on ne remplace jamais un catalogue sans filet
    $horodatage = Get-Date -Format 'yyyy-MM-dd-HHmmss'
    $sauvegarde = Join-Path (Split-Path $cible) "products.avant-diland-$horodatage.json"
    Copy-Item $cible $sauvegarde -Force
    Write-Output "Sauvegarde de l'ancien catalogue : $sauvegarde"

    $anciens = Get-Content $cible -Raw | ConvertFrom-Json
    foreach ($a in $anciens) {
        if (-not $codesNouveaux.ContainsKey($a.Code)) {
            $conserves += $a
            Write-Output ("  conserve : " + $a.Code + " - " + $a.Name)
        }
    }
}

$fusion = @($nouveaux) + @($conserves)
$fusion | ConvertTo-Json -Depth 6 | Set-Content $cible -Encoding UTF8

$nbNouveaux = @($nouveaux).Count
$nbConserves = @($conserves).Count
$nbTotal = @($fusion).Count

Write-Output ""
Write-Output "Catalogue installe : $cible"
Write-Output "  repris de DiLand : $nbNouveaux"
Write-Output "  conserves        : $nbConserves"
Write-Output "  total            : $nbTotal"
