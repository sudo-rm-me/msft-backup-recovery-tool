import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Button, Checkbox, Dialog, Textarea, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Select,
  Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, MessageBar, MessageBarBody, Subtitle2,
} from '@fluentui/react-components'
import { Add24Regular } from '@fluentui/react-icons'
import { api, fmtDate, type Operator, type StorageKind, type StorageTarget, type TenantRole } from '../api'
import { ErrorBar, Loading, PageHeader, Panel, useMe, useTenant } from '../common'

type FieldDef = { key: string; label: string; hint?: string; type?: 'text' | 'number' | 'bool' | 'select'; options?: string[] }

const storageFields: Record<StorageKind, FieldDef[]> = {
  Local: [{ key: 'path', label: 'Folder', hint: 'Absolute path, or relative to the data directory. Use a separate disk or network share.' }],
  S3: [
    { key: 'bucket', label: 'Bucket' },
    { key: 'region', label: 'Region', hint: 'e.g. eu-west-1' },
    { key: 'serviceUrl', label: 'Endpoint URL', hint: 'Only for S3-compatible stores (MinIO, Wasabi, Backblaze B2, Cloudflare R2). Leave empty for AWS.' },
    { key: 'forcePathStyle', label: 'Path-style addressing', type: 'bool' },
    { key: 'prefix', label: 'Key prefix', hint: 'Optional folder inside the bucket' },
    { key: 'credentials', label: 'Credentials', type: 'select', options: ['AzureFederation', 'Default', 'Static', 'Profile'], hint: 'AzureFederation: this app\'s Azure managed identity assumes an IAM role, with no AWS keys (set up by the Terraform deployment). Default uses the AWS credential chain.' },
    { key: 'roleArn', label: 'IAM role ARN', hint: 'For AzureFederation: the role the managed identity assumes' },
    { key: 'webIdentityAudience', label: 'Web identity audience', hint: 'For AzureFederation: the Application ID URI the token is requested for (Terraform output aws_federation_audience_uri)' },
    { key: 'accessKeyId', label: 'Access key ID', hint: 'For Static credentials; the secret key goes below.' },
    { key: 'profileName', label: 'AWS profile name', hint: 'For Profile credentials' },
    { key: 'storageClass', label: 'Storage class', hint: 'e.g. STANDARD_IA, INTELLIGENT_TIERING, GLACIER_IR' },
    { key: 'objectLockDays', label: 'Object Lock retention (days)', type: 'number', hint: 'Requires a bucket created with Object Lock. Objects cannot be deleted until this expires.' },
    { key: 'objectLockMode', label: 'Object Lock mode', type: 'select', options: ['GOVERNANCE', 'COMPLIANCE'] },
  ],
}

const defaults: Record<StorageKind, Record<string, unknown>> = {
  Local: { path: 'storage' },
  S3: { credentials: 'Default', objectLockMode: 'GOVERNANCE', forcePathStyle: false },
}

