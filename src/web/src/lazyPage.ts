import { lazy, type ComponentType } from 'react'

/** A page loaded on first visit, so it isn't in the main bundle. */
export function lazyPage<M, K extends keyof M>(load: () => Promise<M>, name: K) {
  return lazy(async () => ({ default: (await load())[name] as ComponentType }))
}
