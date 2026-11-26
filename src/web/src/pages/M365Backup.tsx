import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody,
  Radio, RadioGroup, Select, Subtitle2, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, tokens,
} from '@fluentui/react-components'
import { ArrowUndo24Regular, Search20Regular } from '@fluentui/react-icons'
import { api, fmtDate } from '../api'
import { ErrorBar, Loading, NeedTenant, PageHeader, Panel, Stat, useHasRole, useTenant } from '../common'

type Kind = 'exchange' | 'oneDrive' | 'sharePoint'
interface Policy { id: string; displayName: string; kind: Kind; status: string; units: number; retention?: string }
interface Unit { id: string; policyId: string; kind: Kind; status: string; resourceId: string; displayName: string }
interface RestorePoint { id: string; protectionDateTime: string; expirationDateTime?: string; tags?: string }
interface Session { id: string; kind: Kind; status: string; createdAt?: string; completedAt?: string; createdBy?: string; error?: string }
interface Status { available: boolean; status: string; message?: string; policies: Policy[]; sessions: Session[] }

const kindLabel: Record<string, string> = { exchange: 'Exchange', oneDrive: 'OneDrive', sharePoint: 'SharePoint', unknown: 'Other' }
const sessionColor = (s: string) => s === 'completed' ? 'success' : s === 'failed' ? 'danger' : s.includes('Error') || s === 'completedWithError' ? 'warning' : 'informative'
/** ISO 8601 duration such as P365D → "365 days". */
const period = (p?: string) => p?.match(/^P(\d+)D$/) ? `${p.slice(1, -1)} days` : p ?? '—'

/**
 * Microsoft 365 Backup (Microsoft's own backup storage): what it protects and native whole-mailbox / OneDrive / site restores to a
 * point in time. It complements the item-level backups on the Files & data page.
 */
export function M365BackupPage() {
  const { tenantId } = useTenant()
  const canRestore = useHasRole(tenantId, 'RestoreOperator')
  const [kind, setKind] = useState<Kind>('exchange')
  const [search, setSearch] = useState('')
  const [restoring, setRestoring] = useState<Unit | null>(null)
  const status = useQuery({ queryKey: ['m365backup', tenantId], enabled: !!tenantId, queryFn: () => api<Status>(`/api/tenants/${tenantId}/m365backup`) })
  const units = useQuery({
    queryKey: ['m365units', tenantId], enabled: !!tenantId && !!status.data?.available,
    queryFn: () => api<Unit[]>(`/api/tenants/${tenantId}/m365backup/units`),
  })
  const rows = useMemo(() => (units.data ?? []).filter(u => u.kind === kind &&
    (!search || u.displayName.toLowerCase().includes(search.toLowerCase()))), [units.data, kind, search])

  if (!tenantId) return <NeedTenant />
  if (status.isLoading) return <Loading />
  const data = status.data
  const policyName = (id: string) => data?.policies.find(p => p.id === id)?.displayName ?? id

  return (
    <>
      <PageHeader
        title="Microsoft 365 Backup"
        subtitle="Microsoft's own backup storage: fast, full-fidelity restores of whole mailboxes, OneDrives and sites. It complements this tool's item-level backups."
      />
      {status.error && <ErrorBar error={status.error} />}
      {data && !data.available && (
        <MessageBar intent="info"><MessageBarBody>{data.message ?? `Microsoft 365 Backup is ${data.status}.`}</MessageBarBody></MessageBar>
      )}
      {data?.available && (
        <>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 16 }}>
            {(['exchange', 'oneDrive', 'sharePoint'] as Kind[]).map(k => {
              const ps = data.policies.filter(p => p.kind === k)
              return <Stat key={k} label={`${kindLabel[k]} protected`} value={ps.reduce((n, p) => n + p.units, 0)}
                hint={ps.length ? `${ps.length} polic${ps.length === 1 ? 'y' : 'ies'}` : 'No policy'} />
            })}
          </div>
          <Panel>
            <Subtitle2>Policies</Subtitle2>
            <Table size="small" style={{ marginTop: 8 }}>
              <TableHeader><TableRow>
                <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Workload</TableHeaderCell><TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Protected</TableHeaderCell><TableHeaderCell>Retention</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {data.policies.map(p => (
                  <TableRow key={p.id}>
                    <TableCell>{p.displayName}</TableCell><TableCell>{kindLabel[p.kind]}</TableCell>
                    <TableCell><Badge appearance="tint" color={p.status === 'active' ? 'success' : 'informative'}>{p.status}</Badge></TableCell>
                    <TableCell>{p.units}</TableCell><TableCell>{period(p.retention)}</TableCell>
                  </TableRow>
                ))}
                {!data.policies.length && <TableRow><TableCell colSpan={5}>No protection policies yet. Create them in the Microsoft 365 admin center.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </Panel>
          <Panel>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
              <TabList selectedValue={kind} onTabSelect={(_, d) => setKind(d.value as Kind)}>
                {(['exchange', 'oneDrive', 'sharePoint'] as Kind[]).map(k => (
                  <Tab key={k} value={k}>{kindLabel[k]} ({(units.data ?? []).filter(u => u.kind === k).length})</Tab>
                ))}
              </TabList>
              <Input contentBefore={<Search20Regular />} placeholder="Search" value={search} onChange={(_, d) => setSearch(d.value)} />
            </div>
            {units.error && <ErrorBar error={units.error} />}
            {units.isLoading ? <Loading /> : (
              <Table size="small" style={{ marginTop: 8 }}>
                <TableHeader><TableRow>
                  <TableHeaderCell>{kind === 'sharePoint' ? 'Site' : 'User'}</TableHeaderCell><TableHeaderCell>Policy</TableHeaderCell>
                  <TableHeaderCell>Status</TableHeaderCell><TableHeaderCell />
                </TableRow></TableHeader>
                <TableBody>
                  {rows.map(u => (
                    <TableRow key={u.id}>
                      <TableCell>{u.displayName}</TableCell><TableCell>{policyName(u.policyId)}</TableCell>
                      <TableCell><Badge appearance="tint" color={u.status === 'protected' ? 'success' : 'warning'}>{u.status}</Badge></TableCell>
                      <TableCell>
                        {canRestore && <Button size="small" icon={<ArrowUndo24Regular />} onClick={() => setRestoring(u)}>Restore…</Button>}
                      </TableCell>
                    </TableRow>
                  ))}
                  {!rows.length && <TableRow><TableCell colSpan={4}>Nothing protected here.</TableCell></TableRow>}
                </TableBody>
              </Table>
            )}
          </Panel>
          <Panel>
            <Subtitle2>Restore sessions</Subtitle2>
            <Table size="small" style={{ marginTop: 8 }}>
              <TableHeader><TableRow>
                <TableHeaderCell>Started</TableHeaderCell><TableHeaderCell>Workload</TableHeaderCell><TableHeaderCell>By</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>Finished</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {data.sessions.map(s => (
                  <TableRow key={s.id}>
                    <TableCell>{fmtDate(s.createdAt)}</TableCell><TableCell>{kindLabel[s.kind]}</TableCell><TableCell>{s.createdBy ?? '—'}</TableCell>
                    <TableCell>
                      <Badge appearance="tint" color={sessionColor(s.status)}>{s.status}</Badge>
                      {s.error && <Text size={200} style={{ display: 'block', color: tokens.colorPaletteRedForeground1 }}>{s.error}</Text>}
                    </TableCell>
                    <TableCell>{fmtDate(s.completedAt)}</TableCell>
                  </TableRow>
                ))}
                {!data.sessions.length && <TableRow><TableCell colSpan={5}>No restores yet.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </Panel>
        </>
      )}
      {restoring && <RestoreDialog tenantId={tenantId} unit={restoring} onClose={() => setRestoring(null)} />}
    </>
  )
}

