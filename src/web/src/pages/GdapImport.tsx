import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Badge, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, DialogTrigger, MessageBar,
  MessageBarBody, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Tooltip, tokens,
} from '@fluentui/react-components'
import { PeopleTeam24Regular } from '@fluentui/react-icons'
import { api, fmtDate, type Tenant } from '../api'
import { ErrorBar, Loading } from '../common'

interface PartnerCustomer {
  tenantId: string; displayName: string; active: boolean; canGrantConsent: boolean; earliestExpiry?: string; onboardedTenantId?: string
  relationships: { id: string; name: string; status: string; endsAt?: string; autoExtends: boolean; roles: string[] }[]
}

/** Bulk-onboards customers from the partner's GDAP relationships (Tenants page). */
export function GdapImportDialog() {
  const qc = useQueryClient()
  const [open, setOpen] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const q = useQuery({ queryKey: ['gdapCustomers'], enabled: open, queryFn: () => api<PartnerCustomer[]>('/api/partner/customers') })
  const importMut = useMutation({
    mutationFn: () => api<Tenant[]>('/api/partner/customers/import', { method: 'POST', body: { tenantIds: [...selected] } }),
    onSuccess: () => {
      setSelected(new Set())
      qc.invalidateQueries({ queryKey: ['tenants'] })
      qc.invalidateQueries({ queryKey: ['gdapCustomers'] })
    },
  })
  const rows = q.data ?? []
  const selectable = rows.filter(c => !c.onboardedTenantId)
  const toggle = (id: string, on: boolean) => {
    const next = new Set(selected)
    if (on) next.add(id); else next.delete(id)
    setSelected(next)
  }
  const soon = (d?: string) => !!d && new Date(d).getTime() - Date.now() < 30 * 86400_000

  return (
    <Dialog open={open} onOpenChange={(_, d) => { setOpen(d.open); importMut.reset() }}>
      <DialogTrigger disableButtonEnhancement><Button icon={<PeopleTeam24Regular />}>Import from GDAP</Button></DialogTrigger>
      <DialogSurface style={{ maxWidth: 900 }}>
        <DialogBody>
          <DialogTitle>Import customers from GDAP relationships</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>
              Customers are added as tenants waiting for consent. Backups use this tool's own apps, so each customer still grants admin consent —
              your admins can do that themselves where the relationship includes Privileged Role Administrator or Global Administrator.
            </Text>
            {q.isLoading ? <Loading /> : q.error ? <ErrorBar error={q.error} /> : (
              <Table size="small">
                <TableHeader><TableRow>
                  <TableHeaderCell style={{ width: 40 }}>
                    <Checkbox disabled={!selectable.length} checked={selectable.length > 0 && selectable.every(c => selected.has(c.tenantId)) ? true : selected.size ? 'mixed' : false}
                      onChange={(_, d) => setSelected(d.checked ? new Set(selectable.map(c => c.tenantId)) : new Set())} />
                  </TableHeaderCell>
                  <TableHeaderCell>Customer</TableHeaderCell><TableHeaderCell>Relationship</TableHeaderCell><TableHeaderCell>Roles</TableHeaderCell>
                  <TableHeaderCell>Expires</TableHeaderCell><TableHeaderCell>Consent</TableHeaderCell>
                </TableRow></TableHeader>
                <TableBody>
                  {rows.map(c => {
                    const rel = c.relationships[0]
                    return (
                      <TableRow key={c.tenantId}>
                        <TableCell>
                          {c.onboardedTenantId ? <Badge appearance="tint" color="success">Added</Badge>
                            : <Checkbox checked={selected.has(c.tenantId)} onChange={(_, d) => toggle(c.tenantId, !!d.checked)} />}
                        </TableCell>
                        <TableCell>
                          <div>{c.displayName}</div>
                          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{c.tenantId}</Text>
                        </TableCell>
                        <TableCell>
                          <div>{rel?.name}</div>
                          <Text size={200} style={{ color: c.active ? tokens.colorNeutralForeground3 : tokens.colorPaletteRedForeground1 }}>
                            {c.active ? 'Active' : rel?.status}{c.relationships.length > 1 ? ` · +${c.relationships.length - 1} more` : ''}
                          </Text>
                        </TableCell>
                        <TableCell>
                          <Tooltip relationship="description" content={c.relationships.flatMap(r => r.roles).filter((r, i, a) => a.indexOf(r) === i).join(', ')}>
                            <span>{new Set(c.relationships.flatMap(r => r.roles)).size} role(s)</span>
                          </Tooltip>
                        </TableCell>
                        <TableCell>
                          <span style={{ color: soon(c.earliestExpiry) && !rel?.autoExtends ? tokens.colorPaletteMarigoldForeground1 : undefined }}>
                            {fmtDate(c.earliestExpiry) || '—'}
                          </span>
                          {rel?.autoExtends && <Text size={200} style={{ display: 'block', color: tokens.colorNeutralForeground3 }}>auto-extends</Text>}
                        </TableCell>
                        <TableCell>{c.canGrantConsent ? <Badge appearance="tint" color="brand">You can grant</Badge> : <Text size={200}>Customer admin</Text>}</TableCell>
                      </TableRow>
                    )
                  })}
                  {rows.length === 0 && <TableRow><TableCell colSpan={6}>No GDAP relationships found.</TableCell></TableRow>}
                </TableBody>
              </Table>
            )}
            {importMut.data && (
              <MessageBar intent="success"><MessageBarBody>Added {importMut.data.length} tenant(s). Open each one to send its consent links.</MessageBarBody></MessageBar>
            )}
            {importMut.error && <ErrorBar error={importMut.error} />}
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement><Button>Close</Button></DialogTrigger>
            <Button appearance="primary" disabled={!selected.size || importMut.isPending} onClick={() => importMut.mutate()}>
              {importMut.isPending ? 'Importing…' : `Import ${selected.size || ''} customer(s)`}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
