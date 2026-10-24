import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Automatic authorization exists ONLY for GETs from the localhost React
// development server. The key stays in this Node process and never appears
// in browser JavaScript, HTML, storage, or client-side network headers.
const readonlyOperatorKey = process.env.TRADEOPS_TERMINAL_OPERATOR_KEY;

export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api/deribit-terminal': {
        target: 'http://127.0.0.1:5058',
        changeOrigin: false,
        configure(proxy) {
          proxy.on('proxyReq', (proxyReq, request) => {
            if (request.method === 'GET' &&
                typeof readonlyOperatorKey === 'string' &&
                readonlyOperatorKey.length >= 32) {
              proxyReq.setHeader('X-TradeOps-Terminal-Key', readonlyOperatorKey);
            }
          });
        }
      }
    }
  }
});
