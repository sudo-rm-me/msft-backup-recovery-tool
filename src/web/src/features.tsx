import type { ComponentType, ReactElement } from 'react'
import { Route } from 'react-router-dom'
import { CloudArrowUp20Regular, ArrowSync20Regular, ArrowUndo20Regular, Cloud20Regular, DataBarVertical20Regular, Delete20Regular, DocumentFolder20Regular, ShieldCheckmark20Regular } from '@fluentui/react-icons'
import type { NavItem } from './App'
import { BackupsPage, LastBackupWidget } from './pages/Backups'
import { SnapshotExplorerPage } from './pages/SnapshotExplorer'
import { DiffPage } from './pages/Diff'
import { RestorePage, RestorePlanPage } from './pages/Restore'
import { RecycleBinPage } from './pages/RecycleBin'
import { AzurePage } from './pages/Azure'
import { ReportsPage } from './pages/Reports'
import { M365BackupPage } from './pages/M365Backup'
import { DataBrowserPage, DataProtectionWidget, DataSourcesPage } from './pages/Data'
import { jobParameterTemplates } from './pages/Schedules'

jobParameterTemplates['backup.config'] = { workloads: ['entra', 'intune', 'azure', 'exchange', 'defender', 'purview', 'teams', 'sharepoint'] }
jobParameterTemplates['config.drift'] = {}
jobParameterTemplates['backup.data'] = { kinds: ['OneDrive', 'SharePointSite'] }
jobParameterTemplates['data.discover'] = {}

/** Navigation entries and routes for backup, snapshot, diff, restore and data pages (added per phase). */
export const featureNav: NavItem[] = [
  { to: '/backups', label: 'Config backups', icon: <CloudArrowUp20Regular />, section: 'tenant' },
  { to: '/diff', label: 'Compare & drift', icon: <ArrowSync20Regular />, section: 'tenant' },
  { to: '/data', label: 'Files & data', icon: <DocumentFolder20Regular />, section: 'tenant' },
  { to: '/restore', label: 'Restore', icon: <ArrowUndo20Regular />, section: 'tenant' },
  { to: '/recycle-bin', label: 'Recycle bin', icon: <Delete20Regular />, section: 'tenant' },
  { to: '/azure', label: 'Azure', icon: <Cloud20Regular />, section: 'tenant' },
  { to: '/m365backup', label: 'Microsoft 365 Backup', icon: <ShieldCheckmark20Regular />, section: 'tenant' },
  { to: '/reports', label: 'Reports', icon: <DataBarVertical20Regular />, section: 'global' },
]

export const featureRoutes: ReactElement[] = [
  <Route key="backups" path="/backups" element={<BackupsPage />} />,
  <Route key="snapshot" path="/snapshots/:id" element={<SnapshotExplorerPage />} />,
  <Route key="diff" path="/diff" element={<DiffPage />} />,
  <Route key="restore" path="/restore" element={<RestorePage />} />,
  <Route key="restorePlan" path="/restore/plans/:id" element={<RestorePlanPage />} />,
  <Route key="recycleBin" path="/recycle-bin" element={<RecycleBinPage />} />,
  <Route key="azure" path="/azure" element={<AzurePage />} />,
  <Route key="m365backup" path="/m365backup" element={<M365BackupPage />} />,
  <Route key="reports" path="/reports" element={<ReportsPage />} />,
  <Route key="data" path="/data" element={<DataSourcesPage />} />,
  <Route key="dataSource" path="/data/sources/:id" element={<DataBrowserPage />} />,
]

/** Extra panels shown on the tenant dashboard. */
export const dashboardWidgets: ComponentType<{ tenantId: string }>[] = [LastBackupWidget, DataProtectionWidget]
