# Liste les elements cliquables d une fenetre, via l automatisation d interface Windows.
# Sert a naviguer sans cliquer a l aveugle : on voit d abord ce qui existe.
#
# Script volontairement sans accent : PowerShell 5.1 lit les .ps1 en ANSI.
param(
    [string]$Processus = 'Studio.App',
    [switch]$Tout
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Get-Process -Name $Processus -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "Application '$Processus' introuvable." }

$fenetre = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

$types = if ($Tout) {
    @([System.Windows.Automation.ControlType]::Button,
      [System.Windows.Automation.ControlType]::Text,
      [System.Windows.Automation.ControlType]::ListItem,
      [System.Windows.Automation.ControlType]::TabItem,
      [System.Windows.Automation.ControlType]::Image)
} else {
    @([System.Windows.Automation.ControlType]::Button,
      [System.Windows.Automation.ControlType]::ListItem,
      [System.Windows.Automation.ControlType]::TabItem)
}

foreach ($t in $types) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $t)
    $elements = $fenetre.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)

    foreach ($e in $elements) {
        $nom = $e.Current.Name
        if ([string]::IsNullOrWhiteSpace($nom)) { $nom = '(sans nom)' }
        $r = $e.Current.BoundingRectangle
        "{0,-12} {1,-45} x={2,-6:0} y={3,-6:0} {4,4:0}x{5:0}" -f `
            $t.ProgrammaticName.Replace('ControlType.', ''), $nom.Trim(), $r.X, $r.Y, $r.Width, $r.Height
    }
}
