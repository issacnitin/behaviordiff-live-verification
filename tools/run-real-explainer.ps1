#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('retry', 'permission', 'config')]
    [string]$Change
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:ANTHROPIC_API_KEY)) {
    throw 'ANTHROPIC_API_KEY is not set. Enter it directly in this terminal environment, then rerun.'
}
$apiKey = $env:ANTHROPIC_API_KEY
Remove-Item Env:ANTHROPIC_API_KEY

$repo = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
$prTree = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-live-pr-$runId"
$work = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-live-$runId"
$case = switch ($Change) {
    'retry' { @{
        File = 'samples/SampleApp/RetryPolicyParser.cs'
        Base = 'DefaultMaxAttempts = 3'
        Pr = 'DefaultMaxAttempts = 2'
    } }
    'permission' { @{
        File = 'samples/SampleApp/PermissionDefaultsParser.cs'
        Base = 'DefaultRole = "Reader"'
        Pr = 'DefaultRole = "None"'
    } }
    'config' { @{
        File = 'samples/SampleApp/SettingsParser.cs'
        Base = 'DefaultFreeShippingThreshold = 50m'
        Pr = 'DefaultFreeShippingThreshold = 30m'
    } }
}

try {
    & (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change $Change `
        -WorkDirectory $work -PrTreeDirectory $prTree
    if ($LASTEXITCODE -ne 0) { throw "$Change behavior diff failed: $LASTEXITCODE" }

    $baseText = Get-Content (Join-Path $repo $case.File) -Raw
    $prText = Get-Content (Join-Path $prTree $case.File) -Raw
    if ($baseText -notmatch [regex]::Escape($case.Base) -or $prText -notmatch [regex]::Escape($case.Pr)) {
        throw "$Change mutation does not match the expected one-line change"
    }

    $patch = Join-Path $work "$Change.patch"
    $baseLine = ($baseText -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($case.Base) } | Select-Object -First 1).Trim()
    $prLine = ($prText -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($case.Pr) } | Select-Object -First 1).Trim()
    @(
        "--- a/$($case.File)"
        "+++ b/$($case.File)"
        '@@ demo default @@'
        "-$baseLine"
        "+$prLine"
    ) | Set-Content $patch

    $liveOutput = Join-Path $work 'live'
    $liveArtifacts = Join-Path $work 'live-artifacts'
    & dotnet publish (Join-Path $PSScriptRoot 'AnthropicLive/BehaviorDiff.AnthropicLive.csproj') `
        -c Release --artifacts-path $liveArtifacts -o $liveOutput --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "AnthropicLive publish failed: $LASTEXITCODE" }
    $liveDll = Join-Path $liveOutput 'BehaviorDiff.AnthropicLive.dll'

    $env:ANTHROPIC_API_KEY = $apiKey
    & dotnet $liveDll (Join-Path $work 'findings.json') $case.File $patch
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
    $apiKey = $null
    Remove-Item $work, $prTree -Recurse -Force -ErrorAction SilentlyContinue
}

