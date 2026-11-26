import { useEffect, useMemo, useState } from 'react'
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Button, Checkbox, Field, Input, MessageBar, MessageBarBody, MessageBarTitle, Radio, RadioGroup, Select, Subtitle2, Switch,
  Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Textarea, Tooltip, tokens,
} from '@fluentui/react-components'
import { ArrowUndo24Regular, Checkmark24Regular, Dismiss24Regular, Play24Regular, Warning16Regular } from '@fluentui/react-icons'
import { api, fmtDate, terminal, type Job } from '../api'
import { ErrorBar, JobProgress, Loading, NeedTenant, PageHeader, Panel, Stat, useHasRole, useMe, useTenant } from '../common'
import { snapshotLabel, useCatalog, useSnapshots, workloadNames } from './Backups'

type PlanStatus = 'Planning' | 'Draft' | 'PendingApproval' | 'Approved' | 'Applying' | 'Applied' | 'PartiallyApplied' | 'Failed' | 'Rejected'
type ActionKind = 'Create' | 'Update' | 'RecycleBin' | 'Delete' | 'Skip'
type ActionStatus = 'Pending' | 'Succeeded' | 'Failed' | 'Skipped' | 'Excluded'

export interface RestorePlan {
  id: string; tenantId: string; sourceTenantId: string; sourceSnapshotId: string; status: PlanStatus; createdBy: string; createdAt: string
  approvedBy?: string; approvedAt?: string; planJobId?: string; applyJobId?: string; appliedAt?: string
  createCount: number; updateCount: number; recycleBinCount: number; deleteCount: number; skipCount: number; warningCount: number
  succeededCount: number; failedCount: number; error?: string; sourceTenantName?: string; targetTenantName?: string; crossTenant: boolean
}
interface RestoreAction {
  index: number; type: string; workload: string; displayName: string; kind: ActionKind; reason?: string; sourceObjectId?: string
  targetObjectId?: string; changes: string[]; warnings: string[]; isDependency: boolean; isRelink: boolean; excluded: boolean; status: ActionStatus
  error?: string; verificationIssues: string[]
}
interface PlanDetail { plan: RestorePlan; actions: RestoreAction[]; notes: string[]; request: { options: RestoreOptions } }
interface RestoreOptions {
  overwriteChanged: boolean; includeDependencies: boolean; includeAssignments: boolean; deleteExtra: boolean
  conditionalAccessReportOnly: boolean; useRecycleBin: boolean; relinkReferences: boolean; stopOnError: boolean; nameSuffix?: string
}

const defaultOptions: RestoreOptions = {
  overwriteChanged: true, includeDependencies: true, includeAssignments: true, deleteExtra: false,
  conditionalAccessReportOnly: false, useRecycleBin: true, relinkReferences: true, stopOnError: false, nameSuffix: '',
}

const kindColor: Record<ActionKind, 'success' | 'warning' | 'brand' | 'danger' | 'informative'> =
  { Create: 'success', Update: 'warning', RecycleBin: 'brand', Delete: 'danger', Skip: 'informative' }
const kindLabel: Record<ActionKind, string> = { Create: 'Create', Update: 'Update', RecycleBin: 'Undelete', Delete: 'Delete', Skip: 'No change' }
const statusColor: Record<ActionStatus, 'success' | 'danger' | 'informative' | 'subtle'> =
  { Succeeded: 'success', Failed: 'danger', Pending: 'informative', Skipped: 'subtle', Excluded: 'subtle' }

export function PlanStatusBadge({ status }: { status: PlanStatus }) {
  const color = ({
    Planning: 'brand', Draft: 'informative', PendingApproval: 'warning', Approved: 'brand', Applying: 'brand',
    Applied: 'success', PartiallyApplied: 'warning', Failed: 'danger', Rejected: 'subtle',
  } as const)[status]
  return <Badge appearance="tint" color={color}>{status.replace(/([a-z])([A-Z])/g, '$1 $2')}</Badge>
}

