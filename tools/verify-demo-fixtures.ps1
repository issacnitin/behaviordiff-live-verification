#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$cases = @(
    @{
        Change = 'retry'
        ChangedFile = 'samples/SampleApp/RetryPolicyParser.cs'
        Headline = 'SampleApp.RetryEvaluator.ShouldRetry(System.Int32)'
        DivergedKeys = 3
        FrontierNodes = 1
        ChangedCallSites = 1
        ChangedCalls = 2
        HeadlineCallSites = 1
        UntestedCallSites = 1
    }
    @{
        Change = 'permission'
        ChangedFile = 'samples/SampleApp/PermissionDefaultsParser.cs'
        Headline = 'SampleApp.PermissionEvaluator.CanRead()'
        DivergedKeys = 2
        FrontierNodes = 1
        ChangedCallSites = 1
        ChangedCalls = 2
        HeadlineCallSites = 1
        UntestedCallSites = 1
    }
    @{
        Change = 'config'
        ChangedFile = 'samples/SampleApp/SettingsParser.cs'
        Headline = 'SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)'
        DivergedKeys = 6
        FrontierNodes = 2
        ChangedCallSites = 5
        ChangedCalls = 10
        HeadlineCallSites = 2
        UntestedCallSites = 1
    }
)

foreach ($case in $cases) {
    Write-Host ''
    Write-Host "=== $($case.Change) demo ===" -ForegroundColor Cyan
    $runId = [Guid]::NewGuid().ToString('N')
    $work = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-demo-$runId"
    $prTree = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-demo-pr-$runId"
    try {
        & (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change $case.Change `
            -WorkDirectory $work -PrTreeDirectory $prTree
        if ($LASTEXITCODE -ne 0) { throw "$($case.Change) behavior diff failed: $LASTEXITCODE" }

    $divergences = Get-Content (Join-Path $work 'divergence-set.json') -Raw | ConvertFrom-Json
    $frontier = Get-Content (Join-Path $work 'frontier-report.json') -Raw | ConvertFrom-Json
    $findings = Get-Content (Join-Path $work 'findings.json') -Raw | ConvertFrom-Json

    $editedDivergences = @($divergences.divergences | Where-Object filePath -eq $case.ChangedFile)
    if ($editedDivergences.Count -ne 0) {
        throw "$($case.Change): edited file produced $($editedDivergences.Count) divergence(s)"
    }

    $coverage = $findings.coverage.files | Where-Object filePath -eq $case.ChangedFile
    if ($null -eq $coverage -or -not $coverage.exercised -or $coverage.tracedMembers -ne 1 `
        -or $coverage.observedCallSites -ne $case.ChangedCallSites `
        -or $coverage.totalCallCount -ne $case.ChangedCalls) {
        throw "$($case.Change): edited parser trace identity changed: $($coverage | ConvertTo-Json -Compress)"
    }

    if ($frontier.counts.expected -ne 0) {
        throw "$($case.Change): edited file left $($frontier.counts.expected) frontier footprint(s)"
    }

    if ($frontier.counts.divergedKeys -ne $case.DivergedKeys `
        -or $frontier.counts.frontierNodes -ne $case.FrontierNodes `
        -or $frontier.counts.frontierVerified -ne $case.FrontierNodes `
        -or $frontier.counts.frontierUnverified -ne 0) {
        throw "$($case.Change): topology drifted: $($frontier.counts | ConvertTo-Json -Compress)"
    }

    $ratio = [double]$frontier.counts.divergedKeys / [double]$frontier.counts.frontierNodes
    if ($ratio -le 1.0) {
        throw "$($case.Change): collapse ratio $ratio is not above 1x"
    }

    $headlineNodes = @($frontier.frontier | Where-Object {
        $_.attribution -eq 'UNEXPECTED' -and $_.methodFullName -eq $case.Headline
    })
    if ($headlineNodes.Count -eq 0) {
        throw "$($case.Change): expected headline member $($case.Headline) was not reported"
    }
    if (@($frontier.frontier | Where-Object attribution -eq 'UNEXPECTED').Count -ne $case.FrontierNodes) {
        throw "$($case.Change): extra or missing unexpected frontier nodes"
    }

    if ($headlineNodes.Count -ne $case.HeadlineCallSites) {
        throw "$($case.Change): headline call-site count drifted to $($headlineNodes.Count)"
    }

    $untestedNodes = @($headlineNodes | Where-Object untested -eq $true)
    if ($untestedNodes.Count -ne $case.UntestedCallSites) {
        throw "$($case.Change): headline untested count drifted to $($untestedNodes.Count)"
    }

    $finding = $findings.members | Where-Object memberName -eq $case.Headline
    if ($null -eq $finding `
        -or $finding.callSiteCount -ne $case.HeadlineCallSites `
        -or $finding.untestedCallSiteCount -ne $case.UntestedCallSites) {
        throw "$($case.Change): canonical findings lost the untested headline evidence"
    }
    if ($findings.summary.unexpectedMembers -ne 1 `
        -or $findings.summary.expectedMembers -ne 0 `
        -or @($findings.members | Where-Object attribution -eq 'unexpected').Count -ne 1) {
        throw "$($case.Change): canonical member rollup drifted: $($findings.summary | ConvertTo-Json -Compress)"
    }

        Write-Host "PASS: edited parser was exercised but left no divergence/frontier footprint" -ForegroundColor Green
        Write-Host ("PASS: {0} diverged keys -> {1} frontier node(s) ({2:N1}x)" -f `
            $frontier.counts.divergedKeys, $frontier.counts.frontierNodes, $ratio) -ForegroundColor Green
        Write-Host "PASS: $($case.Headline) has untested: True" -ForegroundColor Green
    }
    finally {
        Remove-Item $work, $prTree -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'verify-demo-fixtures: PASS' -ForegroundColor Green
