Set-StrictMode -Version 3.0

function Get-CanonicalHash([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Test-Evidence($Evidence, [Collections.IDictionary]$Documents) {
    if ($Evidence -isnot [Collections.IDictionary] -or !$Documents.Contains($Evidence['documentId'])) { return $false }
    $document = $Documents[$Evidence['documentId']]
    $start = $Evidence['startLine']
    $end = $Evidence['endLine']
    if (($start -isnot [int] -and $start -isnot [long]) -or ($end -isnot [int] -and $end -isnot [long]) `
        -or $start -lt 1 -or $end -lt $start -or $end -gt $document.lines.Count) { return $false }
    $content = [string]::Join("`n", [string[]]$document.lines[($start - 1)..($end - 1)])
    $hash = Get-CanonicalHash $content
    return $Evidence['sourceFile'] -ceq $document.sourceFile `
        -and $Evidence['documentType'] -ceq $document.documentType `
        -and $Evidence['content'] -ceq $content -and $Evidence['contentHash'] -ceq $hash `
        -and $Evidence['id'] -ceq "$($Evidence['documentId']):$start-$($end):$hash"
}

function Get-LineSet($Evidence) {
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Evidence) {
        for ($line = $item['startLine']; $line -le $item['endLine']; $line++) {
            $null = $set.Add("$($item['documentId']):$line")
        }
    }
    return ,$set
}

function Measure-ChatUsage($Generations) {
    $totals = [ordered]@{}
    foreach ($field in @('inputTokens', 'outputTokens', 'totalTokens', 'reasoningTokens')) {
        $sum = 0L
        $known = $true
        foreach ($generation in $Generations) {
            $value = $generation['usage'][$field]
            if ($null -eq $value) { $known = $false } else { $sum += [long]$value }
        }
        $totals[$field] = if ($known) { $sum } else { $null }
    }
    return $totals
}

function Test-IncidentEvaluation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [Collections.IDictionary]$Case,
        [Parameter(Mandatory)] [Collections.IDictionary]$Response,
        [Parameter(Mandatory)] [Collections.IDictionary]$Documents,
        [Parameter(Mandatory)] [Collections.IDictionary]$Facts
    )

    $checks = [Collections.Generic.List[object]]::new()
    $flags = [Collections.Generic.List[string]]::new()
    function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
        $checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    }
    $isSearch = $Case['mode'] -ceq 'search'
    $isAgent = $Case['mode'] -ceq 'run'
    $isAnswer = $Case['mode'] -ceq 'answer'
    $report = if ($isAgent) { $Response['report'] } else { $Response }
    $retrieval = if ($isSearch) { $Response } else { $report['retrieval'] }
    if ($retrieval -isnot [Collections.IDictionary] -or $retrieval['matches'] -isnot [Collections.IList]) {
        throw [IO.InvalidDataException]::new('The response is missing retrieval.matches.')
    }
    $initial = @($retrieval['matches'] | ForEach-Object { $_['evidence'] })
    $additional = if ($isAgent) {
        if ($Response['toolEvidence'] -isnot [Collections.IList]) {
            throw [IO.InvalidDataException]::new('The investigation is missing toolEvidence.')
        }
        @($Response['toolEvidence'] | ForEach-Object { $_['evidence'] })
    } else { @() }
    $allEvidence = @($initial) + @($additional)
    $validEvidence = $allEvidence.Count -gt 0
    $evidenceIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($evidence in $allEvidence) {
        if (!(Test-Evidence $evidence $Documents) -or !$evidenceIds.Add([string]$evidence['id'])) { $validEvidence = $false }
    }
    Add-Check 'original-evidence-identity' $validEvidence 'Every evidence range, filename, type, LF text, hash and ID must match the pinned local fixtures; IDs must be unique.'
    $scope = $Case['request']['documentId']
    Add-Check 'document-scope' (@($allEvidence | Where-Object { $_['documentId'] -cne $scope }).Count -eq 0) "All evidence must belong to $scope."
    Add-Check 'retrieval-shape' ($initial.Count -gt 0 -and $initial.Count -le $Case['request']['topK'] `
        -and $retrieval['question'] -ceq $Case['request']['question'] -and $retrieval['documentId'] -ceq $scope `
        -and $retrieval['requestedTopK'] -eq $Case['request']['topK']) 'Retrieval must echo the request and respect topK.'

    $requiredLines = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($anchor in $Case['requiredEvidence']) {
        foreach ($line in $anchor['lines']) { $null = $requiredLines.Add("$($anchor['documentId']):$line") }
    }
    $availableLines = if ($validEvidence) { Get-LineSet $allEvidence } else { Get-LineSet @() }
    $initialLines = if ($validEvidence) { Get-LineSet $initial } else { Get-LineSet @() }
    $covered = @($requiredLines | Where-Object { $availableLines.Contains($_) }).Count
    $initialCovered = @($requiredLines | Where-Object { $initialLines.Contains($_) }).Count
    $recall = if ($requiredLines.Count) { $covered / $requiredLines.Count } else { $null }
    $initialRecall = if ($requiredLines.Count) { $initialCovered / $requiredLines.Count } else { $null }
    if ($null -ne $Case['minimumEvidenceRecall']) {
        Add-Check 'required-evidence-recall' ($recall -ge $Case['minimumEvidenceRecall']) "$covered/$($requiredLines.Count) required source lines were available."
    }

    $factMatches = [Collections.Generic.List[string]]::new()
    $generations = @()
    $toolCalls = @()
    if (!$isSearch) {
        $citations = $report['citations']
        $observations = @(if ($isAnswer) { $report['statements'] } else { @($report['summary']) + @($report['evidence']) })
        if ($citations -isnot [Collections.IList] -or $observations -isnot [Collections.IList]) {
            throw [IO.InvalidDataException]::new('The generated response is missing citation or statement arrays.')
        }
        Add-Check 'insufficiency-flag-type' ($report['insufficientEvidence'] -is [bool]) 'insufficientEvidence must be a JSON boolean.'
        if ($Case.Contains('expectedInsufficientEvidence')) {
            Add-Check 'expected-insufficiency' ($report['insufficientEvidence'] -eq $Case['expectedInsufficientEvidence']) "Expected insufficientEvidence=$($Case['expectedInsufficientEvidence'])."
        }
        if ($isAnswer) {
            Add-Check 'answer-fields' ($report['statements'] -is [Collections.IList] -and $report['limitations'] -is [Collections.IList] `
                -and $report['statements'].Count -le 8 -and $report['limitations'].Count -le 5 `
                -and (!$report['insufficientEvidence'] -or $report['limitations'].Count -gt 0) `
                -and ($report['insufficientEvidence'] -or $report['statements'].Count -gt 0)) 'Answer structure and abstention limitations must be present.'
        } else {
            Add-Check 'report-fields' ($report['summary'] -is [Collections.IList] -and $report['evidence'] -is [Collections.IList] `
                -and $report['uncertainty'] -is [Collections.IList] -and $report['uncertainty'].Count -ge 1 `
                -and $report['uncertainty'].Count -le 5 -and $report['summary'].Count -le 3 -and $report['evidence'].Count -le 8 `
                -and $report['recommendedNextSteps'] -is [Collections.IList] -and $report['recommendedNextSteps'].Count -le 5) 'Report sections, bounds and explicit uncertainty are required.'
            if ($Case.Contains('expectedRootCause')) {
                $expected = $Case['expectedRootCause']
                Add-Check 'expected-root-cause-state' ($report['rootCauseClassification'] -ceq $expected `
                    -and (($expected -ceq 'hypothesis') -eq ($null -ne $report['probableRootCause']))) "Expected root-cause classification: $expected."
            }
            if ($Case.Contains('maximumObservations')) {
                Add-Check 'no-invented-observations' ($report['evidence'].Count -le $Case['maximumObservations']) 'Runbook guidance must not become observed incident evidence.'
            }
            $stepsSafe = @($report['recommendedNextSteps'] | Where-Object { $_['executionStatus'] -cne 'not_executed' }).Count -eq 0
            Add-Check 'recommendations-not-executed' $stepsSafe 'Suggested next steps must remain marked not_executed.'
            $causeStateValid = if ($null -eq $report['probableRootCause']) {
                $report['rootCauseClassification'] -ceq 'not_established'
            } else {
                $report['rootCauseClassification'] -ceq 'hypothesis'
            }
            Add-Check 'root-cause-label' $causeStateValid 'A reported cause must remain a hypothesis, not a confirmed diagnosis.'
            Add-Check 'sufficient-report-observations' ($report['insufficientEvidence'] -or `
                ($report['summary'].Count -gt 0 -and $report['evidence'].Count -gt 0 -and $null -ne $report['probableRootCause'])) 'A sufficient report must include summary, observations and a cause hypothesis.'
        }
        $sourceRegistry = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        for ($index = 0; $index -lt $initial.Count; $index++) { $sourceRegistry.Add("S$($index + 1)", $initial[$index]) }
        $referenceIntegrity = $true
        if ($isAgent) {
            foreach ($source in $Response['toolEvidence']) {
                if ($source['sourceId'] -cnotmatch '^S[1-9][0-9]*$' -or $sourceRegistry.ContainsKey($source['sourceId'])) {
                    $referenceIntegrity = $false
                } else { $sourceRegistry.Add($source['sourceId'], $source['evidence']) }
            }
        }
        $citationMap = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($citation in $citations) {
            $id = $citation['sourceId']
            if ($null -eq $id -or !$sourceRegistry.ContainsKey($id) -or $citationMap.ContainsKey($id) `
                -or !(Test-Evidence $citation['evidence'] $Documents) `
                -or $citation['evidence']['id'] -cne $sourceRegistry[$id]['id']) {
                $referenceIntegrity = $false
            } else { $citationMap.Add($id, $citation['evidence']) }
        }
        $claims = [Collections.Generic.List[object]]::new()
        foreach ($statement in $observations) { $claims.Add($statement) }
        if (!$isAnswer) {
            if ($null -ne $report['probableRootCause']) { $claims.Add($report['probableRootCause']) }
            foreach ($step in $report['recommendedNextSteps']) {
                $claims.Add(@{ text = "$($step['action']) $($step['rationale'])"; sourceIds = $step['sourceIds'] })
            }
        }
        $usedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($claim in $claims) {
            if ($claim -isnot [Collections.IDictionary] -or [string]::IsNullOrWhiteSpace($claim['text']) `
                -or $claim['sourceIds'] -isnot [Collections.IList] -or $claim['sourceIds'].Count -eq 0) {
                $referenceIntegrity = $false
                continue
            }
            $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($id in $claim['sourceIds']) {
                if ($null -eq $id -or !$citationMap.ContainsKey($id) -or !$unique.Add($id)) { $referenceIntegrity = $false }
                $null = $usedIds.Add($id)
            }
        }
        if ($usedIds.Count -ne $citationMap.Count) { $referenceIntegrity = $false }
        Add-Check 'citation-references' $referenceIntegrity 'Every claim must reference unique known labels with original evidence; no unused top-level citations.'
        if (!$isAnswer -and $referenceIntegrity) {
            $observedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $observationsValid = $true
            foreach ($observation in $report['evidence']) {
                $hasObservation = $false
                foreach ($id in $observation['sourceIds']) {
                    if ($citationMap[$id]['documentType'] -cin @('application-log', 'incident-report')) {
                        $hasObservation = $true
                        $null = $observedIds.Add($id)
                    }
                }
                if (!$hasObservation) { $observationsValid = $false }
            }
            if ($null -ne $report['probableRootCause'] -and @($report['probableRootCause']['sourceIds'] | Where-Object { $observedIds.Contains($_) }).Count -eq 0) {
                $observationsValid = $false
            }
            Add-Check 'observational-source-types' $observationsValid 'Observations and cause must use observed log/report evidence, not runbook-only guidance.'
        }

        foreach ($factId in $Case['requiredFacts']) {
            $fact = $Facts[$factId]
            $matched = $false
            foreach ($statement in $observations) {
                if ($statement -isnot [Collections.IDictionary] -or $statement['text'] -isnot [string] `
                    -or ![regex]::IsMatch($statement['text'], $fact['pattern'], [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromMilliseconds(100))) { continue }
                $cited = @($statement['sourceIds'] | Where-Object { $null -ne $_ -and $citationMap.ContainsKey($_) } | ForEach-Object { $citationMap[$_] })
                $lines = Get-LineSet $cited
                if (@($fact['lines'] | Where-Object { !$lines.Contains("$($fact['documentId']):$_") }).Count -eq 0) { $matched = $true; break }
            }
            if ($matched) { $factMatches.Add($factId) }
            Add-Check "fact-$factId" $matched 'A matching observed statement must cite the expected original source lines. This is a surface-pattern check, not semantic entailment.'
        }
        $claimText = [string]::Join("`n", [string[]]@($claims | ForEach-Object { $_['text'] }))
        if ($Case.Contains('forbiddenAnswerPatterns')) {
            $noForbiddenAnswer = $true
            foreach ($pattern in $Case['forbiddenAnswerPatterns']) {
                if ([regex]::IsMatch($claimText, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase, [TimeSpan]::FromMilliseconds(100))) {
                    $noForbiddenAnswer = $false
                }
            }
            Add-Check 'no-unsupported-temperature-value' $noForbiddenAnswer 'Abstention explanations are allowed, but a numeric temperature answer is not supported. Pattern matching still needs human review.'
        }
        if ($claimText -match '(?i)\b(kubernetes|k8s|pods?|cluster(?:-wide)?|AKS)\b') {
            $flags.Add('Potential unsupported infrastructure assumption (pods/cluster/Kubernetes). Review whether wording is conditional and evidence-backed.')
        }
        if (!$isAnswer -and $null -ne $report['probableRootCause'] -and $report['probableRootCause']['text'] -match '(?i)\b(proven|confirmed|definitive|sole cause)\b') {
            $flags.Add('Potentially overstated causal certainty; inspect negation and surrounding wording.')
        }
        if ($isAgent) {
            $toolCalls = $Response['toolCalls']
            $calls = $Response['modelCalls']
            if ($toolCalls -isnot [Collections.IList] -or $calls -isnot [Collections.IList]) {
                throw [IO.InvalidDataException]::new('The investigation is missing tool/model call arrays.')
            }
            Add-Check 'tool-and-chat-bounds' ($toolCalls.Count -le 3 -and $calls.Count -ge 2 -and $calls.Count -le 4 `
                -and $Response['maxToolCalls'] -eq 3 -and $Response['maxChatCalls'] -eq 4) 'At most 3 tool calls and 4 chat calls, including the report.'
            $traceValid = $true
            foreach ($call in $toolCalls) {
                if ($call['status'] -cnotin @('completed', 'rejected')) { $traceValid = $false }
                if ($call['status'] -ceq 'completed' -and ($call['name'] -cnotin @('GetIncidentLogs', 'GetConfigurationChanges', 'GetRunbook') `
                    -or $call['arguments']['documentId'] -cne $scope -or $call['sourceIds'].Count -gt 12 `
                    -or $call['matchingItemCount'] -lt $call['sourceIds'].Count `
                    -or $call['truncated'] -ne ($call['matchingItemCount'] -gt $call['sourceIds'].Count))) { $traceValid = $false }
                foreach ($id in $call['sourceIds']) { if (!$sourceRegistry.ContainsKey($id)) { $traceValid = $false } }
                if ($call['status'] -ceq 'rejected' -and $call['sourceIds'].Count -gt 0) { $traceValid = $false }
            }
            Add-Check 'tool-trace-scope-and-counts' $traceValid 'Completed reads must be allowlisted, scoped and limited; rejected calls must return no evidence.'
            Add-Check 'collection-stop-state' ($Response['stopReason'] -cin @('model_finished', 'tool_limit_reached') `
                -and (($Response['stopReason'] -ceq 'tool_limit_reached') -eq ($toolCalls.Count -eq 3)) `
                -and $Response['evidenceCollectionLimitations'] -is [Collections.IList] `
                -and $Response['evidenceCollectionLimitations'].Count -ge 2) 'Collection must expose a consistent stop reason and server-owned limitations.'
            if ($Response['stopReason'] -ceq 'tool_limit_reached') { $flags.Add('Tool budget reached; additional evidence may remain unread.') }
            if (@($toolCalls | Where-Object { $_['truncated'] }).Count) { $flags.Add('A tool result was truncated; inspect missing evidence.') }
            if (@($toolCalls | Where-Object { $_['status'] -ceq 'rejected' }).Count) { $flags.Add('A tool request was rejected; inspect collection scope and arguments.') }
            Add-Check 'model-call-stages' ($calls[-1]['stage'] -ceq 'report' `
                -and @($calls | Select-Object -SkipLast 1 | Where-Object { $_['stage'] -cne 'tool_selection' }).Count -eq 0 `
                -and ($calls[-1]['generation'] | ConvertTo-Json -Depth 10 -Compress) -ceq ($report['generation'] | ConvertTo-Json -Depth 10 -Compress)) 'Usage must identify selection calls and the final report exactly once.'
            $generations = @($calls | ForEach-Object { $_['generation'] })
            $totals = Measure-ChatUsage $generations
            $totalsMatch = $true
            foreach ($field in $totals.Keys) { if ($totals[$field] -ne $Response['totalChatUsage'][$field]) { $totalsMatch = $false } }
            Add-Check 'aggregate-chat-usage' $totalsMatch 'Aggregate usage must include all chat calls, with unknown fields preserved as null.'
        } else { $generations = @($report['generation']) }
    }

    $usageValid = $true
    foreach ($generation in $generations) {
        if ($generation -isnot [Collections.IDictionary] -or $generation['usage'] -isnot [Collections.IDictionary]) {
            throw [IO.InvalidDataException]::new('A generation is missing actual usage metadata.')
        }
        foreach ($field in @('inputTokens', 'outputTokens', 'totalTokens', 'reasoningTokens')) {
            if (!$generation['usage'].Contains($field)) { $usageValid = $false; continue }
            $value = $generation['usage'][$field]
            if ($null -ne $value -and (($value -isnot [int] -and $value -isnot [long]) -or $value -lt 0)) { $usageValid = $false }
        }
        if ($null -ne $generation['usage']['outputTokens'] -and $null -ne $generation['usage']['reasoningTokens'] `
            -and $generation['usage']['reasoningTokens'] -gt $generation['usage']['outputTokens']) { $usageValid = $false }
    }
    if (!$isSearch) { Add-Check 'actual-usage-shape' $usageValid 'Usage fields must be nonnegative integer counts or explicitly unknown; reasoning is part of output.' }
    $totals = Measure-ChatUsage $generations
    return [ordered]@{
        status = if (@($checks | Where-Object { !$_.passed }).Count) { 'failed' } else { 'passed' }
        checks = $checks.ToArray()
        metrics = [ordered]@{
            initialRequiredEvidenceRecall = $initialRecall
            availableRequiredEvidenceRecall = $recall
            matchedFactCount = $factMatches.Count
            requiredFactCount = $Case['requiredFacts'].Count
            matchedFacts = $factMatches.ToArray()
            toolCallCount = $toolCalls.Count
            chatCallCount = $generations.Count
            chatUsage = $totals
            questionEmbeddingUsage = $retrieval['usage']
            embeddingModel = $retrieval['modelId']
            embeddingDeployment = $retrieval['deployment']
            chatModels = @($generations | ForEach-Object { @{ modelId = $_['modelId']; deployment = $_['deployment'] } })
        }
        reviewFlags = $flags.ToArray()
        humanReviewRequired = !$isSearch
    }
}

Export-ModuleMember -Function Test-IncidentEvaluation, Get-CanonicalHash
