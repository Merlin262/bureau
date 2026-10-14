/// <reference types="svelte" />
/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** "1" quando o app Android foi compilado com google-services.json (Firebase). */
  readonly VITE_FCM?: string
}
