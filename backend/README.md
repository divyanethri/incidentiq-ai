# IncidentIQ

An incident investigation assistant built with C# and ASP.NET Core.
Use synthetic data only. This repository contains the application foundation
and **Milestone 3, part 2: repeatable synthetic evaluations**.
It does not perform automatic remediation.

## What works now

- A .NET 10 HTTP application with `GET /health`.
- Structured JSON console logs with UTC timestamps.
- Standard problem-details responses for application errors.
- Optional, strongly typed Foundry configuration from environment variables,
  a local development settings file, or development User Secrets.
- Three synthetic evidence files: a runbook, a historical incident report,
  and application logs for a checkout timeout incident.
- `GET /api/ingestion/preview` to read and chunk this evidence with source
  filenames, line numbers, and content hashes.
- `POST /api/ingestion/embed` to generate Foundry embeddings for all nine
  chunks and save their vectors and citation metadata locally.
- `POST /api/retrieval/search` to embed a question and return the closest
  evidence chunks with cosine scores and source citations.
- `POST /api/investigation/answer` to retrieve evidence, ask a Foundry chat
  model for a grounded explanation, and validate its source references.
- `POST /api/investigation/report` to produce a cited summary, observations,
  probable root-cause hypothesis, uncertainty, and suggested next checks.
- `POST /api/investigation/run` to let the model select bounded read-only
  evidence tools before generating the same structured report.
- A PowerShell HTTP evaluation runner with pinned synthetic expectations,
  source-identity checks, abstention cases, and an optional fixed/tool comparison.

Embedding requests use Azure model quota and can incur inference charges.
Startup, health checks, and the preview do not call Azure. Valid search requests
embed only the question; stored evidence vectors are reused. Background
ingestion jobs are not implemented. Answer requests also call the
chat model and report its actual input, output, total, and reasoning tokens.
Report requests use the same two-call flow: one question embedding and one
chat completion, not a separate chat call per report section.
Tool-assisted investigations can make additional chat calls as explained below.
Unit tests and a test project are intentionally deferred. The evaluation
runner exercises HTTP/model behavior; it does not introduce xUnit.

## Run locally

Install the .NET 10 SDK. `global.json` selects SDK 10.0.401 and allows newer
patches within that SDK feature band.

From the `backend` directory in PowerShell (the solution and all backend
assets now live under `IIQ\backend`; frontend startup is in the root README):

```powershell
dotnet restore IncidentIQ.sln
dotnet build IncidentIQ.sln --configuration Release --no-restore
$env:DOTNET_ENVIRONMENT = "Development"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src\IncidentIQ.Api --no-launch-profile -- --urls http://localhost:5080
```

In another terminal:

```powershell
Invoke-RestMethod http://localhost:5080/health
```

Expected HTTP status: `200`. Expected JSON:

```json
{"service":"IncidentIQ","status":"Healthy"}
```

The health endpoint checks that this application can respond. It does **not**
verify Azure connectivity, credentials, model deployments, or search.
The root route `/` returns `404`.

The host logs startup/shutdown events and unhandled errors. Request bodies,
configuration objects, and API keys are not logged.

## Preview synthetic evidence

With the application running, use another PowerShell terminal:

```powershell
$preview = Invoke-RestMethod http://localhost:5080/api/ingestion/preview
$preview | Select-Object documentCount, chunkCount, maxLinesPerChunk, overlapLines
$preview.documents | Format-Table documentId, documentType, lineCount, chunkCount
$preview.chunks[0] | Format-List
```

The runbook gives investigation guidance. The historical report describes a
similar but separate incident, INC-SYN-001. Current logs for INC-SYN-002 show
checkout timeouts after a deployment reduced the database connection pool
from 100 to 5. They also include an operator rollback and recovery.
All events and incidents are fictional; the log timestamps are scenario data,
not the application's current clock.

The endpoint loads only these three allowlisted files from `data\synthetic`.
It does not accept paths, uploads, or a request body. Each request rebuilds
the preview in memory; it does not persist chunks, generate embeddings, or
create a search index.

Chunks contain at most **12 complete lines**, with **2 lines of overlap**
between adjacent chunks. For example, lines 1-12 are followed by lines 11-22.
Overlap keeps nearby evidence from being lost at a boundary. JSONL records
stay intact because each record occupies one line.

Each chunk includes its document ID, document type, repository-relative source
filename, and inclusive, 1-based start/end line numbers. Its `content` is exactly
that line range joined with LF newlines, without trimming or rewriting.
SHA-256 hashes and IDs are deterministic: unchanged content and locations
produce the same IDs, while changed chunk content produces a different ID.

