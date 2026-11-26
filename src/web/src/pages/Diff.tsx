import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import {
  Badge, Button, Field, MessageBar, MessageBarBody, Select, Subtitle2, Switch, Table, TableBody, TableCell, TableHeader,
  TableHeaderCell, TableRow, Text, tokens, ProgressBar,
} from '@fluentui/react-components'
import { ArrowSync24Regular } from '@fluentui/react-icons'
import { api, fmtDate, terminal, type Job } from '../api'
import { ErrorBar, Json, Loading, NeedTenant, PageHeader, Panel, Stat, useHasRole, useTenant } from '../common'
import { snapshotLabel, useCatalog, useSnapshots, workloadNames, type Snapshot } from './Backups'

interface DiffEntry {
  type: string; workload: string; change: 'Added' | 'Removed' | 'Modified'; fromObjectId?: string; toObjectId?: string
  displayName: string; fromItemId?: number; toItemId?: number
}
interface DiffResult {
  from: Snapshot; to: Snapshot
  summary: { crossTenant: boolean; added: number; removed: number; modified: number; unchanged: number; entries: DiffEntry[] }
}
interface ItemDiff {
  before?: unknown; after?: unknown; displayName: string; type: string
  changes: { path: string; kind: 'Added' | 'Removed' | 'Changed'; before?: unknown; after?: unknown }[]
}

const changeColor = { Added: 'success', Removed: 'danger', Modified: 'warning', Changed: 'warning' } as const

function short(v: unknown) {
  if (v === undefined || v === null) return ''
  const s = typeof v === 'string' ? v : JSON.stringify(v)
  return s.length > 120 ? s.slice(0, 117) + '…' : s
}

