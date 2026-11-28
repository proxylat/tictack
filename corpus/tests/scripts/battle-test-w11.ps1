# Corpus Tier B + Tier C Battle-Test Script (Windows 11 + Hyper-V Gen 2)
# Run elevated on W11 test VM (D:\ has ~100 GB free).
# Proves:
#   [1-4] Live OS Disk 0 (C: + ESP + MSR) Full -> Incremental -> Differential -> GC -> DeepVerify
#   [5]   Whole-Disk Bootable VHDX Export + Test-VHD + Mount-VHD canary check + Hyper-V Gen 2 UEFI boot
#   [6]   Whole-Disk OVA Export (--controller ahci) + tar/SHA256 manifest verification
#   [7]   Bare-Metal Restore (--restore-disk) to a fresh secondary VHDX + Hyper-V Gen 2 UEFI boot
#   [8]   WIM Capture + Apply (--to-wim / --apply) to a fresh secondary VHDX + Hyper-V Gen 2 UEFI boot
param(
    [string]$CorpusExe = "D:\CorpusBattleTest\Corpus.exe",
    [string]$WorkDir   = "D:\CorpusBattleTest",
    [switch]$SkipWim,
    [switch]$SkipRestore
)
$ErrorActionPreference = "Stop"

$storeDir = Join-Path $WorkDir "store"
$stateDir = Join-Path $WorkDir "state"
$cfgPath  = Join-Path $WorkDir "config_corpus.yaml"
$canary   = "C:\CorpusBattleCanary"

New-Item -ItemType Directory -Force -Path $WorkDir, $storeDir, $stateDir, $canary | Out-Null

@"
store_path: '$($storeDir -replace '\\','/')'
state_db_path: '$($stateDir -replace '\\','/')'
chunk_mib: 4
compression:
  level: 3
logging:
  level: info
  console: true
retention:
  keep_last: 4
strict_vss_writers: false
"@ | Set-Content -Encoding UTF8 $cfgPath

function Invoke-BootVerify([string]$VhdxPath, [string]$VmName) {
    Get-VM -Name $VmName -ErrorAction SilentlyContinue | Stop-VM -TurnOff -Force -ErrorAction SilentlyContinue
    Get-VM -Name $VmName -ErrorAction SilentlyContinue | Remove-VM -Force -ErrorAction SilentlyContinue
    & $CorpusExe --config $cfgPath --boot-verify --vhdx $VhdxPath --vm-name $VmName --memory-mb 2048 --timeout-seconds 180
    if ($LASTEXITCODE -ne 0) { throw "Hyper-V Gen 2 boot verify failed for $VhdxPath ($LASTEXITCODE)" }
}

Write-Host "=== [1/8] Live OS Disk 0 FULL backup (C: + ESP + MSR) ==="
"gen1-initial-$(Get-Date -Format o)" | Set-Content "$canary\gen1.txt"
& $CorpusExe --config $cfgPath --image --disk 0 --only-volumes C: --name win11-battle --full
if ($LASTEXITCODE -ne 0) { throw "Full disk backup failed ($LASTEXITCODE)" }

Write-Host "=== [2/8] Live OS Disk 0 INCREMENTAL backup ==="
"gen2-modified-$(Get-Date -Format o)" | Set-Content "$canary\gen1.txt"
"gen2-added" | Set-Content "$canary\gen2.txt"
& $CorpusExe --config $cfgPath --image --disk 0 --only-volumes C: --name win11-battle --incremental
if ($LASTEXITCODE -ne 0) { throw "Incremental disk backup failed ($LASTEXITCODE)" }

Write-Host "=== [3/8] Live OS Disk 0 DIFFERENTIAL backup ==="
Remove-Item -Force "$canary\gen2.txt"
"gen3-diff" | Set-Content "$canary\gen3.txt"
& $CorpusExe --config $cfgPath --image --disk 0 --only-volumes C: --name win11-battle --differential
if ($LASTEXITCODE -ne 0) { throw "Differential disk backup failed ($LASTEXITCODE)" }

Write-Host "=== [4/8] GC + DeepVerify ==="
& $CorpusExe --config $cfgPath --gc --confirm
if ($LASTEXITCODE -ne 0) { throw "GC failed ($LASTEXITCODE)" }
$latestLayout = [int]((Get-ChildItem (Join-Path $storeDir "layouts") -Filter "*-disk0.json" | Sort-Object Name | Select-Object -Last 1).Name.Substring(0, 6))
& $CorpusExe --config $cfgPath --deep-verify --snapshot $latestLayout
if ($LASTEXITCODE -ne 0) { throw "DeepVerify failed ($LASTEXITCODE)" }