This deliberately simple chunker is **not token-aware**, Markdown-section-aware,
or a structured log parser. It is suitable for the small bundled corpus;
before embedding larger documents, add token budgeting and evaluate whether
section/time-window boundaries improve retrieval.

The build copies evidence into the application output and publish directories.
The loader reads from the running assembly's directory, so it does not depend
on the shell's working directory. After editing source evidence, restart
`dotnet run` to rebuild and copy the changes. Missing or empty evidence causes
an explicit HTTP 500 problem response and a server-side error log, not a
successful empty preview.

This corpus includes post-incident recovery evidence. A future evaluation that
asks "what was known before rollback?" must filter by time rather than letting
the model use later events.

## Generate and save embeddings

An embedding model turns each text chunk into a list of numbers, called a
vector. Later, the app can compare a question's vector with these stored
vectors to find relevant evidence. This step does not generate an answer.

Configure the local development settings below, restart in `Development`,
then deliberately trigger one ingestion from another PowerShell terminal:

```powershell
$result = Invoke-RestMethod -Method Post http://localhost:5080/api/ingestion/embed
$result | Format-List
```

The endpoint has no request body and only embeds the three bundled synthetic
documents. Expect `documentCount` of 3, `chunkCount` of 9, the vector
`dimensions`, deployment/model identifiers, actual reported token `usage`,
and `storeFile`. Vector dimensions come from the model response rather than
a hardcoded assumption. The response omits the large vectors themselves.

The snapshot is saved at
`src\IncidentIQ.Api\.local\embeddings\synthetic.json` during `dotnet run`.
The path is relative to the application's content root, not the shell's
working directory. It includes each chunk's exact text, hash, source and line
range, its vector, the endpoint/deployment/model identity, generation time,
schema version, and token usage. No API key is stored in it. `.local` stores
are ignored by Git and excluded from publish output.

Every POST regenerates all embeddings and can incur another charge; there
is no cache or incremental ingestion yet. Requests are serialized within
this application process. A completed snapshot atomically replaces the
previous file only after every returned vector is validated. Cancellation,
provider failures, or invalid vectors do not replace an existing snapshot.
The file is not a multi-process database. Semantic search scans its vectors
in this application rather than relying on a separate index service.

Missing or invalid embedding configuration returns HTTP 503 without a model
request. Provider failures return problem details with an `upstreamStatus`:
check credentials for 401/403, endpoint/deployment for 404, and quota for 429.
Provider rate limiting maps to HTTP 503; other provider failures map to
HTTP 502. File and unexpected application errors use the standard HTTP 500
handler. No automatic fallback or fabricated vectors are used.

Usage values remain `null` if the provider does not report them. Usage is
logged after generation and persisted in the successful snapshot, not a
cumulative billing ledger. `estimatedCostUsd` is currently `null`, not zero:
no pricing rate is configured. Price-based cost estimation is deferred.

## Search the evidence by meaning

After generating a snapshot, send a question in a second PowerShell terminal:

```powershell
$body = @{
    question = "Why did checkout requests start timing out after the deployment?"
    topK = 3
} | ConvertTo-Json

$result = Invoke-RestMethod -Method Post `
    -Uri http://localhost:5080/api/retrieval/search `
    -ContentType "application/json" -Body $body

$result.matches | Select-Object rank, score,
    @{Name="file"; Expression={$_.evidence.sourceFile}},
    @{Name="startLine"; Expression={$_.evidence.startLine}},
    @{Name="endLine"; Expression={$_.evidence.endLine}} | Format-Table

$result.matches[0].evidence.content
$result.usage
```

`question` must be nonblank and at most 2,000 characters before trimming.
`topK` defaults to 3 and must be between 1 and 10. If fewer chunks exist,
the API returns only those available; it never pads or duplicates results.
Invalid input returns HTTP 400 validation problem details before any model call.

To search only a specific evidence document, add an optional `documentId` to
the request, using an exact ID from `GET /api/ingestion/preview`. For example:

```powershell
$body = @{
    question = "What recovery followed the operator rollback to version 1.7.2?"
    documentId = "inc-syn-002-logs"
    topK = 3
} | ConvertTo-Json

$result = Invoke-RestMethod -Method Post `
    -Uri http://localhost:5080/api/retrieval/search `
    -ContentType "application/json" -Body $body