export function ItemDiffPanel({ fromItem, toItem }: { fromItem?: number; toItem?: number }) {
  const [full, setFull] = useState(false)
  const q = useQuery({
    queryKey: ['itemDiff', fromItem, toItem], enabled: !!(fromItem || toItem),
    queryFn: () => api<ItemDiff>(`/api/diff/item?${fromItem ? `fromItem=${fromItem}` : ''}${fromItem && toItem ? '&' : ''}${toItem ? `toItem=${toItem}` : ''}`),
  })
  if (!fromItem && !toItem) return <Text>Select a change to see what's different.</Text>
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBar error={q.error} />
  const d = q.data!
  return (
    <>
      <Subtitle2>{d.displayName}</Subtitle2>
      <Table size="extra-small" style={{ marginTop: 8 }}>
        <TableHeader><TableRow><TableHeaderCell style={{ width: '35%' }}>Property</TableHeaderCell><TableHeaderCell>Before</TableHeaderCell><TableHeaderCell>After</TableHeaderCell></TableRow></TableHeader>
        <TableBody>
          {d.changes.map((c, i) => (
            <TableRow key={i}>
              <TableCell><Badge size="small" appearance="tint" color={changeColor[c.kind]}>{c.kind}</Badge> <Text font="monospace" size={200}>{c.path || '(object)'}</Text></TableCell>
              <TableCell className={c.before !== undefined ? 'diff-del' : ''}><Text font="monospace" size={200}>{short(c.before)}</Text></TableCell>
              <TableCell className={c.after !== undefined ? 'diff-add' : ''}><Text font="monospace" size={200}>{short(c.after)}</Text></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <Switch label="Show full JSON" checked={full} onChange={(_, x) => setFull(x.checked)} style={{ marginTop: 8 }} />
      {full && (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
          <div><Text weight="semibold">Before</Text><Json value={d.before ?? null} /></div>
          <div><Text weight="semibold">After</Text><Json value={d.after ?? null} /></div>
        </div>
      )}
    </>
  )
}

export function DiffPage() {
  const { tenantId, tenants } = useTenant()
  const canRun = useHasRole(tenantId, 'BackupOperator')
  const [params, setParams] = useSearchParams()
  const { data: catalog } = useCatalog()
  const snaps = useSnapshots(tenantId)
  const [otherTenant, setOtherTenant] = useState<string>('')
  const other = useSnapshots(otherTenant || null)
  const [selected, setSelected] = useState<DiffEntry | null>(null)
  const [liveJob, setLiveJob] = useState<string | null>(null)

  const from = params.get('from') ?? ''
  const to = params.get('to') ?? ''
  const set = (k: string, v: string) => { const p = new URLSearchParams(params); p.set(k, v); setParams(p); setSelected(null) }

  // Sensible defaults: baseline (or previous) → latest.
  useEffect(() => {
    if (!snaps.data?.length || (from && to)) return
    const baseline = snaps.data.find((s) => s.isBaseline)
    const p = new URLSearchParams(params)
    if (!from) p.set('from', (baseline ?? snaps.data[1] ?? snaps.data[0]).id)
    if (!to) p.set('to', snaps.data[0].id)
    setParams(p, { replace: true })
  }, [snaps.data, from, to, params, setParams])

  const diff = useQuery({
    queryKey: ['diff', from, to], enabled: !!from && !!to && to !== 'live',
    queryFn: () => api<DiffResult>(`/api/diff?from=${from}&to=${to}&tenantId=${tenantId}`),
  })

  const startLive = useMutation({
    mutationFn: () => api<Job>('/api/jobs', { body: { type: 'config.drift', tenantId, parameters: { against: from } } }),
    onSuccess: (j) => setLiveJob(j.id),
  })
  const live = useQuery({
    queryKey: ['job', liveJob], enabled: !!liveJob, queryFn: () => api<Job>(`/api/jobs/${liveJob}`),
    refetchInterval: (q) => (q.state.data && terminal(q.state.data.status) ? false : 1500),
  })
  useEffect(() => {
    const r = live.data?.result as { to?: string } | undefined
    if (live.data?.status === 'Succeeded' && r?.to) { setLiveJob(null); set('to', r.to) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [live.data])

  const grouped = useMemo(() => {
    const g: Record<string, DiffEntry[]> = {}
    for (const e of diff.data?.summary.entries ?? []) (g[e.type] ??= []).push(e)
    return g
  }, [diff.data])
  const typeName = (t: string) => catalog?.find((c) => c.type === t)?.displayName ?? t

  if (!tenantId) return <NeedTenant />
  const otherSnaps = otherTenant ? other.data ?? [] : []

  return (
    <>
      <PageHeader title="Compare & drift" subtitle="What changed between two snapshots, or between a snapshot and the tenant right now" />
      <Panel style={{ marginBottom: 16 }}>
        <div style={{ display: 'flex', gap: 12, alignItems: 'end', flexWrap: 'wrap' }}>
          <Field label="From">
            <Select value={from} onChange={(_, d) => set('from', d.value)} style={{ minWidth: 280 }}>
              {(snaps.data ?? []).map((s) => <option key={s.id} value={s.id}>{snapshotLabel(s)}</option>)}
            </Select>
          </Field>
          <Text size={500}>→</Text>
          <Field label="To">
            <Select value={to} onChange={(_, d) => set('to', d.value)} style={{ minWidth: 280 }}>
              <option value="live">Live tenant (take a new snapshot)</option>
              {(otherTenant ? otherSnaps : snaps.data ?? []).map((s) => <option key={s.id} value={s.id}>{snapshotLabel(s)}</option>)}
            </Select>
          </Field>
          <Field label="Compare with tenant">
            <Select value={otherTenant} onChange={(_, d) => { setOtherTenant(d.value); set('to', d.value ? '' : snaps.data?.[0]?.id ?? '') }}>
              <option value="">Same tenant</option>
              {tenants.filter((t) => t.id !== tenantId).map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
            </Select>
          </Field>
          {to === 'live' && canRun && (
            <Button appearance="primary" icon={<ArrowSync24Regular />} disabled={!!liveJob || startLive.isPending || !from} onClick={() => startLive.mutate()}>
              Compare with live
            </Button>
          )}
        </div>
        {liveJob && live.data && (
          <div style={{ marginTop: 12 }}>
            <Text>Taking a live snapshot… {live.data.progressMessage}</Text>
            <ProgressBar value={live.data.progress} />
            {live.data.status === 'Failed' && <ErrorBar error={live.data.error} />}
          </div>
        )}
        <ErrorBar error={startLive.error} />
      </Panel>

      {to === 'live' ? null : diff.isLoading ? <Loading /> : diff.error ? <ErrorBar error={diff.error} /> : diff.data && (
        <>
          {diff.data.summary.crossTenant && (
            <MessageBar intent="info" style={{ marginBottom: 12 }}><MessageBarBody>
              Comparing two different tenants: objects are matched by name, and IDs (groups, filters…) naturally differ.
            </MessageBarBody></MessageBar>
          )}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 12, marginBottom: 16 }}>
            <Stat label="Added" value={diff.data.summary.added} />
            <Stat label="Modified" value={diff.data.summary.modified} />
            <Stat label="Removed" value={diff.data.summary.removed} />
            <Stat label="Unchanged" value={diff.data.summary.unchanged} hint={`${fmtDate(diff.data.from.createdAt)} → ${fmtDate(diff.data.to.createdAt)}`} />
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1.4fr)', gap: 12, alignItems: 'start' }}>
            <Panel style={{ maxHeight: '70vh', overflow: 'auto' }}>
              {Object.keys(grouped).length === 0 && <Text>No differences.</Text>}
              {Object.entries(grouped).map(([type, entries]) => (
                <div key={type} style={{ marginBottom: 12 }}>
                  <Text weight="semibold">{typeName(type)}</Text> <Text size={200}>· {workloadNames[entries[0].workload] ?? entries[0].workload}</Text>
                  {entries.map((e, i) => (
                    <div key={i} onClick={() => setSelected(e)} style={{
                      display: 'flex', gap: 8, alignItems: 'center', padding: '4px 6px', cursor: 'pointer', borderRadius: 4,
                      background: selected === e ? tokens.colorNeutralBackground1Selected : undefined,
                    }}>
                      <Badge size="small" appearance="tint" color={changeColor[e.change]}>{e.change}</Badge>
                      <span>{e.displayName}</span>
                    </div>
                  ))}
                </div>
              ))}
            </Panel>
            <Panel style={{ maxHeight: '70vh', overflow: 'auto' }}>
              <ItemDiffPanel fromItem={selected?.fromItemId} toItem={selected?.toItemId} />
            </Panel>
          </div>
        </>
      )}
    </>
  )
}
