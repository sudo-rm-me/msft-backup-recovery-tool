import { PublicClientApplication, type AccountInfo, InteractionRequiredAuthError } from '@azure/msal-browser'

export interface AuthConfig {
  mode: 'Development' | 'Entra'
  clientId?: string
  authority?: string
  scopes?: string[]
}

let msal: PublicClientApplication | null = null
let scopes: string[] = []
let account: AccountInfo | null = null

/** Initialises sign-in. In Development mode the server signs every request in, so nothing happens here. */
export async function initAuth(): Promise<AuthConfig> {
  const cfg: AuthConfig = await (await fetch('/api/auth/config')).json()
  if (cfg.mode !== 'Entra') return cfg
  msal = new PublicClientApplication({
    auth: { clientId: cfg.clientId!, authority: cfg.authority!, redirectUri: window.location.origin },
    cache: { cacheLocation: 'sessionStorage' },
  })
  await msal.initialize()
  const redirect = await msal.handleRedirectPromise()
  scopes = cfg.scopes ?? []
  account = redirect?.account ?? msal.getAllAccounts()[0] ?? null
  if (!account) {
    await msal.loginRedirect({ scopes })
  }
  return cfg
}

export function signOut() {
  msal?.logoutRedirect()
}

async function token(): Promise<string | null> {
  if (!msal || !account) return null
  try {
    return (await msal.acquireTokenSilent({ scopes, account })).accessToken
  } catch (e) {
    if (e instanceof InteractionRequiredAuthError) await msal.acquireTokenRedirect({ scopes, account })
    throw e
  }
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export async function api<T = unknown>(path: string, init: { method?: string; body?: unknown } = {}): Promise<T> {
  const headers: Record<string, string> = {}
  const t = await token()
  if (t) headers.Authorization = `Bearer ${t}`
  if (init.body !== undefined) headers['Content-Type'] = 'application/json'
  const resp = await fetch(path, {
    method: init.method ?? (init.body !== undefined ? 'POST' : 'GET'),
    headers,
    body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
  })
  const text = await resp.text()
  if (!resp.ok) {
    let msg = text
    try {
      const j = JSON.parse(text)
      msg = j.detail ?? j.title ?? text
    } catch {
      /* not JSON */
    }
    throw new ApiError(resp.status, msg || resp.statusText)
  }
  return (text ? JSON.parse(text) : null) as T
}

/** Downloads a file from the API with auth (used for exports). */
export async function download(path: string, filename: string) {
  const headers: Record<string, string> = {}
  const t = await token()
  if (t) headers.Authorization = `Bearer ${t}`
  const resp = await fetch(path, { headers })
  if (!resp.ok) throw new ApiError(resp.status, await resp.text())
  const blob = await resp.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}

// ---------------------------------------------------------------- types
export type TenantStatus = 'PendingConsent' | 'Active' | 'Disabled'
export type TenantRole = 'Viewer' | 'BackupOperator' | 'RestoreOperator' | 'Admin'
export type JobStatus = 'Queued' | 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed' | 'Cancelled'

export interface PermissionGap { workload: string; permission: string; reason: string }
export interface AppPermissionStatus {
  app: string; configured: boolean; tokenAcquired: boolean; error?: string; granted: string[]; missing: PermissionGap[]
}
export interface PermissionCheck {
  checkedAt: string
  backup: AppPermissionStatus
  restore: AppPermissionStatus
  azure: { tokenAcquired: boolean; error?: string; subscriptions: { subscriptionId: string; displayName: string; state: string }[] }
}

export interface Tenant {
  id: string; directoryId: string; defaultDomain: string; displayName: string; status: TenantStatus; createdAt: string
  backupConsentedAt?: string; restoreConsentedAt?: string; lastPermissionCheckAt?: string; defaultStorageTargetId?: string
  notes?: string; lastPermissionCheck?: PermissionCheck
}

export interface Job {
  id: string; tenantId?: string; type: string; status: JobStatus; createdAt: string; startedAt?: string; completedAt?: string
  createdBy?: string; scheduleId?: string; attempt: number; maxAttempts: number; progress: number; progressMessage?: string
  error?: string; cancelRequested: boolean; parameters: Record<string, unknown>; result?: unknown
}

export interface JobLog { id: number; at: string; level: 'Debug' | 'Info' | 'Warning' | 'Error'; message: string }

export interface Schedule {
  id: string; tenantId: string; name: string; jobType: string; parameters: Record<string, unknown>; cron: string
  timeZone: string; enabled: boolean; nextRunAt?: string; lastRunAt?: string; lastJobId?: string
}

export type StorageKind = 'Local' | 'S3'
export interface StorageTarget {
  id: string; name: string; kind: StorageKind; settings: Record<string, unknown>; hasSecret: boolean; isDefault: boolean
}

export interface Me {
  id: string; displayName: string; userPrincipalName: string; isGlobalAdmin: boolean
  roles: { tenantId?: string; role: TenantRole }[]
}

export interface Operator extends Me {
  objectId: string; lastSeenAt?: string; roles: { id: string; tenantId?: string; role: TenantRole }[]
}

export interface AuditEntry {
  id: number; at: string; actor: string; tenantId?: string; action: string; target?: string; details?: unknown; ipAddress?: string
}

export const terminal = (s: JobStatus) => ['Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled'].includes(s)

export const fmtDate = (s?: string | null) => (s ? new Date(s).toLocaleString() : '—')

export function fmtBytes(n?: number | null): string {
  if (n == null) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let v = n
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}