```

Unknown or blank document IDs return HTTP 400 before a model call. Omit the
field (or use `null`) to search all documents. The response distinguishes the
total `indexedChunkCount` from the filtered `searchedChunkCount`. Filtering
limits eligible documents before ranking; it does not change cosine scores
or automatically infer an incident from the question.

The app first reads the existing snapshot and current bundled evidence. It
rejects missing/invalid snapshots, unsupported schema versions, changed
endpoint/deployment, missing model identity, stale or duplicate evidence,
altered citation metadata, and empty/zero/nonfinite/inconsistent vectors.
These failures return HTTP 409 with instructions to regenerate the snapshot,
before paying to embed a question. Valid embedding configuration is required;
missing configuration still returns HTTP 503.

For a valid snapshot, the existing embedding client generates exactly one
question vector using the same deployment. Its reported model ID and dimensions
must match the snapshot before scoring. This final compatibility check needs a
model call, so a mismatch at this stage can still consume tokens. Silent model
version changes that retain the same deployment, model ID, and dimensions
cannot be detected here; regenerate evidence after changing the deployed
model/version.

The app calculates **exact cosine similarity** against each stored vector,
sorts scores descending, breaks ties by chunk ID, and returns the top matches.
Scores range from -1 to 1; higher means a closer vector match. They are not
probabilities, root-cause confidence, or proof that a chunk answers the question.
The API always returns the nearest available chunks, even for unrelated
questions; there is no calibrated relevance cutoff or abstention policy yet.
Overlapping chunks can appear together. Prose runbooks and historical reports
can outrank dense current-incident JSON logs, even when a question names the
current incident. This was observed for a rollback/recovery query during
development; use explicit document scoping when only current logs are wanted.
There is no keyword/hybrid search, reranker, document-diversity filtering, or
time-window filtering.

Each match includes its rank, score, full chunk text, stable ID, original
filename, document type, inclusive line range, and content hash. The response
also reports the question's actual token usage, snapshot timestamp,
deployment/model identity, dimensions, and a `null` estimated cost until
pricing is configured. Question text is returned to the caller but not logged;
query vectors are neither returned nor persisted. Searching never rewrites
the evidence snapshot.

This is intentionally a small, file-backed local vector store: each request
reads and validates nine vectors, then compares all of them. It needs no new
NuGet package, Azure AI Search resource, or database. A managed vector index
becomes useful when document volume and concurrency grow.

This endpoint retrieves evidence only. It does not generate a diagnosis,
call a chat model, or execute investigation/remediation tools.

## Generate a grounded answer

Deploy `gpt-5-mini` in the same Foundry resource as the embedding deployment.
Set `Foundry:ChatDeployment` to its deployment name, preserving the existing
OpenAI v1 endpoint, API key, and embedding deployment. Do not substitute a
project endpoint for the working `/openai/v1/` endpoint. Restart the app in
`Development` after configuration or code changes.

The new endpoint accepts the same question, optional document scope, and
`topK` limits as semantic search:

```powershell
$body = @{
    question = "What recovery followed the operator rollback to version 1.7.2?"
    documentId = "inc-syn-002-logs"
    topK = 3
} | ConvertTo-Json

$result = Invoke-RestMethod -Method Post `
    -Uri http://localhost:5080/api/investigation/answer `
    -ContentType "application/json" -Body $body

$result.answer
$result.insufficientEvidence
$result.limitations
$result.citations | Select-Object sourceId,
    @{Name="file"; Expression={$_.evidence.sourceFile}},
    @{Name="startLine"; Expression={$_.evidence.startLine}},
    @{Name="endLine"; Expression={$_.evidence.endLine}} | Format-Table
