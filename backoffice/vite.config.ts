import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// El backoffice se sirve bajo /backoffice/ y habla directo con la API Core (/api).
export default defineConfig({
  base: '/backoffice/',
  plugins: [react()],
  server: {
    port: 5174,
    proxy: {
      '/api': 'http://localhost:5080',
      '/fhir': 'http://localhost:5080',
    },
  },
});
