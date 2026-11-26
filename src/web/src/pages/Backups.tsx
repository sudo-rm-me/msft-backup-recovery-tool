import { useEffect, useMemo, useState } from 'react'
import { Link as RouterLink, useNavigate } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, SpinButton,
  Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Subtitle2, Tooltip, Menu, MenuTrigger,
  MenuPopover, MenuList, MenuItem, tokens,
} from '@fluentui/react-components'
import { CloudArrowUp24Regular, CalendarClock24Regular, MoreHorizontal20Regular, Star16Filled } from '@fluentui/react-icons'
import { api, download, fmtBytes, fmtDate, type Job } from '../api'
import { ErrorBar, Loading, NeedTenant, PageHeader, Panel, useHasRole, useTenant } from '../common'
import { ScheduleDialog } from './Schedules'

export interface CatalogEntry {
  type: string; workload: string; displayName: string; restorable: boolean; notes?: string; restoreNotes?: string; dependsOn: string[]
}

export interface Snapshot {
  id: string; tenantId: string; status: 'Running' | 'Completed' | 'Partial' | 'Failed'; createdAt: string; completedAt?: string
  workloads: string[]; itemCount: number; errorCount: number; warningCount: number; totalBytes: number
  addedCount: number; modifiedCount: number; removedCount: number; label?: string; isBaseline: boolean; expiresAt?: string; createdBy?: string
  jobId?: string
}

export const workloadNames: Record<string, string> = {
  entra: 'Entra ID', intune: 'Intune', azure: 'Azure', exchange: 'Exchange Online', teams: 'Teams', sharepoint: 'SharePoint',
  defender: 'Defender', purview: 'Purview',
}

export function useCatalog() {
  return useQuery({ queryKey: ['catalog'], queryFn: () => api<CatalogEntry[]>('/api/catalog'), staleTime: 300_000 })
}

export function useSnapshots(tenantId: string | null) {
  return useQuery({
    queryKey: ['snapshots', tenantId], enabled: !!tenantId,
    queryFn: () => api<Snapshot[]>(`/api/tenants/${tenantId}/snapshots`), refetchInterval: 10_000,
  })
}

export function SnapshotStatusBadge({ s }: { s: Snapshot }) {
  const color = { Running: 'brand', Completed: 'success', Partial: 'warning', Failed: 'danger' }[s.status] as 'brand' | 'success' | 'warning' | 'danger'
  return <Badge appearance="tint" color={color}>{s.status}</Badge>
}

export function snapshotLabel(s: Snapshot) {
  return `${fmtDate(s.createdAt)}${s.label ? ` · ${s.label}` : ''}${s.isBaseline ? ' · baseline' : ''}`
}

function Changes({ s }: { s: Snapshot }) {
  if (!s.addedCount && !s.modifiedCount && !s.removedCount) return <Text size={200}>no changes</Text>
  return (
    <Text size={200}>
      <span style={{ color: tokens.colorPaletteGreenForeground1 }}>+{s.addedCount}</span>{' '}
      <span style={{ color: tokens.colorPaletteMarigoldForeground1 }}>~{s.modifiedCount}</span>{' '}
      <span style={{ color: tokens.colorPaletteRedForeground1 }}>−{s.removedCount}</span>
    </Text>
  )
}