$result.retrieval.usage
$result.generation.usage
```

The flow is: validate input and chat configuration, reuse semantic search to
embed the question and rank stored evidence, give the question and retrieved
sources to the chat model, then validate its structured response. The app
labels retrieved chunks `S1`, `S2`, etc. Labels are local to each answer, while
the underlying evidence IDs and hashes remain stable. Similarity scores are
not given to the chat model and must not be treated as confidence.

The model returns concise `statements`, their `sourceIds`, an
`insufficientEvidence` flag, and `limitations`. Every statement must cite at
least one retrieved source. The server rejects missing, unknown, duplicated,
or malformed citation references and creates the final `[S1]` markers itself.
`citations` contains only referenced sources; original filenames, line ranges,
hashes, and text come from validated retrieval, never model-generated metadata.
`retrieval` preserves all selected chunks and their scores for inspection.

The instructions require evidence-only answers, separate historical incidents
from current observations, distinguish runbook guidance from actions actually
taken, and ask for explicit uncertainty rather than invented facts or numerical
confidence. Questions and documents are treated as untrusted input; embedded
instructions must not override the system instructions. No tools, web search,
conversation history, or automatic remediation are enabled.

This is **not a factual correctness guarantee**. A valid citation proves that
the referenced chunk was retrieved, not that it supports every word in the
statement. Abstention and prompt-injection resistance are model behaviors,
not deterministic guarantees. The model may still miss evidence, overstate
causality, or combine sources incorrectly; inspect citations and add repeatable
answer-quality evaluations before relying on it.

An unrelated question should return `insufficientEvidence: true`, a description
of missing evidence, and no unsupported statements. Partial supported statements
are allowed with that flag. This model-level abstention does not add a calibrated
relevance threshold to semantic search. Use `documentId` when only one incident's
logs should be eligible.

The serialized question/source context is capped at 32,000 characters, not
tokens. Oversized context returns HTTP 422 with instructions to reduce `topK`;
the query embedding has already occurred, but the chat call has not. Context is
not silently truncated. Chat output is limited to 4,096 tokens, including any
reasoning tokens, with a 90-second chat-call timeout and no SDK retries for chat.
Cancellation propagates from the HTTP request. These are per-call bounds, not
an account spending limit. This step does not regenerate stored evidence.

The existing SDK's Chat Completions adapter supplies `IChatClient`, a strict JSON
response schema, and the reasoning-model-compatible completion token limit.
No temperature or experimental reasoning-effort setting is sent. No package
or Agent Framework dependency is added.

Missing configuration returns HTTP 503; missing/incompatible snapshots return
409. Provider failures use the same safe 502/503 handling as embeddings.
Incomplete, refused, filtered, malformed, or uncited model answers return 502,
not a fabricated successful answer. Chat timeout returns 504. Model usage may
still have occurred on failed or cancelled requests.

`generation` reports the deployment, provider model ID, actual token `usage`,
and a `null` `estimatedCostUsd` until pricing is configured. Reasoning tokens
are a subset of output tokens, not an additional amount to add to total.
Embedding usage is separate under `retrieval.usage`. Chat usage is logged before
answer validation, but there is no cumulative usage ledger. Neither prompts
nor generated answers are persisted or logged by this feature.

## Generate a structured incident report

Restart the updated app in `Development`, using the existing working Foundry
settings and stored embeddings. No new Azure resource or re-ingestion is needed.

```powershell
$body = @{
    question = "Investigate INC-SYN-002: what happened, what is the probable cause, and what should an operator check next?"
    documentId = "inc-syn-002-logs"
    topK = 3
} | ConvertTo-Json

$report = Invoke-RestMethod -Method Post `
    -Uri http://localhost:5080/api/investigation/report `
    -ContentType "application/json" -Body $body

$report.report
$report.insufficientEvidence
$report.uncertainty
$report.recommendedNextSteps | Format-List
$report.citations | ForEach-Object {
    "$($_.sourceId): $($_.evidence.sourceFile), lines $($_.evidence.startLine)-$($_.evidence.endLine)"
}
$report.generation.usage
```

The request has the same validation and optional exact document scope as search
and answers. This example selects all three current-incident log chunks, keeping
the historical incident out of the report. Source labels are local to this
response; do not reuse labels from an earlier answer.

The response includes a readable `report` plus structured fields:

| Field | Meaning |
| --- | --- |
| `summary` | Up to 3 cited overview statements. |
| `evidence` | Up to 8 cited observations; runbook-only references are rejected here. |
| `probableRootCause` | A cited inference, or `null` when not established. |
| `rootCauseClassification` | Server-owned `hypothesis` or `not_established`, never a confirmed diagnosis or numeric confidence score. |
| `uncertainty` | 1 to 5 evidence gaps or causal limitations, even when a probable cause is proposed. |
| `recommendedNextSteps` | Up to 5 suggested checks with action, rationale, and source IDs. Each has server-owned `executionStatus: "not_executed"`. |
| `insufficientEvidence` | The report cannot fully address the question or establish a supported probable cause. Partial supported findings are allowed. |
| `citations` | Only referenced, original retrieved evidence with filenames, line ranges, text, and hashes. |
| `retrieval`, `generation` | Selected chunks, question embedding usage, and chat model/token metadata. Dollar cost remains `null`. |

The model receives a strict JSON schema. The server validates required fields,
section/text bounds, source references, and that a root-cause hypothesis cites
an observational source already referenced by `evidence`. A runbook alone
cannot meet that rule. Sufficient reports require a summary, observations,
and a probable cause; partial reports explicitly identify missing evidence.

The prompt restricts recommendations to read-only checks, not rollbacks,
restarts, deployment, or data changes. This remains a **model instruction**,
not a deterministic semantic guarantee: inspect recommendations before using
them. No tools are registered on this report endpoint, no actions are executed, and reports are not
persisted. The execution status and hypothesis label are set by application
code, not trusted model output.

The shared context, output-token, timeout, usage, and HTTP error bounds from
the answer endpoint apply here too. Invalid generated reports return 502
rather than partially validated success. Citation identity validation does
not prove factual support; a cited hypothesis can still be wrong. Review
causal claims and source applicability, especially if multiple incidents are
eligible. The application does not infer a document filter from an incident
ID in natural-language text or implement an as-of time filter.

## Run a tool-assisted investigation

This separate endpoint keeps existing search, answer, and report behavior
unchanged. It starts with one semantic search, lets the chat model request
additional synthetic evidence, then generates a report from the initial and
tool-returned evidence. No new Azure deployment, packages, or re-embedding
is required. Restart the updated application in `Development`.

```powershell
$body = @{
    question = "Investigate INC-SYN-002. Check recorded configuration changes and recovery log events, then explain the probable cause and uncertainty."
    documentId = "inc-syn-002-logs"
    topK = 1
} | ConvertTo-Json

