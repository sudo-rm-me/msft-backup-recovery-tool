import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The production build is served by the ASP.NET Core API from its wwwroot folder.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../Mbr.Api/wwwroot', emptyOutDir: true, chunkSizeWarningLimit: 2000 },
  server: {
    port: 5173,
    proxy: { '/api': { target: 'http://localhost:5180', changeOrigin: true } },
  },
})