function RunBackupDialog({ open, onClose, tenantId }: { open: boolean; onClose: () => void; tenantId: string }) {
  const nav = useNavigate()
  const { data: catalog } = useCatalog()
  const workloads = useMemo(() => [...new Set((catalog ?? []).map((c) => c.workload))], [catalog])
  const [selected, setSelected] = useState<string[]>([])
  const [label, setLabel] = useState('')
  useEffect(() => { if (open) setSelected(workloads) }, [open, workloads])
  const run = useMutation({
    mutationFn: () => api<Job>('/api/jobs', { body: { type: 'backup.config', tenantId, parameters: { workloads: selected, label: label || undefined } } }),
    onSuccess: (j) => nav(`/jobs/${j.id}`),
  })
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Back up configuration now</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <ErrorBar error={run.error} />
            <Text>Workloads</Text>
            {workloads.map((w) => (
              <Checkbox key={w} label={`${workloadNames[w] ?? w} (${catalog?.filter((c) => c.workload === w).length} types)`} checked={selected.includes(w)}
                onChange={(_, d) => setSelected(d.checked ? [...selected, w] : selected.filter((x) => x !== w))} />
            ))}
            <Field label="Label (optional)"><Input value={label} onChange={(_, d) => setLabel(d.value)} placeholder="Before Q3 policy rollout" /></Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={!selected.length || run.isPending} onClick={() => run.mutate()}>Start backup</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function RetentionPanel({ tenantId }: { tenantId: string }) {
  const qc = useQueryClient()
  const isAdmin = useHasRole(tenantId, 'Admin')
  type S = { configRetentionDays: number; configKeepMinimum: number; dataRetentionDays: number; driftAlerts: boolean; requireRestoreApproval: boolean }
  const q = useQuery({ queryKey: ['tenantSettings', tenantId], queryFn: () => api<S>(`/api/tenants/${tenantId}/settings`) })
  const [s, setS] = useState<S | null>(null)
  useEffect(() => { if (q.data) setS(q.data) }, [q.data])
  const save = useMutation({
    mutationFn: () => api(`/api/tenants/${tenantId}/settings`, { method: 'PUT', body: s }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['tenantSettings', tenantId] }),
  })
  if (!s) return null
  const dirty = JSON.stringify(s) !== JSON.stringify(q.data)
  return (
    <Panel>
      <Subtitle2>Retention and safeguards</Subtitle2>
      <ErrorBar error={save.error} />
      <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'end', marginTop: 8 }}>
        <Field label="Keep config snapshots (days)"><SpinButton disabled={!isAdmin} min={1} value={s.configRetentionDays} onChange={(_, d) => setS({ ...s, configRetentionDays: d.value ?? Number(d.displayValue) })} /></Field>
        <Field label="Always keep newest"><SpinButton disabled={!isAdmin} min={1} value={s.configKeepMinimum} onChange={(_, d) => setS({ ...s, configKeepMinimum: d.value ?? Number(d.displayValue) })} /></Field>
        <Field label="Keep changed/deleted data (days)"><SpinButton disabled={!isAdmin} min={1} value={s.dataRetentionDays} onChange={(_, d) => setS({ ...s, dataRetentionDays: d.value ?? Number(d.displayValue) })} /></Field>
        <Switch disabled={!isAdmin} label="Drift alerts" checked={s.driftAlerts} onChange={(_, d) => setS({ ...s, driftAlerts: d.checked })} />
        <Switch disabled={!isAdmin} label="Restores need a second approver" checked={s.requireRestoreApproval} onChange={(_, d) => setS({ ...s, requireRestoreApproval: d.checked })} />
        {isAdmin && <Button disabled={!dirty || save.isPending} onClick={() => save.mutate()}>Save</Button>}
      </div>
      <Text size={200} style={{ display: 'block', marginTop: 8, color: tokens.colorNeutralForeground3 }}>
        Retention runs daily. Baseline snapshots are never pruned. With S3 Object Lock, objects are deleted after their lock expires.
      </Text>
    </Panel>
  )
}