$investigation = Invoke-RestMethod -Method Post `
    -Uri http://localhost:5080/api/investigation/run `
    -ContentType "application/json" -Body $body `
    -TimeoutSec 260

$investigation.toolCalls | Select-Object name, status, matchingItemCount, truncated | Format-Table
$investigation.report.report
$investigation.evidenceCollectionLimitations
$investigation.report.citations | ForEach-Object {
    "$($_.sourceId): $($_.evidence.sourceFile), lines $($_.evidence.startLine)-$($_.evidence.endLine)"
}
$investigation.totalChatUsage
```

Here `topK = 1` deliberately supplies a smaller initial context to demonstrate
additional evidence collection. It does not guarantee which tools the model
will request. The model can stop without requesting any tools.

| Tool | Permitted input and result |
| --- | --- |
| `GetIncidentLogs` | An exact application-log document ID and optional inclusive `startTimeUtc`/`endTimeUtc`; returns up to 12 original log lines, oldest first. |
| `GetConfigurationChanges` | Same inputs, restricted to recorded configuration, configuration-review, deployment, and rollback-start events. It does not inspect real manifests or live configuration. |
| `GetRunbook` | An exact runbook document ID with null time filters; returns the bundled runbook chunks as guidance, not observed incident facts. |

UTC filters use ISO 8601 with seconds and a trailing `Z`, for example
`2026-10-17T10:08:00Z`. Fractional seconds up to seven digits are supported.
Filters apply to tool results only; the initial retrieval is not time-filtered.
This does not implement an as-of investigation. A missing matching event
does not establish that an event never happened.

The catalog contains only our fixed synthetic documents. A supplied
`documentId` restricts **all tools**, including runbooks, to that document.
The example therefore permits log/configuration reads but not a separate
runbook. Omit `documentId` to allow the full bundled catalog, while reviewing
the risk of mixing historical and current incidents. No arbitrary paths,
URLs, shell commands, database queries, live Azure access, or write tools
are accepted.

The application explicitly dispatches only the three named tools and validates
argument types, extra fields, document/type/scope, and time windows. Rejected
requests produce a visible `rejected` trace with a safe reason and no evidence;
they still consume the tool-call budget. File/parsing failures are real errors,
not successful empty results. A completed result with zero matches is different
from rejection. `matchingItemCount` reports all matching records/chunks;
`truncated` indicates whether the 12-item result cap omitted some.

Hard bounds are 3 tool calls, at most 3 tool-selection chat calls plus 1 report
chat call, 4,096 output tokens per chat call, 90 seconds per chat call, and a
240-second overall investigation deadline. Request cancellation propagates.
Tool selection uses optional native function calls with parallel calls disabled,
but the server independently checks the remaining budget and call identities.
Over-budget or malformed tool protocols return 502, not unrestricted execution.
The application executes accepted calls sequentially. Reaching the budget
normally ends collection and generates the report, with
`stopReason: "tool_limit_reached"` rather than claiming an exhaustive investigation.
The model's voluntary stop is `model_finished`, not a guarantee of completeness.

Planning-message JSON and final source-context JSON are each capped at 32,000
characters. Oversized context returns 422 without a report; earlier model usage
may already have occurred. Timeout returns 504. The usual configuration,
snapshot, provider, and generated-report validation errors still apply.
These per-request limits are not a subscription spending cap or cross-request
rate limit.

`report` contains the existing report response shape. Its `retrieval` remains
the initial vector-search result and `generation` is only the final report call.
`toolEvidence` contains additional evidence supplied by accepted tool reads.
Log evidence uses original, exact single-line ranges, canonical LF text, and
SHA-256 IDs/hashes; runbooks reuse the original chunks. Source labels are
allocated once per investigation and deduplicated by evidence ID.
Final citations can resolve to either initial or tool-returned evidence.
`toolCalls` records application-owned execution status, validated arguments,
source IDs, counts, and limits; it is not model reasoning.

