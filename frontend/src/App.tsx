import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { investigate, loadDocuments } from './api'
import type { EvidenceDocument, InvestigationResult } from './api'
import ReportView from './ReportView'

const defaultQuestion = 'Investigate INC-SYN-002. What probably caused the checkout timeouts, what evidence supports that, and what happened after rollback?'
const collectionNames: Record<string, string> = {
  'inc-syn-002-logs': 'INC-SYN-002 / Checkout application logs',
  'inc-syn-001-report': 'INC-SYN-001 / Historical incident report',
  'checkout-timeouts-runbook': 'Checkout timeouts / Runbook guidance',
}
const errorMessage = (failure: unknown) => failure instanceof Error ? failure.message : 'An unexpected request error occurred.'

export default function App() {
  const [documents, setDocuments] = useState<EvidenceDocument[]>([])
  const [documentId, setDocumentId] = useState('')
  const [question, setQuestion] = useState(defaultQuestion)
  const [topK, setTopK] = useState(3)
  const [withTools, setWithTools] = useState(false)
  const [catalogState, setCatalogState] = useState<'loading' | 'ready' | 'error'>('loading')
  const [retry, setRetry] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [status, setStatus] = useState('')
  const [result, setResult] = useState<{ data: InvestigationResult; seconds: string } | null>(null)
  const activeRequest = useRef<AbortController | null>(null)
  const questionInput = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    const controller = new AbortController()
    let mounted = true
    const timeout = window.setTimeout(() => controller.abort(), 10000)
    loadDocuments(controller.signal)
      .then((docs) => {
        if (!mounted) return
        setDocuments(docs)
        setDocumentId(docs.find((doc) => doc.documentId === 'inc-syn-002-logs')?.documentId ?? docs[0].documentId)
        setCatalogState('ready')
      })
      .catch((failure: unknown) => {
        if (!mounted) return
        setCatalogState('error')
        setError(`Could not load synthetic evidence. ${controller.signal.aborted ? 'The backend connection timed out.' : errorMessage(failure)} Check the backend address and its allowed CORS origins.`)
      })
      .finally(() => window.clearTimeout(timeout))
    return () => { mounted = false; window.clearTimeout(timeout); controller.abort() }
  }, [retry])

  useEffect(() => () => activeRequest.current?.abort(), [])

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (activeRequest.current || catalogState !== 'ready') return
    const trimmedQuestion = question.trim()
    if (!trimmedQuestion || trimmedQuestion.length > 2000) {
      setError('Enter a nonblank question of at most 2,000 characters.')
      questionInput.current?.focus()
      return
    }
    const controller = new AbortController()
    activeRequest.current = controller
    let timedOut = false
    const started = performance.now()
    const timeout = window.setTimeout(() => { timedOut = true; controller.abort() }, 260000)
    setBusy(true)
    setError('')
    setResult(null)
    setStatus(withTools
      ? 'Retrieving evidence and investigating with bounded tools. This can take up to four minutes...'
      : 'Retrieving evidence and generating a cited report. This may take up to 90 seconds...')
    try {
      const data = await investigate({ question: trimmedQuestion, documentId, topK }, withTools, controller.signal)
      const seconds = ((performance.now() - started) / 1000).toFixed(1)
      setResult({ data, seconds })
      setStatus(`Report received in ${seconds}s. Review its sources and uncertainty before drawing conclusions.`)
    } catch (failure) {
      setStatus('')
      setError(controller.signal.aborted
        ? `${timedOut ? 'The browser request timed out.' : 'Investigation cancelled.'} No report was displayed. Earlier model calls may still have used Azure quota.`
        : `${errorMessage(failure)}${failure instanceof TypeError ? ' Check the backend connection, API address, HTTPS and CORS settings.' : ''}`)
    } finally {
      window.clearTimeout(timeout)
      activeRequest.current = null
      setBusy(false)
    }
  }

  return (
    <>
      <a className="skip-link" href="#workspace">Skip to investigation</a>
      <header className="site-header">
        <a className="brand" href="/" aria-label="IncidentIQ home"><span className="brand-mark" aria-hidden="true">IQ</span>IncidentIQ</a>
        <span className="environment-label">SYNTHETIC SANDBOX</span>
      </header>
      <main id="workspace">
        <div className="page-heading">
          <div>
            <p className="eyebrow">EVIDENCE BEFORE ASSUMPTIONS</p>
            <h1>Investigate with context.</h1>
            <p className="intro">Trace the symptoms. Examine the evidence. Keep uncertainty visible.</p>
          </div>
          <p className={`connection-status ${catalogState === 'ready' ? 'ready' : ''}`} role="status">
            {catalogState === 'loading' ? 'Checking local evidence...' : catalogState === 'ready'
              ? `${documents.length} synthetic evidence collections available` : 'Evidence connection unavailable'}
          </p>
        </div>
        <div className="workspace-grid">
          <aside className="request-panel panel" aria-labelledby="request-heading">
            <div className="panel-heading"><span className="step-number">01</span><h2 id="request-heading">Start an investigation</h2></div>
            <form onSubmit={submit}>
              <fieldset disabled={busy || catalogState !== 'ready'}>
                <label htmlFor="document">Evidence collection</label>
                <select id="document" required value={documentId} onChange={(event) => setDocumentId(event.target.value)} aria-describedby="scope-help">
                  {documents.map((doc) => <option key={doc.documentId} value={doc.documentId}>{collectionNames[doc.documentId] ?? `${doc.documentId} / ${doc.documentType}`}</option>)}
                </select>
                <p id="scope-help" className="field-help">Only the selected document is in scope, including tool reads.</p>
                <label htmlFor="question">What do you want to understand?</label>
                <textarea id="question" ref={questionInput} rows={7} maxLength={2000} required value={question}
                  onChange={(event) => setQuestion(event.target.value)} aria-describedby="question-help" />
                <p id="question-help" className="field-help">Ask about symptoms, recorded changes, recovery, or missing evidence. Maximum 2,000 characters.</p>
                <label htmlFor="top-k">Initial evidence chunks</label>
                <select id="top-k" value={topK} onChange={(event) => setTopK(Number(event.target.value))} aria-describedby="chunks-help">
                  <option value={1}>1 - Small context</option>
                  <option value={3}>3 - Recommended for this dataset</option>
                  <option value={5}>5 - Broader context</option>
                </select>
                <p id="chunks-help" className="field-help">Vector similarity selects these chunks. Scores are not confidence.</p>
                <label className="toggle-option" htmlFor="use-tools">
                  <input id="use-tools" type="checkbox" checked={withTools} onChange={(event) => setWithTools(event.target.checked)} aria-describedby="tools-help" />
                  <span>Use investigation tools <span className="tag">OPTIONAL</span></span>
                </label>
                <p id="tools-help" className="field-help">Allow up to 3 read-only evidence calls. More model calls, not a guarantee of a better answer.</p>
                <button type="submit" className="primary-button">Investigate incident <span aria-hidden="true">&rarr;</span></button>
              </fieldset>
            </form>
            {catalogState === 'error' && <button type="button" className="secondary-button" onClick={() => { setError(''); setCatalogState('loading'); setRetry((value) => value + 1) }}>Retry evidence connection</button>}
            {busy && <button type="button" className="secondary-button" onClick={() => activeRequest.current?.abort()}>Cancel request</button>}
            <p className="quota-note">Each investigation uses Azure model quota. Evidence must already be embedded. No automatic retries.</p>
            <div className="boundary-note"><strong>Read-only by design.</strong> Bundled synthetic evidence only. No live systems, production data, or automatic remediation.</div>
          </aside>
          <section className="results-panel" aria-labelledby="results-heading">
            <div className="result-heading"><h2 id="results-heading">Investigation report</h2><span className="tag">HUMAN REVIEW REQUIRED</span></div>
            <p className="request-status" role="status" aria-live="polite">{status}</p>
            {error && <div className="error-message" role="alert">{error}</div>}
            <div aria-busy={busy}>
              {!result && !busy && !error && <div className="empty-state panel">
                <span className="empty-symbol" aria-hidden="true">[ &middot; ]</span>
                <h3>A clear report starts with evidence.</h3>
                <p>Choose a collection and ask a question. Your report will connect observations to original source lines, explain a probable cause, and identify what remains unknown.</p>
                <div className="empty-steps"><span>Retrieve</span><span aria-hidden="true">&rarr;</span><span>Investigate</span><span aria-hidden="true">&rarr;</span><span>Review</span></div>
              </div>}
              {result && <ReportView result={result.data} seconds={result.seconds} />}
            </div>
          </section>
        </div>
        <footer>IncidentIQ / Synthetic incident investigation <span>Grounded evidence. Explicit uncertainty. Human judgment.</span></footer>
      </main>
    </>
  )
}