export function BackupsPage() {
  const { tenantId, tenant } = useTenant()
  const canRun = useHasRole(tenantId, 'BackupOperator')
  const isAdmin = useHasRole(tenantId, 'Admin')
  const qc = useQueryClient()
  const nav = useNavigate()
  const [runOpen, setRunOpen] = useState(false)
  const [schedOpen, setSchedOpen] = useState(false)
  const snaps = useSnapshots(tenantId)
  const inv = () => qc.invalidateQueries({ queryKey: ['snapshots', tenantId] })
  const patch = useMutation({ mutationFn: ({ id, body }: { id: string; body: object }) => api(`/api/snapshots/${id}`, { method: 'PATCH', body }), onSuccess: inv })
  const del = useMutation({ mutationFn: (id: string) => api(`/api/snapshots/${id}`, { method: 'DELETE' }), onSuccess: inv })
  const [exporting, setExporting] = useState<string | null>(null)

  if (!tenantId) return <NeedTenant />
  return (
    <>
      <PageHeader title="Configuration backups" subtitle={`Snapshots of ${tenant?.displayName}'s configuration`}
        actions={canRun && <>
          <Button icon={<CalendarClock24Regular />} onClick={() => setSchedOpen(true)}>Schedule</Button>
          <Button appearance="primary" icon={<CloudArrowUp24Regular />} onClick={() => setRunOpen(true)}>Back up now</Button>
        </>} />
      <ErrorBar error={snaps.error ?? patch.error ?? del.error} />
      <Panel style={{ marginBottom: 16 }}>
        {snaps.isLoading ? <Loading /> : (
          <Table size="small">
            <TableHeader><TableRow>
              <TableHeaderCell>Taken</TableHeaderCell><TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>Workloads</TableHeaderCell>
              <TableHeaderCell>Items</TableHeaderCell><TableHeaderCell>Changes</TableHeaderCell><TableHeaderCell>Label</TableHeaderCell>
              <TableHeaderCell>Expires</TableHeaderCell><TableHeaderCell />
            </TableRow></TableHeader>
            <TableBody>
              {(snaps.data ?? []).map((s) => (
                <TableRow key={s.id}>
                  <TableCell>
                    <RouterLink to={`/snapshots/${s.id}`}>{fmtDate(s.createdAt)}</RouterLink>
                    {s.isBaseline && <Tooltip content="Baseline" relationship="label"><Star16Filled color={tokens.colorPaletteMarigoldForeground1} style={{ marginLeft: 4 }} /></Tooltip>}
                  </TableCell>
                  <TableCell><SnapshotStatusBadge s={s} />{(s.warningCount > 0 || s.errorCount > 0) && <Text size={200}> {s.errorCount + s.warningCount} issue(s)</Text>}</TableCell>
                  <TableCell><Text size={200}>{s.workloads.map((w) => workloadNames[w] ?? w).join(', ')}</Text></TableCell>
                  <TableCell>{s.itemCount} <Text size={200}>({fmtBytes(s.totalBytes)})</Text></TableCell>
                  <TableCell><Changes s={s} /></TableCell>
                  <TableCell>{s.label}</TableCell>
                  <TableCell><Text size={200}>{s.isBaseline ? 'never' : fmtDate(s.expiresAt)}</Text></TableCell>
                  <TableCell>
                    <Menu>
                      <MenuTrigger disableButtonEnhancement><Button appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label="Actions" /></MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          <MenuItem onClick={() => nav(`/snapshots/${s.id}`)}>Explore</MenuItem>
                          <MenuItem onClick={() => nav(`/diff?from=${s.id}&to=live`)}>Compare with live</MenuItem>
                          {canRun && <MenuItem onClick={() => nav(`/restore?snapshot=${s.id}`)}>Restore from this…</MenuItem>}
                          {canRun && <MenuItem disabled={exporting === s.id} onClick={async () => {
                            setExporting(s.id)
                            try { await download(`/api/snapshots/${s.id}/export`, `snapshot-${s.createdAt.slice(0, 10)}.zip`) } finally { setExporting(null) }
                          }}>Download as zip</MenuItem>}
                          {canRun && <MenuItem onClick={() => patch.mutate({ id: s.id, body: { isBaseline: !s.isBaseline } })}>{s.isBaseline ? 'Unset baseline' : 'Set as baseline'}</MenuItem>}
                          {canRun && <MenuItem onClick={() => { const l = prompt('Label', s.label ?? ''); if (l !== null) patch.mutate({ id: s.id, body: { label: l } }) }}>Edit label</MenuItem>}
                          {isAdmin && <MenuItem onClick={() => confirm('Delete this snapshot? Content only referenced by it is removed at the next retention run.') && del.mutate(s.id)}>Delete</MenuItem>}
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  </TableCell>
                </TableRow>
              ))}
              {snaps.data?.length === 0 && <TableRow><TableCell colSpan={8}>No snapshots yet. Run a backup or create a schedule.</TableCell></TableRow>}
            </TableBody>
          </Table>
        )}
      </Panel>
      <RetentionPanel tenantId={tenantId} />
      <RunBackupDialog open={runOpen} onClose={() => setRunOpen(false)} tenantId={tenantId} />
      <ScheduleDialog open={schedOpen} onClose={() => setSchedOpen(false)} tenantId={tenantId} defaultJobType="backup.config" />
    </>
  )
}

/** Dashboard widget: latest snapshot. */
export function LastBackupWidget({ tenantId }: { tenantId: string }) {
  const snaps = useSnapshots(tenantId)
  const last = snaps.data?.[0]
  return (
    <Panel style={{ marginBottom: 16 }}>
      <Subtitle2>Latest configuration backup</Subtitle2>
      {!last ? <Text style={{ display: 'block' }}>No backups yet.</Text> : (
        <div style={{ display: 'flex', gap: 24, alignItems: 'center', marginTop: 8, flexWrap: 'wrap' }}>
          <RouterLink to={`/snapshots/${last.id}`}>{fmtDate(last.createdAt)}</RouterLink>
          <SnapshotStatusBadge s={last} />
          <Text>{last.itemCount} items</Text>
          <Changes s={last} />
          <Text size={200}>{snaps.data!.length} snapshot(s) retained</Text>
        </div>
      )}
    </Panel>
  )
}
