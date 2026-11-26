import { useEffect, useRef, useState } from 'react'
import { Link as RouterLink, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Button, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Select, Subtitle2, Switch, tokens,
} from '@fluentui/react-components'
import { Dismiss24Regular } from '@fluentui/react-icons'
import { api, fmtDate, terminal, type Job, type JobLog } from '../api'
import { ErrorBar, JobProgress, JobStatusBadge, Json, Loading, PageHeader, Panel, useTenant } from '../common'

export function JobsTable({ jobs, showTenant }: { jobs: Job[]; showTenant?: boolean }) {
  const { tenants } = useTenant()
  return (
    <Table size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Job</TableHeaderCell>
          {showTenant && <TableHeaderCell>Tenant</TableHeaderCell>}
          <TableHeaderCell>Status</TableHeaderCell>
          <TableHeaderCell>Progress</TableHeaderCell>
          <TableHeaderCell>Created</TableHeaderCell>
          <TableHeaderCell>By</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {jobs.map((j) => (
          <TableRow key={j.id}>
            <TableCell><RouterLink to={`/jobs/${j.id}`}>{j.type}</RouterLink></TableCell>
            {showTenant && <TableCell>{tenants.find((t) => t.id === j.tenantId)?.displayName ?? '—'}</TableCell>}
            <TableCell><JobStatusBadge status={j.status} /></TableCell>
            <TableCell>
              {j.status === 'Running'
                ? <div><JobProgress progress={j.progress} status={j.status} /><Text size={200}>{j.progressMessage}</Text></div>
                : <Text size={200}>{j.error ?? ''}</Text>}
            </TableCell>
            <TableCell>{fmtDate(j.createdAt)}</TableCell>
            <TableCell><Text size={200}>{j.createdBy}</Text></TableCell>
          </TableRow>
        ))}
        {jobs.length === 0 && <TableRow><TableCell colSpan={6}>No jobs.</TableCell></TableRow>}
      </TableBody>
    </Table>
  )
}

export function JobsPage() {
  const { tenantId } = useTenant()
  const [allTenants, setAllTenants] = useState(false)
  const [status, setStatus] = useState('')
  const q = useQuery({
    queryKey: ['jobs', allTenants ? null : tenantId, status],
    queryFn: () => api<Job[]>(`/api/jobs?take=200${!allTenants && tenantId ? `&tenantId=${tenantId}` : ''}${status ? `&status=${status}` : ''}`),
    refetchInterval: 3000,
  })
  return (
    <>
      <PageHeader
        title="Jobs"
        subtitle="Backups, restores and checks. Updates live."
        actions={
          <>
            <Switch label="All tenants" checked={allTenants} onChange={(_, d) => setAllTenants(d.checked)} />
            <Select value={status} onChange={(_, d) => setStatus(d.value)}>
              <option value="">Any status</option>
              {['Queued', 'Running', 'Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled'].map((s) => <option key={s}>{s}</option>)}
            </Select>
          </>
        }
      />
      <ErrorBar error={q.error} />
      <Panel>{q.isLoading ? <Loading /> : <JobsTable jobs={q.data ?? []} showTenant={allTenants} />}</Panel>
    </>
  )
}

export function JobDetailPage() {
  const { id } = useParams()
  const qc = useQueryClient()
  const job = useQuery({
    queryKey: ['job', id],
    queryFn: () => api<Job>(`/api/jobs/${id}`),
    refetchInterval: (q) => (q.state.data && terminal(q.state.data.status) ? false : 2000),
  })
  const [logs, setLogs] = useState<JobLog[]>([])
  const after = useRef(0)
  const done = job.data ? terminal(job.data.status) : false

  useEffect(() => {
    let stop = false
    const poll = async () => {
      try {
        const more = await api<JobLog[]>(`/api/jobs/${id}/logs?after=${after.current}`)
        if (more.length) {
          after.current = Math.max(after.current, more[more.length - 1].id)
          // Overlapping polls (e.g. when the job finishes) can return the same lines: keep each once.
          setLogs((l) => {
            const seen = new Set(l.map((x) => x.id))
            return [...l, ...more.filter((m) => !seen.has(m.id))]
          })
        }
      } catch {
        /* retry on next tick */
      }
    }
    poll()
    const t = setInterval(() => !stop && poll(), 2000)
    if (done) setTimeout(poll, 500)
    return () => {
      stop = true
      clearInterval(t)
    }
  }, [id, done])

  const cancel = useMutation({
    mutationFn: () => api(`/api/jobs/${id}/cancel`, { method: 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['job', id] }),
  })

  if (job.isLoading) return <Loading />
  if (job.error) return <ErrorBar error={job.error} />
  const j = job.data!
  const levelColor = { Debug: tokens.colorNeutralForeground3, Info: tokens.colorNeutralForeground1, Warning: tokens.colorPaletteMarigoldForeground1, Error: tokens.colorPaletteRedForeground1 }

  return (
    <>
      <PageHeader
        title={j.type}
        subtitle={`Job ${j.id}`}
        actions={
          <>
            <JobStatusBadge status={j.status} />
            {!done && <Button icon={<Dismiss24Regular />} disabled={j.cancelRequested || cancel.isPending} onClick={() => cancel.mutate()}>
              {j.cancelRequested ? 'Cancelling…' : 'Cancel'}
            </Button>}
          </>
        }
      />
      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 1fr)', gap: 16 }}>
        <Panel>
          <Subtitle2>Log</Subtitle2>
          {j.status === 'Running' && <div style={{ margin: '8px 0' }}><JobProgress progress={j.progress} status={j.status} /><Text size={200}>{j.progressMessage}</Text></div>}
          <div style={{ maxHeight: 520, overflow: 'auto', marginTop: 8 }}>
            {logs.map((l) => (
              <div key={l.id} style={{ fontFamily: 'Consolas, monospace', fontSize: 12, color: levelColor[l.level] }}>
                {new Date(l.at).toLocaleTimeString()} {l.level.padEnd(7)} {l.message}
              </div>
            ))}
            {logs.length === 0 && <Text size={200}>No log entries yet.</Text>}
          </div>
        </Panel>
        <Panel>
          <Subtitle2>Details</Subtitle2>
          <dl style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 12px', fontSize: 13 }}>
            <dt>Created</dt><dd>{fmtDate(j.createdAt)} by {j.createdBy}</dd>
            <dt>Started</dt><dd>{fmtDate(j.startedAt)}</dd>
            <dt>Completed</dt><dd>{fmtDate(j.completedAt)}</dd>
            <dt>Attempt</dt><dd>{j.attempt} of {j.maxAttempts}</dd>
            {j.error && <><dt>Error</dt><dd style={{ color: tokens.colorPaletteRedForeground1 }}>{j.error}</dd></>}
          </dl>
          <Subtitle2>Parameters</Subtitle2>
          <Json value={j.parameters} />
          {j.result != null && <><Subtitle2>Result</Subtitle2><div style={{ maxHeight: 300, overflow: 'auto' }}><Json value={j.result} /></div></>}
        </Panel>
      </div>
    </>
  )
}
