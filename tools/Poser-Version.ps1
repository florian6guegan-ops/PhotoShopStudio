# Pose une version de Studio Photo (ou de Studio Photo Identite) SANS RIEN COUPER.
#
# Le poste bascule au prochain demarrage de l'application : on installe dans un dossier
# NEUF versionne, puis on repointe les raccourcis. Aucun fichier de l'installation en
# service n'est touche, il n'y a donc rien a arreter en pleine journee.
#
# ⚠ On ne LANCE PAS l'application : une session SSH est la session 0, l'operateur ne
# verrait jamais la fenetre et le processus tiendrait le tube du relais.
#
# Sans accent : PowerShell 5.1 lit les .ps1 en ANSI.

param(
    [Parameter(Mandatory = $true)][string]$Url,
    [Parameter(Mandatory = $true)][string]$Sha256,
    [Parameter(Mandatory = $true)][string]$Cible,
    [Parameter(Mandatory = $true)][string]$Exe      # Studio.App.exe ou Studio.Identite.exe
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Output "== $env:COMPUTERNAME : $Exe vers $Cible =="

if (Test-Path (Join-Path $Cible $Exe)) {
    Write-Output "DEJA_POSE $Cible"
} else {
    $zip = Join-Path $env:TEMP ([IO.Path]::GetFileName($Url))
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Write-Output "Telechargement..."
    $chrono = [Diagnostics.Stopwatch]::StartNew()
    try {
        & curl.exe -sSL -o $zip $Url
        if ($LASTEXITCODE -ne 0) { throw "curl a rendu $LASTEXITCODE" }
    } catch {
        Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    }
    $chrono.Stop()
    $mo = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Output "  $mo Mo en $([Math]::Round($chrono.Elapsed.TotalSeconds, 1)) s"

    # ⚠ On verifie AVANT d'extraire : une archive tronquee s'extrait souvent sans erreur
    # et laisse une installation amputee, qui ne se voit qu'au lancement chez l'operateur.
    $vu = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($vu -ne $Sha256.ToLower()) {
        Remove-Item $zip -Force
        throw "SHA256 different : attendu $Sha256, obtenu $vu. Rien n'a ete installe."
    }
    Write-Output "  SHA256 conforme"

    Write-Output "Extraction..."
    if (Test-Path $Cible) { Remove-Item $Cible -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $Cible -Force
    Remove-Item $zip -Force
}

$exeComplet = Join-Path $Cible $Exe
if (-not (Test-Path $exeComplet)) { throw "L'executable manque : $exeComplet" }

$dll = [IO.Path]::ChangeExtension($exeComplet, '.dll')
$version = (Get-Item $dll).VersionInfo.ProductVersion
Write-Output "POSE version=$version"

# --- Les raccourcis, retrouves sur place ---
#
# On ne les recoit PAS en parametre : le compte de Creteil porte un espace ET un accent,
# et un chemin accentue passe en ligne de commande revient corrompu. Les chercher ici evite
# la question, et attrape ceux qu'on n'aurait pas enumeres — c'est la barre des taches,
# oubliee, qui a laisse precision six semaines en retard.
$shell = New-Object -ComObject WScript.Shell
$emplacements = @()
foreach ($profil in (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue)) {
    $emplacements += Join-Path $profil.FullName 'Desktop'
    $emplacements += Join-Path $profil.FullName 'AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
    $emplacements += Join-Path $profil.FullName 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs'
}

$repointes = 0
foreach ($dossier in ($emplacements | Sort-Object -Unique)) {
    if (-not (Test-Path $dossier)) { continue }
    foreach ($fichier in (Get-ChildItem $dossier -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue)) {
        try {
            $lnk = $shell.CreateShortcut($fichier.FullName)
            if ([IO.Path]::GetFileName($lnk.TargetPath) -ne $Exe) { continue }
            if ($lnk.TargetPath -eq $exeComplet) {
                Write-Output "RACCOURCI (deja bon) $($fichier.FullName)"
                $repointes++
                continue
            }
            $avant = $lnk.TargetPath
            $lnk.TargetPath = $exeComplet
            $lnk.WorkingDirectory = $Cible
            $lnk.Save()
            Write-Output "RACCOURCI $($fichier.FullName) : $avant --> $exeComplet"
            $repointes++
        } catch {
            Write-Output "RACCOURCI ILLISIBLE $($fichier.FullName) : $($_.Exception.Message)"
        }
    }
}

if ($repointes -eq 0) {
    Write-Output "AUCUN_RACCOURCI - l'operateur ouvrirait encore l'ancienne version"
}

# --- Ce qui tourne en ce moment, pour savoir ce qu'il reste a faire cote humain ---
$nomProc = [IO.Path]::GetFileNameWithoutExtension($Exe)
$enCours = @(Get-Process -Name $nomProc -ErrorAction SilentlyContinue)
if ($enCours.Count -eq 0) {
    Write-Output "EN_COURS aucun - la prochaine ouverture prendra la $version"
} else {
    foreach ($p in $enCours) {
        Write-Output "EN_COURS $($p.Path) - A RELANCER par l'operateur pour basculer"
    }
}
