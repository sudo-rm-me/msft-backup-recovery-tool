import { useEffect, useState } from 'react'
import { Link as RouterLink, useNavigate } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Select, Switch,
  Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Textarea, Menu, MenuTrigger, MenuPopover, MenuList, MenuItem,
} from '@fluentui/react-components'
import { Add24Regular, MoreHorizontal20Regular } from '@fluentui/react-icons'
import { api, fmtDate, type Job, type Schedule } from '../api'
import { ErrorBar, Loading, NeedTenant, PageHeader, Panel, useHasRole, useTenant } from '../common'

interface Preset { name: string; cron: string }

/** Default parameters offered for well-known job types, so operators don't start from an empty object. */
export const jobParameterTemplates: Record<string, Record<string, unknown>> = {
  'system.diagnostics': { steps: 5 },
}

const guessTimeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'

export function ScheduleDialog({ open, onClose, tenantId, existing, defaultJobType }: {
  open: boolean; onClose: () => void; tenantId: string; existing?: Schedule; defaultJobType?: string
}) {
  const qc = useQueryClient()
  const types = useQuery({ queryKey: ['jobTypes'], queryFn: () => api<{ type: string }[]>('/api/jobs/types') })
  const presets = useQuery({ queryKey: ['presets'], queryFn: () => api<Preset[]>('/api/schedules/presets') })
  const [name, setName] = useState('')
  const [jobType, setJobType] = useState('')
  const [preset, setPreset] = useState('daily')
  const [cron, setCron] = useState('0 2 * * *')
  const [tz, setTz] = useState(guessTimeZone())
  const [params, setParams] = useState('{}')
  const [enabled, setEnabled] = useState(true)

  useEffect(() => {
    if (!open) return
    if (existing) {
      setName(existing.name); setJobType(existing.jobType); setCron(existing.cron); setTz(existing.timeZone)
      setParams(JSON.stringify(existing.parameters, null, 2)); setEnabled(existing.enabled)
      setPreset(presets.data?.find((p) => p.cron === existing.cron)?.name ?? 'custom')
    } else {
      const jt = defaultJobType ?? ''
      setName(''); setJobType(jt); setPreset('daily'); setCron('0 2 * * *'); setTz(guessTimeZone()); setEnabled(true)
      setParams(JSON.stringify(jobParameterTemplates[jt] ?? {}, null, 2))
    }
  }, [open, existing, defaultJobType, presets.data])

  const preview = useQuery({
    queryKey: ['cronPreview', cron, tz],
    queryFn: () => api<{ cron: string; next: string[] }>('/api/schedules/preview', { body: { cron, timeZone: tz } }),
    enabled: open && !!cron, retry: false,
  })

  const save = useMutation({
    mutationFn: () => {
      let parameters: unknown
      try { parameters = JSON.parse(params || '{}') } catch { throw new Error('Parameters must be valid JSON.') }
      const body = { name, jobType, cron, timeZone: tz, parameters, enabled }
      return existing
        ? api(`/api/schedules/${existing.id}`, { method: 'PUT', body })
        : api(`/api/tenants/${tenantId}/schedules`, { body })
    },
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['schedules'] }); onClose() },
  })

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{existing ? 'Edit schedule' : 'New schedule'}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <ErrorBar error={save.error} />
            <Field label="Name" required><Input value={name} onChange={(_, d) => setName(d.value)} placeholder="Nightly config backup" /></Field>
            <Field label="Job" required>
              <Select value={jobType} onChange={(_, d) => {
                setJobType(d.value)
                if (!existing) setParams(JSON.stringify(jobParameterTemplates[d.value] ?? {}, null, 2))
              }}>
                <option value="">Choose…</option>
                {types.data?.map((t) => <option key={t.type}>{t.type}</option>)}
              </Select>
            </Field>
            <div style={{ display: 'flex', gap: 12 }}>
              <Field label="Frequency" style={{ flex: 1 }}>
                <Select value={preset} onChange={(_, d) => {
                  setPreset(d.value)
                  const p = presets.data?.find((x) => x.name === d.value)
                  if (p) setCron(p.cron)
                }}>
                  {presets.data?.map((p) => <option key={p.name} value={p.name}>{p.name}</option>)}
                  <option value="custom">custom cron</option>
                </Select>
              </Field>
              <Field label="Cron (min hour day month weekday)" style={{ flex: 1 }}
                validationState={preview.error ? 'error' : 'none'} validationMessage={preview.error ? (preview.error as Error).message : undefined}>
                <Input value={cron} onChange={(_, d) => { setCron(d.value); setPreset('custom') }} />
              </Field>
              <Field label="Time zone" style={{ flex: 1 }}><Input value={tz} onChange={(_, d) => setTz(d.value)} /></Field>
            </div>
            {preview.data && (
              <Text size={200}>Next runs: {preview.data.next.slice(0, 3).map((n) => new Date(n).toLocaleString()).join(' · ')}</Text>
            )}
            <Field label="Job parameters (JSON)">
              <Textarea value={params} onChange={(_, d) => setParams(d.value)} rows={6} style={{ fontFamily: 'Consolas, monospace' }} />
            </Field>
            <Switch label="Enabled" checked={enabled} onChange={(_, d) => setEnabled(d.checked)} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={!name || !jobType || save.isPending} onClick={() => save.mutate()}>Save</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

