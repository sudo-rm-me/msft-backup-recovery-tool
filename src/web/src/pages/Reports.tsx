import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  Badge, Button, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Tooltip, tokens,
} from '@fluentui/react-components'
import { ArrowDownload24Regular } from '@fluentui/react-icons'
import { api, download, fmtDate } from '../api'
import { ErrorBar, Loading, PageHeader, Panel, Stat, useTenant } from '../common'
import { workloadNames } from './Backups'

interface TenantProtection {
  tenantId: string; tenant: string; status: string; health: 'Healthy' | 'AtRisk' | 'Unprotected'
  lastConfigBackupAt?: string; lastConfigBackupStatus?: string; lastConfigItems: number; lastConfigWarnings: number; lastConfigErrors: number
  workloads: { workload: string; items: number; lastCapturedAt?: string }[]; workloadsNeverBackedUp: string[]
  dataSources: number; dataSourcesProtected: number; dataSourcesFailing: number; oldestDataBackupAt?: string; dataBytes: number
  failedJobs7Days: number; restores30Days: number; nextScheduledBackupAt?: string; problems: string[]
}
interface Report { generatedAt: string; summary: { tenants: number; healthy: number; atRisk: number; unprotected: number }; tenants: TenantProtection[] }
interface Workers {
  queued: number
  workers: { id: string; host: string; processId: number; lastSeenAt: string; maxConcurrency: number; jobs: { id: string; type: string }[] }[]
}

const healthBadge = {
  Healthy: <Badge appearance="tint" color="success">Healthy</Badge>,
  AtRisk: <Badge appearance="tint" color="warning">At risk</Badge>,
  Unprotected: <Badge appearance="filled" color="danger">Unprotected</Badge>,
}

const gb = (b: number) => (b / 1024 ** 3).toFixed(b > 10 * 1024 ** 3 ? 0 : 2)

/** Protection across all tenants: coverage, freshness and problems, with CSV export; plus the worker fleet. */
export function ReportsPage() {
  const nav = useNavigate()
  const { setTenantId } = useTenant()
  const [exporting, setExporting] = useState(false)
  const q = useQuery({ queryKey: ['protectionReport'], queryFn: () => api<Report>('/api/reports/protection'), refetchInterval: 60_000 })
  const workers = useQuery({ queryKey: ['workers'], queryFn: () => api<Workers>('/api/workers'), refetchInterval: 10_000 })
  const r = q.data

  const exportCsv = async () => {
    setExporting(true)
    try { await download('/api/reports/protection?format=csv', `protection-report-${new Date().toISOString().slice(0, 10)}.csv`) } finally { setExporting(false) }
  }

  return (
    <>
      <PageHeader
        title="Protection report"
        subtitle="Every tenant you can see: what is backed up, how recently, and what needs attention."
        actions={<Button icon={<ArrowDownload24Regular />} disabled={exporting || !r} onClick={exportCsv}>Export CSV</Button>}
      />
      <ErrorBar error={q.error} />
      {q.isLoading ? <Loading /> : r && (
        <>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 16 }}>
            <Panel><Stat label="Tenants" value={r.summary.tenants} /></Panel>
            <Panel><Stat label="Healthy" value={<span style={{ color: tokens.colorPaletteGreenForeground1 }}>{r.summary.healthy}</span>} /></Panel>
            <Panel><Stat label="At risk" value={<span style={{ color: tokens.colorPaletteMarigoldForeground1 }}>{r.summary.atRisk}</span>} /></Panel>
            <Panel><Stat label="Unprotected" value={<span style={{ color: tokens.colorPaletteRedForeground1 }}>{r.summary.unprotected}</span>} /></Panel>
          </div>
          <Panel>
            <Table size="small">
              <TableHeader><TableRow>
                <TableHeaderCell>Tenant</TableHeaderCell><TableHeaderCell>Health</TableHeaderCell><TableHeaderCell>Last config backup</TableHeaderCell>
                <TableHeaderCell>Workloads</TableHeaderCell><TableHeaderCell>Data sources</TableHeaderCell><TableHeaderCell>Next backup</TableHeaderCell>
                <TableHeaderCell>Needs attention</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {r.tenants.map(t => (
                  <TableRow key={t.tenantId}>
                    <TableCell>
                      <Button appearance="transparent" style={{ padding: 0, minWidth: 0 }} onClick={() => { setTenantId(t.tenantId); nav('/') }}>{t.tenant}</Button>
                    </TableCell>
                    <TableCell>{healthBadge[t.health]}</TableCell>
                    <TableCell>
                      <div>{fmtDate(t.lastConfigBackupAt) || 'Never'}</div>
                      {t.lastConfigBackupAt && (
                        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                          {t.lastConfigItems} items{t.lastConfigWarnings ? ` · ${t.lastConfigWarnings} warnings` : ''}{t.lastConfigErrors ? ` · ${t.lastConfigErrors} errors` : ''}
                        </Text>
                      )}
                    </TableCell>
                    <TableCell>
                      <Tooltip relationship="description" content={t.workloadsNeverBackedUp.length ? `Not backed up: ${t.workloadsNeverBackedUp.map(w => workloadNames[w] ?? w).join(', ')}` : 'All workloads covered'}>
                        <span>{t.workloads.length} of {t.workloads.length + t.workloadsNeverBackedUp.length}</span>
                      </Tooltip>
                    </TableCell>
                    <TableCell>
                      {t.dataSources === 0 ? <Text size={200}>None discovered</Text> : (
                        <>
                          <div>{t.dataSourcesProtected} of {t.dataSources} protected · {gb(t.dataBytes)} GB</div>
                          {t.dataSourcesFailing > 0 && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{t.dataSourcesFailing} failing</Text>}
                        </>
                      )}
                    </TableCell>
                    <TableCell>{fmtDate(t.nextScheduledBackupAt) || '—'}</TableCell>
                    <TableCell>
                      {t.problems.length === 0 ? <Text size={200}>—</Text> : (
                        <ul style={{ margin: 0, paddingLeft: 16, fontSize: 12 }}>{t.problems.map(p => <li key={p}>{p}</li>)}</ul>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>Generated {fmtDate(r.generatedAt)}</Text>
          </Panel>
        </>
      )}
      <Panel>
        <Subtitle2>Workers</Subtitle2>
        {workers.error ? <ErrorBar error={workers.error} /> : !workers.data ? <Loading /> : (
          <>
            <Text size={200} style={{ display: 'block', margin: '4px 0 8px' }}>
              {workers.data.workers.length} replica(s) online · {workers.data.queued} job(s) waiting. Raise max_replicas in the deployment to run
              more tenants in parallel; each tenant runs at most a few jobs at once so it isn't throttled.
            </Text>
            <Table size="small">
              <TableHeader><TableRow>
                <TableHeaderCell>Host</TableHeaderCell><TableHeaderCell>Running</TableHeaderCell><TableHeaderCell>Last seen</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {workers.data.workers.map(w => (
                  <TableRow key={w.id}>
                    <TableCell>{w.host} <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>pid {w.processId}</Text></TableCell>
                    <TableCell>{w.jobs.length} / {w.maxConcurrency}{w.jobs.length > 0 && <Text size={200}> ({[...new Set(w.jobs.map(j => j.type))].join(', ')})</Text>}</TableCell>
                    <TableCell>{fmtDate(w.lastSeenAt)}</TableCell>
                  </TableRow>
                ))}
                {workers.data.workers.length === 0 && <TableRow><TableCell colSpan={3}>No replica has reported in during the last 10 minutes.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </>
        )}
      </Panel>
    </>
  )
}