function RestoreDialog({ tenantId, unit, onClose }: { tenantId: string; unit: Unit; onClose: () => void }) {
  const qc = useQueryClient()
  const points = useQuery({
    queryKey: ['m365points', tenantId, unit.id],
    queryFn: () => api<RestorePoint[]>(`/api/tenants/${tenantId}/m365backup/units/${encodeURIComponent(unit.id)}/restorePoints`),
  })
  const [pointId, setPointId] = useState<string>('')
  const [inPlace, setInPlace] = useState(false)
  const chosen = pointId || points.data?.[0]?.id || ''
  const restore = useMutation({
    mutationFn: () => api<Session>(`/api/tenants/${tenantId}/m365backup/restore`, { method: 'POST', body: { unitId: unit.id, restorePointId: chosen, inPlace } }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['m365backup', tenantId] }); onClose() },
  })
  const where = unit.kind === 'exchange' ? 'a new folder in the mailbox' : unit.kind === 'oneDrive' ? 'a new folder in the OneDrive' : 'a new site'
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Restore {unit.displayName}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <ErrorBar error={restore.error} />
            {points.error && <ErrorBar error={points.error} />}
            {points.isLoading ? <Loading /> : !points.data?.length ? <Text>No restore points in the last 30 days.</Text> : (
              <Field label="Restore point" hint="Points tagged fastRestore restore fastest.">
                <Select value={chosen} onChange={(_, d) => setPointId(d.value)}>
                  {points.data.map(p => <option key={p.id} value={p.id}>{fmtDate(p.protectionDateTime)}{p.tags && p.tags !== 'none' ? ` · ${p.tags}` : ''}</option>)}
                </Select>
              </Field>
            )}
            <Field label="Restore to">
              <RadioGroup value={inPlace ? 'inPlace' : 'new'} onChange={(_, d) => setInPlace(d.value === 'inPlace')}>
                <Radio value="new" label={`New location (${where}); nothing current is touched`} />
                <Radio value="inPlace" label="In place: roll the whole thing back to that point" />
              </RadioGroup>
            </Field>
            {inPlace && (
              <MessageBar intent="warning"><MessageBarBody>
                An in-place restore replaces current content with the restore point's, including changes made since. Microsoft runs it; it can't be cancelled here.
              </MessageBarBody></MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={!chosen || restore.isPending} onClick={() => restore.mutate()}>
              {restore.isPending ? 'Starting…' : 'Restore'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
