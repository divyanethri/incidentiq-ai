export interface EvidenceDocument {
  documentId: string
  documentType: string
}

export interface Evidence {
  sourceFile: string
  documentType: string
  content: string
  contentHash: string
  startLine: number
  endLine: number
}

export interface Statement {
  text: string
  sourceIds: string[]
}

export interface Citation {
  sourceId: string
  evidence: Evidence
}

export interface Usage {
  inputTokens: number | null
  outputTokens: number | null
  totalTokens: number | null
  reasoningTokens: number | null
}

interface Generation {
  deployment: string
  modelId: string | null
  usage: Usage
  estimatedCostUsd: number | null
}

export interface IncidentReport {
  question: string
  insufficientEvidence: boolean
  rootCauseClassification: 'hypothesis' | 'not_established'
  summary: Statement[]
  evidence: Statement[]
  probableRootCause: Statement | null
  uncertainty: string[]
  recommendedNextSteps: { action: string; rationale: string; sourceIds: string[]; executionStatus: 'not_executed' }[]
  citations: Citation[]
  generation: Generation
  retrieval: { usage: { inputTokens: number | null } }
}

export interface ToolRun {
  report: IncidentReport
  toolCalls: {
    callId: string
    name: string
    status: 'completed' | 'rejected'
    message: string
    matchingItemCount: number
    truncated: boolean
    arguments: { documentId: string; startTimeUtc?: string | null; endTimeUtc?: string | null } | null
  }[]
  modelCalls: { stage: string; generation: Generation }[]
  totalChatUsage: Usage
  stopReason: string
  maxToolCalls: number
  evidenceCollectionLimitations: string[]
}

export interface InvestigationResult {
  report: IncidentReport
  toolRun: ToolRun | null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isStrings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string')
}

function isCount(value: unknown): value is number | null {
  return value === null || (typeof value === 'number' && Number.isSafeInteger(value) && value >= 0)
}

function isUsage(value: unknown): value is Usage {
  return isRecord(value) && ['inputTokens', 'outputTokens', 'totalTokens', 'reasoningTokens'].every((key) => isCount(value[key]))
}

function isGeneration(value: unknown): value is Generation {
  return isRecord(value) && typeof value.deployment === 'string' &&
    (value.modelId === null || typeof value.modelId === 'string') && isUsage(value.usage) &&
    (value.estimatedCostUsd === null || (typeof value.estimatedCostUsd === 'number' && Number.isFinite(value.estimatedCostUsd) && value.estimatedCostUsd >= 0))
}

function isStatement(value: unknown): value is Statement {
  return isRecord(value) && typeof value.text === 'string' && isStrings(value.sourceIds) && value.sourceIds.length > 0
}

function isEvidence(value: unknown): value is Evidence {
  return isRecord(value) && typeof value.sourceFile === 'string' && typeof value.content === 'string' &&
    typeof value.documentType === 'string' && typeof value.contentHash === 'string' &&
    typeof value.startLine === 'number' && Number.isInteger(value.startLine) && value.startLine > 0 &&
    typeof value.endLine === 'number' && Number.isInteger(value.endLine) && value.endLine >= value.startLine
}

function isReport(value: unknown): value is IncidentReport {
  if (!isRecord(value) || typeof value.question !== 'string' || typeof value.insufficientEvidence !== 'boolean' ||
      !['hypothesis', 'not_established'].includes(String(value.rootCauseClassification)) ||
      !Array.isArray(value.summary) || !value.summary.every(isStatement) ||
      !Array.isArray(value.evidence) || !value.evidence.every(isStatement) ||
      !(value.probableRootCause === null || isStatement(value.probableRootCause)) ||
      !isStrings(value.uncertainty) || !Array.isArray(value.recommendedNextSteps) ||
      !Array.isArray(value.citations) || !isGeneration(value.generation) ||
      !isRecord(value.retrieval) || !isRecord(value.retrieval.usage) || !isCount(value.retrieval.usage.inputTokens)) return false
  const sourceIds = new Set<string>()
  for (const citation of value.citations) {
    if (!isRecord(citation) || typeof citation.sourceId !== 'string' || !/^S[1-9][0-9]*$/.test(citation.sourceId) ||
        sourceIds.has(citation.sourceId) || !isEvidence(citation.evidence)) return false
    sourceIds.add(citation.sourceId)
  }
  const claims = [...value.summary, ...value.evidence]
  if (value.probableRootCause !== null) claims.push(value.probableRootCause)
  for (const step of value.recommendedNextSteps) {
    if (!isRecord(step) || typeof step.action !== 'string' || typeof step.rationale !== 'string' ||
        !isStrings(step.sourceIds) || !step.sourceIds.length || step.executionStatus !== 'not_executed') return false
    claims.push({ text: step.action, sourceIds: step.sourceIds })
  }
  return claims.every((claim) => claim.sourceIds.every((id) => sourceIds.has(id)))
}

