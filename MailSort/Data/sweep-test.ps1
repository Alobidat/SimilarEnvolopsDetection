$url = "http://localhost:5199"
$samples = "C:\work\ELC\AI Detection\samples"
$scanId = 0

function Ingest-Sample($file, $barcode) {
    $script:scanId++
    try {
        # Use curl.exe for proper multipart upload.
        $args = @("-s", "-X", "POST", "$url/api/ingest",
                  "-F", "image=@$file",
                  "-F", "scanId=scan-$($script:scanId)")
        if ($barcode) { $args += @("-F", "barcode=$barcode") }
        $json = & curl.exe @args
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  curl failed (exit=$LASTEXITCODE) on $file" -ForegroundColor Red
            return $null
        }
        return ($json | ConvertFrom-Json)
    } catch {
        Write-Host "  ERROR on $file : $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

Write-Host "--- 2nd-pass sweep across the 00010-00200 series (all should match as same envelope):"
$pass = 0
$fail = 0
foreach ($i in 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200) {
    $name = if ($i -lt 100) { "000{0:D2}.tif" -f $i } else { "00{0}.tif" -f $i }
    $f = Join-Path $samples $name
    $r = Ingest-Sample $f $null
    $ok = ($r.status -eq "Processed" -and $r.tray -eq 99)
    if ($ok) { $pass++ } else { $fail++ }
    $trayStr = if ($null -ne $r.tray) { $r.tray.ToString() } else { "" }
    $addrStr = if ($null -ne $r.addressPHashDistance) { $r.addressPHashDistance.ToString() } else { "" }
    $barStr  = if ($null -ne $r.barcodePHashDistance) { $r.barcodePHashDistance.ToString() } else { "" }
    $label = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("{0,-15} status={1,-15} tray={2,3} addr={3,3} barcode={4,3}  {5}" -f $name, $r.status, $trayStr, $addrStr, $barStr, $label)
}
Write-Host ""
Write-Host ("--- Summary: {0} pass, {1} fail (out of {2})" -f $pass, $fail, ($pass+$fail))

Write-Host ""
Write-Host "--- Decoy test (different envelope):"
$decoyFiles = @("03240.tif", "03250.tif", "04960.tif", "04970.tif", "x00070.tif")
foreach ($d in $decoyFiles) {
    $r = Ingest-Sample (Join-Path $samples $d) $null
    $trayStr = if ($null -ne $r.tray) { $r.tray.ToString() } else { "" }
    $addrStr = if ($null -ne $r.addressPHashDistance) { $r.addressPHashDistance.ToString() } else { "" }
    $label = if ($r.status -eq "NeedsManualEntry") { "PASS rejected" } else { "FAIL accepted" }
    Write-Host ("{0,-25} status={1,-15} tray={2,3} addr={3,3}  {4}" -f $d, $r.status, $trayStr, $addrStr, $label)
}
