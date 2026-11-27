import { useEffect, useMemo, useState } from 'react'
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent,
  DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody, Radio, RadioGroup, Select, Subtitle2, Switch, Tab, TabList, Table,
  TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Tooltip, tokens,
} from '@fluentui/react-components'
import {
  ArrowDownload20Regular, ArrowUndo24Regular, CalendarLtr20Regular, CloudArrowUp24Regular, Document20Regular, Folder20Filled, History20Regular,
  Mail20Regular, Person20Regular, Search20Regular, SearchInfo24Regular,
} from '@fluentui/react-icons'
import { api, download, fmtBytes, fmtDate, type Job } from '../api'
import { ErrorBar, Loading, NeedTenant, PageHeader, Panel, Stat, useHasRole, useTenant, useTenants } from '../common'
import { DomainMapField } from '../DomainMapField'
import { parseDomainMap } from '../domainMap'

export type DataKind = 'OneDrive' | 'SharePointSite' | 'Mailbox'
export interface DataSource {
  id: string; tenantId: string; kind: DataKind; externalId: string; displayName: string; address?: string; enabled: boolean; missing: boolean
  discoveredAt: string; lastBackupAt?: string; lastStatus?: string; lastError?: string; itemCount: number; totalBytes: number
}
export interface ItemVersion {
  id: number; sourceId: string; containerId: string; itemId: string; parentItemId?: string; name: string; path: string; isFolder: boolean
  size: number; contentType?: string; modifiedAt?: string; modifiedBy?: string; capturedAt: string; supersededAt?: string; deletedAt?: string
}
interface SourceDetail {
  source: DataSource; protectedSince?: string
  containers: { id: string; name: string; kind: string; webUrl?: string; lastSyncAt?: string; missing: boolean; pendingRetries: boolean; resuming: boolean }[]
}

export const kindLabels: Record<DataKind, string> = { OneDrive: 'OneDrive', SharePointSite: 'SharePoint', Mailbox: 'Mailboxes' }
/** Data kinds shown as tabs; later phases add to this list. */
export const dataKinds: DataKind[] = ['OneDrive', 'SharePointSite', 'Mailbox']

function StatusBadge({ s }: { s: DataSource }) {
  if (s.missing) return <Badge appearance="tint" color="subtle">Gone from tenant</Badge>
  if (!s.lastStatus) return <Badge appearance="tint" color="informative">Not backed up yet</Badge>
  const color = s.lastStatus === 'Succeeded' ? 'success' : s.lastStatus === 'Partial' ? 'warning' : 'danger'
  return <Tooltip content={s.lastError ?? s.lastStatus} relationship="description"><Badge appearance="tint" color={color}>{s.lastStatus}</Badge></Tooltip>
}

/** Starts a job and returns its ID (the Jobs page shows progress). */
function useStartJob(tenantId: string | null) {
  const nav = useNavigate()
  return useMutation({
    mutationFn: (v: { type: string; parameters?: Record<string, unknown> }) =>
      api<Job>('/api/jobs', { method: 'POST', body: { type: v.type, tenantId, parameters: v.parameters ?? {} } }),
    onSuccess: j => nav(`/jobs/${j.id}`),
  })
}

// ------------------------------------------------------------------ sources

