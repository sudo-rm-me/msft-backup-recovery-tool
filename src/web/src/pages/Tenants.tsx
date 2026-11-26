import { useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, DialogTrigger, Field, Input,
  Link, MessageBar, MessageBarBody, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Select,
  Textarea, Subtitle2, tokens, Tooltip,
} from '@fluentui/react-components'
import { Add24Regular, ArrowClockwise24Regular, Checkmark16Filled, Dismiss16Filled, Copy16Regular } from '@fluentui/react-icons'
import { api, fmtDate, type PermissionCheck, type StorageTarget, type Tenant } from '../api'
import { ErrorBar, Loading, PageHeader, Panel, useHasRole, useMe, useTenant, useTenants } from '../common'
import { GdapImportDialog } from './GdapImport'

export function TenantStatusBadge({ status }: { status: Tenant['status'] }) {
  const color = status === 'Active' ? 'success' : status === 'PendingConsent' ? 'warning' : 'subtle'
  return <Badge appearance="tint" color={color}>{status === 'PendingConsent' ? 'Pending consent' : status}</Badge>
}

export function TenantsPage() {
  const { data, isLoading, error } = useTenants()
  const { data: me } = useMe()
  const nav = useNavigate()
  const { setTenantId } = useTenant()
  return (
    <>
      <PageHeader title="Tenants" subtitle="Customer tenants managed by this deployment" actions={me?.isGlobalAdmin && <><GdapImportDialog /><AddTenantDialog /></>} />
      <ErrorBar error={error} />
      {isLoading ? <Loading /> : (
        <Panel>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Name</TableHeaderCell>
                <TableHeaderCell>Domain</TableHeaderCell>
                <TableHeaderCell>Directory ID</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Last permission check</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(data ?? []).map((t) => (
                <TableRow key={t.id} onClick={() => { setTenantId(t.id); nav(`/tenants/${t.id}`) }} style={{ cursor: 'pointer' }}>
                  <TableCell>{t.displayName}</TableCell>
                  <TableCell>{t.defaultDomain || '—'}</TableCell>
                  <TableCell><Text font="monospace" size={200}>{t.directoryId}</Text></TableCell>
                  <TableCell><TenantStatusBadge status={t.status} /></TableCell>
                  <TableCell>{fmtDate(t.lastPermissionCheckAt)}</TableCell>
                </TableRow>
              ))}
              {data?.length === 0 && (
                <TableRow><TableCell colSpan={5}>No tenants yet. Add one to start onboarding.</TableCell></TableRow>
              )}
            </TableBody>
          </Table>
        </Panel>
      )}
    </>
  )
}

