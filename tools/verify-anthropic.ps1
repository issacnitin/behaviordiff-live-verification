#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'verify-coverage.ps1')
if ($LASTEXITCODE -ne 0) { throw "verify-coverage failed: $LASTEXITCODE" }

$findings = Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-diff/coverage-findings.json'
& dotnet run --project (Join-Path $PSScriptRoot 'AnthropicProof/BehaviorDiff.AnthropicProof.csproj') `
    -c Release -- $findings
if ($LASTEXITCODE -ne 0) { throw "Anthropic proof failed: $LASTEXITCODE" }
