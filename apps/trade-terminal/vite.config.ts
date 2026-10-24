import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api/deribit-terminal': {
        target: 'http://127.0.0.1:5058',
        changeOrigin: false
      }
    }
  }
});
