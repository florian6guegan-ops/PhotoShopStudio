# Mire de controle 10x15 pour le DE100.
# Polices en PIXELS (et non en points) : a 300 ppp, 1 point vaut 4,17 px, ce qui
# avait fait exploser la mise en page de la premiere version.
Add-Type -AssemblyName System.Drawing

$dpi = 300
$mm  = { param($v) [int][math]::Round($v * $dpi / 25.4) }

$w = & $mm 152
$h = & $mm 102

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$bmp.SetResolution($dpi, $dpi)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)

$noir = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::Black)
$gris = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120,120,120))

# --- cadres imbriques a 2, 4, 6, 8 mm du bord ---
# Ceux qui survivent au tirage donnent la marge reellement perdue.
$couleurs = @(
    @{ mmBord = 2; c = [System.Drawing.Color]::Red },
    @{ mmBord = 4; c = [System.Drawing.Color]::Blue },
    @{ mmBord = 6; c = [System.Drawing.Color]::FromArgb(0,150,0) },
    @{ mmBord = 8; c = [System.Drawing.Color]::Magenta }
)
$policeRepere = New-Object System.Drawing.Font("Arial", 26, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
foreach ($r in $couleurs) {
    $d = & $mm $r.mmBord
    $pen = New-Object System.Drawing.Pen($r.c, 3)
    $g.DrawRectangle($pen, $d, $d, $w - 2*$d, $h - 2*$d)
    $br = New-Object System.Drawing.SolidBrush($r.c)
    $g.DrawString("$($r.mmBord) mm", $policeRepere, $br, $d + 8, $d + 4)
    $pen.Dispose(); $br.Dispose()
}

# --- croix de centrage ---
$penGris = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150,150,150), 2)
$g.DrawLine($penGris, [int]($w/2), [int]($h/2 - (& $mm 8)), [int]($w/2), [int]($h/2 + (& $mm 8)))
$g.DrawLine($penGris, [int]($w/2 - (& $mm 8)), [int]($h/2), [int]($w/2 + (& $mm 8)), [int]($h/2))

# --- textes, dans la zone sure, interlignes calcules ---
$titre = New-Object System.Drawing.Font("Arial", 62, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$corps = New-Object System.Drawing.Font("Arial", 34, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

$x = & $mm 12
$y = & $mm 14
$g.DrawString("MIRE DE CONTROLE", $titre, $noir, $x, $y)
$y += 78
$g.DrawString("Studio Photo - 10x15 - 300 ppp", $corps, $gris, $x, $y)
$y += 46
$g.DrawString((Get-Date -Format 'dd/MM/yyyy HH:mm'), $corps, $gris, $x, $y)
$y += 46
$g.DrawString("Cadre le plus externe visible = marge perdue", $corps, $gris, $x, $y)

# --- reperes d angle : L dans chaque coin, a 10 mm ---
$penAngle = New-Object System.Drawing.Pen([System.Drawing.Color]::Black, 4)
$a = & $mm 10
$b = & $mm 18
foreach ($coin in @(@(1,1), @(-1,1), @(1,-1), @(-1,-1))) {
    $cx = if ($coin[0] -gt 0) { $a } else { $w - $a }
    $cy = if ($coin[1] -gt 0) { $a } else { $h - $a }
    $g.DrawLine($penAngle, $cx, $cy, $cx + $coin[0]*($b-$a), $cy)
    $g.DrawLine($penAngle, $cx, $cy, $cx, $cy + $coin[1]*($b-$a))
}

# --- bandes de couleur en bas, dans la zone sure ---
$bandes = @(
    [System.Drawing.Color]::Cyan, [System.Drawing.Color]::Magenta,
    [System.Drawing.Color]::Yellow, [System.Drawing.Color]::Black,
    [System.Drawing.Color]::FromArgb(128,128,128), [System.Drawing.Color]::White
)
$bx = & $mm 12
$by = & $mm 66
$bw = [int](($w - 2*$bx) / $bandes.Count)
$bh = & $mm 24
for ($i = 0; $i -lt $bandes.Count; $i++) {
    $br = New-Object System.Drawing.SolidBrush($bandes[$i])
    $g.FillRectangle($br, $bx + $i*$bw, $by, $bw, $bh)
    $br.Dispose()
}
$penCadre = New-Object System.Drawing.Pen([System.Drawing.Color]::Black, 2)
$g.DrawRectangle($penCadre, $bx, $by, $bw*$bandes.Count, $bh)

$g.Dispose()
$out = "C:\Users\DELL\AppData\Local\Temp\claude\C--Windows-system32\95e4618e-42af-43ed-be9a-e21677f83854\scratchpad\mire-controle-10x15.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"Mire generee : $out  (${w}x${h} px, 152x102 mm a $dpi ppp)"