function isToolRun(value: unknown): value is ToolRun {
  return isRecord(value) && isReport(value.report) && isUsage(value.totalChatUsage) &&
    typeof value.stopReason === 'string' && typeof value.maxToolCalls === 'number' &&
    isStrings(value.evidenceCollectionLimitations) &&
    Array.isArray(value.modelCalls) && value.modelCalls.every((call: unknown) =>
      isRecord(call) && typeof call.stage === 'string' && isGeneration(call.generation)) &&
    Array.isArray(value.toolCalls) && value.toolCalls.every((call: unknown) =>
      isRecord(call) && typeof call.callId === 'string' && typeof call.name === 'string' &&
      (call.status === 'completed' || call.status === 'rejected') && typeof call.message === 'string' &&
      typeof call.matchingItemCount === 'number' && typeof call.truncated === 'boolean' &&
      (call.arguments === null || (isRecord(call.arguments) && typeof call.arguments.documentId === 'string' &&
        (call.arguments.startTimeUtc == null || typeof call.arguments.startTimeUtc === 'string') &&
        (call.arguments.endTimeUtc == null || typeof call.arguments.endTimeUtc === 'string'))))
}

function apiUrl(path: string): string {
  const configured: unknown = import.meta.env.VITE_API_BASE_URL
  const base = typeof configured === 'string' && configured.trim()
    ? configured.trim()
    : import.meta.env.DEV ? 'http://localhost:5000' : ''
  if (!base) throw new Error('Frontend configuration missing: set VITE_API_BASE_URL to the backend origin and rebuild.')
  const url = new URL(base)
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash || url.pathname !== '/') {
    throw new Error('VITE_API_BASE_URL must be an HTTP(S) backend origin without credentials, a path, query, or fragment.')
  }
  return new URL(path, url).href
}

async function request(path: string, signal: AbortSignal, body?: object): Promise<unknown> {
  const response = await fetch(apiUrl(path), {
    method: body ? 'POST' : 'GET',
    signal,
    credentials: 'omit',
    redirect: 'error',
    ...(body ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}),
  })
  let data: unknown
  try {
    data = await response.json()
  } catch {
    throw new Error(`HTTP ${response.status}: the backend did not return readable JSON.`)
  }
  if (!response.ok) {
    const lines = [`HTTP ${response.status}: ${isRecord(data) && typeof data.title === 'string' ? data.title : 'The request failed.'}`]
    if (isRecord(data)) {
      if (typeof data.detail === 'string') lines.push(data.detail)
      if (isRecord(data.errors)) {
        for (const messages of Object.values(data.errors)) if (isStrings(messages)) lines.push(...messages)
      }
      if (typeof data.traceId === 'string') lines.push(`Trace ID: ${data.traceId}`)
    }
    lines.push('No automatic retry was made. Failed model requests may still use Azure quota.')
    throw new Error(lines.join('\n'))
  }
  return data
}

export async function loadDocuments(signal: AbortSignal): Promise<EvidenceDocument[]> {
  const data = await request('/api/ingestion/preview', signal)
  if (!isRecord(data) || !Array.isArray(data.documents) || !data.documents.length) throw new Error('The backend returned no evidence collections.')
  const documents: EvidenceDocument[] = []
  for (const doc of data.documents) {
    if (!isRecord(doc) || typeof doc.documentId !== 'string' || typeof doc.documentType !== 'string') {
      throw new Error('The backend returned an unexpected evidence catalog.')
    }
    documents.push({ documentId: doc.documentId, documentType: doc.documentType })
  }
  return documents
}

export async function investigate(
  body: { question: string; documentId: string; topK: number },
  withTools: boolean,
  signal: AbortSignal,
): Promise<InvestigationResult> {
  const data = await request(withTools ? '/api/investigation/run' : '/api/investigation/report', signal, body)
  if (withTools && isToolRun(data)) return { report: data.report, toolRun: data }
  if (!withTools && isReport(data)) return { report: data, toolRun: null }
  throw new Error('The backend returned an unexpected report format. No report was displayed.')
}
