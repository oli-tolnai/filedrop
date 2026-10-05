import type { CapacitorConfig } from '@capacitor/cli';

const config: CapacitorConfig = {
  appId: 'hu.tolnaioli.filedrop',
  appName: 'FileDrop',
  webDir: 'dist/filedrop-web/browser',
  server: {
    url: 'http://192.168.0.34:8090',
    cleartext: true,
    allowNavigation: ['192.168.0.34'],
  },
  android: {
    allowMixedContent: false,
  },
};

export default config;
