@echo off
setlocal
cd /d "C:\work\ELC\AI Detection\MailSort"
dotnet run --project Tools/HashProbe -- ^
  "C:\work\ELC\AI Detection\samples\00010.tif" ^
  "C:\work\ELC\AI Detection\samples\00010-ROTATEED.tiff" ^
  "C:\work\ELC\AI Detection\samples\00020.tif" ^
  "C:\work\ELC\AI Detection\samples\00030.tif" ^
  "C:\work\ELC\AI Detection\samples\00040.tif" ^
  "C:\work\ELC\AI Detection\samples\00050.tif" ^
  "C:\work\ELC\AI Detection\samples\00060.tif" ^
  "C:\work\ELC\AI Detection\samples\00070.tif" ^
  "C:\work\ELC\AI Detection\samples\00080.tif" ^
  "C:\work\ELC\AI Detection\samples\00090.tif" ^
  "C:\work\ELC\AI Detection\samples\00100.tif" ^
  "C:\work\ELC\AI Detection\samples\00110.tif" ^
  "C:\work\ELC\AI Detection\samples\00120.tif" ^
  "C:\work\ELC\AI Detection\samples\00130.tif" ^
  "C:\work\ELC\AI Detection\samples\00140.tif" ^
  "C:\work\ELC\AI Detection\samples\00150.tif" ^
  "C:\work\ELC\AI Detection\samples\00160.tif" ^
  "C:\work\ELC\AI Detection\samples\00170.tif" ^
  "C:\work\ELC\AI Detection\samples\00180.tif" ^
  "C:\work\ELC\AI Detection\samples\00190.tif" ^
  "C:\work\ELC\AI Detection\samples\00200.tif" ^
  "C:\work\ELC\AI Detection\samples\important areas.tif"
