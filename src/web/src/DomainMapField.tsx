import { Field, Textarea } from '@fluentui/react-components'
import { parseDomainMap } from './domainMap'

/** Old → new domains for restores into another tenant, one "old.com = new.com" per line. */
export function DomainMapField({ value, onChange, sourceDomain, targetDomain }: {
  value: string; onChange: (v: string) => void; sourceDomain?: string; targetDomain?: string
}) {
  const { error } = parseDomainMap(value)
  const auto = sourceDomain?.endsWith('.onmicrosoft.com') && targetDomain?.endsWith('.onmicrosoft.com') && sourceDomain !== targetDomain
  return (
    <Field
      label="Map domains"
      validationState={error ? 'error' : 'none'}
      validationMessage={error}
      hint={<>
        One per line, e.g. <code>contoso.com = contoso-new.onmicrosoft.com</code>. Applied to user names, mail and proxy addresses, so users
        match (or are created) in the new tenant. Each new domain must be verified there.
        {auto && <> {sourceDomain} → {targetDomain} is mapped automatically.</>}
      </>}
    >
      <Textarea value={value} onChange={(_, d) => onChange(d.value)} placeholder="contoso.com = contoso-new.onmicrosoft.com" resize="vertical" />
    </Field>
  )
}
