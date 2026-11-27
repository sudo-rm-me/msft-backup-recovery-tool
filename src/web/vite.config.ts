import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The production build is served by the ASP.NET Core API from its wwwroot folder.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Mbr.Api/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 600, // Fluent UI is one ~520 kB chunk
    rollupOptions: {
      output: {
        // Libraries change less often than the app, so they get their own long-cached chunks.
        manualChunks(id) {
          if (!id.includes('node_modules')) return undefined
          if (id.includes('@fluentui/react-icons')) return 'icons'
          if (id.includes('@fluentui') || id.includes('@griffel') || id.includes('tabster')) return 'fluent'
          if (id.includes('@azure/msal')) return 'msal'
          return 'vendor'
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: { '/api': { target: 'http://localhost:5180', changeOrigin: true } },
  },
})
