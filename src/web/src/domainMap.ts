/** Parses "old.com = new.com" lines (also "old.com=new.com" or "old.com → new.com") into a domain map. */
export function parseDomainMap(text: string): { map?: Record<string, string>; error?: string } {
  const map: Record<string, string> = {}
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim()
    if (!line) continue
    const parts = line.split(/\s*(?:=|→|->)\s*/)
    if (parts.length !== 2 || !parts[0] || !parts[1]) return { error: `Use one "old.com = new.com" per line (not "${line}").` }
    map[parts[0].toLowerCase()] = parts[1].toLowerCase()
  }
  return { map: Object.keys(map).length ? map : undefined }
}
