import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// "vite build" → interface servida pelo BUREAU (server/wwwroot)
// "vite build --mode app" → interface embutida no app Android/iOS (dist-app, usada pelo Capacitor)
export default defineConfig(({ mode }) => ({
  plugins: [svelte()],
  build: {
    outDir: mode === 'app' ? 'dist-app' : '../server/wwwroot',
    emptyOutDir: true,
    target: 'es2022',
    cssCodeSplit: false,
    modulePreload: { polyfill: false },
  },
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:8420' },
  },
}))
