import type { CapacitorConfig } from '@capacitor/cli'

const config: CapacitorConfig = {
  // Troque pelo seu identificador (ex.: com.seunome.bureau) — deve ser igual ao do projeto no Firebase.
  appId: 'dev.bureau.app',
  appName: 'Bureau',
  webDir: 'dist-app',
  backgroundColor: '#0b0b0c',
  server: {
    androidScheme: 'https',
    // Permite falar com o servidor por http:// na rede local (o Tailscale usa https).
    cleartext: true,
  },
  android: {
    allowMixedContent: true,
  },
  plugins: {
    PushNotifications: {
      presentationOptions: ['badge', 'sound', 'alert'],
    },
  },
}

export default config
