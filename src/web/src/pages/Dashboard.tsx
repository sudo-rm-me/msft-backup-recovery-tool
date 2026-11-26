import { useNavigate } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Button, Subtitle2, Text } from '@fluentui/react-components'
import { api, fmtDate, type Job } from '../api'
import { ErrorBar, NeedTenant, PageHeader, Panel, Stat, useTenant } from '../common'
import { JobsTable } from './Jobs'
import { TenantStatusBadge } from './Tenants'
import { dashboardWidgets } from '../features'

export function Dashboard() {
  const { tenantId, tenant } = useTenant()
  const nav = useNavigate()
  const jobs = useQuery({
    queryKey: ['jobs', tenantId, 'dash'], enabled: !!tenantId,
    queryFn: () => api<Job[]>(`/api/jobs?take=10&tenantId=${tenantId}`), refetchInterval: 5000,
  })
  const diag = useMutation({
    mutationFn: () => api<Job>('/api/jobs', { body: { type: 'system.diagnostics', tenantId, parameters: { steps: 5 } } }),
    onSuccess: (j) => nav(`/jobs/${j.id}`),
  })
  if (!tenantId || !tenant) return <NeedTenant />
  const pc = tenant.lastPermissionCheck
  const running = jobs.data?.filter((j) => j.status === 'Running' || j.status === 'Queued').length ?? 0
  const failed = jobs.data?.filter((j) => j.status === 'Failed').length ?? 0

  return (
    <>
      <PageHeader title={tenant.displayName} subtitle={tenant.defaultDomain} actions={<TenantStatusBadge status={tenant.status} />} />
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 16, marginBottom: 16 }}>
        <Stat label="Backup app" value={pc ? (pc.backup.tokenAcquired ? 'Connected' : 'Not connected') : 'Not checked'}
          hint={pc ? `${pc.backup.missing.length} permission(s) missing` : 'Run a permission check'} />
        <Stat label="Restore app" value={pc ? (pc.restore.tokenAcquired ? 'Connected' : 'Not connected') : 'Not checked'} />
        <Stat label="Active jobs" value={running} hint={failed ? `${failed} recent failure(s)` : undefined} />
        <Stat label="Last permission check" value={<Text size={400}>{fmtDate(tenant.lastPermissionCheckAt)}</Text>} />
      </div>
      {dashboardWidgets.map((W, i) => <W key={i} tenantId={tenantId} />)}
      <Panel>
        <div style={{ display: 'flex', alignItems: 'center', marginBottom: 8 }}>
          <Subtitle2 style={{ flexGrow: 1 }}>Recent jobs</Subtitle2>
          <Button size="small" onClick={() => diag.mutate()} disabled={diag.isPending}>Run diagnostics job</Button>
        </div>
        <ErrorBar error={jobs.error ?? diag.error} />
        <JobsTable jobs={jobs.data ?? []} />
      </Panel>
    </>
  )
}
