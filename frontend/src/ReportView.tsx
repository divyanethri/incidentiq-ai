import type { InvestigationResult, Statement } from './api'

function Citations({ ids }: { ids: string[] }) {
  return ids.map((id) => (
    <a key={id} className="citation-link" href={`#source-${id}`} aria-label={`Inspect source ${id}`}
      onClick={() => {
        const source = document.getElementById(`source-${id}`)
        if (source instanceof HTMLDetailsElement) {
          source.open = true
          source.querySelector('summary')?.focus()
        }
      }}>{id}</a>
  ))
}

function Statements({ title, items, empty }: { title: string; items: Statement[]; empty: string }) {
  return (
    <section className="panel report-section">
      <h3>{title}</h3>
      {items.length ? <ul>{items.map((item, index) => <li key={index}>{item.text}<Citations ids={item.sourceIds} /></li>)}</ul>
        : <p className="field-help">{empty}</p>}
    </section>
  )
}

const count = (value: number | null | undefined) => value == null ? 'Unknown' : value.toLocaleString()

export default function ReportView({ result, seconds }: { result: InvestigationResult; seconds: string }) {
  const { report, toolRun } = result
  const usage = toolRun?.totalChatUsage ?? report.generation.usage
  const metrics = [
    ['Elapsed time', `${seconds}s`],
    ['Chat calls', String(toolRun?.modelCalls.length ?? 1)],
    ['Chat input tokens', count(usage.inputTokens)],
    ['Chat output tokens', count(usage.outputTokens)],
    ['Chat total tokens', count(usage.totalTokens)],
    ['Reasoning tokens (within output)', count(usage.reasoningTokens)],
    ['Question embedding tokens', count(report.retrieval.usage.inputTokens)],
    ['Estimated cost (USD)', report.generation.estimatedCostUsd == null ? 'Unknown' : String(report.generation.estimatedCostUsd)],
  ]
  return (
    <div id="report">
      <div className="report-banner panel">
        <p className="eyebrow">{toolRun ? 'TOOL-ASSISTED INVESTIGATION' : 'FIXED-RETRIEVAL INVESTIGATION'}</p>
        <h3>{report.insufficientEvidence ? 'Partial report / insufficient evidence' : 'Probable cause / hypothesis, not confirmed'}</h3>
        <p id="report-question">{report.question}</p>
        <p className="field-help">Citations verify source identity, not causal proof. Review each claim against its evidence.</p>
      </div>
      <Statements title="Summary" items={report.summary} empty="No supported incident summary was established." />
      <Statements title="Observed evidence" items={report.evidence} empty="No incident observations were established from the supplied evidence." />
      <Statements title="Probable root cause / hypothesis" items={report.probableRootCause ? [report.probableRootCause] : []}
        empty="Not established from the supplied evidence." />
      <section className="panel report-section">
        <h3>Uncertainty &amp; missing evidence</h3>
        <ul>{report.uncertainty.map((text, index) => <li key={index}>{text}</li>)}</ul>
      </section>
      <section className="panel report-section">
        <h3>Recommended next checks</h3>
        {report.recommendedNextSteps.length ? <ul>{report.recommendedNextSteps.map((step, index) => (
          <li key={index}>
            <span className="next-step-status">SUGGESTED / NOT EXECUTED</span>
            <div>{step.action}</div>
            <p className="next-step-rationale">{step.rationale}<Citations ids={step.sourceIds} /></p>
          </li>
        ))}</ul> : <p>No supported next checks were returned.</p>}
      </section>
      <section className="panel report-section" aria-labelledby="sources-heading">
        <h3 id="sources-heading">Original sources</h3>
        <p className="field-help">Expand a citation to inspect its exact source text and line range.</p>
        {report.citations.map(({ sourceId, evidence }) => (
          <details key={sourceId} id={`source-${sourceId}`} className="source">
            <summary>{sourceId} / {evidence.sourceFile} / lines {evidence.startLine}-{evidence.endLine}</summary>
            <p className="source-note">{evidence.documentType} | SHA-256: {evidence.contentHash}</p>
            <pre>{evidence.content}</pre>
          </details>
        ))}
        {!report.citations.length && <p className="field-help">No sources were cited in this partial report.</p>}
      </section>
      <details className="panel report-section">
        <summary>Execution &amp; model usage</summary>
        <dl className="metrics">{metrics.map(([label, value]) => (
          <div key={label} className="metric"><dt>{label}</dt><dd>{value}</dd></div>
        ))}</dl>
        <p className="field-help">Report model: {report.generation.modelId ?? 'Unknown'} / deployment: {report.generation.deployment}</p>
        <p className="field-help">Token counts describe this response, not cumulative spending. Reasoning is included in output. Embedding usage is separate.</p>
        {toolRun && <>
          <h3>Read-only tool trace</h3>
          <p className="field-help">Stop reason: {toolRun.stopReason}. Tool calls: {toolRun.toolCalls.length}/{toolRun.maxToolCalls}.</p>
          {!toolRun.toolCalls.length && <p>The model stopped without requesting tools.</p>}
          {toolRun.toolCalls.map((call) => (
            <div key={call.callId} className="tool-call">
              <strong>{call.name} / {call.status}</strong>
              <p>{call.message}</p>
              <p>Matching items: {call.matchingItemCount}. Truncated: {call.truncated ? 'Yes' : 'No'}.</p>
              {call.arguments && <p>Document: {call.arguments.documentId}. UTC window: {call.arguments.startTimeUtc ?? 'Unbounded'} to {call.arguments.endTimeUtc ?? 'Unbounded'}.</p>}
            </div>
          ))}
          <h3>Collection limitations</h3>
          <ul>{toolRun.evidenceCollectionLimitations.map((text, index) => <li key={index}>{text}</li>)}</ul>
          <h3>Model calls</h3>
          <ul>{toolRun.modelCalls.map((call, index) => (
            <li key={index}>{call.stage}: {call.generation.modelId ?? 'Unknown'} / {count(call.generation.usage.totalTokens)} total tokens</li>
          ))}</ul>
        </>}
      </details>
    </div>
  )
}
