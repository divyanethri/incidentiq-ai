#Requires -Version 7.2
[CmdletBinding()]
param(
    [uri]$BaseUri = 'http://localhost:5080',
    [switch]$IncludeTools,
    [string[]]$CaseId
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'IncidentEvaluation.psm1') -Force

if (!$BaseUri.IsAbsoluteUri -or !$BaseUri.IsLoopback -or $BaseUri.Scheme -notin @('http', 'https') `
    -or $BaseUri.AbsolutePath -ne '/' -or $BaseUri.Query -or $BaseUri.Fragment -or $BaseUri.UserInfo) {
    throw 'BaseUri must be a loopback HTTP(S) origin without a path, credentials, query, or fragment.'
}
$root = Split-Path $PSScriptRoot -Parent
$datasetPath = Join-Path $root 'data\evaluation\cases.json'
$datasetText = Get-Content -LiteralPath $datasetPath -Raw
$dataset = $datasetText | ConvertFrom-Json -AsHashtable
if ($dataset['schemaVersion'] -ne 1) { throw 'Unsupported evaluation dataset schema version.' }
$documents = @{}
foreach ($document in $dataset['documents']) {
    $sourceFile = $document['sourceFile']
    if ($sourceFile -cnotmatch '^data\\synthetic\\[a-z0-9-]+\.(md|jsonl)$') { throw 'Evaluation documents must be bundled synthetic files.' }
    $lines = [IO.File]::ReadAllLines((Join-Path $root $sourceFile))
    $hash = Get-CanonicalHash ([string]::Join("`n", $lines))
    if ($hash -cne $document['canonicalSha256'] -or $lines.Count -ne $document['lineCount']) {
        throw "Fixture changed: $sourceFile. Review and update evaluation anchors and hashes before running."
    }
    $documents[$document['documentId']] = @{
        sourceFile = $sourceFile; documentType = $document['documentType']; lines = $lines
    }
}
$routes = @{ search = '/api/retrieval/search'; answer = '/api/investigation/answer'; report = '/api/investigation/report'; run = '/api/investigation/run' }
foreach ($fact in $dataset['facts'].Values) {
    $null = [regex]::new($fact['pattern'], [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromMilliseconds(100))
}
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($case in $dataset['cases']) {
    if ($case.Contains('forbiddenAnswerPatterns')) {
        foreach ($pattern in $case['forbiddenAnswerPatterns']) {
            $null = [regex]::new($pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromMilliseconds(100))
        }
    }
    if ($case['id'] -cnotmatch '^[a-z0-9-]+$' -or !$ids.Add($case['id']) -or !$routes.ContainsKey($case['mode']) `
        -or $case['suite'] -cnotin @('core', 'tools') -or !$documents.ContainsKey($case['request']['documentId']) `
        -or [string]::IsNullOrWhiteSpace($case['request']['question']) -or $case['request']['question'].Length -gt 2000 `
        -or $case['request']['topK'] -lt 1 -or $case['request']['topK'] -gt 10 `
        -or ($null -ne $case['minimumEvidenceRecall'] -and ($case['minimumEvidenceRecall'] -lt 0 -or $case['minimumEvidenceRecall'] -gt 1))) { throw 'Invalid evaluation case definition.' }
    foreach ($anchor in @($case['requiredEvidence']) + @($case['requiredFacts'] | ForEach-Object {
        if (!$dataset['facts'].Contains($_)) { throw "Unknown fact: $_" }
        $dataset['facts'][$_]
    })) {
        if (!$documents.ContainsKey($anchor['documentId'])) { throw 'Unknown expected evidence document.' }
        foreach ($line in $anchor['lines']) {
            if ($line -lt 1 -or $line -gt $documents[$anchor['documentId']].lines.Count) { throw 'Expected source line is out of range.' }
        }
    }
}
foreach ($id in $CaseId) { if (!$ids.Contains($id)) { throw "Unknown case ID: $id" } }
$cases = @($dataset['cases'] | Where-Object {
    ($IncludeTools -or $_['suite'] -ceq 'core') -and (!$CaseId -or $_['id'] -cin $CaseId)
})
if (!$cases.Count) { throw 'No cases selected. Tool comparison cases require -IncludeTools.' }
$comparisonCases = @($dataset['cases'] | Where-Object { $_.Contains('comparisonGroup') })
if ($comparisonCases.Count -ne 2 -or ($comparisonCases[0]['request'] | ConvertTo-Json -Compress) -cne ($comparisonCases[1]['request'] | ConvertTo-Json -Compress)) {
    throw 'The fixed/tool comparison must use identical requests.'
}

$health = Invoke-RestMethod -Uri ([uri]::new($BaseUri, '/health')) -TimeoutSec 10 -MaximumRedirection 0
if ($health.service -cne 'IncidentIQ' -or $health.status -cne 'Healthy') { throw 'The target is not a healthy IncidentIQ application.' }
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root ".local\evaluations\$runId"
$null = New-Item -ItemType Directory -Path (Join-Path $directory 'responses')
$results = [Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::UtcNow

function Write-Summary([string]$State) {
    $comparisons = @($results | Where-Object { $_.Contains('comparisonGroup') } | ForEach-Object {
        [ordered]@{ caseId = $_.caseId; status = $_.status; metrics = $_.metrics }
    })
    $summary = [ordered]@{
        schemaVersion = 1; dataset = $dataset['name']; datasetSha256 = Get-CanonicalHash $datasetText
        runId = $runId; state = $State; startedAt = $startedAt.ToString('O'); updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        baseUri = $BaseUri.AbsoluteUri; includeTools = [bool]$IncludeTools; selectedCaseCount = $cases.Count
        passed = @($results | Where-Object { $_.status -ceq 'passed' }).Count
        failed = @($results | Where-Object { $_.status -ceq 'failed' }).Count
        errors = @($results | Where-Object { $_.status -ceq 'error' }).Count
        results = $results.ToArray(); comparison = $comparisons
        humanReviewChecklist = @(
            'Check each claim against its cited lines; regex fact checks do not prove semantic support or correct negation.',
            'Review causal uncertainty and alternative explanations; citation validity is not factual correctness.',
            'Inspect recommendations for unsupported infrastructure assumptions, conditional wording and read-only intent.',
            'For tools, inspect rejected/truncated reads, stop reasons and missing evidence before judging quality.',
            'Compare repeated runs; one synthetic scenario is not a calibrated quality score or proof that tools improve answers.'
        )
        usageNote = 'Per-case usage contains successful responses only. Failed/timed-out requests may also incur usage. Reasoning tokens are included in output; dollar costs remain unknown.'
    }
    [IO.File]::WriteAllText((Join-Path $directory 'summary.json'), ($summary | ConvertTo-Json -Depth 60))
}
Write-Summary 'running'
Write-Host "Running $($cases.Count) cases against $BaseUri. These calls use Azure quota; no automatic retries."
foreach ($case in $cases) {
    Write-Host "Evaluating $($case['id'])..."
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $entry = [ordered]@{
        caseId = $case['id']; mode = $case['mode']; request = $case['request']; status = 'error'
        httpStatus = $null; durationMs = 0; metrics = $null; checks = @(); reviewFlags = @(); humanReviewRequired = $true
    }
    if ($case.Contains('comparisonGroup')) { $entry['comparisonGroup'] = $case['comparisonGroup'] }
    try {
        $response = Invoke-WebRequest -Method Post -Uri ([uri]::new($BaseUri, $routes[$case['mode']])) `
            -ContentType 'application/json' -Body ($case['request'] | ConvertTo-Json -Compress) `
            -TimeoutSec 260 -SkipHttpErrorCheck -MaximumRedirection 0
        $entry.httpStatus = [int]$response.StatusCode
        $text = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
        $body = $text | ConvertFrom-Json -AsHashtable
        [IO.File]::WriteAllText((Join-Path $directory "responses\$($case['id']).json"), $text)
        if ($entry.httpStatus -ne 200) {
            $entry.checks = @(@{ name = 'http-success'; passed = $false; detail = "HTTP $($entry.httpStatus); model usage may have occurred. Inspect the saved problem response." })
            Write-Warning "$($case['id']) returned HTTP $($entry.httpStatus)."
        } else {
            $score = Test-IncidentEvaluation -Case $case -Response $body -Documents $documents -Facts $dataset['facts']
            foreach ($key in $score.Keys) { $entry[$key] = $score[$key] }
            foreach ($check in $score['checks'] | Where-Object { !$_.passed }) {
                Write-Warning "$($case['id']) / $($check.name): $($check.detail)"
            }
        }
    }
    catch {
        $entry.status = 'error'
        $entry.checks = @(@{ name = 'request-or-evaluation-error'; passed = $false; detail = $_.Exception.Message })
        Write-Warning "$($case['id']): $($_.Exception.Message)"
    }
    finally {
        $watch.Stop()
        $entry.durationMs = $watch.ElapsedMilliseconds
        $results.Add($entry)
        Write-Summary 'running'
    }
}
Write-Summary 'completed'
$rows = foreach ($result in $results) {
    [pscustomobject]@{
        case = $result.caseId; status = $result.status
        evidenceRecall = if ($null -ne $result.metrics) { $result.metrics.availableRequiredEvidenceRecall } else { $null }
        facts = if ($null -ne $result.metrics) { "$($result.metrics.matchedFactCount)/$($result.metrics.requiredFactCount)" } else { '-' }
        reviewFlags = $result.reviewFlags.Count; seconds = [math]::Round($result.durationMs / 1000, 1)
    }
}
$rows | Format-Table -AutoSize | Out-Host
Write-Host "Results and synthetic responses: $directory"
Write-Host 'These are mechanical checks, not an AI correctness guarantee. Review the saved responses and checklist.'
if (@($results | Where-Object { $_.status -cne 'passed' }).Count) { exit 1 }
exit 0
