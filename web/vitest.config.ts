import { defineConfig } from 'vitest/config'
import { svelte } from '@sveltejs/vite-plugin-svelte'
import { svelteTesting } from '@testing-library/svelte/vite'

// Testes da interface: jsdom (DOM simulado), fetch falso por teste e cobertura do código em src/.
export default defineConfig({
  plugins: [svelte(), svelteTesting()],
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    setupFiles: ['src/test/setup.ts'],
    restoreMocks: true,
    unstubGlobals: true,
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,svelte}'],
      exclude: ['src/**/*.test.ts', 'src/test/**', 'src/vite-env.d.ts', 'src/lib/types.ts'],
      reporter: ['text-summary', 'json-summary', 'cobertura', 'html'],
      reportsDirectory: 'coverage',
      // A pipeline falha se a cobertura cair abaixo disso.
      thresholds: { lines: 95, statements: 95, functions: 95, branches: 85 },
    },
  },
})
