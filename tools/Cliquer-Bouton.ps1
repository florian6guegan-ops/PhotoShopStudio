# Clique un bouton de Studio Photo par son libelle, via l automatisation d interface
# Windows. Permet de parcourir les ecrans sans souris et de les capturer un a un.
#
# Script volontairement sans accent dans le code : PowerShell 5.1 lit les .ps1 en ANSI.
# Le libelle recherche, lui, peut contenir des accents (il vient de la ligne de commande).
param(
    [Parameter(Mandatory = $true)][string]$Libelle,
    [string]$Processus = 'Studio.App',
    [int]$TimeoutSecondes = 10
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Get-Process -Name $Processus -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "Application '$Processus' introuvable." }

$fenetre = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

$fin = (Get-Date).AddSeconds($TimeoutSecondes)
do {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)

    $boutons = $fenetre.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)

    foreach ($b in $boutons) {
        $nom = $b.Current.Name
        if ($nom -and $nom.Trim() -like "*$Libelle*") {
            $motif = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $motif.Invoke()
            return "Clique : $($nom.Trim())"
        }
    }
    Start-Sleep -Milliseconds 400
} while ((Get-Date) -lt $fin)

# aide au diagnostic : on liste ce qui etait cliquable
$dispo = @()
foreach ($b in $boutons) { if ($b.Current.Name) { $dispo += $b.Current.Name.Trim() } }
throw "Bouton '$Libelle' introuvable. Boutons presents : " + ($dispo -join ' | ')
