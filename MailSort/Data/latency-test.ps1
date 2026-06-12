$url = "http://localhost:5199"
$samples = "C:\work\ELC\AI Detection\samples"

# Re-add BC-REF
$null = & curl.exe -s -X POST "$url/api/tray-map" -H "Content-Type: application/json" -d '{"barcode":"BC-REF","tray":99}'

# Re-ingest 00010 with barcode to seed
$null = & curl.exe -s -X POST "$url/api/ingest" -F "image=@$samples/00010.tif" -F "barcode=BC-REF" -F "scanId=seed-1" | Out-Null

# Warm up
$null = & curl.exe -s -X POST "$url/api/ingest" -F "image=@$samples/00020.tif" -F "scanId=warmup" | Out-Null

Write-Host "--- Latency on real samples (5 warm runs of 00020.tif as a 2nd pass):"
for ($i = 1; $i -le 5; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $null = & curl.exe -s -X POST "$url/api/ingest" -F "image=@$samples/00020.tif" -F "scanId=lat-$i" | Out-Null
    $sw.Stop()
    Write-Host ("  warm-$i : " + [int]$sw.ElapsedMilliseconds + " ms")
}

Write-Host "--- Cold start:"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$null = & curl.exe -s -X POST "$url/api/ingest" -F "image=@$samples/00030.tif" -F "scanId=cold" | Out-Null
$sw.Stop()
Write-Host ("  cold : " + [int]$sw.ElapsedMilliseconds + " ms")
