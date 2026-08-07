# Publie une version de Studio Photo sur GitHub, pour que les postes des collegues la
# recuperent tout seuls.
#
# Ce script fait TOUT ce qu'il faut et rien de plus :
#   1. il lit la version de Directory.Build.props ;
#   2. il refuse de publier une version deja parue (sinon aucun poste ne verrait rien) ;
#   3. il compile une version autonome - le collegue n'a AUCUN runtime a installer ;
#   4. il en fait une archive ;
#   5. il cree la publication GitHub et y joint l'archive.
#
# Usage :
#   .\tools\Publier.ps1                      publie la version de Directory.Build.props
#   .\tools\Publier.ps1 -Notes "..."         avec la description montree aux collegues
#   .\tools\Publier.ps1 -Essai               prepare tout SANS rien publier
#
# Prerequis : GitHub CLI (gh) installe et connecte - "gh auth login" une fois pour toutes.
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.

param(
    [string]$Notes = "",
    [switch]$Essai
)

$ErrorActionPreference = 'Stop'

$racine = Split-Path -Parent $PSScriptRoot
$props = Join-Path $racine 'Directory.Build.props'
$projet = Join-Path $racine 'src\Studio.App\Studio.App.csproj'

# ---------------------------------------------------------------------------
# 1. La version
# ---------------------------------------------------------------------------

[xml]$xml = Get-Content $props
$version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (-not $version) {
    throw "Aucune <Version> dans Directory.Build.props."
}

$etiquette = "v$version"
Write-Host ""
Write-Host "  Studio Photo - publication de la version $version"
Write-Host "  ------------------------------------------------"
Write-Host ""

# ---------------------------------------------------------------------------
# 2. Cette version est-elle deja parue ?
#
# Republier la meme etiquette ne mettrait a jour AUCUN poste : la comparaison est
# strictement superieure. Mieux vaut s'arreter ici que de croire avoir livre.
# ---------------------------------------------------------------------------

if (-not $Essai) {
    # gh ecrit « release not found » sur sa sortie d'erreur quand la version n'existe pas
    # — c'est le cas NORMAL, et c'est justement celui qu'on cherche. Sous
    # $ErrorActionPreference = 'Stop', PowerShell transforme cette ligne en erreur
    # terminante et le script s'arretait avant meme de compiler. On desarme donc le temps
    # de l'appel, et l'on ne juge que sur le code de sortie.
    $avant = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & gh release view $etiquette --json tagName *> $null
    $dejaPubliee = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $avant

    if ($dejaPubliee) {
        throw "La version $version est deja publiee. Montez <Version> dans Directory.Build.props avant de publier."
    }
}

# ---------------------------------------------------------------------------
# 3. La compilation, en version AUTONOME
#
# self-contained : le collegue n'installe aucun runtime .NET. L'archive est plus
# grosse (~150 Mo) mais elle s'installe partout, et c'est exactement le probleme
# qu'on cherche a resoudre.
# ---------------------------------------------------------------------------

$publication = Join-Path $racine 'publish\StudioPhoto'

if (Test-Path $publication) { Remove-Item $publication -Recurse -Force }

Write-Host "  Compilation (version autonome, cela prend quelques minutes)..."
Write-Host ""

& dotnet publish $projet `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publication `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "La compilation a echoue : rien n'est publie." }

# Le relais du minilab est un executable 32 BITS separe : il ne sort pas de la
# publication ci-dessus, et sans lui le minilab ne repond plus du tout.
#
# Le sous-dossier s'appelle "de100" et pas autrement : c'est la ou l'application le
# cherche (De100BridgeClient.ProbeHostPaths). Ailleurs, elle ne le trouverait qu'en
# remontant vers une sortie de compilation qui n'existe pas chez un collegue.
$relais = Join-Path $racine 'tools\Studio.De100Host\Studio.De100Host.csproj'
$dossierRelais = Join-Path $publication 'de100'

Write-Host ""
Write-Host "  Compilation du relais 32 bits du minilab..."

& dotnet publish $relais -c Release -r win-x86 --self-contained true -o $dossierRelais --nologo
if ($LASTEXITCODE -ne 0) { throw "La compilation du relais a echoue : rien n'est publie." }

