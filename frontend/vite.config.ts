import { defineConfig } from 'vite';

// JSX is compiled by Vite's built-in esbuild (automatic runtime) - no extra plugin needed.
export default defineConfig({
  base: './',
  esbuild: { jsx: 'automatic' },
  // the demo data lives in ../seed (shared with the .NET seeder)
  server: { port: 5173, fs: { allow: ['..'] } },
  build: { outDir: 'dist', chunkSizeWarningLimit: 900 },
});
