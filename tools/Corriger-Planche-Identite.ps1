# Passe la planche d identite en paysage et la regle sur 8 vignettes.
#
# Sur un 10x15 PORTRAIT (102 x 152 mm), des vignettes de 35 x 45 ne tiennent qu a
# 2 colonnes x 3 rangees, soit 6 au maximum. En PAYSAGE (152 x 102), on obtient
# 4 colonnes x 2 rangees, soit 8. C est une contrainte geometrique, pas un reglage.
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.
param(
    [string]$DataRoot = 'D:\PhotoStudioData',
    [int]$Vignettes = 8
)

$ErrorActionPreference = 'Stop'

$cible = Join-Path $DataRoot 'catalog\products.json'
if (-not (Test-Path $cible)) { throw "Catalogue introuvable : $cible" }

$horodatage = Get-Date -Format 'yyyy-MM-dd-HHmmss'
$sauvegarde = Join-Path (Split-Path $cible) "products.avant-planche-$horodatage.json"
Copy-Item $cible $sauvegarde -Force
Write-Output "Sauvegarde : $sauvegarde"

$produits = Get-Content $cible -Raw | ConvertFrom-Json
$planches = $produits | Where-Object { $_.Sheet }

if (-not $planches) { throw "Aucun produit planche dans le catalogue." }

foreach ($p in $planches) {
    $ancien = "$($p.WidthMm) x $($p.HeightMm), $($p.Sheet.Copies) vignettes"

    # paysage : le grand cote en largeur
    $grand = [math]::Max($p.WidthMm, $p.HeightMm)
    $petit = [math]::Min($p.WidthMm, $p.HeightMm)
    $p.WidthMm = $grand
    $p.HeightMm = $petit
    $p.Sheet.Copies = $Vignettes

    if ($p.Name -match '\(planche de \d+\)') {
        $p.Name = $p.Name -replace '\(planche de \d+\)', "(planche de $Vignettes)"
    }

    Write-Output "  $($p.Code) : $ancien  ->  $($p.WidthMm) x $($p.HeightMm), $($p.Sheet.Copies) vignettes"
}

$produits | ConvertTo-Json -Depth 6 | Set-Content $cible -Encoding UTF8
Write-Output "Catalogue mis a jour : $cible"