# ---------------------------------------------------------------------------
# 3 bis. Le catalogue de la boutique
#
# Sans lui, un poste neuf demarre sur les cinq produits d'amorcage - dont quatre
# pointent sur « Microsoft Print to PDF ». Le logiciel s'ouvre, affiche des tirages,
# et rien ne sort des machines pourtant presentes. C'est ce qu'a eu le poste de
# Creteil le 07/08/2026, et rien dans le guide ne disait comment s'en sortir.
#
# Le dossier s'appelle "catalogue" et pas autrement : c'est la que l'application le
# cherche au premier demarrage (CatalogueLivre.NomDuDossier).
#
# Il n'ecrase JAMAIS le catalogue d'un poste qui tourne deja : la pose n'a lieu que
# si catalog\products.json est absent.
$catalogueSource = Join-Path $racine 'catalog\boutique'
$catalogueCible = Join-Path $publication 'catalogue'

if (-not (Test-Path (Join-Path $catalogueSource 'products.json'))) {
    throw "Catalogue de la boutique introuvable : $catalogueSource. Lancez tools\Sauver-Catalogue.cmd."
}

Write-Host ""
Write-Host "  Catalogue de la boutique..."

New-Item -ItemType Directory -Path $catalogueCible -Force | Out-Null
Copy-Item (Join-Path $catalogueSource 'products.json') $catalogueCible -Force
Copy-Item (Join-Path $catalogueSource 'devmode-*.bin') $catalogueCible -Force -ErrorAction SilentlyContinue

# ConvertFrom-Json APPELE, et non branche sur un pipe : au bout d'un pipe, PowerShell 5.1
# rend le tableau comme un seul objet, et @(...).Count vaut 1 quel que soit le catalogue.
$produits = ConvertFrom-Json (Get-Content (Join-Path $catalogueCible 'products.json') -Raw)
$nbProduits = @($produits).Count
$nbDevmodes = @(Get-ChildItem $catalogueCible -Filter 'devmode-*.bin' -ErrorAction SilentlyContinue).Count
Write-Host "  $nbProduits produits, $nbDevmodes reglage(s) pilote."

# Le catalogue livre date-t-il de la derniere modification du catalogue VIVANT ? Un
# oubli de Sauver-Catalogue.cmd livrerait des prix perimes sans que rien ne le dise.
$vivant = Join-Path (Split-Path $racine -Qualifier) '\PhotoStudioData\catalog\products.json'
if (Test-Path $vivant) {
    $ecart = (Get-Item $vivant).LastWriteTime - (Get-Item (Join-Path $catalogueSource 'products.json')).LastWriteTime
    if ($ecart.TotalDays -gt 1) {
        Write-Host ""
        Write-Host ("  ATTENTION : le catalogue de la boutique a ete modifie il y a " +
                    [Math]::Round($ecart.TotalDays) + " jour(s) sans que la copie soit rafraichie.") -ForegroundColor Yellow
        Write-Host "  Lancez tools\Sauver-Catalogue.cmd puis recommencez, sinon vous livrez des prix perimes." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# 4. L'archive
# ---------------------------------------------------------------------------

$archive = Join-Path $racine "publish\StudioPhoto-$version.zip"
if (Test-Path $archive) { Remove-Item $archive -Force }

Write-Host ""
Write-Host "  Fabrication de l'archive..."
Compress-Archive -Path "$publication\*" -DestinationPath $archive -CompressionLevel Optimal

$taille = [Math]::Round((Get-Item $archive).Length / 1MB, 1)
Write-Host "  Archive : $archive ($taille Mo)"

if ($Essai) {
    Write-Host ""
    Write-Host "  ESSAI : rien n'a ete publie. L'archive est prete ci-dessus."
    Write-Host ""
    exit 0
}

# ---------------------------------------------------------------------------
# 5. La publication
# ---------------------------------------------------------------------------

if (-not $Notes) {
    $Notes = "Version $version de Studio Photo."
}

Write-Host ""
Write-Host "  Publication sur GitHub..."

& gh release create $etiquette $archive --title "Version $version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "La publication a echoue. L'archive reste dans publish\." }

Write-Host ""
Write-Host "  Publie. Les postes verront la mise a jour dans Parametres."
Write-Host "  Pensez a monter <Version> dans Directory.Build.props pour la prochaine fois."
Write-Host ""
