import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Button, Checkbox, Input, MessageBar, MessageBarBody, Tab, TabList, Table, TableBody, TableCell, TableHeader,
  TableHeaderCell, TableRow, Text, tokens,
} from '@fluentui/react-components'
import { ArrowUndo24Regular, Search20Regular } from '@fluentui/react-icons'
import { api, fmtDate, terminal, type Job } from '../api'
import { ErrorBar, JobProgress, Loading, NeedTenant, PageHeader, Panel, useHasRole, useTenant } from '../common'

interface DeletedObject {
  id: string; kind: string; displayName: string; userPrincipalName?: string; mail?: string; deletedDateTime?: string
  daysLeft?: number; snapshotItemId?: number; snapshotId?: string
}

const kinds = [
  { value: '', label: 'All' }, { value: 'user', label: 'Users' }, { value: 'group', label: 'Groups' },
  { value: 'application', label: 'App registrations' }, { value: 'servicePrincipal', label: 'Enterprise apps' },
  { value: 'administrativeUnit', label: 'Administrative units' },
]

/** Live view of the Entra recycle bin, with one-click undelete (keeps object IDs, so nothing needs re-linking). */
export function RecycleBinPage() {
  const { tenantId } = useTenant()
  const qc = useQueryClient()
  const nav = useNavigate()
  const canRestore = useHasRole(tenantId, 'RestoreOperator')
  const [kind, setKind] = useState('')
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [jobId, setJobId] = useState<string | null>(null)

  const q = useQuery({
    queryKey: ['recycleBin', tenantId, kind], enabled: !!tenantId,
    queryFn: () => api<DeletedObject[]>(`/api/tenants/${tenantId}/entra/deleted${kind ? `?kind=${kind}` : ''}`),
  })
  const job = useQuery({
    queryKey: ['job', jobId], enabled: !!jobId, queryFn: () => api<Job>(`/api/jobs/${jobId}`),
    refetchInterval: d => (d.state.data && terminal(d.state.data.status) ? false : 1000),
  })
  const restore = useMutation({
    mutationFn: (ids: string[]) => api<{ jobId: string }>(`/api/tenants/${tenantId}/entra/deleted/restore`, { method: 'POST', body: { ids } }),
    onSuccess: r => { setJobId(r.jobId); setSelected(new Set()) },
  })
  const finished = !!job.data && terminal(job.data.status)
  useEffect(() => { if (finished) qc.invalidateQueries({ queryKey: ['recycleBin', tenantId] }) }, [finished, qc, tenantId])

  const rows = useMemo(() => (q.data ?? []).filter(d => {
    const s = search.toLowerCase()
    return !s || d.displayName.toLowerCase().includes(s) || d.userPrincipalName?.toLowerCase().includes(s) || d.id === s
  }), [q.data, search])

  if (!tenantId) return <NeedTenant />
  const toggle = (id: string, on: boolean) => {
    const next = new Set(selected)
    if (on) next.add(id); else next.delete(id)
    setSelected(next)
  }

  return (
    <>
      <PageHeader
        title="Entra recycle bin"
        subtitle="Deleted users, groups and apps stay here for 30 days. Undeleting keeps the original ID, so memberships, roles and policy references come back with it."
        actions={canRestore && (
          <Button appearance="primary" icon={<ArrowUndo24Regular />} disabled={!selected.size || restore.isPending || (!!jobId && !finished)}
            onClick={() => restore.mutate([...selected])}>Restore {selected.size || ''} selected</Button>
        )}
      />
      {restore.error && <ErrorBar error={restore.error} />}
      {job.data && (
        <Panel>
          <Text>{terminal(job.data.status) ? `Finished: ${job.data.status}` : 'Restoring…'}</Text>
          <JobProgress progress={job.data.progress} status={job.data.status} />
        </Panel>
      )}
      <MessageBar intent="info">
        <MessageBarBody>
          Purged (permanently deleted) objects aren't listed here. Restore them from a snapshot on the Restore page: they are recreated
          with a new ID and every group, role, policy and assignment that referenced them is re-linked.
        </MessageBarBody>
      </MessageBar>
      <Panel>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
          <TabList selectedValue={kind} onTabSelect={(_, d) => { setKind(d.value as string); setSelected(new Set()) }}>
            {kinds.map(k => <Tab key={k.value} value={k.value}>{k.label}</Tab>)}
          </TabList>
          <Input contentBefore={<Search20Regular />} placeholder="Search" value={search} onChange={(_, d) => setSearch(d.value)} />
        </div>
        {q.isLoading ? <Loading /> : q.error ? <ErrorBar error={q.error} /> : !rows.length ? <Text>The recycle bin is empty.</Text> : (
          <Table size="small" style={{ marginTop: 8 }}>
            <TableHeader><TableRow>
              {canRestore && <TableHeaderCell style={{ width: 40 }}>
                <Checkbox checked={rows.every(r => selected.has(r.id)) ? true : selected.size ? 'mixed' : false}
                  onChange={(_, d) => setSelected(d.checked ? new Set(rows.map(r => r.id)) : new Set())} />
              </TableHeaderCell>}
              <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Kind</TableHeaderCell><TableHeaderCell>Deleted</TableHeaderCell>
              <TableHeaderCell>Purged in</TableHeaderCell><TableHeaderCell>In latest backup</TableHeaderCell>
            </TableRow></TableHeader>
            <TableBody>
              {rows.map(d => (
                <TableRow key={d.id}>
                  {canRestore && <TableCell><Checkbox checked={selected.has(d.id)} onChange={(_, x) => toggle(d.id, !!x.checked)} /></TableCell>}
                  <TableCell>
                    <div>{d.displayName}</div>
                    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{d.userPrincipalName ?? d.mail ?? d.id}</Text>
                  </TableCell>
                  <TableCell>{kinds.find(k => k.value === d.kind)?.label ?? d.kind}</TableCell>
                  <TableCell>{fmtDate(d.deletedDateTime)}</TableCell>
                  <TableCell>{d.daysLeft == null ? '—' : <Badge appearance="tint" color={d.daysLeft <= 5 ? 'danger' : d.daysLeft <= 14 ? 'warning' : 'informative'}>{d.daysLeft} days</Badge>}</TableCell>
                  <TableCell>
                    {d.snapshotItemId
                      ? <Button size="small" appearance="subtle" onClick={() => nav(`/snapshots/${d.snapshotId}`)}>Yes</Button>
                      : <Text size={200}>No</Text>}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </>
  )
}
