@echo off
rem Corpus PE restore launcher (%1 = media drive, passed by the startnet hook).
set MED=%1
if "%MED%"=="" set MED=%CD:~0,2%
echo Corpus recovery: media on %MED%
echo.
"%MED%\Corpus\Corpus.exe" --list-disks
echo.
echo To restore: %MED%\Corpus\Corpus.exe --restore --snapshot GEN --target DISK# --store %MED%\Corpus\store --confirm-type TOKEN
echo   (run once: the refusal names the want-TOKEN. --dry-run plans without writing.)
cmd /k "echo Restore shell ready. See %MED%\Corpus\README-restore.txt"
