@echo off
setlocal
cd /d "C:\work\ELC\AI Detection\MailSort"
dotnet run --project Tools/HashProbe -- ^
  "C:\work\ELC\AI Detection\samples\00010.tif" ^
  "C:\work\ELC\AI Detection\samples\00010-ROTATEED.tiff" ^
  "C:\work\ELC\AI Detection\samples\00020.tif" ^
  "C:\work\ELC\AI Detection\samples\00070.tif" ^
  "C:\work\ELC\AI Detection\samples\x00070.tif" ^
  "C:\work\ELC\AI Detection\samples\important areas.tif" ^
  "C:\work\ELC\AI Detection\samples\04960.tif" ^
  "C:\work\ELC\AI Detection\samples\04960 - Copy.tif" ^
  "C:\work\ELC\AI Detection\samples\04970.tif" ^
  "C:\work\ELC\AI Detection\samples\04970 - Copy.tif" ^
  "C:\work\ELC\AI Detection\samples\03240.tif" ^
  "C:\work\ELC\AI Detection\samples\03250.tif"