// ------------------------------------------------------------------ wizard + plan list

export function RestorePage() {
  const { tenantId, tenants } = useTenant()
  const [params] = useSearchParams()
  const nav = useNavigate()
  const qc = useQueryClient()
  const snapshots = useSnapshots(tenantId)
  const catalog = useCatalog()
  const canRestore = useHasRole(tenantId, 'RestoreOperator')
  const [snapshotId, setSnapshotId] = useState(params.get('snapshot') ?? '')
  const [targetId, setTargetId] = useState('')
  const [scope, setScope] = useState<'all' | 'workloads' | 'types' | 'items'>(params.get('item') ? 'items' : 'all')
  const [workloads, setWorkloads] = useState<string[]>([])
  const [types, setTypes] = useState<string[]>(params.get('type') ? [params.get('type')!] : [])
  const [options, setOptions] = useState<RestoreOptions>(defaultOptions)
  const itemIds = useMemo(() => params.getAll('item').map(Number), [params])

  const plans = useQuery({
    queryKey: ['restorePlans', tenantId], enabled: !!tenantId, refetchInterval: 5000,
    queryFn: () => api<RestorePlan[]>(`/api/tenants/${tenantId}/restore/plans`),
  })

  const usable = (snapshots.data ?? []).filter(s => s.status !== 'Running' && s.status !== 'Failed')
  useEffect(() => { if (!snapshotId && usable.length) setSnapshotId(usable[0].id) }, [usable, snapshotId])
  useEffect(() => setTargetId(tenantId ?? ''), [tenantId])
  const snapshot = usable.find(s => s.id === snapshotId)
  const restorable = (catalog.data ?? []).filter(c => c.restorable && (!snapshot || snapshot.workloads.includes(c.workload)))

  const create = useMutation({
    mutationFn: () => api<{ planId: string; jobId: string }>('/api/restore/plans', {
      method: 'POST',
      body: {
        snapshotId, targetTenantId: targetId || undefined, options,
        workloads: scope === 'workloads' ? workloads : undefined, types: scope === 'types' ? types : undefined,
        itemIds: scope === 'items' ? itemIds : undefined,
      },
    }),
    onSuccess: r => { qc.invalidateQueries({ queryKey: ['restorePlans'] }); nav(`/restore/plans/${r.planId}`) },
  })

  if (!tenantId) return <NeedTenant />
  const opt = (k: keyof RestoreOptions, label: string, hint?: string) => (
    <Tooltip content={hint ?? label} relationship="description">
      <Switch label={label} checked={!!options[k]} onChange={(_, d) => setOptions({ ...options, [k]: d.checked })} />
    </Tooltip>
  )
  const cross = !!targetId && targetId !== tenantId
  const canPlan = canRestore && snapshotId && (scope !== 'workloads' || workloads.length) && (scope !== 'types' || types.length) && (scope !== 'items' || itemIds.length)

  return (
    <>
      <PageHeader title="Restore" subtitle="Plan a restore, review exactly what will change, then apply it. Planning never writes to the tenant." />
      {!canRestore && <MessageBar intent="info"><MessageBarBody>You need the Restore operator role on this tenant to plan restores.</MessageBarBody></MessageBar>}
      <Panel>
        <Subtitle2>New restore plan</Subtitle2>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 16, marginTop: 12 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label="Restore from snapshot">
              <Select value={snapshotId} onChange={(_, d) => setSnapshotId(d.value)}>
                {usable.map(s => <option key={s.id} value={s.id}>{snapshotLabel(s)} ({s.itemCount} items)</option>)}
              </Select>
            </Field>
            <Field label="Into tenant" hint={cross ? 'Cross-tenant restore: objects are matched by name and references are remapped to the target.' : 'Same tenant (in-place restore).'}>
              <Select value={targetId} onChange={(_, d) => setTargetId(d.value)}>
                {tenants.filter(t => t.status === 'Active').map(t => <option key={t.id} value={t.id}>{t.displayName}{t.id === tenantId ? ' (this tenant)' : ''}</option>)}
              </Select>
            </Field>
            <Field label="What to restore">
              <RadioGroup value={scope} onChange={(_, d) => setScope(d.value as typeof scope)}>
                <Radio value="all" label="Everything in the snapshot" />
                <Radio value="workloads" label="Selected workloads" />
                <Radio value="types" label="Selected resource types" />
                {itemIds.length > 0 && <Radio value="items" label={`The ${itemIds.length} selected item(s)`} />}
              </RadioGroup>
            </Field>
            {scope === 'workloads' && (
              <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
                {(snapshot?.workloads ?? []).map(w => (
                  <Checkbox key={w} label={workloadNames[w] ?? w} checked={workloads.includes(w)}
                    onChange={(_, d) => setWorkloads(d.checked ? [...workloads, w] : workloads.filter(x => x !== w))} />
                ))}
              </div>
            )}
            {scope === 'types' && (
              <div style={{ maxHeight: 260, overflow: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: 4, padding: 8 }}>
                {restorable.map(c => (
                  <div key={c.type}>
                    <Checkbox label={`${workloadNames[c.workload] ?? c.workload}: ${c.displayName}`} checked={types.includes(c.type)}
                      onChange={(_, d) => setTypes(d.checked ? [...types, c.type] : types.filter(x => x !== c.type))} />
                  </div>
                ))}
              </div>
            )}
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <Text weight="semibold">Options</Text>
            {opt('overwriteChanged', 'Revert objects that changed since the snapshot')}
            {opt('includeDependencies', 'Include dependencies', 'Also restore groups, filters, locations etc. that selected objects reference, if they are missing.')}
            {opt('includeAssignments', 'Restore assignments and memberships')}
            {opt('useRecycleBin', 'Undelete from the Entra recycle bin when possible', 'Keeps the original object ID, so nothing else has to be re-pointed.')}
            {opt('relinkReferences', 'Re-link references to restored objects', 'When a user or group comes back, add it back to the groups, policies, assignments and roles that referenced it. Nothing else on those objects is changed.')}
            {opt('conditionalAccessReportOnly', 'Restore Conditional Access policies as report-only', 'Safer: review sign-in impact before enforcing.')}
            {opt('stopOnError', 'Stop at the first failure')}
            {opt('deleteExtra', 'Delete objects that are not in the snapshot (mirror)', 'Admins only. Removes policies created after the snapshot for the selected types.')}
            <Field label="Suffix for newly created names" hint="e.g. ' (restored)'. Leave empty to keep the original names.">
              <Input value={options.nameSuffix ?? ''} onChange={(_, d) => setOptions({ ...options, nameSuffix: d.value })} />
            </Field>
          </div>
        </div>
        {options.deleteExtra && <MessageBar intent="warning" style={{ marginTop: 12 }}><MessageBarBody>Mirror mode deletes objects. Review the plan carefully before applying.</MessageBarBody></MessageBar>}
        {create.error && <ErrorBar error={create.error} />}
        <div style={{ marginTop: 16 }}>
          <Button appearance="primary" icon={<ArrowUndo24Regular />} disabled={!canPlan || create.isPending} onClick={() => create.mutate()}>
            {create.isPending ? 'Starting…' : 'Build plan'}
          </Button>
        </div>
      </Panel>

      <Panel>
        <Subtitle2>Restore plans</Subtitle2>
        {plans.isLoading ? <Loading /> : plans.error ? <ErrorBar error={plans.error} /> : !plans.data?.length ? <Text>No restore plans yet.</Text> : (
          <Table size="small" style={{ marginTop: 8 }}>
            <TableHeader><TableRow>
              <TableHeaderCell>Created</TableHeaderCell><TableHeaderCell>Source</TableHeaderCell><TableHeaderCell>Status</TableHeaderCell>
              <TableHeaderCell>Changes</TableHeaderCell><TableHeaderCell>By</TableHeaderCell>
            </TableRow></TableHeader>
            <TableBody>
              {plans.data.map(p => (
                <TableRow key={p.id}>
                  <TableCell><RouterLink to={`/restore/plans/${p.id}`}>{fmtDate(p.createdAt)}</RouterLink></TableCell>
                  <TableCell>{p.crossTenant ? `${p.sourceTenantName} → ${p.targetTenantName}` : 'This tenant'}</TableCell>
                  <TableCell><PlanStatusBadge status={p.status} /></TableCell>
                  <TableCell><PlanCounts p={p} /></TableCell>
                  <TableCell>{p.createdBy}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </>
  )
}

function PlanCounts({ p }: { p: RestorePlan }) {
  if (p.status === 'Planning') return <Text size={200}>…</Text>
  const parts = [
    p.createCount && `${p.createCount} create`, p.updateCount && `${p.updateCount} update`,
    p.recycleBinCount && `${p.recycleBinCount} undelete`, p.deleteCount && `${p.deleteCount} delete`,
  ].filter(Boolean)
  return <Text size={200}>{parts.length ? parts.join(' · ') : 'nothing to change'}{p.failedCount ? ` · ${p.failedCount} failed` : ''}</Text>
}

// ------------------------------------------------------------------ plan detail

export function RestorePlanPage() {
  const { id } = useParams()
  const qc = useQueryClient()
  const me = useMe()
  const [showSkipped, setShowSkipped] = useState(false)
  const [selected, setSelected] = useState<number | null>(null)
  const [rejectReason, setRejectReason] = useState('')
  const q = useQuery({
    queryKey: ['restorePlan', id],
    queryFn: () => api<PlanDetail>(`/api/restore/plans/${id}`),
    refetchInterval: d => (d.state.data && ['Planning', 'Applying', 'Approved'].includes(d.state.data.plan.status) ? 1500 : false),
  })
  const plan = q.data?.plan
  const jobId = plan?.status === 'Planning' ? plan.planJobId : plan?.status === 'Applying' ? plan.applyJobId : undefined
  const job = useQuery({
    queryKey: ['job', jobId], enabled: !!jobId, refetchInterval: 1500,
    queryFn: () => api<Job>(`/api/jobs/${jobId}`),
  })
  const canRestore = useHasRole(plan?.tenantId, 'RestoreOperator')
  const refresh = () => { qc.invalidateQueries({ queryKey: ['restorePlan', id] }); qc.invalidateQueries({ queryKey: ['restorePlans'] }) }

  const toggle = useMutation({
    mutationFn: (a: RestoreAction) => api(`/api/restore/plans/${id}/actions`, { method: 'PATCH', body: a.excluded ? { include: [a.index] } : { exclude: [a.index] } }),
    onSuccess: refresh,
  })
  const apply = useMutation({ mutationFn: () => api(`/api/restore/plans/${id}/apply`, { method: 'POST' }), onSuccess: refresh })
  const approve = useMutation({ mutationFn: () => api(`/api/restore/plans/${id}/approve`, { method: 'POST' }), onSuccess: refresh })
  const reject = useMutation({ mutationFn: () => api(`/api/restore/plans/${id}/reject`, { method: 'POST', body: { reason: rejectReason } }), onSuccess: refresh })

  // A failed plan job leaves the plan in Planning: surface the job error.
  useEffect(() => { if (job.data && terminal(job.data.status)) refresh() }, [job.data?.status]) // eslint-disable-line react-hooks/exhaustive-deps

  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBar error={q.error} />
  const { actions, notes } = q.data!
  const p = plan!
  const visible = actions.filter(a => showSkipped || a.kind !== 'Skip')
  const sel = actions.find(a => a.index === selected)
  const applied = ['Applied', 'PartiallyApplied', 'Failed'].includes(p.status) && !!p.appliedAt
  const toDo = actions.filter(a => a.kind !== 'Skip' && !a.excluded).length
  const mine = me.data && p.createdBy.toLowerCase() === me.data.userPrincipalName?.toLowerCase()

  return (
    <>
      <PageHeader
        title={<span>Restore plan <PlanStatusBadge status={p.status} /></span>}
        subtitle={<>
          {p.crossTenant ? `${p.sourceTenantName} → ${p.targetTenantName}` : `In place: ${p.targetTenantName}`} · from{' '}
          <RouterLink to={`/snapshots/${p.sourceSnapshotId}`}>snapshot</RouterLink> · planned by {p.createdBy} {fmtDate(p.createdAt)}
          {p.approvedBy && ` · approved by ${p.approvedBy}`}
        </>}
        actions={<>
          {p.status === 'Draft' && canRestore && (
            <Button appearance="primary" icon={<Play24Regular />} disabled={!toDo || apply.isPending} onClick={() => {
              if (confirm(`Apply ${toDo} change(s) to ${p.targetTenantName}?`)) apply.mutate()
            }}>Apply {toDo} change{toDo === 1 ? '' : 's'}</Button>
          )}
        </>}
      />
      {(apply.error || approve.error || reject.error || toggle.error) && <ErrorBar error={apply.error ?? approve.error ?? reject.error ?? toggle.error} />}
      {p.error && <MessageBar intent="error"><MessageBarBody>{p.error}</MessageBarBody></MessageBar>}
      {job.data && !terminal(job.data.status) && (
        <Panel>
          <Text>{p.status === 'Planning' ? 'Comparing the snapshot with the tenant…' : 'Applying changes…'} {job.data.progressMessage}</Text>
          <JobProgress progress={job.data.progress} status={job.data.status} />
          <RouterLink to={`/jobs/${job.data.id}`}>View job log</RouterLink>
        </Panel>
      )}
      {p.status === 'Planning' && job.data?.status === 'Failed' && <MessageBar intent="error"><MessageBarBody>Planning failed: {job.data.error}</MessageBarBody></MessageBar>}

      {p.status === 'PendingApproval' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Waiting for approval</MessageBarTitle>
            This tenant requires a second restore operator to approve restores before they run.
            {mine ? ' You planned this restore, so someone else has to approve it.' : ''}
          </MessageBarBody>
        </MessageBar>
      )}
      {p.status === 'PendingApproval' && canRestore && !mine && (
        <Panel>
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end' }}>
            <Button appearance="primary" icon={<Checkmark24Regular />} disabled={approve.isPending} onClick={() => approve.mutate()}>Approve and apply</Button>
            <Field label="Reason (optional)" style={{ flex: 1 }}><Textarea value={rejectReason} onChange={(_, d) => setRejectReason(d.value)} rows={1} /></Field>
            <Button icon={<Dismiss24Regular />} disabled={reject.isPending} onClick={() => reject.mutate()}>Reject</Button>
          </div>
        </Panel>
      )}

      {p.status !== 'Planning' && (
        <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
          <Stat label="Create" value={p.createCount} />
          <Stat label="Update" value={p.updateCount} />
          <Stat label="Undelete" value={p.recycleBinCount} />
          <Stat label="Delete" value={p.deleteCount} />
          <Stat label="No change" value={p.skipCount} />
          {applied && <Stat label="Succeeded" value={p.succeededCount} />}
          {applied && <Stat label="Failed" value={p.failedCount} />}
        </div>
      )}
      {notes?.length > 0 && <MessageBar intent="info"><MessageBarBody>{notes.map((n, i) => <div key={i}>{n}</div>)}</MessageBarBody></MessageBar>}

      {p.status !== 'Planning' && (
        <div style={{ display: 'grid', gridTemplateColumns: sel ? 'minmax(0, 3fr) minmax(0, 2fr)' : '1fr', gap: 16 }}>
          <Panel>
            <div style={{ display: 'flex', justifyContent: 'space-between' }}>
              <Subtitle2>Actions, in the order they run</Subtitle2>
              <Switch label="Show unchanged" checked={showSkipped} onChange={(_, d) => setShowSkipped(d.checked)} />
            </div>
            <Table size="small" style={{ marginTop: 8 }}>
              <TableHeader><TableRow>
                {p.status === 'Draft' && <TableHeaderCell style={{ width: 40 }}>Run</TableHeaderCell>}
                <TableHeaderCell>Action</TableHeaderCell><TableHeaderCell>Object</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell>
                {applied && <TableHeaderCell>Result</TableHeaderCell>}
              </TableRow></TableHeader>
              <TableBody>
                {visible.map(a => (
                  <TableRow key={a.index} onClick={() => setSelected(a.index)} style={{ cursor: 'pointer', opacity: a.excluded ? 0.5 : 1 }}
                    appearance={a.index === selected ? 'brand' : 'none'}>
                    {p.status === 'Draft' && (
                      <TableCell onClick={e => e.stopPropagation()}>
                        {a.kind !== 'Skip' && <Checkbox checked={!a.excluded} disabled={!canRestore || toggle.isPending} onChange={() => toggle.mutate(a)} />}
                      </TableCell>
                    )}
                    <TableCell><Badge appearance="tint" color={kindColor[a.kind]}>{kindLabel[a.kind]}</Badge></TableCell>
                    <TableCell>
                      {a.displayName}{a.isDependency && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}> (dependency)</Text>}
                      {a.isRelink && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}> (re-link)</Text>}
                      {(a.warnings.length > 0 || a.verificationIssues.length > 0) && <Warning16Regular style={{ color: tokens.colorPaletteMarigoldForeground1, marginLeft: 4 }} />}
                    </TableCell>
                    <TableCell><Text size={200}>{workloadNames[a.workload] ?? a.workload} · {a.type.split('.')[1]}</Text></TableCell>
                    {applied && <TableCell>{a.kind !== 'Skip' && <Badge appearance="tint" color={statusColor[a.status]}>{a.status}</Badge>}</TableCell>}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {!visible.length && <Text>Everything already matches the snapshot. Nothing to restore.</Text>}
          </Panel>
          {sel && (
            <Panel>
              <Subtitle2>{sel.displayName}</Subtitle2>
              <div><Text size={200}>{sel.type}</Text></div>
              {sel.reason && <p><Text>{sel.reason}</Text></p>}
              {sel.error && <MessageBar intent="error"><MessageBarBody>{sel.error}</MessageBarBody></MessageBar>}
              {sel.warnings.length > 0 && (
                <MessageBar intent="warning"><MessageBarBody>{sel.warnings.map((w, i) => <div key={i}>{w}</div>)}</MessageBarBody></MessageBar>
              )}
              {sel.verificationIssues.length > 0 && (
                <MessageBar intent="warning"><MessageBarBody><MessageBarTitle>After restore, differs from the snapshot</MessageBarTitle>
                  {sel.verificationIssues.map((w, i) => <div key={i}><Text font="monospace" size={200}>{w}</Text></div>)}</MessageBarBody></MessageBar>
              )}
              {sel.changes.length > 0 && (<>
                <Text weight="semibold">Properties that will change</Text>
                <ul style={{ margin: '4px 0' }}>{sel.changes.slice(0, 50).map(c => <li key={c}><Text font="monospace" size={200}>{c}</Text></li>)}</ul>
              </>)}
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                Source ID {sel.sourceObjectId ?? '—'}{sel.targetObjectId ? ` · target ID ${sel.targetObjectId}` : ''}
              </Text>
            </Panel>
          )}
        </div>
      )}
    </>
  )
}