`modelCalls` exposes provider model/deployment and actual usage for each
selection and final report call. `totalChatUsage` aggregates those calls,
preserving unknown token fields as null; it does not include the separate
question embedding usage under `report.retrieval.usage`. Reasoning tokens are
included in output tokens. Estimated dollar cost remains null. Completed chat
usage is logged before validation; failed requests may incur usage without
returning a result. No prompts, reports, or tool results are persisted or logged.

`evidenceCollectionLimitations` is server-owned: it always identifies the
synthetic-only and non-as-of scope, and flags rejected/truncated calls and an
exhausted budget. The final report also receives collection metadata and is
instructed to preserve uncertainty. Planner prose is discarded, never treated
as evidence. Citations still validate identity, not semantic correctness.
The model can choose unhelpful tools, miss recovery evidence, or stop early;
inspect the trace and source lines rather than assuming an agent improves quality.

The small loop uses the already installed Microsoft.Extensions.AI native
function-call contracts. No automatic function-invocation middleware or Agent
Framework dependency is needed for this bounded three-tool milestone.
Registered tools perform read-only synthetic evidence collection; the report's
`recommendedNextSteps` remain separate suggestions marked `not_executed`.

## Repeatable evaluations

In simple terms: ask the same questions again, check what evidence was found,
check that citations really point to the original files, and check a few known
recovery facts. This makes regressions and failures visible rather than judging
quality from one convincing-looking report.

With the configured application running and its existing embeddings available,
run from the `backend` directory using PowerShell 7.2 or newer:

```powershell
.\scripts\Invoke-IncidentEvaluation.ps1
```

The default six cases cover recovery retrieval, configuration retrieval,
a recovery answer, an unrelated question requiring abstention, a current-incident
report, and a runbook-only report that must not invent incident observations.
This makes six question-embedding requests and four chat requests if all
requests complete successfully. It reuses stored document embeddings.

For the two additional, identically worded fixed-report/tool-assisted cases:

```powershell
.\scripts\Invoke-IncidentEvaluation.ps1 -IncludeTools

# Rerun only the comparison, or one core case, to limit usage:
.\scripts\Invoke-IncidentEvaluation.ps1 -IncludeTools `
    -CaseId comparison-fixed-report,comparison-tool-report
.\scripts\Invoke-IncidentEvaluation.ps1 -CaseId answer-recovery
```

The comparison adds two question embeddings and up to five chat requests.
It does not require any particular tool or minimum tool-call count; a model
may stop without using tools. Calls run sequentially without retries and use
Azure quota. Health and fixture validation run before paid calls. An alternate
local application can be selected with `-BaseUri http://localhost:5087`;
remote hosts, credentials, URL paths and redirects are not accepted.

Expectations live in `data\evaluation\cases.json`, outside the application.
Each synthetic document is pinned by its line count and SHA-256 of canonical
LF-joined lines without a trailing separator. Fixture drift stops the runner
before model calls: deliberately review the anchors and expectations rather
than changing hashes just to get a pass.

| Check/metric | Meaning and limit |
| --- | --- |
| Required-evidence recall | Fraction of distinct expected document/line anchors available in the initial retrieval, and then initial plus tool evidence. Overlapping chunks do not double-count. Not a relevance, precision or confidence score. |
| Original evidence identity | File, document ID/type, exact line range/text, content hash and stable ID must match the local pinned fixtures. |
| Citation references | Claims reference known original sources with no duplicate labels or unused top-level citations. This proves identity, not that the cited text entails the claim. |
| Recovery fact patterns | Check observed statements for pool maximum 100, p95 latency 185, and zero waiting requests with the corresponding cited recovery lines. These are surface regex checks, not an entailment judge; paraphrases or negation can defeat them. Recommendations do not count as observed facts. |
| Abstention/report structure | Check expected insufficiency, absent runbook-only observations/cause, explicit uncertainty, hypothesis labels and suggestions marked `not_executed`. |
| Tool trace and usage | Check scope, allowlisted completed reads, call/result bounds, stop state, source labels and aggregate chat usage. Question embeddings are separate; reasoning is already included in output tokens. |
| Manual review flags | Surface potentially unsupported Kubernetes/pod/cluster language, overstated causal certainty, rejected/truncated reads and exhausted budgets. Conditional wording may be legitimate; flags alone do not fail a case. |

Every run writes a unique ignored `.local\evaluations\<run-id>` folder with
`summary.json` and raw synthetic JSON responses for human review. The summary
is updated after each case, includes actual model identities/usage and timings,
and exposes comparison results side by side. Backend persistence is still
disabled; this explicit local runner saves its own results. It reads no secrets.
Failed or timed-out requests may incur usage absent from a successful response;
dollar costs remain unknown, not fabricated.

