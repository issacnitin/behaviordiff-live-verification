#requires -Version 7.0
<#
    Proof harness for Engine part 1.

    Builds a second worktree at a different absolute path so base and PR PDB paths genuinely differ -
    without that, path normalization is never exercised and STEP 0 proves nothing.

    Usage:
      tools/verify-diff.ps1              identical commit in both worktrees; expect 0 divergences
      tools/verify-diff.ps1 -Mutate      apply one deliberate one-line change to the PR worktree
#>
[CmdletBinding()]
param(
    [switch]$Mutate,
    [ValidateSet('discount', 'config', 'downgrade')]
    [string]$Change = 'discount',
    [switch]$SkipPrRebuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$prTree = Join-Path (Split-Path -Parent $repo) 'BehaviorDiff-prtree'
$work = Join-Path ([System.IO.Path]::GetTempPath()) 'behaviordiff-diff'

function Invoke-Suite([string]$stagedBin, [string]$outputDir) {
    Remove-Item $outputDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $outputDir | Out-Null

    $env:BEHAVIORDIFF_NAMESPACES = 'SampleApp'
    $env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics'
    $env:BEHAVIORDIFF_BACKEND = 'cecil'
    $env:BEHAVIORDIFF_TRACE = Join-Path $outputDir 'run.ndjson'

    dotnet test (Join-Path $stagedBin 'SampleApp.Tests.dll') --nologo | Out-Null
}

Write-Host '=== preparing worktrees ===' -ForegroundColor Cyan
Push-Location $repo
dotnet build BehaviorDiff.sln -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'base build failed' }
Pop-Location

if (-not $SkipPrRebuild) {
    Remove-Item $prTree -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $prTree | Out-Null

    # Copy sources only. bin/obj carry PDBs pointing at the base path, which would mask a normalization bug.
    Get-ChildItem $repo -Force | Where-Object { $_.Name -notin @('.git', '.vs') } | ForEach-Object {
        Copy-Item $_.FullName -Destination $prTree -Recurse -Force -Exclude @()
    }
    Get-ChildItem $prTree -Include bin, obj -Recurse -Directory -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    if ($Mutate) {
        if ($Change -eq 'discount') {
            $target = Join-Path $prTree 'samples/SampleApp/OrderService.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace 'quantity >= 10', 'quantity >= 5'
            $label = 'OrderService.ApplyDiscount threshold 10 -> 5'
        }
        elseif ($Change -eq 'config') {
            # Config-parser shape: only the parser file is touched, and only a constant it returns.
            $target = Join-Path $prTree 'samples/SampleApp/SettingsParser.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace 'DefaultFreeShippingThreshold = 50m', 'DefaultFreeShippingThreshold = 30m'
            $label = 'SettingsParser default free-shipping threshold 50 -> 30'
        }
        else {
            # Drives the frontier downgrade reasons. The edited file declares no methods.
            $target = Join-Path $prTree 'samples/SampleApp/DowngradeConfig.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace 'Magnitude = 3', 'Magnitude = 4'
            $label = 'DowngradeConfig.Magnitude 3 -> 4'
        }

        if ($mutated -eq $text) { throw 'mutation did not apply - the anchor text was not found' }
        Set-Content $target $mutated -NoNewline
        Write-Host "  MUTATION: $label" -ForegroundColor Yellow
    }

    Push-Location $prTree
    dotnet build BehaviorDiff.sln -c Release --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'pr worktree build failed' }
    Pop-Location
}

Write-Host "  base worktree : $repo"
Write-Host "  pr   worktree : $prTree"

Write-Host ''
Write-Host '=== running suites ===' -ForegroundColor Cyan
$stage = Join-Path $PSScriptRoot 'Stage-WovenSample.ps1'
$baseBin = Join-Path $work 'base-bin'
$prBin = Join-Path $work 'pr-bin'
& $stage -TreeRoot $repo -OutDir $baseBin
& $stage -TreeRoot $prTree -OutDir $prBin

Invoke-Suite $baseBin (Join-Path $work 'base_run1')
Write-Host '  base_run1 done'
Invoke-Suite $baseBin (Join-Path $work 'base_run2')
Write-Host '  base_run2 done'
Invoke-Suite $prBin (Join-Path $work 'pr_run')
Write-Host '  pr_run done'

Write-Host ''
$out = Join-Path $work 'divergence-set.json'
& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
    diff --base1 (Join-Path $work 'base_run1') --base2 (Join-Path $work 'base_run2') --pr (Join-Path $work 'pr_run') `
    --base-root $repo --pr-root $prTree --out $out

$engineExit = $LASTEXITCODE
Write-Host ''
Write-Host "engine exit = $engineExit"

if ($engineExit -ne 0) { exit $engineExit }

# Stands in for `git diff --name-only base..pr`, computed by comparing the two worktrees. Repo-relative
# with forward slashes, matching the Step 0 normalization the traces went through.
Write-Host ''
Write-Host '=== changed files (base..pr) ===' -ForegroundColor Cyan
$changedList = Join-Path $work 'changed-files.txt'
$changed = @()
Get-ChildItem $repo -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $rel = $_.FullName.Substring($repo.Length).TrimStart('\', '/').Replace('\', '/')
        $counterpart = Join-Path $prTree $rel
        if (-not (Test-Path $counterpart)) { $changed += $rel }
        elseif ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash $counterpart).Hash) { $changed += $rel }
    }

$changed | Set-Content $changedList
$changed | ForEach-Object { Write-Host "  $_" }
if ($changed.Count -eq 0) { Write-Host '  (none)' }

Write-Host ''
$report = Join-Path $work 'frontier-report.json'
& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
    frontier --in $out --changed-files $changedList --out $report

exit $LASTEXITCODE
