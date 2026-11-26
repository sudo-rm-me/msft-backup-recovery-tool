import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { FluentProvider } from '@fluentui/react-components'
import { initAuth } from './api'
import App from './App'
import './index.css'
import './suite.css'
import { suiteTheme } from './theme'

const theme = suiteTheme()

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 5_000 } },
})

function Root() {
  return (
    <FluentProvider theme={theme} style={{ height: '100%', background: 'var(--bg-deep)' }}>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </QueryClientProvider>
    </FluentProvider>
  )
}

initAuth()
  .then(() => createRoot(document.getElementById('root')!).render(<StrictMode><Root /></StrictMode>))
  .catch((e) => {
    document.getElementById('root')!.innerText = `Could not reach the server: ${e}`
  })
