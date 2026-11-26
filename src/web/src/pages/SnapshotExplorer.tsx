import { useMemo, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  Badge, Button, Input, Tab, TabList, Text, Subtitle2, tokens, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components'
import { api, fmtBytes, fmtDate } from '../api'
import { ErrorBar, Json, Loading, PageHeader, Panel, useHasRole } from '../common'
import { SnapshotStatusBadge, useCatalog, workloadNames, type Snapshot } from './Backups'

interface Detail {
  snapshot: Snapshot
  types: { workload: string; resourceType: string; count: number }[]
  issues: { resourceType: string; objectId?: string; severity: string; message: string }[]
}
interface ItemRow { id: number; resourceType: string; workload: string; objectId: string; displayName: string; hash: string; size: number }
interface ItemContent extends ItemRow { content: unknown }
interface Version { snapshotId: string; createdAt: string; itemId: number; hash: string; displayName: string }

const sev = (s: string) => ({ Error: 0, Warning: 1, Info: 2 })[s] ?? 3

export function SnapshotExplorerPage() {
  const { id } = useParams()
  const nav = useNavigate()
  const { data: catalog } = useCatalog()
  const detail = useQuery({ queryKey: ['snapshot', id], queryFn: () => api<Detail>(`/api/snapshots/${id}`) })
  const [type, setType] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<{ snapshotId: string; itemId: number } | null>(null)
  const [tab, setTab] = useState('items')
  const canRestore = useHasRole(detail.data?.snapshot.tenantId, 'RestoreOperator')

  const items = useQuery({
    queryKey: ['snapshotItems', id, type, search],
    queryFn: () => api<{ total: number; items: ItemRow[] }>(`/api/snapshots/${id}/items?take=500${type ? `&type=${type}` : ''}${search ? `&search=${encodeURIComponent(search)}` : ''}`),
    enabled: !!detail.data,
  })
  const item = useQuery({
    queryKey: ['snapshotItem', selected?.snapshotId, selected?.itemId], enabled: !!selected,
    queryFn: () => api<ItemContent>(`/api/snapshots/${selected!.snapshotId}/items/${selected!.itemId}`),
  })
  const tenantId = detail.data?.snapshot.tenantId
  const history = useQuery({
    queryKey: ['history', tenantId, item.data?.resourceType, item.data?.objectId], enabled: !!item.data && !!tenantId,
    queryFn: () => api<Version[]>(`/api/tenants/${tenantId}/objects/${item.data!.resourceType}/${encodeURIComponent(item.data!.objectId)}/history`),
  })

  const grouped = useMemo(() => {
    const g: Record<string, Detail['types']> = {}
    for (const t of detail.data?.types ?? []) (g[t.workload] ??= []).push(t)
    return g
  }, [detail.data])
  const typeName = (t: string) => catalog?.find((c) => c.type === t)?.displayName ?? t

  if (detail.isLoading) return <Loading />
  if (detail.error) return <ErrorBar error={detail.error} />
  const s = detail.data!.snapshot

  return (
    <>
      <PageHeader title="Snapshot explorer" subtitle={`${fmtDate(s.createdAt)}${s.label ? ' · ' + s.label : ''} · ${s.itemCount} items · ${fmtBytes(s.totalBytes)}`}
        actions={<>
          <SnapshotStatusBadge s={s} />
          <Button onClick={() => nav(`/diff?from=${s.id}&to=live`)}>Compare with live</Button>
        </>} />
      <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as string)} style={{ marginBottom: 12 }}>
        <Tab value="items">Items</Tab>
        <Tab value="issues">Issues ({detail.data!.issues.filter((i) => i.severity !== 'Info').length})</Tab>
      </TabList>
      {tab === 'issues' ? (
        <Panel>
          <Table size="small">
            <TableHeader><TableRow><TableHeaderCell>Severity</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell><TableHeaderCell>Message</TableHeaderCell></TableRow></TableHeader>
            <TableBody>
              {[...detail.data!.issues].sort((a, b) => sev(a.severity) - sev(b.severity)).map((i, k) => (
                <TableRow key={k}>
                  <TableCell><Badge color={i.severity === 'Error' ? 'danger' : i.severity === 'Warning' ? 'warning' : 'informative'} appearance="tint">{i.severity}</Badge></TableCell>
                  <TableCell>{typeName(i.resourceType)}</TableCell>
                  <TableCell><Text size={200}>{i.message}</Text></TableCell>
                </TableRow>
              ))}
              {detail.data!.issues.length === 0 && <TableRow><TableCell colSpan={3}>No issues. Everything was captured.</TableCell></TableRow>}
            </TableBody>
          </Table>
        </Panel>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: '260px minmax(0, 1fr) minmax(0, 1.3fr)', gap: 12, alignItems: 'start' }}>
          <Panel style={{ maxHeight: '75vh', overflow: 'auto', padding: 8 }}>
            <div onClick={() => setType(null)} style={{ padding: '4px 8px', cursor: 'pointer', fontWeight: type === null ? 600 : 400 }}>All types</div>
            {Object.entries(grouped).map(([w, types]) => (
              <div key={w} style={{ marginTop: 8 }}>
                <Text size={200} weight="semibold" style={{ color: tokens.colorNeutralForeground3, padding: '0 8px', textTransform: 'uppercase' }}>{workloadNames[w] ?? w}</Text>
                {types.map((t) => (
                  <div key={t.resourceType} onClick={() => setType(t.resourceType)}
                    style={{ padding: '4px 8px', cursor: 'pointer', borderRadius: 4, display: 'flex', background: type === t.resourceType ? tokens.colorNeutralBackground1Selected : undefined }}>
                    <span style={{ flexGrow: 1 }}>{typeName(t.resourceType)}</span><Text size={200}>{t.count}</Text>
                  </div>
                ))}
              </div>
            ))}
          </Panel>
          <Panel style={{ maxHeight: '75vh', overflow: 'auto' }}>
            <Input placeholder="Search by name or ID" value={search} onChange={(_, d) => setSearch(d.value)} style={{ width: '100%', marginBottom: 8 }} />
            {items.isLoading ? <Loading /> : (
              <>
                <Text size={200}>{items.data?.total} item(s)</Text>
                {items.data?.items.map((i) => (
                  <div key={i.id} onClick={() => setSelected({ snapshotId: s.id, itemId: i.id })}
                    style={{ padding: '6px 8px', cursor: 'pointer', borderRadius: 4, background: selected?.itemId === i.id ? tokens.colorNeutralBackground1Selected : undefined }}>
                    <div>{i.displayName}</div>
                    <Text size={100} style={{ color: tokens.colorNeutralForeground3 }}>{typeName(i.resourceType)} · {i.objectId}</Text>
                  </div>
                ))}
              </>
            )}
          </Panel>
          <Panel style={{ maxHeight: '75vh', overflow: 'auto' }}>
            {!selected ? <Text>Select an item to see its saved configuration.</Text> : item.isLoading ? <Loading /> : item.error ? <ErrorBar error={item.error} /> : (
              <>
                <Subtitle2>{item.data!.displayName}</Subtitle2>
                <Text size={200} style={{ display: 'block', marginBottom: 8 }}>{typeName(item.data!.resourceType)} · {item.data!.objectId}</Text>
                {canRestore && catalog?.find(c => c.type === item.data!.resourceType)?.restorable && (
                  <Button size="small" style={{ marginBottom: 8 }} onClick={() => nav(`/restore?snapshot=${selected.snapshotId}&item=${selected.itemId}`)}>Restore this item…</Button>
                )}
                {history.data && history.data.length > 1 && (
                  <div style={{ marginBottom: 8 }}>
                    <Text size={200}>Versions: </Text>
                    {history.data.map((v) => (
                      <Button key={v.itemId} size="small" appearance={v.itemId === selected.itemId ? 'primary' : 'subtle'}
                        onClick={() => setSelected({ snapshotId: v.snapshotId, itemId: v.itemId })}>{fmtDate(v.createdAt)}</Button>
                    ))}
                  </div>
                )}
                <Json value={item.data!.content} />
              </>
            )}
          </Panel>
        </div>
      )}
    </>
  )
}