Exit code `0` means all selected mechanical checks passed. Exit code `1`
indicates a failed check or request/scoring error. Inspect each case's
`checks`, `reviewFlags`, and saved response; an HTTP/model error is not an
answer-quality pass. A successful score still requires human review of
causality, source applicability, unsupported assumptions and recommendation
safety. One small synthetic incident is not a calibrated benchmark.
Abstention may include cited explanations of why evidence is missing; the
unrelated-question case does not demand an empty statement array. It also
checks for unsupported numeric temperature answers, with the same regex limits.

Initial live findings were intentionally retained rather than weakening the
expectations: the configuration query missed its required override line at
`topK = 1`; the full-context answer/report and both abstention cases passed their
mechanical checks. The matched comparison covered 3/6 required lines with fixed
retrieval and 4/6 with tools, not the required 6/6. The tool planner incorrectly
restricted the configuration read to a later window, missing the deployment
override. Its report also inferred an operator made the change from a review
event, which the evidence does not establish. Source-identity validation cannot
catch that causal attribution. Both reports matched the three recovery fact
patterns, while the tool run consumed substantially more tokens. These results
motivate retrieval/tool-planning improvements, not a claim that agents always win.
On a later full-suite run the tool-assisted case returned HTTP 502 because
generation did not pass completion validation (the safe response groups
refusal, filtering and incomplete output together). The runner saved it as
an error, not an answer-quality score. That run had five passes, two failed
evidence-coverage checks, and one request error.

## Configuration and secrets

Azure configuration is optional. Leave the committed defaults empty to run
without Azure access. Supplying settings does not activate model calls;
only explicit embedding ingestion, search, answer, report, or investigation requests do.

| Environment variable | Meaning |
| --- | --- |
| `Foundry__Endpoint` | Azure model endpoint, ending in `/openai/v1/`. |
| `Foundry__ChatDeployment` | Your deployed chat model's deployment name. |
| `Foundry__EmbeddingDeployment` | Your embedding model's deployment name. |
| `Foundry__ApiKey` | Secret credential; never put this in a tracked file. |
| `ASPNETCORE_ENVIRONMENT` | Use `Development` to load development JSON settings and User Secrets. |
| `ASPNETCORE_URLS` | Optional HTTP binding override. |

Double underscores map environment variables to nested configuration keys.
Environment variables override JSON settings and User Secrets. A supplied
Foundry endpoint must be an absolute HTTPS URL or startup fails with a
configuration validation error. Embedding requests additionally require an
API key, a deployment name, and an HTTPS endpoint on `*.openai.azure.com` or
`*.services.ai.azure.com` with the `/openai/v1/` path. Incomplete embedding
settings do not prevent health checks or local preview. Actual credentials
and deployment compatibility are checked by the provider when invoked.
Answer and report generation additionally require `Foundry:ChatDeployment`.

### Manual local development settings

Open `src\IncidentIQ.Api\appsettings.Development.json` in your editor.
Under `Foundry`, paste your full Azure OpenAI endpoint (ending in
`/openai/v1/`) into `Endpoint` and your API key into `ApiKey`, keeping the
values inside the double quotes. `EmbeddingDeployment` is already
`text-embedding-3-small`; set `ChatDeployment` to your chat deployment name
(for example, `gpt-5-mini`) when enabling grounded answers.

This local file is ignored by Git and excluded from publish output. It stores
the key in plain text on your computer: do not share it or include its contents
in screenshots. If the file is absent in a fresh checkout, create it with a
`Foundry` object containing `Endpoint`, `ApiKey`, `ChatDeployment`, and
`EmbeddingDeployment`.

Stop your running app with Ctrl+C and use the commands under "Run locally"
to restart in `Development`. Saving these settings does not make model calls.
Previously saved User Secrets override this file; environment variables
override both. Use one configuration method to avoid conflicting values.

### Alternative: User Secrets

User Secrets keep values outside the repository:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet user-secrets set "Foundry:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/openai/v1/" --project src\IncidentIQ.Api
dotnet user-secrets set "Foundry:ChatDeployment" "YOUR-CHAT-DEPLOYMENT" --project src\IncidentIQ.Api
dotnet user-secrets set "Foundry:EmbeddingDeployment" "YOUR-EMBEDDING-DEPLOYMENT" --project src\IncidentIQ.Api
```

To add the API key without typing it into terminal command history:

```powershell
$key = Read-Host "Foundry API key" -MaskInput
@{ Foundry = @{ ApiKey = $key } } | ConvertTo-Json -Depth 3 | dotnet user-secrets set --project src\IncidentIQ.Api
Remove-Variable key
```

User Secrets are not encrypted and are only for development. Do not paste
secret values into shared logs or screenshots. `.env` files are ignored for
safety but are **not automatically loaded** by this application.

Local `appsettings.Development.json` or `appsettings.Production.json` files
are ignored by Git. Never add secrets to
the tracked `appsettings.json`. Ignore rules do not protect files that have
already been committed.

The API is unauthenticated and intended for local development only. Use
loopback bindings; authentication and HTTPS termination are required before
public deployment.

## Structure and design decisions

```text
IncidentIQ.sln
global.json
data\
  evaluation\
    cases.json
  synthetic\
    checkout-timeouts-runbook.md
    inc-syn-001-report.md
    inc-syn-002-checkout.jsonl