export function SchedulesPage() {
  const { tenantId, tenant } = useTenant()
  const canEdit = useHasRole(tenantId, 'BackupOperator')
  const qc = useQueryClient()
  const nav = useNavigate()
  const [editing, setEditing] = useState<Schedule | undefined>()
  const [open, setOpen] = useState(false)
  const q = useQuery({
    queryKey: ['schedules', tenantId], enabled: !!tenantId,
    queryFn: () => api<Schedule[]>(`/api/schedules?tenantId=${tenantId}`), refetchInterval: 15000,
  })
  const run = useMutation({ mutationFn: (id: string) => api<Job>(`/api/schedules/${id}/run`, { method: 'POST' }), onSuccess: (j) => nav(`/jobs/${j.id}`) })
  const del = useMutation({ mutationFn: (id: string) => api(`/api/schedules/${id}`, { method: 'DELETE' }), onSuccess: () => qc.invalidateQueries({ queryKey: ['schedules'] }) })
  const toggle = useMutation({
    mutationFn: (s: Schedule) => api(`/api/schedules/${s.id}`, { method: 'PUT', body: { ...s, enabled: !s.enabled } }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['schedules'] }),
  })

  if (!tenantId) return <NeedTenant />
  return (
    <>
      <PageHeader title="Schedules" subtitle={`Recurring jobs for ${tenant?.displayName}`}
        actions={canEdit && <Button appearance="primary" icon={<Add24Regular />} onClick={() => { setEditing(undefined); setOpen(true) }}>New schedule</Button>} />
      <ErrorBar error={q.error ?? run.error ?? del.error ?? toggle.error} />
      <Panel>
        {q.isLoading ? <Loading /> : (
          <Table size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Job</TableHeaderCell><TableHeaderCell>When</TableHeaderCell>
                <TableHeaderCell>Next run</TableHeaderCell><TableHeaderCell>Last run</TableHeaderCell><TableHeaderCell>Enabled</TableHeaderCell>
                <TableHeaderCell />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(q.data ?? []).map((s) => (
                <TableRow key={s.id}>
                  <TableCell>{s.name}</TableCell>
                  <TableCell>{s.jobType}</TableCell>
                  <TableCell><Text font="monospace" size={200}>{s.cron}</Text> <Text size={200}>({s.timeZone})</Text></TableCell>
                  <TableCell>{s.enabled ? fmtDate(s.nextRunAt) : '—'}</TableCell>
                  <TableCell>{s.lastJobId ? <RouterLink to={`/jobs/${s.lastJobId}`}>{fmtDate(s.lastRunAt)}</RouterLink> : '—'}</TableCell>
                  <TableCell><Switch checked={s.enabled} disabled={!canEdit} onChange={() => toggle.mutate(s)} /></TableCell>
                  <TableCell>
                    {canEdit && (
                      <Menu>
                        <MenuTrigger disableButtonEnhancement><Button appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label="Actions" /></MenuTrigger>
                        <MenuPopover>
                          <MenuList>
                            <MenuItem onClick={() => run.mutate(s.id)}>Run now</MenuItem>
                            <MenuItem onClick={() => { setEditing(s); setOpen(true) }}>Edit</MenuItem>
                            <MenuItem onClick={() => confirm(`Delete schedule "${s.name}"?`) && del.mutate(s.id)}>Delete</MenuItem>
                          </MenuList>
                        </MenuPopover>
                      </Menu>
                    )}
                  </TableCell>
                </TableRow>
              ))}
              {q.data?.length === 0 && <TableRow><TableCell colSpan={7}>No schedules yet.</TableCell></TableRow>}
            </TableBody>
          </Table>
        )}
      </Panel>
      <ScheduleDialog open={open} onClose={() => setOpen(false)} tenantId={tenantId} existing={editing} />
    </>
  )
}
