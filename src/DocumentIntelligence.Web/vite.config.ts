import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import mkcert from 'vite-plugin-mkcert'

export default defineConfig({
  plugins: [react(), tailwindcss(), mkcert()],
  server: {
    host: process.env.VITE_HOST ?? '127.0.0.1',
    port: parseInt(process.env.VITE_PORT ?? '5300'),
    https: {},
    strictPort: true,
    proxy: {
      '/api': {
        target:
          process.env.services__apiservice__https__0 ??
          process.env.services__apiservice__http__0 ??
          'http://localhost:5000',
        changeOrigin: true,
        secure: false,
      },
      '/hubs': {
        target:
          process.env.services__apiservice__https__0 ??
          process.env.services__apiservice__http__0 ??
          'http://localhost:5000',
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
})
