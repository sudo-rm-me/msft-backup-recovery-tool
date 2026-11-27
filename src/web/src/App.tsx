import { Suspense, useEffect, useMemo, useState, type ReactElement } from 'react'
import { Link, NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { Dropdown, Option, Spinner, Text } from '@fluentui/react-components'
import {
  Board20Regular, Building20Regular, Settings20Regular, History20Regular, CalendarClock20Regular, ShieldTask20Regular,
  Navigation20Regular, Dismiss20Regular,
} from '@fluentui/react-icons'
import { signOut } from './api'
import { TenantContext, useMe, useTenants } from './common'
import { Dashboard } from './pages/Dashboard'
import { SchedulesPage } from './pages/Schedules'
import { featureNav, featureRoutes } from './features'
import { lazyPage } from './lazyPage'

const TenantsPage = lazyPage(() => import('./pages/Tenants'), 'TenantsPage')
const TenantDetailPage = lazyPage(() => import('./pages/Tenants'), 'TenantDetailPage')
const JobsPage = lazyPage(() => import('./pages/Jobs'), 'JobsPage')
const JobDetailPage = lazyPage(() => import('./pages/Jobs'), 'JobDetailPage')
const SettingsPage = lazyPage(() => import('./pages/Settings'), 'SettingsPage')
const AuditPage = lazyPage(() => import('./pages/Audit'), 'AuditPage')

export interface NavItem { to: string; label: string; icon: ReactElement; section: 'tenant' | 'global' }

const coreNav: NavItem[] = [
  { to: '/', label: 'Dashboard', icon: <Board20Regular />, section: 'tenant' },
  { to: '/jobs', label: 'Jobs', icon: <History20Regular />, section: 'tenant' },
  { to: '/schedules', label: 'Schedules', icon: <CalendarClock20Regular />, section: 'tenant' },
  { to: '/tenants', label: 'Tenants', icon: <Building20Regular />, section: 'global' },
  { to: '/audit', label: 'Audit log', icon: <ShieldTask20Regular />, section: 'global' },
  { to: '/settings', label: 'Settings', icon: <Settings20Regular />, section: 'global' },
]


export default function App() {
  const { data: tenants = [] } = useTenants()
  const { data: me } = useMe()
  const location = useLocation()
  const [tenantId, setTenantIdState] = useState<string | null>(() => localStorage.getItem('mbr.tenant'))
  // Small screens: the menu folds away under the header; it closes again after each navigation.
  const [menuOpen, setMenuOpen] = useState(false)
  useEffect(() => setMenuOpen(false), [location.pathname])

  useEffect(() => {
    if (tenants.length && (!tenantId || !tenants.some((t) => t.id === tenantId))) setTenantIdState(tenants[0].id)
  }, [tenants, tenantId])

  const setTenantId = (id: string | null) => {
    setTenantIdState(id)
    if (id) localStorage.setItem('mbr.tenant', id)
    else localStorage.removeItem('mbr.tenant')
  }
  const tenant = tenants.find((t) => t.id === tenantId) ?? null
  const ctx = useMemo(() => ({ tenants, tenantId, tenant, setTenantId }), [tenants, tenantId, tenant])

  const nav = [...coreNav.slice(0, 1), ...featureNav, ...coreNav.slice(1)]
  const link = (n: NavItem) => (
    <NavLink key={n.to} to={n.to} end={n.to === '/'} className={({ isActive }) => (isActive ? 'active' : undefined)}>
      <span className="suite-nav-icon">{n.icon}</span>
      <span>{n.label}</span>
    </NavLink>
  )

  return (
    <TenantContext.Provider value={ctx}>
      <div className="suite-shell">
        <aside className="suite-sidebar">
          <div className="suite-sidebar-brand">
            <h1 className="suite-sidebar-brand-stack">MBR</h1>
            <div className="suite-connected" aria-live="polite">
              {tenant ? (
                <Link to="/tenants" title="Change tenant">
                  <span className="suite-connected-label">Tenant</span>
                  <span className="suite-connected-name">{tenant.displayName}</span>
                </Link>
              ) : <span className="suite-connected-label">No tenant</span>}
            </div>
            <button type="button" className="suite-menu-toggle" aria-expanded={menuOpen} aria-controls="suite-menu"
              aria-label={menuOpen ? 'Close menu' : 'Open menu'} onClick={() => setMenuOpen((o) => !o)}>
              {menuOpen ? <Dismiss20Regular /> : <Navigation20Regular />}
            </button>
          </div>
          <div id="suite-menu" className={`suite-sidebar-body${menuOpen ? ' open' : ''}`}>
          <div className="suite-tenant">
            <Dropdown
              size="small"
              placeholder={tenants.length ? 'Choose a tenant' : 'No tenants yet'}
              value={tenant ? tenant.displayName : ''}
              selectedOptions={tenantId ? [tenantId] : []}
              onOptionSelect={(_, d) => setTenantId(d.optionValue ?? null)}
              aria-label="Tenant"
            >
              {tenants.map((t) => (
                <Option key={t.id} value={t.id} text={t.displayName}>
                  {t.displayName}{t.defaultDomain ? <Text size={200}>&nbsp;· {t.defaultDomain}</Text> : null}
                  {t.status !== 'Active' && <Text size={200}>&nbsp;· {t.status}</Text>}
                </Option>
              ))}
            </Dropdown>
          </div>
          <nav className="suite-nav" aria-label="Primary">
            <div className="suite-nav-group" role="group" aria-label="Tenant">
              <div className="suite-nav-group-label">Tenant</div>
              {nav.filter((n) => n.section === 'tenant').map(link)}
            </div>
            <div className="suite-nav-group" role="group" aria-label="Administration">
              <div className="suite-nav-group-label">Administration</div>
              {nav.filter((n) => n.section === 'global').map(link)}
            </div>
          </nav>
          {me && (
            <div className="suite-sidebar-footer">
              <div className="suite-sidebar-user" title={me.userPrincipalName}>
                <strong>{me.displayName}</strong>
                {me.userPrincipalName}{me.isGlobalAdmin ? ' · global admin' : ''}
              </div>
              <button type="button" className="suite-sidebar-signout" onClick={signOut}>Sign Out</button>
            </div>
          )}
          </div>
        </aside>
        <main className="suite-main" key={location.pathname.split('/')[1]}>
          <Suspense fallback={<Spinner label="Loading…" />}>
          <Routes>
            <Route path="/" element={<Dashboard />} />
            <Route path="/tenants" element={<TenantsPage />} />
            <Route path="/tenants/:id" element={<TenantDetailPage />} />
            <Route path="/jobs" element={<JobsPage />} />
            <Route path="/jobs/:id" element={<JobDetailPage />} />
            <Route path="/schedules" element={<SchedulesPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="/audit" element={<AuditPage />} />
            {featureRoutes}
            <Route path="*" element={<Text>Page not found.</Text>} />
          </Routes>
          </Suspense>
        </main>
      </div>
    </TenantContext.Provider>
  )
}
