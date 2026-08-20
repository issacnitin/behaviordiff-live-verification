#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$cases = @(
    @{
        Change = 'enum'
        ChangedFile = 'samples/SampleApp/AccountStatus.cs'
        Headline = 'SampleApp.AccessControl.CanWithdraw(SampleApp.AccountStatus)'
        DivergedKeys = 3
        FrontierNodes = 2
        ChangedCallSites = 3
        ChangedCalls = 6
        HeadlineCallSites = 2
        UntestedCallSites = 1
    }
    @{
        Change = 'retry'
        ChangedFile = 'samples/SampleApp/ConfigParser.cs'
        Headline = 'SampleApp.RetryPolicy.ShouldRetry(System.Int32,System.Int32)'
        DivergedKeys = 5
        FrontierNodes = 2
        ChangedCallSites = 2
        ChangedCalls = 4
        HeadlineCallSites = 2
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

    $changedFiles = @($frontier.attributionInputs.changedFiles)
    if ($changedFiles.Count -ne 1 -or $changedFiles[0] -ne $case.ChangedFile) {
        throw "$($case.Change): expected exactly one edited file, got $($changedFiles -join ', ')"
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


    if ($case.Change -eq 'retry') {
        $attemptFive = @($finding.evidence | Where-Object {
            $_.baseArgs -eq 'statusCode=Primitive:503, attempt=Primitive:5' `
                -and $_.prArgs -eq 'statusCode=Primitive:503, attempt=Primitive:5' `
                -and $_.baseReturn -eq 'Primitive:true' `
                -and $_.prReturn -eq 'Primitive:false'
        })
        if ($attemptFive.Count -ne 2 `
            -or @($attemptFive | Select-Object -ExpandProperty testId -Unique).Count -ne 2) {
            throw 'retry: ShouldRetry(503, 5) did not change true -> false in both payment tests'
        }

        $paymentConsequence = @($divergences.divergences | Where-Object {
            $_.testId -like '*Payment_survives_extended_outage' `
                -and $_.methodFullName -eq 'SampleApp.PaymentClient.ChargeAsync(System.Decimal)' `
                -and $_.baseReturnRendered -match 'AttemptCount>k__BackingField=7' `
                -and $_.baseReturnRendered -match 'Succeeded>k__BackingField=true' `
                -and $_.prReturnRendered -match 'AttemptCount>k__BackingField=3' `
                -and $_.prReturnRendered -match 'Succeeded>k__BackingField=false'
        })
        if ($paymentConsequence.Count -ne 1) {
            throw 'retry: payment outcome did not change from success on attempt 7 to failure on attempt 3'
        }

        $canonicalConsequence = @($finding.consequences | Where-Object {
            $_.memberName -eq 'SampleApp.PaymentClient.ChargeAsync(System.Decimal)' `
                -and $_.evidence.baseReturn -match 'AttemptCount>k__BackingField=7' `
                -and $_.evidence.baseReturn -match 'Succeeded>k__BackingField=true' `
                -and $_.evidence.prReturn -match 'AttemptCount>k__BackingField=3' `
                -and $_.evidence.prReturn -match 'Succeeded>k__BackingField=false'
        })
        if ($canonicalConsequence.Count -ne 1) {
            throw 'retry: canonical findings lost the downstream payment consequence'
        }
    }

    if ($case.Change -eq 'enum') {
        $closedEvidence = @($finding.evidence | Where-Object {
            $_.testId -like '*Closed_account_cannot_withdraw' `
                -and $_.baseArgs -eq 'status=Primitive:AccountStatus.Closed' `
                -and $_.prArgs -eq 'status=Primitive:AccountStatus.Suspended' `
                -and $_.baseReturn -eq 'Primitive:false' `
                -and $_.prReturn -eq 'Primitive:false' `
                -and $_.assertionReacted -eq $false
        })
        if ($closedEvidence.Count -ne 1) {
            throw 'enum: closed account partial oracle was not retained'
        }

        $suspendedEvidence = @($finding.evidence | Where-Object {
            $_.testId -like '*Suspended_account_cannot_withdraw' `
                -and $_.baseArgs -eq 'status=Primitive:AccountStatus.Suspended' `
                -and $_.prArgs -eq 'status=Primitive:AccountStatus.Frozen' `
                -and $_.baseReturn -eq 'Primitive:false' `
                -and $_.prReturn -eq 'Primitive:true' `
                -and $_.assertionReacted -eq $true
        })
        if ($suspendedEvidence.Count -ne 1) {
            throw 'enum: suspended account did not change from blocked to allowed'
        }

        $executingTests = @($divergences.callTree | Where-Object {
            $_.methodFullName -eq $case.Headline
        } | Select-Object -ExpandProperty testId -Unique)
        if ($executingTests.Count -ne 3) {
            throw "enum: expected three tests to execute CanWithdraw, got $($executingTests.Count)"
        }
    }

        Write-Host "PASS: edited file was exercised but left no divergence/frontier footprint" -ForegroundColor Green
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
