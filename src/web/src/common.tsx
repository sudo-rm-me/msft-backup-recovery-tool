import { createContext, useContext, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  Badge, MessageBar, MessageBarBody, MessageBarTitle, Spinner, Text, tokens, ProgressBar,
} from '@fluentui/react-components'
import { api, type JobStatus, type Me, type Tenant } from './api'

// ---------------------------------------------------------------- selected tenant
interface TenantCtx {
  tenants: Tenant[]
  tenantId: string | null
  tenant: Tenant | null
  setTenantId: (id: string | null) => void
}

export const TenantContext = createContext<TenantCtx>({ tenants: [], tenantId: null, tenant: null, setTenantId: () => {} })
export const useTenant = () => useContext(TenantContext)

export function useTenants() {
  return useQuery({ queryKey: ['tenants'], queryFn: () => api<Tenant[]>('/api/tenants') })
}

export function useMe() {
  return useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/me'), staleTime: 60_000 })
}

const roleRank = { Viewer: 0, BackupOperator: 1, RestoreOperator: 2, Admin: 3 } as const

/** Whether the current operator has at least the given role on a tenant. */
export function useHasRole(tenantId: string | null | undefined, role: keyof typeof roleRank): boolean {
  const { data: me } = useMe()
  if (!me) return false
  if (me.isGlobalAdmin) return true
  const best = me.roles
    .filter((r) => r.tenantId == null || r.tenantId === tenantId)
    .reduce((m, r) => Math.max(m, roleRank[r.role]), -1)
  return best >= roleRank[role]
}

// ---------------------------------------------------------------- layout pieces
/** The suite toolbar: title with a muted line beside it, actions on the right; sticks to the top of the page. */
export function PageHeader({ title, subtitle, actions }: { title: ReactNode; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="suite-toolbar">
      <div className="suite-toolbar-titles">
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {actions && <div className="suite-toolbar-actions">{actions}</div>}
    </header>
  )
}

export function Panel({ children, style }: { children: ReactNode; style?: React.CSSProperties }) {
  return <div className="suite-panel" style={style}>{children}</div>
}

export function ErrorBar({ error, title }: { error: unknown; title?: string }) {
  if (!error) return null
  return (
    <MessageBar intent="error" style={{ marginBottom: 12 }}>
      <MessageBarBody>
        {title && <MessageBarTitle>{title}</MessageBarTitle>}
        {error instanceof Error ? error.message : String(error)}
      </MessageBarBody>
    </MessageBar>
  )
}

export function Loading({ label }: { label?: string }) {
  return <Spinner label={label ?? 'Loading…'} style={{ margin: 24 }} />
}

export function NeedTenant() {
  return (
    <MessageBar intent="info">
      <MessageBarBody>Choose a tenant in the header to see this page.</MessageBarBody>
    </MessageBar>
  )
}

export function JobStatusBadge({ status }: { status: JobStatus }) {
  const color = {
    Queued: 'informative', Running: 'brand', Succeeded: 'success', PartiallySucceeded: 'warning', Failed: 'danger', Cancelled: 'subtle',
  }[status] as 'informative' | 'brand' | 'success' | 'warning' | 'danger' | 'subtle'
  return <Badge appearance="tint" color={color}>{status === 'PartiallySucceeded' ? 'Partial' : status}</Badge>
}

export function JobProgress({ progress, status }: { progress: number; status: JobStatus }) {
  if (status !== 'Running') return null
  return <ProgressBar value={progress} thickness="large" style={{ minWidth: 120 }} />
}

export function Stat({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <Panel>
      <span className="suite-stat-label">{label}</span>
      <span className="suite-stat-value">{value}</span>
      {hint && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{hint}</Text>}
    </Panel>
  )
}

export function Json({ value }: { value: unknown }) {
  return <pre className="code">{JSON.stringify(value, null, 2)}</pre>
}
