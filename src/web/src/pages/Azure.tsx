import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  Badge, Button, Checkbox, Input, MessageBar, MessageBarBody, Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Tooltip,
} from '@fluentui/react-components'
import { ArrowUndo24Regular, Search20Regular } from '@fluentui/react-icons'
import { api, fmtDate } from '../api'
import { ErrorBar, Loading, NeedTenant, PageHeader, Panel, useHasRole, useTenant } from '../common'

interface ResourceGroup {
  snapshotItemId: number; id: string; name: string; subscriptionId: string; subscriptionName?: string; location?: string
  resourceCount: number; resourceTypes: string[]; status: 'present' | 'deleted' | 'unknown'
}
interface Inventory { snapshotId?: string; snapshotAt?: string; resourceGroups: ResourceGroup[]; liveError?: string }

const statusBadge = {
  present: <Badge appearance="tint" color="success">Exists</Badge>,
  deleted: <Badge appearance="filled" color="danger">Deleted</Badge>,
  unknown: <Badge appearance="tint" color="informative">Unknown</Badge>,
}

/** Resource groups in the latest backup and whether they still exist; redeploying goes through a normal restore plan. */
export function AzurePage() {
  const { tenantId } = useTenant()
  const nav = useNavigate()
  const canRestore = useHasRole(tenantId, 'RestoreOperator')
  const [search, setSearch] = useState('')
  const [deletedOnly, setDeletedOnly] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const q = useQuery({
    queryKey: ['azureResourceGroups', tenantId], enabled: !!tenantId,
    queryFn: () => api<Inventory>(`/api/tenants/${tenantId}/azure/resourceGroups`),
  })
  const rows = useMemo(() => (q.data?.resourceGroups ?? []).filter(r => {
    const s = search.toLowerCase()
    return (!deletedOnly || r.status === 'deleted') &&
      (!s || r.name.toLowerCase().includes(s) || r.subscriptionName?.toLowerCase().includes(s) || r.resourceTypes.some(t => t.toLowerCase().includes(s)))
  }), [q.data, search, deletedOnly])

  if (!tenantId) return <NeedTenant />
  const data = q.data
  const redeploy = (ids: number[]) => nav(`/restore?snapshot=${data!.snapshotId}&${ids.map(i => `item=${i}`).join('&')}`)
  const toggle = (id: number, on: boolean) => {
    const next = new Set(selected)
    if (on) next.add(id); else next.delete(id)
    setSelected(next)
  }
  const deletedCount = data?.resourceGroups.filter(r => r.status === 'deleted').length ?? 0

  return (
    <>
      <PageHeader
        title="Azure resource groups"
        subtitle="Every resource group is backed up with an ARM template of its resources, plus its role assignments and policy assignments."
        actions={canRestore && data?.snapshotId && (
          <Button appearance="primary" icon={<ArrowUndo24Regular />} disabled={!selected.size} onClick={() => redeploy([...selected])}>
            Redeploy {selected.size || ''} selected…
          </Button>
        )}
      />
      {q.error && <ErrorBar error={q.error} />}
      {data?.liveError && (
        <MessageBar intent="warning"><MessageBarBody>Couldn't check which groups still exist: {data.liveError}</MessageBarBody></MessageBar>
      )}
      <MessageBar intent="info">
        <MessageBarBody>
          Redeploying creates a restore plan you can review first: the group is recreated, its template is deployed in incremental mode (nothing
          else is touched), and its role and policy assignments are added back. Secrets and data inside resources (blobs, databases, disks) aren't in
          templates.
        </MessageBarBody>
      </MessageBar>
      <Panel>
        {q.isLoading ? <Loading /> : !data?.snapshotId ? (
          <Text>No backup includes Azure yet. Run a config backup with the Azure workload (the backup app needs Reader on the subscriptions).</Text>
        ) : (
          <>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
              <Text size={200}>
                From the backup of {fmtDate(data.snapshotAt)} · {data.resourceGroups.length} group(s){deletedCount ? `, ${deletedCount} deleted since` : ''}
              </Text>
              <div style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
                <Switch label="Deleted only" checked={deletedOnly} onChange={(_, d) => setDeletedOnly(d.checked)} />
                <Input contentBefore={<Search20Regular />} placeholder="Search" value={search} onChange={(_, d) => setSearch(d.value)} />
              </div>
            </div>
            <Table size="small" style={{ marginTop: 8 }}>
              <TableHeader><TableRow>
                {canRestore && <TableHeaderCell style={{ width: 40 }} />}
                <TableHeaderCell>Resource group</TableHeaderCell><TableHeaderCell>Subscription</TableHeaderCell><TableHeaderCell>Location</TableHeaderCell>
                <TableHeaderCell>Resources</TableHeaderCell><TableHeaderCell>Now</TableHeaderCell><TableHeaderCell />
              </TableRow></TableHeader>
              <TableBody>
                {rows.map(r => (
                  <TableRow key={r.id}>
                    {canRestore && <TableCell><Checkbox checked={selected.has(r.snapshotItemId)} onChange={(_, d) => toggle(r.snapshotItemId, !!d.checked)} /></TableCell>}
                    <TableCell>
                      <Tooltip relationship="description" content={r.id}><span>{r.name}</span></Tooltip>
                    </TableCell>
                    <TableCell>{r.subscriptionName ?? r.subscriptionId}</TableCell>
                    <TableCell>{r.location}</TableCell>
                    <TableCell>
                      <Tooltip relationship="description" content={r.resourceTypes.join(', ') || 'Empty'}>
                        <span>{r.resourceCount}</span>
                      </Tooltip>
                    </TableCell>
                    <TableCell>{statusBadge[r.status]}</TableCell>
                    <TableCell>
                      {canRestore && (
                        <Button size="small" style={{ whiteSpace: 'nowrap' }} appearance={r.status === 'deleted' ? 'primary' : 'subtle'} onClick={() => redeploy([r.snapshotItemId])}>
                          {r.status === 'deleted' ? 'Redeploy…' : 'Reset to backup…'}
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
                {rows.length === 0 && <TableRow><TableCell colSpan={7}>Nothing matches.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </>
        )}
      </Panel>
    </>
  )
}
