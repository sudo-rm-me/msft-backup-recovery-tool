import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Input, Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text } from '@fluentui/react-components'
import { api, fmtDate, type AuditEntry } from '../api'
import { ErrorBar, Loading, PageHeader, Panel, useTenant } from '../common'

export function AuditPage() {
  const { tenantId, tenants } = useTenant()
  const [onlyTenant, setOnlyTenant] = useState(false)
  const [action, setAction] = useState('')
  const q = useQuery({
    queryKey: ['audit', onlyTenant ? tenantId : null, action],
    queryFn: () => api<AuditEntry[]>(`/api/audit?take=500${onlyTenant && tenantId ? `&tenantId=${tenantId}` : ''}${action ? `&action=${encodeURIComponent(action)}` : ''}`),
  })
  return (
    <>
      <PageHeader title="Audit log" subtitle="Every operator action, including restores and setting changes"
        actions={<>
          <Input placeholder="Filter action (e.g. restore.)" value={action} onChange={(_, d) => setAction(d.value)} />
          <Switch label="Selected tenant only" checked={onlyTenant} onChange={(_, d) => setOnlyTenant(d.checked)} />
        </>} />
      <ErrorBar error={q.error} />
      <Panel>
        {q.isLoading ? <Loading /> : (
          <Table size="small">
            <TableHeader><TableRow>
              <TableHeaderCell>When</TableHeaderCell><TableHeaderCell>Who</TableHeaderCell><TableHeaderCell>Action</TableHeaderCell>
              <TableHeaderCell>Tenant</TableHeaderCell><TableHeaderCell>Target</TableHeaderCell><TableHeaderCell>Details</TableHeaderCell>
            </TableRow></TableHeader>
            <TableBody>
              {(q.data ?? []).map((a) => (
                <TableRow key={a.id}>
                  <TableCell>{fmtDate(a.at)}</TableCell>
                  <TableCell>{a.actor}</TableCell>
                  <TableCell><code>{a.action}</code></TableCell>
                  <TableCell>{tenants.find((t) => t.id === a.tenantId)?.displayName ?? ''}</TableCell>
                  <TableCell>{a.target}</TableCell>
                  <TableCell><Text size={200} font="monospace">{a.details ? JSON.stringify(a.details).slice(0, 160) : ''}</Text></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </>
  )
}
