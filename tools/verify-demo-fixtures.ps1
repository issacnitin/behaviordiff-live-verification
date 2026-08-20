#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$cases = @(
    @{
        Change = 'cache'
        ChangedFile = 'samples/SampleApp/CacheSettings.cs'
        Headline = 'SampleApp.PriceCache.BuildKey(System.Int32,System.String)'
        DivergedKeys = 14
        FrontierNodes = 9
        FrontierVerified = 7
        ChangedFileExercised = $false
        ChangedTracedMembers = 0
        ChangedCallSites = 0
        ChangedCalls = 0
        HeadlineCallSites = 3
        UntestedCallSites = 2
        UnexpectedMembers = 3
    }
    @{
        Change = 'retry'
        ChangedFile = 'samples/SampleApp/ConfigParser.cs'
        Headline = 'SampleApp.RetryPolicy.ShouldRetry(System.Int32,System.Int32)'
        DivergedKeys = 5
        FrontierNodes = 2
        FrontierVerified = 2
        ChangedCallSites = 2
        ChangedCalls = 4
        ChangedFileExercised = $true
        ChangedTracedMembers = 1
        HeadlineCallSites = 2
        UntestedCallSites = 1
        UnexpectedMembers = 1
    }
    @{
        Change = 'config'
        ChangedFile = 'samples/SampleApp/SettingsParser.cs'
        Headline = 'SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)'
        DivergedKeys = 6
        FrontierNodes = 2
        FrontierVerified = 2
        ChangedCallSites = 5
        ChangedCalls = 10
        ChangedFileExercised = $true
        ChangedTracedMembers = 1
        HeadlineCallSites = 2
        UntestedCallSites = 1
        UnexpectedMembers = 1
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
    $findingsPath = Join-Path $work 'findings.json'
    $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json

    $editedDivergences = @($divergences.divergences | Where-Object filePath -eq $case.ChangedFile)
    if ($editedDivergences.Count -ne 0) {
        throw "$($case.Change): edited file produced $($editedDivergences.Count) divergence(s)"
    }

    $changedFiles = @($frontier.attributionInputs.changedFiles)
    if ($changedFiles.Count -ne 1 -or $changedFiles[0] -ne $case.ChangedFile) {
        throw "$($case.Change): expected exactly one edited file, got $($changedFiles -join ', ')"
    }

    $coverage = $findings.coverage.files | Where-Object filePath -eq $case.ChangedFile
    if ($null -eq $coverage -or $coverage.exercised -ne $case.ChangedFileExercised `
        -or $coverage.tracedMembers -ne $case.ChangedTracedMembers `
        -or $coverage.observedCallSites -ne $case.ChangedCallSites `
        -or $coverage.totalCallCount -ne $case.ChangedCalls) {
        throw "$($case.Change): edited-file trace identity changed: $($coverage | ConvertTo-Json -Compress)"
    }

    if ($frontier.counts.expected -ne 0) {
        throw "$($case.Change): edited file left $($frontier.counts.expected) frontier footprint(s)"
    }

    if ($frontier.counts.divergedKeys -ne $case.DivergedKeys `
        -or $frontier.counts.frontierNodes -ne $case.FrontierNodes `
        -or $frontier.counts.frontierVerified -ne $case.FrontierVerified `
        -or $frontier.counts.frontierUnverified -ne ($case.FrontierNodes - $case.FrontierVerified)) {
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
    if ($findings.summary.unexpectedMembers -ne $case.UnexpectedMembers `
        -or $findings.summary.expectedMembers -ne 0 `
        -or @($findings.members | Where-Object attribution -eq 'unexpected').Count -ne $case.UnexpectedMembers) {
        throw "$($case.Change): canonical member rollup drifted: $($findings.summary | ConvertTo-Json -Compress)"
    }

    $commentOutput = & dotnet run --project (Join-Path $PSScriptRoot 'CommentPreview/BehaviorDiff.CommentPreview.csproj') `
        -c Release -- $findingsPath
    if ($LASTEXITCODE -ne 0) { throw "$($case.Change): comment preview failed: $LASTEXITCODE" }
    $comment = $commentOutput -join "`n"
    $comment | Set-Content (Join-Path $work 'comment.md')
    $changeWord = if ($case.UnexpectedMembers -eq 1) { 'change' } else { 'changes' }
    $expectedHeading = '^## BehaviorDiff: {0} behavior {1} outside this diff' -f `
        $case.UnexpectedMembers, $changeWord
    if ($comment -notmatch $expectedHeading `
        -or $comment -notmatch '<details><summary>Why, and the evidence</summary>' `
        -or $comment -match 'Unexpected means' `
        -or $comment -match 'k__BackingField') {
        throw "$($case.Change): concise comment contract drifted"
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

    if ($case.Change -eq 'cache') {
        if ($finding.assertionReactionSummary -ne '3 tests executed this; 1 test had an assertion react.' `
            -or $comment -notmatch 'PriceCache\.BuildKey.*changed, but this PR didn''t edit it' `
            -or $comment -notmatch 'BuildKey returned "P:123\|T:Standard", now returns "P:123"' `
            -or $comment -notmatch 'PricingService\.GetPrice returned 100, now returns 80' `
            -or $comment -notmatch '2 of the 3 tests that executed this did not assert on the change' `
            -or $comment -notmatch '_0 of 1 edited files exercised') {
            throw 'cache: concise consequence-first comment drifted'
        }

        $standardKeys = @($finding.evidence | Where-Object {
            $_.ordinal -eq 1 `
                -and $_.baseArgs -eq 'productId=Primitive:123, customerTier=Primitive:"Standard"' `
                -and $_.prArgs -eq 'productId=Primitive:123, customerTier=Primitive:"Standard"' `
                -and $_.baseReturn -eq 'Primitive:"P:123|T:Standard"' `
                -and $_.prReturn -eq 'Primitive:"P:123"'
        })
        if ($standardKeys.Count -ne 2) {
            throw 'cache: Standard key did not deterministically lose CustomerTier in both warmed-cache tests'
        }

        $goldKeys = @($finding.evidence | Where-Object {
            $_.baseArgs -eq 'productId=Primitive:123, customerTier=Primitive:"Gold"' `
                -and $_.prArgs -eq 'productId=Primitive:123, customerTier=Primitive:"Gold"' `
                -and $_.baseReturn -eq 'Primitive:"P:123|T:Gold"' `
                -and $_.prReturn -eq 'Primitive:"P:123"'
        })
        if ($goldKeys.Count -ne 3) {
            throw 'cache: Gold key did not deterministically lose CustomerTier in all tests'
        }

        $partialOracle = @($finding.consequences | Where-Object {
            $_.memberName -eq 'SampleApp.PricingService.GetPrice(System.Int32,System.String)' `
                -and $_.evidence.testId -like '*Price_is_never_negative' `
                -and $_.evidence.baseReturn -eq 'Primitive:100' `
                -and $_.evidence.prReturn -eq 'Primitive:80' `
                -and $_.evidence.baseArgs -match 'customerTier=Primitive:"Standard"' `
                -and $_.evidence.assertionReacted -eq $false
        })
        if ($partialOracle.Count -ne 1) {
            throw 'cache: positive-price partial oracle did not retain the wrong Standard price evidence'
        }

        $caughtOracle = @($finding.consequences | Where-Object {
            $_.memberName -eq 'SampleApp.PricingService.GetPrice(System.Int32,System.String)' `
                -and $_.evidence.testId -like '*Standard_customer_pays_full_price' `
                -and $_.evidence.baseReturn -eq 'Primitive:100' `
                -and $_.evidence.prReturn -eq 'Primitive:80' `
                -and $_.evidence.assertionReacted -eq $true
        })
        if ($caughtOracle.Count -ne 1) {
            throw 'cache: failing Standard-price assertion did not retain the 100 to 80 consequence'
        }

        $skipped = @($divergences.coverage.members | Where-Object {
            $_.methodFullName -like 'SampleApp.CacheSettings.*'
        })
        if (@($skipped | Where-Object skipReason -eq 'PropertyOrOperator').Count -ne 1 `
            -or @($skipped | Where-Object skipReason -eq 'TypeInitializer').Count -ne 1 `
            -or @($skipped | Where-Object status -ne 'Skipped').Count -ne 0) {
            throw 'cache: edited settings file was not skipped solely as property and type initializer'
        }
    }

    if ($case.Change -eq 'retry' `
        -and $comment -notmatch 'A payment that previously succeeded after retries now fails prematurely') {
        throw 'retry: concise comment lost the payment consequence lead'
    }

    if ($case.Change -eq 'config' `
        -and $comment -notmatch 'An order totaling 40 now qualifies for free shipping') {
        throw 'config: concise comment lost the free-shipping consequence lead'
    }

        Write-Host "PASS: edited file left no traced divergence/frontier footprint" -ForegroundColor Green
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