function StorageDialog({ open, onClose, existing }: { open: boolean; onClose: () => void; existing?: StorageTarget }) {
  const qc = useQueryClient()
  const [name, setName] = useState(existing?.name ?? '')
  const [kind, setKind] = useState<StorageKind>(existing?.kind ?? 'S3')
  const [settings, setSettings] = useState<Record<string, unknown>>(existing?.settings ?? defaults.S3)
  const [secret, setSecret] = useState('')
  const [isDefault, setIsDefault] = useState(existing?.isDefault ?? false)
  const save = useMutation({
    mutationFn: () => {
      const body = { name, kind, settings, secret: secret || (existing ? null : undefined), isDefault }
      return existing ? api(`/api/storage-targets/${existing.id}`, { method: 'PUT', body }) : api('/api/storage-targets', { body })
    },
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['storage'] }); onClose() },
  })
  const needsSecret = kind === 'S3' && settings.credentials === 'Static'

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{existing ? `Edit ${existing.name}` : 'Add storage target'}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10, maxHeight: '70vh', overflowY: 'auto' }}>
            <ErrorBar error={save.error} />
            <Field label="Name" required><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>
            <Field label="Type">
              <Select value={kind} disabled={!!existing} onChange={(_, d) => { setKind(d.value as StorageKind); setSettings(defaults[d.value as StorageKind]) }}>
                <option value="S3">Amazon S3 / S3-compatible</option>
                <option value="Local">Local folder (development only)</option>
              </Select>
            </Field>
            {storageFields[kind].map((f) => (
              <Field key={f.key} label={f.label} hint={f.hint}>
                {f.type === 'bool' ? (
                  <Checkbox checked={!!settings[f.key]} onChange={(_, d) => setSettings({ ...settings, [f.key]: !!d.checked })} />
                ) : f.type === 'select' ? (
                  <Select value={String(settings[f.key] ?? '')} onChange={(_, d) => setSettings({ ...settings, [f.key]: d.value || null })}>
                    {f.options!.map((o) => <option key={o} value={o}>{o || '(default)'}</option>)}
                  </Select>
                ) : (
                  <Input type={f.type === 'number' ? 'number' : 'text'} value={String(settings[f.key] ?? '')}
                    onChange={(_, d) => setSettings({ ...settings, [f.key]: f.type === 'number' ? (d.value ? Number(d.value) : null) : d.value })} />
                )}
              </Field>
            ))}
            {needsSecret && (
              <Field label={kind === 'S3' ? 'Secret access key' : 'Connection string'}
                hint={existing?.hasSecret ? 'A secret is stored. Leave empty to keep it.' : 'Stored encrypted.'}>
                <Input type="password" value={secret} onChange={(_, d) => setSecret(d.value)} />
              </Field>
            )}
            <Checkbox label="Default storage target for new tenants" checked={isDefault} onChange={(_, d) => setIsDefault(!!d.checked)} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={!name || save.isPending} onClick={() => save.mutate()}>Save</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function StorageTab({ admin }: { admin: boolean }) {
  const q = useQuery({ queryKey: ['storage'], queryFn: () => api<StorageTarget[]>('/api/storage-targets') })
  const [dialog, setDialog] = useState<{ open: boolean; existing?: StorageTarget }>({ open: false })
  const [results, setResults] = useState<Record<string, { ok: boolean; steps: { name: string; ok: boolean; error?: string }[] }>>({})
  const test = useMutation({
    mutationFn: async (id: string) => ({ id, r: await api<{ ok: boolean; steps: { name: string; ok: boolean; error?: string }[] }>(`/api/storage-targets/${id}/test`, { method: 'POST' }) }),
    onSuccess: ({ id, r }) => setResults((x) => ({ ...x, [id]: r })),
  })
  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 12 }}>
        <Text style={{ flexGrow: 1 }}>Backups are encrypted per tenant before they are written. Use S3 Object Lock for ransomware protection.
          The <b>S3 (deployment)</b> target follows the Terraform variables until you edit it here; after that your edits are kept.</Text>
        {admin && <Button appearance="primary" icon={<Add24Regular />} onClick={() => setDialog({ open: true })}>Add storage</Button>}
      </div>
      <ErrorBar error={q.error ?? test.error} />
      {q.isLoading ? <Loading /> : (
        <Table size="small">
          <TableHeader><TableRow>
            <TableHeaderCell>Name</TableHeaderCell><TableHeaderCell>Type</TableHeaderCell><TableHeaderCell>Location</TableHeaderCell>
            <TableHeaderCell>Test</TableHeaderCell><TableHeaderCell />
          </TableRow></TableHeader>
          <TableBody>
            {(q.data ?? []).map((t) => (
              <TableRow key={t.id}>
                <TableCell>{t.name} {t.isDefault && <Badge appearance="tint">default</Badge>}</TableCell>
                <TableCell>{t.kind}</TableCell>
                <TableCell><Text size={200}>{t.kind === 'S3' ? `s3://${t.settings.bucket}/${t.settings.prefix ?? ''}${t.settings.objectLockDays ? ` · Object Lock ${t.settings.objectLockDays}d` : ''}`
                  : String(t.settings.path)}</Text></TableCell>
                <TableCell>
                  {results[t.id] && (
                    <Text size={200}>{results[t.id].ok ? '✓ usable' : '✗ ' + results[t.id].steps.filter((s) => !s.ok).map((s) => `${s.name}: ${s.error}`).join('; ')}</Text>
                  )}
                </TableCell>
                <TableCell>
                  <Button size="small" onClick={() => test.mutate(t.id)} disabled={test.isPending}>Test</Button>{' '}
                  {admin && <Button size="small" onClick={() => setDialog({ open: true, existing: t })}>Edit</Button>}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {dialog.open && <StorageDialog open onClose={() => setDialog({ open: false })} existing={dialog.existing} />}
    </>
  )
}

type AppSetting = {
  key: string; group: string; label: string; hint: string; type: 'Text' | 'Number' | 'Boolean' | 'List'; secret: boolean
  min?: number; max?: number; value?: unknown; deploymentValue?: unknown; overridden: boolean; isSet: boolean; deploymentIsSet: boolean
}

const show = (s: AppSetting, v: unknown) =>
  s.secret ? (s.deploymentIsSet ? 'set (hidden)' : 'not set')
    : Array.isArray(v) ? (v.length ? v.join(', ') : 'none')
    : v === '' || v == null ? 'not set' : String(v)

/** Deployment-wide settings changed in the app; they override the Terraform / appsettings values until reset. */
function GeneralTab() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['app-settings'], queryFn: () => api<AppSetting[]>('/api/app-settings') })
  const [draft, setDraft] = useState<Record<string, unknown>>({})
  const save = useMutation({
    mutationFn: (values: Record<string, unknown>) => api<AppSetting[]>('/api/app-settings', { method: 'PUT', body: { values } }),
    onSuccess: (data) => { qc.setQueryData(['app-settings'], data); setDraft({}) },
  })
  if (q.isLoading) return <Loading />
  const settings = q.data ?? []
  const groups = [...new Set(settings.map((s) => s.group))]
  const dirty = Object.keys(draft).length > 0
  const current = (s: AppSetting) => (s.key in draft ? draft[s.key] : s.secret ? (s.type === 'List' ? [] : '') : s.value)
  const set = (s: AppSetting, v: unknown) => setDraft({ ...draft, [s.key]: v })

  const input = (s: AppSetting) => {
    const v = current(s)
    const placeholder = s.secret && s.isSet ? 'Stored. Type to replace.' : undefined
    switch (s.type) {
      case 'Boolean':
        return <Checkbox checked={!!v} onChange={(_, d) => set(s, !!d.checked)} />
      case 'Number':
        return <Input type="number" min={s.min} max={s.max} value={v == null ? '' : String(v)} onChange={(_, d) => set(s, d.value === '' ? null : Number(d.value))} />
      case 'List':
        return <Textarea resize="vertical" placeholder={placeholder ?? 'One per line'} value={((v as string[] | undefined) ?? []).join('\n')}
          onChange={(_, d) => set(s, d.value.split('\n'))} />
      default:
        return <Input type={s.secret ? 'password' : 'text'} placeholder={placeholder} value={String(v ?? '')} onChange={(_, d) => set(s, d.value)} />
    }
  }

  const values = () => Object.fromEntries(Object.entries(draft).map(([k, v]) => [k, Array.isArray(v) ? v.filter((x) => String(x).trim()) : v]))

  return (
    <>
      <MessageBar style={{ marginBottom: 12 }}><MessageBarBody>
        These settings apply to the whole deployment. A value saved here overrides the one from the deployment (Terraform variables
        or <code>appsettings.json</code>) on every replica within a minute; <b>Use deployment value</b> brings that back. The database,
        Key Vault, app registrations and sign-in come from the deployment only.
      </MessageBarBody></MessageBar>
      <ErrorBar error={q.error ?? save.error} />
      {groups.map((g) => (
        <div key={g} style={{ marginBottom: 16 }}>
          <Subtitle2 style={{ display: 'block', marginBottom: 6 }}>{g}</Subtitle2>
          <div style={{ display: 'grid', gridTemplateColumns: 'minmax(220px, 1fr) minmax(260px, 2fr)', gap: '10px 16px', alignItems: 'start' }}>
            {settings.filter((s) => s.group === g).map((s) => (
              <div key={s.key} style={{ display: 'contents' }}>
                <div>
                  <Text weight="semibold">{s.label}</Text>{' '}
                  {s.overridden ? <Badge appearance="tint" color="brand">set in app</Badge> : <Badge appearance="outline">deployment</Badge>}
                  {s.hint && <Text size={200} block style={{ color: 'var(--text-muted)' }}>{s.hint}</Text>}
                  <Text size={200} block style={{ color: 'var(--text-muted)' }}><code>{s.key}</code></Text>
                </div>
                <div>
                  {input(s)}
                  <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 2 }}>
                    <Text size={200} style={{ color: 'var(--text-muted)' }}>Deployment value: {show(s, s.deploymentValue)}</Text>
                    {s.overridden && (
                      <Button size="small" appearance="subtle" disabled={save.isPending} onClick={() => save.mutate({ [s.key]: null })}>
                        Use deployment value
                      </Button>
                    )}
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      ))}
      <div style={{ display: 'flex', gap: 8 }}>
        <Button appearance="primary" disabled={!dirty || save.isPending} onClick={() => save.mutate(values())}>Save changes</Button>
        <Button disabled={!dirty || save.isPending} onClick={() => setDraft({})}>Discard</Button>
      </div>
    </>
  )
}

const roles: TenantRole[] = ['Viewer', 'BackupOperator', 'RestoreOperator', 'Admin']

function OperatorsTab() {
  const qc = useQueryClient()
  const { tenants } = useTenant()
  const q = useQuery({ queryKey: ['operators'], queryFn: () => api<Operator[]>('/api/operators') })
  const [form, setForm] = useState({ objectId: '', userPrincipalName: '', isGlobalAdmin: false })
  const [roleForm, setRoleForm] = useState<Record<string, { tenantId: string; role: TenantRole }>>({})
  const inv = () => qc.invalidateQueries({ queryKey: ['operators'] })
  const add = useMutation({ mutationFn: () => api('/api/operators', { body: form }), onSuccess: () => { inv(); setForm({ objectId: '', userPrincipalName: '', isGlobalAdmin: false }) } })
  const setRole = useMutation({ mutationFn: ({ id, tenantId, role }: { id: string; tenantId: string; role: TenantRole }) => api(`/api/operators/${id}/roles`, { body: { tenantId: tenantId || null, role } }), onSuccess: inv })
  const removeRole = useMutation({ mutationFn: ({ id, roleId }: { id: string; roleId: string }) => api(`/api/operators/${id}/roles/${roleId}`, { method: 'DELETE' }), onSuccess: inv })
  const del = useMutation({ mutationFn: (id: string) => api(`/api/operators/${id}`, { method: 'DELETE' }), onSuccess: inv })
  const tenantName = (id?: string) => (id ? tenants.find((t) => t.id === id)?.displayName ?? id : 'All tenants')

  return (
    <>
      <MessageBar style={{ marginBottom: 12 }}><MessageBarBody>
        Operators sign in with their account in this deployment's home tenant. People listed in <code>Auth:BootstrapAdmins</code> become
        global admins on first sign-in; everyone else needs a role here.
      </MessageBarBody></MessageBar>
      <ErrorBar error={q.error ?? add.error ?? setRole.error ?? removeRole.error ?? del.error} />
      {q.isLoading ? <Loading /> : (
        <Table size="small">
          <TableHeader><TableRow>
            <TableHeaderCell>Operator</TableHeaderCell><TableHeaderCell>Roles</TableHeaderCell><TableHeaderCell>Grant role</TableHeaderCell><TableHeaderCell>Last seen</TableHeaderCell><TableHeaderCell />
          </TableRow></TableHeader>
          <TableBody>
            {(q.data ?? []).map((o) => {
              const rf = roleForm[o.id] ?? { tenantId: '', role: 'Viewer' as TenantRole }
              return (
                <TableRow key={o.id}>
                  <TableCell>{o.displayName}<br /><Text size={200}>{o.userPrincipalName}</Text></TableCell>
                  <TableCell>
                    {o.isGlobalAdmin && <Badge color="brand">Global admin</Badge>}
                    {o.roles.map((r) => (
                      <div key={r.id}><Text size={200}>{r.role} · {tenantName(r.tenantId)}</Text>{' '}
                        <Button size="small" appearance="subtle" onClick={() => removeRole.mutate({ id: o.id, roleId: r.id })}>remove</Button></div>
                    ))}
                  </TableCell>
                  <TableCell>
                    {!o.isGlobalAdmin && (
                      <div style={{ display: 'flex', gap: 4 }}>
                        <Select size="small" value={rf.tenantId} onChange={(_, d) => setRoleForm({ ...roleForm, [o.id]: { ...rf, tenantId: d.value } })}>
                          <option value="">All tenants</option>
                          {tenants.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
                        </Select>
                        <Select size="small" value={rf.role} onChange={(_, d) => setRoleForm({ ...roleForm, [o.id]: { ...rf, role: d.value as TenantRole } })}>
                          {roles.map((r) => <option key={r}>{r}</option>)}
                        </Select>
                        <Button size="small" onClick={() => setRole.mutate({ id: o.id, ...rf })}>Grant</Button>
                      </div>
                    )}
                  </TableCell>
                  <TableCell>{fmtDate(o.lastSeenAt)}</TableCell>
                  <TableCell><Button size="small" appearance="subtle" onClick={() => confirm(`Remove ${o.userPrincipalName}?`) && del.mutate(o.id)}>Remove</Button></TableCell>
                </TableRow>
              )
            })}
          </TableBody>
        </Table>
      )}
      <Subtitle2 style={{ display: 'block', marginTop: 16 }}>Add operator</Subtitle2>
      <div style={{ display: 'flex', gap: 8, alignItems: 'end', flexWrap: 'wrap' }}>
        <Field label="Entra object ID"><Input value={form.objectId} onChange={(_, d) => setForm({ ...form, objectId: d.value })} /></Field>
        <Field label="User principal name"><Input value={form.userPrincipalName} onChange={(_, d) => setForm({ ...form, userPrincipalName: d.value })} /></Field>
        <Checkbox label="Global admin" checked={form.isGlobalAdmin} onChange={(_, d) => setForm({ ...form, isGlobalAdmin: !!d.checked })} />
        <Button disabled={!form.objectId || !form.userPrincipalName} onClick={() => add.mutate()}>Add</Button>
      </div>
    </>
  )
}

function AppsTab() {
  const q = useQuery({
    queryKey: ['setup'],
    queryFn: () => api<{
      backupApp: { configured: boolean; clientId?: string }; restoreApp: { configured: boolean; clientId?: string }; consentRedirectUri: string
      permissions: { workload: string; api: string; name: string; app: string; reason: string }[]
    }>('/api/setup'),
  })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBar error={q.error} />
  const d = q.data!
  return (
    <>
      <Text style={{ display: 'block', marginBottom: 12 }}>
        Customer tenants consent to two multi-tenant apps registered in your home tenant. The Terraform deployment
        (<code>deploy/terraform/modules/mbr</code>) creates them and their Key Vault certificates.
        Redirect URI: <code>{d.consentRedirectUri}</code>
      </Text>
      {(['backupApp', 'restoreApp'] as const).map((k) => (
        <div key={k} style={{ marginBottom: 8 }}>
          <Subtitle2>{k === 'backupApp' ? 'Backup app (read-only)' : 'Restore app (write)'}</Subtitle2>{' '}
          {d[k].configured ? <Badge color="success">configured · {d[k].clientId}</Badge> : <Badge color="danger">not configured</Badge>}
        </div>
      ))}
      <Table size="small">
        <TableHeader><TableRow><TableHeaderCell>App</TableHeaderCell><TableHeaderCell>Workload</TableHeaderCell><TableHeaderCell>Permission</TableHeaderCell><TableHeaderCell>Why</TableHeaderCell></TableRow></TableHeader>
        <TableBody>
          {d.permissions.map((p) => (
            <TableRow key={p.app + p.name + p.workload}><TableCell>{p.app}</TableCell><TableCell>{p.workload}</TableCell><TableCell><code>{p.name}</code></TableCell><TableCell>{p.reason}</TableCell></TableRow>
          ))}
        </TableBody>
      </Table>
    </>
  )
}

export function SettingsPage() {
  const { data: me } = useMe()
  const [tab, setTab] = useState('storage')
  const admin = !!me?.isGlobalAdmin
  return (
    <>
      <PageHeader title="Settings" />
      <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as string)} style={{ marginBottom: 12 }}>
        <Tab value="storage">Storage</Tab>
        {admin && <Tab value="general">General</Tab>}
        {admin && <Tab value="operators">Operators</Tab>}
        <Tab value="apps">App registrations</Tab>
      </TabList>
      <Panel>
        {tab === 'storage' && <StorageTab admin={admin} />}
        {tab === 'general' && admin && <GeneralTab />}
        {tab === 'operators' && admin && <OperatorsTab />}
        {tab === 'apps' && <AppsTab />}
      </Panel>
    </>
  )
}