function AddTenantDialog() {
  const [open, setOpen] = useState(false)
  const [domain, setDomain] = useState('')
  const [name, setName] = useState('')
  const [storage, setStorage] = useState('')
  const qc = useQueryClient()
  const nav = useNavigate()
  const { setTenantId } = useTenant()
  const { data: targets } = useQuery({ queryKey: ['storage'], queryFn: () => api<StorageTarget[]>('/api/storage-targets') })
  const add = useMutation({
    mutationFn: () => api<Tenant>('/api/tenants', { body: { domain, displayName: name || undefined, storageTargetId: storage || undefined } }),
    onSuccess: (t) => {
      qc.invalidateQueries({ queryKey: ['tenants'] })
      setOpen(false)
      setTenantId(t.id)
      nav(`/tenants/${t.id}`)
    },
  })
  return (
    <Dialog open={open} onOpenChange={(_, d) => setOpen(d.open)}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="primary" icon={<Add24Regular />}>Add tenant</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Add a tenant</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <ErrorBar error={add.error} />
            <Field label="Tenant domain or ID" required hint="For example contoso.onmicrosoft.com. The directory ID is looked up automatically.">
              <Input value={domain} onChange={(_, d) => setDomain(d.value)} />
            </Field>
            <Field label="Display name"><Input value={name} onChange={(_, d) => setName(d.value)} placeholder="Contoso" /></Field>
            <Field label="Storage target" hint="Where this tenant's backups are written. Defaults to the global default.">
              <Select value={storage} onChange={(_, d) => setStorage(d.value)}>
                <option value="">Default</option>
                {targets?.map((t) => <option key={t.id} value={t.id}>{t.name} ({t.kind})</option>)}
              </Select>
            </Field>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement><Button>Cancel</Button></DialogTrigger>
            <Button appearance="primary" disabled={!domain || add.isPending} onClick={() => add.mutate()}>
              {add.isPending ? 'Adding…' : 'Add tenant'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function CopyButton({ text }: { text: string }) {
  return (
    <Tooltip content="Copy link" relationship="label">
      <Button size="small" appearance="subtle" icon={<Copy16Regular />} onClick={() => navigator.clipboard.writeText(text)} />
    </Tooltip>
  )
}

function Step({ n, title, done, children }: { n: number; title: string; done: boolean; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: 16, padding: '12px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` }}>
      <div style={{
        width: 28, height: 28, borderRadius: 14, flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
        background: done ? tokens.colorPaletteGreenBackground3 : tokens.colorNeutralBackground5,
        color: done ? tokens.colorNeutralForegroundOnBrand : tokens.colorNeutralForeground1, fontWeight: 600,
      }}>{done ? <Checkmark16Filled /> : n}</div>
      <div style={{ flexGrow: 1 }}>
        <Subtitle2>{title}</Subtitle2>
        <div style={{ marginTop: 6 }}>{children}</div>
      </div>
    </div>
  )
}

function PermissionList({ check }: { check: PermissionCheck }) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
      {[check.backup, check.restore].map((a) => (
        <div key={a.app}>
          <Subtitle2>{a.app} app</Subtitle2>
          <div style={{ margin: '6px 0' }}>
            {a.tokenAcquired
              ? <Badge color="success" appearance="tint">Connected · {a.granted.length} granted</Badge>
              : <Badge color="danger" appearance="tint">Not connected</Badge>}
          </div>
          {a.error && <Text size={200} style={{ display: 'block', color: tokens.colorPaletteRedForeground1 }}>{a.error}</Text>}
          {a.tokenAcquired && a.missing.length === 0 && <Text size={200}>All required permissions are granted.</Text>}
          {a.tokenAcquired && a.missing.map((m) => (
            <div key={m.permission} style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
              <Dismiss16Filled color={tokens.colorPaletteRedForeground1} />
              <Text size={200}><b>{m.permission}</b> · {m.workload} · {m.reason}</Text>
            </div>
          ))}
        </div>
      ))}
      <div style={{ gridColumn: '1 / 3' }}>
        <Subtitle2>Azure</Subtitle2>
        <Text size={200} style={{ display: 'block' }}>
          {check.azure.tokenAcquired
            ? `${check.azure.subscriptions.length} subscription(s) visible: ${check.azure.subscriptions.map((s) => s.displayName).join(', ') || 'none'}`
            : check.azure.error}
        </Text>
        {check.azure.tokenAcquired && check.azure.error && <Text size={200}>{check.azure.error}</Text>}
      </div>
    </div>
  )
}

export function TenantDetailPage() {
  const { id } = useParams()
  const [params] = useSearchParams()
  const qc = useQueryClient()
  const isAdmin = useHasRole(id, 'Admin')
  const tenantQ = useQuery({ queryKey: ['tenant', id], queryFn: () => api<Tenant>(`/api/tenants/${id}`) })
  const urls = useQuery({
    queryKey: ['consent', id], enabled: isAdmin,
    queryFn: () => api<{ backup?: string; restore?: string; redirectUri: string }>(`/api/tenants/${id}/consent-urls`),
  })
  const setup = useQuery({ queryKey: ['setup'], queryFn: () => api<{ backupApp: { configured: boolean }; restoreApp: { configured: boolean } }>('/api/setup') })
  const { data: targets } = useQuery({ queryKey: ['storage'], queryFn: () => api<StorageTarget[]>('/api/storage-targets') })
  const check = useMutation({
    mutationFn: () => api<PermissionCheck>(`/api/tenants/${id}/permission-check`, { method: 'POST' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['tenant', id] })
      qc.invalidateQueries({ queryKey: ['tenants'] })
    },
  })
  const update = useMutation({
    mutationFn: (body: Record<string, unknown>) => api<Tenant>(`/api/tenants/${id}`, { method: 'PATCH', body }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['tenant', id] })
      qc.invalidateQueries({ queryKey: ['tenants'] })
    },
  })
  const [notes, setNotes] = useState<string | null>(null)

  if (tenantQ.isLoading) return <Loading />
  if (tenantQ.error) return <ErrorBar error={tenantQ.error} />
  const t = tenantQ.data!
  const pc = check.data ?? t.lastPermissionCheck
  const consent = params.get('consent')

  return (
    <>
      <PageHeader
        title={t.displayName}
        subtitle={<>{t.defaultDomain || 'No domain'} · <Text font="monospace" size={200}>{t.directoryId}</Text></>}
        actions={<TenantStatusBadge status={t.status} />}
      />
      {consent === 'ok' && <MessageBar intent="success" style={{ marginBottom: 12 }}><MessageBarBody>Consent granted for the {params.get('app')} app. A permission check has been queued.</MessageBarBody></MessageBar>}
      {consent === 'failed' && <MessageBar intent="error" style={{ marginBottom: 12 }}><MessageBarBody>Consent for the {params.get('app')} app was not granted ({params.get('error')}).</MessageBarBody></MessageBar>}

      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 1fr)', gap: 16 }}>
        <Panel>
          <Subtitle2>Onboarding</Subtitle2>
          <Step n={1} title="Grant the backup app (read-only)" done={!!t.backupConsentedAt}>
            {!setup.data?.backupApp.configured ? (
              <Text>The backup app isn't configured on this server yet. See docs/setup.md.</Text>
            ) : urls.data?.backup ? (
              <>
                <Text size={200} style={{ display: 'block' }}>A Global Admin of <b>{t.displayName}</b> opens this link and accepts the permissions.</Text>
                <Link href={urls.data.backup} target="_blank">Open admin consent</Link> <CopyButton text={urls.data.backup} />
              </>
            ) : <Text size={200}>Only tenant admins can see the consent link.</Text>}
          </Step>
          <Step n={2} title="Grant the restore app (optional, write access)" done={!!t.restoreConsentedAt}>
            {!setup.data?.restoreApp.configured ? (
              <Text>The restore app isn't configured on this server yet.</Text>
            ) : urls.data?.restore ? (
              <>
                <Text size={200} style={{ display: 'block' }}>Only needed before restoring. You can grant it just in time.</Text>
                <Link href={urls.data.restore} target="_blank">Open admin consent</Link> <CopyButton text={urls.data.restore} />
              </>
            ) : null}
          </Step>
          <Step n={3} title="Extra access consent can't grant" done={(pc?.azure.subscriptions.length ?? 0) > 0}>
            <Text size={200} style={{ display: 'block' }}>
              <b>Azure:</b> assign <i>Reader</i> (backup) and, for restores, <i>Contributor</i> to the apps' service principals on each
              subscription or management group.
            </Text>
            <Text size={200} style={{ display: 'block' }}>
              <b>Exchange / Security &amp; Compliance:</b> assign the <i>Exchange Administrator</i> and <i>Compliance Administrator</i> directory
              roles to the backup app's service principal.
            </Text>
          </Step>
          <Step n={4} title="Check permissions" done={!!pc && pc.backup.tokenAcquired && pc.backup.missing.length === 0}>
            <Button icon={<ArrowClockwise24Regular />} onClick={() => check.mutate()} disabled={check.isPending}>
              {check.isPending ? 'Checking…' : 'Run permission check'}
            </Button>
            <ErrorBar error={check.error} />
            {pc && <div style={{ marginTop: 12 }}><Text size={200}>Checked {fmtDate(pc.checkedAt)}</Text><PermissionList check={pc} /></div>}
          </Step>
        </Panel>

        <Panel>
          <Subtitle2>Settings</Subtitle2>
          <Field label="Storage target" style={{ marginTop: 8 }}>
            <Select disabled={!isAdmin} value={t.defaultStorageTargetId ?? ''} onChange={(_, d) => d.value && update.mutate({ storageTargetId: d.value })}>
              <option value="">Global default</option>
              {targets?.map((s) => <option key={s.id} value={s.id}>{s.name} ({s.kind})</option>)}
            </Select>
          </Field>
          <Field label="Notes" style={{ marginTop: 8 }}>
            <Textarea disabled={!isAdmin} value={notes ?? t.notes ?? ''} onChange={(_, d) => setNotes(d.value)} />
          </Field>
          {isAdmin && (
            <div style={{ display: 'flex', gap: 8, marginTop: 12, flexWrap: 'wrap' }}>
              <Button disabled={notes === null} onClick={() => update.mutate({ notes })}>Save notes</Button>
              {t.status === 'Disabled'
                ? <Button onClick={() => update.mutate({ status: 'Active' })}>Enable tenant</Button>
                : <Button onClick={() => update.mutate({ status: 'Disabled' })}>Disable tenant</Button>}
            </div>
          )}
          <ErrorBar error={update.error} />
          <Text size={200} style={{ display: 'block', marginTop: 16, color: tokens.colorNeutralForeground3 }}>
            Added {fmtDate(t.createdAt)}. Disabling a tenant pauses its schedules; existing backups are kept.
          </Text>
        </Panel>
      </div>
    </>
  )
}