scripts\
  IncidentEvaluation.psm1
  Invoke-IncidentEvaluation.ps1
src\
  IncidentIQ.Api\
    Program.cs
    Configuration\
      ApiExceptionHandler.cs
      FoundryClientConfiguration.cs
      FoundryOptions.cs
    Features\
      Embeddings\
        EmbeddingIngestionService.cs
        EmbeddingSnapshot.cs
        FoundryEmbeddingGenerator.cs
        LocalEmbeddingStore.cs
        VectorMath.cs
      Ingestion\
        DocumentChunker.cs
        EvidenceChunk.cs
        IngestionPreview.cs
        SyntheticIngestionService.cs
      Investigation\
        FoundryChatClient.cs
        GroundedAnswerResult.cs
        GroundedAnswerService.cs
        GroundedGenerationService.cs
        GroundedResponse.cs
        IncidentReportResult.cs
        IncidentReportService.cs
        InvestigationResult.cs
        InvestigationService.cs
        ReadOnlyInvestigationTools.cs
      Retrieval\
        SemanticSearchResult.cs
        SemanticSearchService.cs
    appsettings.json
```

`Program.cs` wires configuration, logging, error handling, health checks,
ingestion endpoints, and search request validation. `ApiExceptionHandler`
shares safe configuration/provider/snapshot problem responses across endpoints.
`FoundryOptions` gives model integration named configuration properties.
`SyntheticIngestionService` reads the bundled evidence, while `DocumentChunker`
handles deterministic splitting. Separating file loading from splitting lets
later retrieval reuse the same chunks without depending on HTTP.
`FoundryEmbeddingGenerator` lazily creates a reusable client behind
`IEmbeddingGenerator<string, Embedding<float>>` from Microsoft.Extensions.AI.
`EmbeddingIngestionService` coordinates the existing chunks, model call,
vector validation, and local snapshot. `LocalEmbeddingStore` shares atomic
snapshot writes and cancellable reads between ingestion and retrieval.
`VectorMath` validates vectors and computes cosine similarity.
`SemanticSearchService` checks snapshot compatibility, embeds the question,
and returns ranked evidence without generating an answer.
`FoundryClientConfiguration` shares endpoint and credential validation across
both model clients. `FoundryChatClient` lazily creates the reusable `IChatClient`.
`GroundedGenerationService` shares retrieval, bounded source context, chat
invocation, completion checks, and usage metadata between answers and reports.
`GroundedResponse` shares strict JSON parsing and citation validation.
`GroundedAnswerService` and `IncidentReportService` keep their own schemas,
instructions, section validation, and readable response formatting.
`InvestigationService` adds bounded native tool selection and shares the final
report builder. `ReadOnlyInvestigationTools` is the explicit allowlisted
dispatcher over bundled evidence, not a general-purpose execution engine.

One application keeps the two-day project manageable. Later features will be
organized into ingestion, retrieval, and investigation folders inside this
project rather than deployed as separate microservices.

ASP.NET Core supplies hosting, logging, configuration, health checks, and JSON.
The model integration uses these pinned NuGet dependencies:

| Package | Version | Purpose |
| --- | --- | --- |
| `Microsoft.Extensions.AI.OpenAI` | 10.11.0 | Adapts the SDK to provider-neutral embedding and chat interfaces. |
| `OpenAI` | 2.14.0 | Calls Azure's OpenAI-compatible v1 embeddings and chat endpoints. |

The adapter brings in Microsoft.Extensions.AI.Abstractions transitively.
No Agent Framework or Foundry project-management SDK is needed for this step.

## Next milestone

Continue the smallest grounded retrieval flow:

1. Review generated answers against the original evidence and document failure cases.
2. Improve the measured top-1 configuration retrieval miss without weakening the pinned evaluation.
3. Improve tool time-window selection and review causal attribution using the fixed/tool comparison.
4. Add configured pricing and cumulative usage/cost tracking.
5. Consider Azure AI Search Free if a managed index is useful and available.

No production data, automatic remediation, or AKS deployment is planned.
