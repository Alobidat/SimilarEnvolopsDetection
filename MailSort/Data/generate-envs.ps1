# Generates realistic envelope images for hash benchmarking.
# Output: data/env-real-N.jpg  (three different envelopes)
#         data/env-real-N-2nd*.jpg  (perturbed 2nd-pass variants of envelope 1)

$data = "C:\work\ELC\AI Detection\MailSort\data"
Add-Type -AssemblyName System.Drawing

function New-EnvelopeImage {
    param([string]$Abs, [int]$Seed)
    $rnd = New-Object System.Random $Seed
    $W = 603; $H = 960
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $bmp2 = New-Object System.Drawing.Bitmap $W, $H
    $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g2.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $paper = [System.Drawing.Color]::FromArgb(232, 224, 210)
    $g2.Clear($paper)

    $addrFont  = New-Object System.Drawing.Font "Arial", 22, ([System.Drawing.FontStyle]::Bold)
    $addrFont2 = New-Object System.Drawing.Font "Arial", 18
    $barFont   = New-Object System.Drawing.Font "Consolas", 24, ([System.Drawing.FontStyle]::Bold)

    $lines = @(
        "To: $($rnd.Next(100,999)) Main Street",
        "Recipient #$Seed",
        "City, State $($rnd.Next(10000,99999))"
    )
    $yStart = 480
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $g2.DrawString($lines[$i], $addrFont, [System.Drawing.Brushes]::Black, 30, $yStart + $i * 36)
    }

    # 2D barcode region (bottom-right) -- varied pattern
    $bx = 380; $by = 760; $cellSize = 8; $grid = 18
    for ($yy = 0; $yy -lt $grid; $yy++) {
        for ($xx = 0; $xx -lt $grid; $xx++) {
            if ($rnd.NextDouble() -lt 0.5) {
                $g2.FillRectangle([System.Drawing.Brushes]::Black,
                    ($bx + $xx * $cellSize), ($by + $yy * $cellSize), $cellSize, $cellSize)
            }
        }
    }
    $g2.FillRectangle([System.Drawing.Brushes]::White, $bx - 12, $by - 12,
        ($grid * $cellSize) + 24, ($grid * $cellSize) + 24)
    $g2.DrawString("BC-$('{0:D6}' -f $Seed)", $barFont, [System.Drawing.Brushes]::Black, 30, 880)

    # Paper grain
    for ($n = 0; $n -lt 5000; $n++) {
        $nx = $rnd.Next(0, $W)
        $ny = $rnd.Next(0, $H)
        $nv = $rnd.Next(-12, 12)
        $pc = $bmp2.GetPixel($nx, $ny)
        $r = [Math]::Max(0, [Math]::Min(255, $pc.R + $nv))
        $gv = [Math]::Max(0, [Math]::Min(255, $pc.G + $nv))
        $b = [Math]::Max(0, [Math]::Min(255, $pc.B + $nv))
        $bmp2.SetPixel($nx, $ny, [System.Drawing.Color]::FromArgb($r, $gv, $b))
    }

    $bmp2.Save($Abs, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $g2.Dispose(); $bmp2.Dispose(); $bmp.Dispose()
}

function New-Perturbed {
    param([string]$Src, [string]$Dst, [float]$Tilt, [float]$Brightness)
    $bmp = [System.Drawing.Image]::FromFile($Src)
    $W = $bmp.Width; $H = $bmp.Height
    $bmp2 = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp2)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    if ($Tilt -ne 0) {
        $g.TranslateTransform($W/2, $H/2)
        $g.Rotate($Tilt)
        $g.TranslateTransform(-$W/2, -$H/2)
    }
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $m = New-Object System.Drawing.Imaging.ColorMatrix
    $b = $Brightness / 100.0
    $m.Matrix00 = [float](1.0 + $b)
    $m.Matrix11 = [float](1.0 + $b)
    $m.Matrix22 = [float](1.0 + $b)
    $m.Matrix33 = [float]1.0
    $ia.SetColorMatrix($m)
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0,0,$W,$H),
        0,0,$W,$H, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $bmp2.Save($Dst, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $g.Dispose(); $bmp2.Dispose(); $bmp.Dispose(); $ia.Dispose()
}

# Three different envelopes
1..3 | ForEach-Object {
    New-EnvelopeImage -Abs "$data\env-real-$_.jpg" -Seed $_
}

# 2nd-pass variants of envelope 1 with realistic perturbations
New-Perturbed -Src "$data\env-real-1.jpg" -Dst "$data\env-real-1-2nd-tilt.jpg"  -Tilt 3   -Brightness 0
New-Perturbed -Src "$data\env-real-1.jpg" -Dst "$data\env-real-1-2nd-bright.jpg" -Tilt 2 -Brightness 25
New-Perturbed -Src "$data\env-real-1.jpg" -Dst "$data\env-real-1-2nd-dim.jpg"    -Tilt 4 -Brightness -25
New-Perturbed -Src "$data\env-real-1.jpg" -Dst "$data\env-real-1-2nd-tilt8.jpg"   -Tilt 8 -Brightness -10

Get-ChildItem "$data\env-real-*.jpg" | Format-Table Name, Length