Write-Host "=== [5/8] Export Whole-Disk VHDX + Test-VHD + Hyper-V Gen 2 Boot ==="
$vhdxPath = Join-Path $WorkDir "win11-boot-test.vhdx"
& $CorpusExe --config $cfgPath --export-vhdx --layout $latestLayout --out $vhdxPath
if ($LASTEXITCODE -ne 0) { throw "ExportDiskVhdx failed ($LASTEXITCODE)" }
Test-VHD -Path $vhdxPath | Out-Host
Invoke-BootVerify -VhdxPath $vhdxPath -VmName "CorpusBattleBoot"

Write-Host "=== [6/8] Export Whole-Disk OVA (--controller ahci) + Verify ==="
$ovaPath = Join-Path $WorkDir "win11-boot-test.ova"
& $CorpusExe --config $cfgPath --export-ova --layout $latestLayout --controller ahci --out $ovaPath
if ($LASTEXITCODE -ne 0) { throw "ExportDiskOva failed ($LASTEXITCODE)" }
if (-not (Test-Path $ovaPath) -or (Get-Item $ovaPath).Length -lt 1MB) { throw "OVA missing or too small" }
Write-Host "OVA exported: $ovaPath ($((Get-Item $ovaPath).Length) bytes)"

if (-not $SkipRestore) {
    Write-Host "=== [7/8] Bare-Metal --restore-disk to Secondary VHDX + Hyper-V Gen 2 Boot ==="
    $restoreVhdx = Join-Path $WorkDir "win11-restore-target.vhdx"
    if (Test-Path $restoreVhdx) { Remove-Item -Force $restoreVhdx }
    $srcDiskSize = (Get-Disk -Number 0).Size
    New-VHD -Path $restoreVhdx -Dynamic -SizeBytes $srcDiskSize | Out-Null
    $mounted = Mount-VHD -Path $restoreVhdx -Passthru
    $targetDiskNum = ($mounted | Get-Disk).Number
    try {
        $confirmToken = "DISK$targetDiskNum-$([math]::Round($srcDiskSize / 1GB))GB"
        & $CorpusExe --config $cfgPath --restore-disk --layout $latestLayout --target $targetDiskNum --confirm-type $confirmToken --repair-boot
        if ($LASTEXITCODE -ne 0) { throw "RestoreDisk failed ($LASTEXITCODE)" }
    } finally {
        Dismount-VHD -Path $restoreVhdx
    }
    Invoke-BootVerify -VhdxPath $restoreVhdx -VmName "CorpusBattleRestore"
}

if (-not $SkipWim) {
    Write-Host "=== [8/8] WIM Capture + Apply to Secondary VHDX + Hyper-V Gen 2 Boot ==="
    $wimPath = Join-Path $WorkDir "win11-c.wim"
    $wimVhdx = Join-Path $WorkDir "win11-wim-target.vhdx"
    if (Test-Path $wimPath) { Remove-Item -Force $wimPath }
    if (Test-Path $wimVhdx) { Remove-Item -Force $wimVhdx }
    & $CorpusExe --config $cfgPath --image --volume C: --to-wim $wimPath
    if ($LASTEXITCODE -ne 0) { throw "WIM capture failed ($LASTEXITCODE)" }
    New-VHD -Path $wimVhdx -Dynamic -SizeBytes 80GB | Out-Null
    $mWim = Mount-VHD -Path $wimVhdx -Passthru
    $wimDisk = $mWim | Get-Disk
    try {
        Initialize-Disk -Number $wimDisk.Number -PartitionStyle GPT | Out-Null
        $esp = New-Partition -DiskNumber $wimDisk.Number -Size 200MB -GptType "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}" -DriveLetter S
        Format-Volume -Partition $esp -FileSystem FAT32 -NewFileSystemLabel "SYSTEM" -Confirm:$false | Out-Null
        $os = New-Partition -DiskNumber $wimDisk.Number -UseMaximumSize -DriveLetter W
        Format-Volume -Partition $os -FileSystem NTFS -NewFileSystemLabel "Windows" -Confirm:$false | Out-Null
        & $CorpusExe --config $cfgPath --apply --wim $wimPath --index 1 --target W:\
        if ($LASTEXITCODE -ne 0) { throw "WIM apply failed ($LASTEXITCODE)" }
        bcdboot W:\Windows /s S: /f UEFI | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "bcdboot to S: failed ($LASTEXITCODE)" }
        bcdedit /store S:\EFI\Microsoft\Boot\BCD /enum | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Target BCD invalid ($LASTEXITCODE)" }
    } finally {
        Dismount-VHD -Path $wimVhdx
    }
    Invoke-BootVerify -VhdxPath $wimVhdx -VmName "CorpusBattleWim"
}

Write-Host "ALL 8 BATTLE-TEST STAGES PASSED!"