export function DataSourcesPage() {
  const { tenantId } = useTenant()
  const qc = useQueryClient()
  const nav = useNavigate()
  const [kind, setKind] = useState<DataKind>(dataKinds[0])
  const [filter, setFilter] = useState('')
  const [search, setSearch] = useState('')
  const canBackup = useHasRole(tenantId, 'BackupOperator')
  const sources = useQuery({
    queryKey: ['dataSources', tenantId], enabled: !!tenantId, refetchInterval: 15_000,
    queryFn: () => api<DataSource[]>(`/api/tenants/${tenantId}/data/sources`),
  })
  const toggle = useMutation({
    mutationFn: (s: DataSource) => api(`/api/data/sources/${s.id}`, { method: 'PATCH', body: { enabled: !s.enabled } }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['dataSources', tenantId] }),
  })
  const start = useStartJob(tenantId)
  const rows = useMemo(() => (sources.data ?? []).filter(s => s.kind === kind &&
    (!filter || s.displayName.toLowerCase().includes(filter.toLowerCase()) || s.address?.toLowerCase().includes(filter.toLowerCase()))), [sources.data, kind, filter])

  if (!tenantId) return <NeedTenant />
  const all = sources.data ?? []
  const protectedCount = all.filter(s => s.enabled && !s.missing).length
  return (
    <>
      <PageHeader
        title="Files & data"
        subtitle="OneDrive, SharePoint and Exchange mailboxes, backed up incrementally with every version kept for the retention period."
        actions={canBackup && <>
          <Button icon={<SearchInfo24Regular />} disabled={start.isPending} onClick={() => start.mutate({ type: 'data.discover' })}>Discover sources</Button>
          <Button appearance="primary" icon={<CloudArrowUp24Regular />} disabled={start.isPending}
            onClick={() => start.mutate({ type: 'backup.data', parameters: { kinds: [kind] } })}>Back up all {kindLabels[kind]}</Button>
        </>}
      />
      {start.error && <ErrorBar error={start.error} />}
      <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
        <Stat label="Protected sources" value={protectedCount} hint={`${all.length} discovered`} />
        <Stat label="Items" value={all.reduce((n, s) => n + s.itemCount, 0).toLocaleString()} />
        <Stat label="Protected data" value={fmtBytes(all.reduce((n, s) => n + s.totalBytes, 0))} />
        <Stat label="Needing attention" value={all.filter(s => s.enabled && (s.lastStatus === 'Failed' || s.lastStatus === 'Partial')).length} />
      </div>

      <Panel>
        <Field label="Find a file or message by name (all sources, including deleted items)">
          <Input contentBefore={<Search20Regular />} value={search} onChange={(_, d) => setSearch(d.value)} placeholder="e.g. budget.xlsx" />
        </Field>
        {search.length >= 2 && <SearchResults tenantId={tenantId} q={search} />}
      </Panel>

      <Panel>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
          <TabList selectedValue={kind} onTabSelect={(_, d) => setKind(d.value as DataKind)}>
            {dataKinds.map(k => <Tab key={k} value={k}>{kindLabels[k]} ({all.filter(s => s.kind === k).length})</Tab>)}
          </TabList>
          <Input contentBefore={<Search20Regular />} placeholder="Filter" value={filter} onChange={(_, d) => setFilter(d.value)} />
        </div>
        {sources.isLoading ? <Loading /> : sources.error ? <ErrorBar error={sources.error} /> : !rows.length ? (
          <Text>No {kindLabels[kind]} sources yet. {canBackup ? 'Run discovery to find them.' : ''}</Text>
        ) : (
          <Table size="small" style={{ marginTop: 8 }}>
            <TableHeader><TableRow>
              <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Protected</TableHeaderCell><TableHeaderCell>Last backup</TableHeaderCell>
              <TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>Items</TableHeaderCell><TableHeaderCell>Size</TableHeaderCell>
            </TableRow></TableHeader>
            <TableBody>
              {rows.map(s => (
                <TableRow key={s.id} style={{ cursor: 'pointer' }} onClick={() => nav(`/data/sources/${s.id}`)}>
                  <TableCell>
                    <div>{s.displayName}</div>
                    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{s.address}</Text>
                  </TableCell>
                  <TableCell onClick={e => e.stopPropagation()}>
                    <Switch checked={s.enabled} disabled={!canBackup || toggle.isPending} onChange={() => toggle.mutate(s)} />
                  </TableCell>
                  <TableCell>{fmtDate(s.lastBackupAt)}</TableCell>
                  <TableCell><StatusBadge s={s} /></TableCell>
                  <TableCell>{s.itemCount.toLocaleString()}</TableCell>
                  <TableCell>{fmtBytes(s.totalBytes)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </>
  )
}

function SearchResults({ tenantId, q }: { tenantId: string; q: string }) {
  const [debounced, setDebounced] = useState(q)
  useEffect(() => { const t = setTimeout(() => setDebounced(q), 300); return () => clearTimeout(t) }, [q])
  const r = useQuery({
    queryKey: ['dataSearch', tenantId, debounced], enabled: debounced.length >= 2,
    queryFn: () => api<{ item: ItemVersion; sourceName?: string }[]>(`/api/tenants/${tenantId}/data/search?q=${encodeURIComponent(debounced)}`),
  })
  if (r.isLoading) return <Loading />
  if (r.error) return <ErrorBar error={r.error} />
  if (!r.data?.length) return <Text>No matches.</Text>
  return (
    <Table size="extra-small" style={{ marginTop: 8 }}>
      <TableHeader><TableRow><TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Where</TableHeaderCell><TableHeaderCell>Modified</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
      <TableBody>
        {r.data.map(({ item, sourceName }) => (
          <TableRow key={item.id}>
            <TableCell>{item.isFolder ? <Folder20Filled /> : <Document20Regular />} {item.name} {item.deletedAt && <Badge size="small" color="danger" appearance="tint">deleted</Badge>}</TableCell>
            <TableCell><RouterLink to={`/data/sources/${item.sourceId}?container=${item.containerId}&parent=${item.parentItemId ?? ''}${item.deletedAt ? '&deleted=1' : ''}`}>{sourceName}{item.path}</RouterLink></TableCell>
            <TableCell>{fmtDate(item.modifiedAt)}</TableCell>
            <TableCell />
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

// ------------------------------------------------------------------ browser

export function DataBrowserPage() {
  const { id } = useParams()
  const [params, setParams] = useSearchParams()
  const detail = useQuery({ queryKey: ['dataSource', id], queryFn: () => api<SourceDetail>(`/api/data/sources/${id}`) })
  const order = (k: string) => ['drive', 'mail', 'calendar', 'contacts'].indexOf(k)
  const sortedContainers = [...(detail.data?.containers ?? [])].sort((a, b) => order(a.kind) - order(b.kind) || a.name.localeCompare(b.name))
  const containerId = params.get('container') ?? sortedContainers.find(c => !c.missing)?.id ?? sortedContainers[0]?.id
  const parent = params.get('parent') || undefined
  const at = params.get('at') || undefined
  const showDeleted = params.get('deleted') === '1'
  const [selected, setSelected] = useState<ItemVersion | null>(null)
  const [checked, setChecked] = useState<Map<string, ItemVersion>>(new Map())
  const [restoring, setRestoring] = useState(false)
  const canBackup = useHasRole(detail.data?.source.tenantId, 'BackupOperator')
  const canDownload = useHasRole(detail.data?.source.tenantId, 'RestoreOperator')
  const start = useStartJob(detail.data?.source.tenantId ?? null)

  const set = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k) }
    setParams(next)
    setSelected(null)
    if ('container' in changes || 'at' in changes) setChecked(new Map())
  }
  const listing = useQuery({
    queryKey: ['dataChildren', containerId, parent, at, showDeleted], enabled: !!containerId,
    queryFn: () => api<{ items: ItemVersion[]; breadcrumb: { itemId: string; name: string; deletedAt?: string }[] }>(
      `/api/data/containers/${containerId}/children?deleted=${showDeleted && !at}${parent ? `&parent=${encodeURIComponent(parent)}` : ''}${at ? `&at=${encodeURIComponent(new Date(at).toISOString())}` : ''}`),
  })

  if (detail.isLoading) return <Loading />
  if (detail.error) return <ErrorBar error={detail.error} />
  const { source, containers, protectedSince } = detail.data!
  const container = containers.find(c => c.id === containerId)
  const toggle = (v: ItemVersion, on: boolean) => {
    const next = new Map(checked)
    if (on) next.set(v.itemId, v); else next.delete(v.itemId)
    setChecked(next)
  }
  const icon = (v: ItemVersion) => v.isFolder ? <Folder20Filled style={{ color: tokens.colorPaletteMarigoldForeground1, verticalAlign: 'middle' }} />
    : container?.kind === 'mail' ? <Mail20Regular style={{ verticalAlign: 'middle' }} />
    : container?.kind === 'calendar' ? <CalendarLtr20Regular style={{ verticalAlign: 'middle' }} />
    : container?.kind === 'contacts' ? <Person20Regular style={{ verticalAlign: 'middle' }} />
    : <Document20Regular style={{ verticalAlign: 'middle' }} />
  return (
    <>
      <PageHeader
        title={source.displayName}
        subtitle={<>{kindLabels[source.kind]} · {source.address} · protected since {fmtDate(protectedSince)} · <StatusBadge s={source} /></>}
        actions={<>
          {canDownload && (
            <Button icon={<ArrowUndo24Regular />} disabled={!checked.size} onClick={() => setRestoring(true)}>
              Restore {checked.size ? `${checked.size} selected` : '…'}
            </Button>
          )}
          {canBackup && (
            <Button appearance="primary" icon={<CloudArrowUp24Regular />} disabled={start.isPending}
              onClick={() => start.mutate({ type: 'backup.data', parameters: { sourceIds: [source.id] } })}>Back up now</Button>
          )}
        </>}
      />
      {start.error && <ErrorBar error={start.error} />}
      <Panel>
        <div style={{ display: 'flex', gap: 16, alignItems: 'flex-end', flexWrap: 'wrap' }}>
          {containers.length > 1 && (
            <TabList selectedValue={containerId} onTabSelect={(_, d) => set({ container: d.value as string, parent: undefined })}>
              {sortedContainers.map(c => <Tab key={c.id} value={c.id}>{c.name}{c.missing ? ' (deleted)' : ''}</Tab>)}
            </TabList>
          )}
          <Field label="As of" hint="Pick a time to see (and restore) the content as it was then.">
            <Input type="datetime-local" value={at ?? ''} onChange={(_, d) => set({ at: d.value || undefined })} />
          </Field>
          {at && <Button onClick={() => set({ at: undefined })}>Back to latest</Button>}
          <Switch label="Show deleted items" disabled={!!at} checked={showDeleted} onChange={(_, d) => set({ deleted: d.checked ? '1' : undefined })} />
        </div>
      </Panel>
      <div style={{ display: 'grid', gridTemplateColumns: selected ? 'minmax(0, 3fr) minmax(0, 2fr)' : '1fr', gap: 16 }}>
        <Panel>
          <Breadcrumb size="small">
            <BreadcrumbItem><BreadcrumbButton onClick={() => set({ parent: undefined })}>{containers.find(c => c.id === containerId)?.name ?? 'Root'}</BreadcrumbButton></BreadcrumbItem>
            {listing.data?.breadcrumb.map(b => (
              <span key={b.itemId} style={{ display: 'contents' }}>
                <BreadcrumbDivider />
                <BreadcrumbItem><BreadcrumbButton onClick={() => set({ parent: b.itemId })}>{b.name}</BreadcrumbButton></BreadcrumbItem>
              </span>
            ))}
          </Breadcrumb>
          {listing.isLoading ? <Loading /> : listing.error ? <ErrorBar error={listing.error} /> : !listing.data?.items.length ? <Text>This folder is empty{at ? ' at that time' : ''}.</Text> : (
            <Table size="small" style={{ marginTop: 8 }}>
              <TableHeader><TableRow>
                {canDownload && <TableHeaderCell style={{ width: 40 }} />}
                <TableHeaderCell>{container?.kind === 'mail' ? 'Subject' : 'Name'}</TableHeaderCell>
                <TableHeaderCell>{container?.kind === 'mail' ? 'Received' : container?.kind === 'calendar' ? 'Starts' : 'Modified'}</TableHeaderCell>
                <TableHeaderCell>{container?.kind === 'mail' ? 'From' : 'By'}</TableHeaderCell><TableHeaderCell>Size</TableHeaderCell>
              </TableRow></TableHeader>
              <TableBody>
                {listing.data.items.map(v => (
                  <TableRow key={v.id} style={{ cursor: 'pointer', opacity: v.deletedAt ? 0.7 : 1 }} appearance={selected?.id === v.id ? 'brand' : 'none'}
                    onClick={() => (v.isFolder ? set({ parent: v.itemId }) : setSelected(v))}>
                    {canDownload && (
                      <TableCell onClick={e => e.stopPropagation()}>
                        <Checkbox checked={checked.has(v.itemId)} onChange={(_, d) => toggle(v, !!d.checked)} />
                      </TableCell>
                    )}
                    <TableCell>
                      {icon(v)}{' '}
                      {v.name}{v.deletedAt && <Badge size="small" color="danger" appearance="tint" style={{ marginLeft: 6 }}>deleted {fmtDate(v.deletedAt)}</Badge>}
                    </TableCell>
                    <TableCell>{fmtDate(v.modifiedAt)}</TableCell>
                    <TableCell><Text size={200}>{v.modifiedBy}</Text></TableCell>
                    <TableCell>{v.isFolder ? '' : fmtBytes(v.size)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Panel>
        {selected && <VersionsPanel item={selected} canDownload={canDownload} />}
      </div>
      {restoring && container && (
        <RestoreDialog source={source} containerId={container.id} containerKind={container.kind} items={[...checked.values()]} at={at}
          onClose={() => setRestoring(false)} />
      )}
    </>
  )
}

function RestoreDialog({ source, containerId, containerKind, items, at, onClose }: {
  source: DataSource; containerId: string; containerKind: string; items: ItemVersion[]; at?: string; onClose: () => void
}) {
  const nav = useNavigate()
  const mailbox = source.kind === 'Mailbox'
  const tenants = (useTenants().data ?? []).filter(t => t.status === 'Active' || t.id === source.tenantId)
  const [targetTenant, setTargetTenant] = useState(source.tenantId)
  const crossTenant = targetTenant !== source.tenantId
  const targets = useQuery({
    queryKey: ['dataSources', targetTenant],
    queryFn: () => api<DataSource[]>(`/api/tenants/${targetTenant}/data/sources`),
  })
  const compatible = (targets.data ?? []).filter(t => !t.missing && (t.kind === 'Mailbox') === mailbox)
  // In another tenant, '' means "the same user or site there", matched by the server.
  const [target, setTarget] = useState(source.id)
  const [location, setLocation] = useState<'original' | 'folder'>('original')
  const [folderName, setFolderName] = useState(`Restored ${new Date().toISOString().slice(0, 10)}`)
  const [conflict, setConflict] = useState<'rename' | 'replace' | 'skip'>(containerKind === 'drive' ? 'rename' : 'skip')
  const [domains, setDomains] = useState('')
  const domainMap = parseDomainMap(domains)
  const other = !crossTenant && target !== source.id
  const pickTenant = (id: string) => { setTargetTenant(id); setTarget(id === source.tenantId ? source.id : '') }
  const restore = useMutation({
    mutationFn: () => api<{ jobId: string }>(`/api/tenants/${source.tenantId}/data/restore`, {
      method: 'POST',
      body: {
        containerId, itemIds: items.map(i => i.itemId), at: at ? new Date(at).toISOString() : undefined,
        targetTenantId: crossTenant ? targetTenant : undefined, domainMap: crossTenant ? domainMap.map : undefined,
        targetSourceId: (crossTenant ? target : other ? target : '') || undefined,
        location: other ? 'folder' : location, folderName, conflict,
      },
    }),
    onSuccess: r => nav(`/jobs/${r.jobId}`),
  })
  const deleted = items.filter(i => i.deletedAt).length
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Restore {items.length} item{items.length === 1 ? '' : 's'}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>
              {items.slice(0, 5).map(i => i.name).join(', ')}{items.length > 5 ? ` and ${items.length - 5} more` : ''}
              {items.some(i => i.isFolder) ? ' (folders with everything in them)' : ''},
              {at ? ` as they were at ${fmtDate(new Date(at).toISOString())}` : deleted ? ' as they were just before they were deleted' : ' from the latest backup'}.
            </Text>
            {tenants.length > 1 && (
              <Field label="Tenant" hint={crossTenant ? 'Into another tenant, e.g. a replacement for a lost one. Its users and sites must have been discovered there.' : undefined}>
                <Select value={targetTenant} onChange={(_, d) => pickTenant(d.value)}>
                  {tenants.map(t => <option key={t.id} value={t.id}>{t.displayName}{t.id === source.tenantId ? ' (original)' : ''}</option>)}
                </Select>
              </Field>
            )}
            <Field label="Restore to">
              <Select value={target} onChange={(_, d) => setTarget(d.value)}>
                {crossTenant && <option value="">The same {source.kind === 'SharePointSite' ? 'site (by name)' : 'user (by address)'} in that tenant</option>}
                {compatible.map(t => <option key={t.id} value={t.id}>{t.displayName}{t.id === source.id ? ' (original)' : ''} · {kindLabels[t.kind]}</option>)}
              </Select>
            </Field>
            {crossTenant && source.kind !== 'SharePointSite' && (
              <DomainMapField value={domains} onChange={setDomains}
                sourceDomain={tenants.find(t => t.id === source.tenantId)?.defaultDomain} targetDomain={tenants.find(t => t.id === targetTenant)?.defaultDomain} />
            )}
            {!other && (
              <RadioGroup value={location} onChange={(_, d) => setLocation(d.value as 'original' | 'folder')}>
                <Radio value="original" label="Original location (missing folders are recreated)" />
                <Radio value="folder" label="A new folder, keeping the folder structure" />
              </RadioGroup>
            )}
            {(other || location === 'folder') && (
              <Field label={containerKind === 'calendar' ? 'Calendar name' : containerKind === 'contacts' ? 'Contact folder name' : 'Folder name'}>
                <Input value={folderName} onChange={(_, d) => setFolderName(d.value)} />
              </Field>
            )}
            {containerKind !== 'calendar' && containerKind !== 'contacts' && (
              <Field label={containerKind === 'mail' ? 'If the message is already there' : 'If a file with the same name exists'}>
                <RadioGroup value={conflict} onChange={(_, d) => setConflict(d.value as typeof conflict)}>
                  {containerKind === 'drive' && <Radio value="rename" label="Keep both (restored copy gets a new name)" />}
                  <Radio value="skip" label="Skip it" />
                  <Radio value="replace" label="Replace it" />
                  {containerKind === 'mail' && <Radio value="rename" label="Restore anyway (may create duplicates)" />}
                </RadioGroup>
              </Field>
            )}
            {containerKind === 'calendar' && (
              <MessageBar intent="info"><MessageBarBody>Events are restored without attendees so nobody receives invitations; the attendee list is added to the event description.</MessageBarBody></MessageBar>
            )}
            {restore.error && <ErrorBar error={restore.error} />}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={restore.isPending || !!domainMap.error || ((other || location === 'folder') && !folderName.trim())} onClick={() => restore.mutate()}>
              {restore.isPending ? 'Starting…' : 'Restore'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function VersionsPanel({ item, canDownload }: { item: ItemVersion; canDownload: boolean }) {
  const [busy, setBusy] = useState<number | null>(null)
  const [error, setError] = useState<unknown>(null)
  const history = useQuery({
    queryKey: ['dataHistory', item.containerId, item.itemId],
    queryFn: () => api<ItemVersion[]>(`/api/data/containers/${item.containerId}/items/${encodeURIComponent(item.itemId)}/history`),
  })
  return (
    <Panel>
      <Subtitle2>{item.name}</Subtitle2>
      <div><Text size={200}>{item.path}</Text></div>
      {error ? <ErrorBar error={error} /> : null}
      <Text weight="semibold" style={{ display: 'block', marginTop: 12 }}><History20Regular style={{ verticalAlign: 'middle' }} /> Versions</Text>
      {history.isLoading ? <Loading /> : (
        <Table size="extra-small">
          <TableHeader><TableRow><TableHeaderCell>Backed up</TableHeaderCell><TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Size</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
          <TableBody>
            {history.data?.map(v => (
              <TableRow key={v.id}>
                <TableCell>{fmtDate(v.capturedAt)}{v.deletedAt && <Badge size="small" color="danger" appearance="tint" style={{ marginLeft: 4 }}>then deleted</Badge>}</TableCell>
                <TableCell><Text size={200}>{v.name}</Text></TableCell>
                <TableCell>{fmtBytes(v.size)}</TableCell>
                <TableCell>
                  {canDownload && (
                    <Button size="small" icon={<ArrowDownload20Regular />} disabled={busy === v.id} onClick={async () => {
                      setBusy(v.id); setError(null)
                      try { await download(`/api/data/versions/${v.id}/content`, v.name) } catch (e) { setError(e) } finally { setBusy(null) }
                    }}>Download</Button>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {!canDownload && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>Downloading backed-up content needs the Restore operator role.</Text>}
    </Panel>
  )
}

export function DataProtectionWidget({ tenantId }: { tenantId: string }) {
  const q = useQuery({ queryKey: ['dataSources', tenantId], queryFn: () => api<DataSource[]>(`/api/tenants/${tenantId}/data/sources`) })
  if (!q.data?.length) return null
  const enabled = q.data.filter(s => s.enabled && !s.missing)
  const failing = enabled.filter(s => s.lastStatus === 'Failed' || s.lastStatus === 'Partial')
  const last = enabled.map(s => s.lastBackupAt).filter(Boolean).sort().at(-1)
  return (
    <Panel>
      <Subtitle2>Files & data</Subtitle2>
      <div style={{ marginTop: 8 }}>
        <Text>{enabled.length} sources protected · {fmtBytes(enabled.reduce((n, s) => n + s.totalBytes, 0))} · last backup {fmtDate(last)}</Text>
      </div>
      {failing.length > 0 && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{failing.length} source(s) need attention</Text>}
      <div style={{ marginTop: 8 }}><RouterLink to="/data">Open</RouterLink></div>
    </Panel>
  )
}
