# App mobile e notificações push

O app do BUREAU é a **mesma interface** do painel web, empacotada com [Capacitor](https://capacitorjs.com) para Android (e iOS). Tudo o que existe no navegador existe no app — e o app ganha push nativo, vibração nas ações, botão voltar do Android e abertura instantânea (a interface vem embutida; só os dados trafegam).

## Instalar no Android

1. Baixe o `bureau-android.apk` da [última release](https://github.com/Merlin262/bureau/releases/latest) (ou compile — veja abaixo).
2. No celular, abra o arquivo e permita "instalar apps desta fonte".
3. Abra o app e informe o **servidor**:
   - em casa: `192.168.0.5` (o IP do servidor; a porta 8420 é assumida);
   - de qualquer lugar: o endereço do Tailscale, ex. `servidor.sua-tailnet.ts.net` (o app do Tailscale precisa estar ligado no celular).
4. Digite a senha. O app recebe um **token próprio** deste aparelho — revogável a qualquer momento em *Sistema → Aparelhos*.
5. Em *Sistema → Notificações*, toque em **Ativar** e depois em **Testar**.

## Alertas enviados

| Alerta | Quando |
|---|---|
| **Serviço caiu / voltou** | Um serviço monitorado sai de *ativo* para falha/parado (ou entra em loop de reinício) |
| **Contêiner parou / voltou** | Um contêiner que estava rodando sai, morre ou fica reiniciando |
| **Queda de energia / energia restabelecida** | A bateria do notebook começa a descarregar (15 s de confirmação) |
| **Bateria crítica** | Durante a queda, bateria ≤ `Alerts.BatteryCritical` (20%) |
| **Disco quase cheio** | Uso ≥ `Alerts.DiskPercent` (90%) |
| **Temperatura alta** | CPU ≥ `Alerts.TempCelsius` (85 °C) por mais de 1 minuto |
| **Pi-hole fora do ar / voltou** | Três verificações seguidas sem resposta |

Ações feitas pela própria interface (ex.: você clicou em *Parar*) **não** geram alerta. O mesmo alerta não se repete por 5 minutos. Os últimos 50 ficam em *Painel → Registro de ocorrências*.

## Canais de push

Escolha um ou mais — todos recebem os mesmos alertas.

### 1. Firebase (push nativo no app Android)

O Android só entrega push nativo via Firebase Cloud Messaging. É gratuito; leva uns 5 minutos:

1. Em [console.firebase.google.com](https://console.firebase.google.com), crie um projeto (pode desligar o Analytics).
2. **Adicionar app → Android**, com o nome de pacote igual ao `appId` de [`web/capacitor.config.ts`](../web/capacitor.config.ts) (padrão `dev.bureau.app`). Baixe o **`google-services.json`**.
3. Coloque-o no app:
   - build local: copie para `web/android/app/google-services.json` e compile com `VITE_FCM=1` (veja abaixo);
   - build pelo GitHub: crie o segredo `GOOGLE_SERVICES_JSON` com o conteúdo em base64 (`base64 -w0 google-services.json`).
4. No Firebase: **Configurações do projeto → Contas de serviço → Gerar nova chave privada**. No servidor:
   ```bash
   sudo cp chave-baixada.json /etc/bureau/firebase.json
   sudo chown root:bureau /etc/bureau/firebase.json && sudo chmod 640 /etc/bureau/firebase.json
   sudo systemctl restart bureau
   ```
5. Em *Sistema → Notificações* o indicador **FIREBASE** fica aceso. Ative e teste no app.

> Os dois arquivos do Firebase são segredos: já estão no `.gitignore`, nunca os publique.

### 2. Web Push (navegador e iPhone, sem conta nenhuma)

Funciona no Chrome/Edge/Firefox e no **iPhone (iOS 16.4+)**:

1. Abra o BUREAU por **HTTPS** — o endereço do `tailscale serve` (`https://servidor.sua-tailnet.ts.net`).
2. iPhone: Safari → Compartilhar → **Adicionar à Tela de Início**, e abra pelo ícone (no iOS o push só funciona assim).
3. *Sistema → Notificações → Ativar*.

Configure seu e-mail em `Push.WebPushSubject` (`mailto:voce@exemplo.com`) — a Apple rejeita endereços inválidos. As chaves VAPID são geradas sozinhas em `/var/lib/bureau/vapid.json`.

### 3. ntfy (alternativa sem Firebase, Android e iOS)

1. Instale o app [ntfy](https://ntfy.sh) e assine um tópico com nome difícil de adivinhar, ex. `bureau-7f3k9q`.
2. Em `/etc/bureau/bureau.json`: `"Push": { "NtfyUrl": "https://ntfy.sh/bureau-7f3k9q" }` (aceita servidor ntfy próprio e `NtfyToken`).
3. `sudo systemctl restart bureau` e use **Testar**.

## Compilar o app

### Android (Windows, macOS ou Linux)

Requisitos: [Android Studio](https://developer.android.com/studio) (traz o JDK 21 e o SDK) e Node 20+.

```bash
cd web
npm install
npm run android:sync        # compila a interface (modo app) e copia para o projeto Android
npx cap open android        # abre no Android Studio → ▶ Run (celular via USB ou emulador)
```

Com Firebase: coloque o `google-services.json` em `web/android/app/` e compile a interface com `VITE_FCM=1` (no PowerShell: `$env:VITE_FCM=1; npm run android:sync`).

APK pela linha de comando: `cd web/android && ./gradlew assembleDebug` → `app/build/outputs/apk/debug/app-debug.apk`.

**APK assinado (para atualizar sem desinstalar):** gere uma keystore uma única vez e guarde-a bem:

```bash
keytool -genkeypair -v -keystore release.jks -keyalg RSA -keysize 2048 -validity 10000 -alias bureau
```

Localmente, crie `web/android/keystore.properties` (`storeFile=../release.jks`, `storePassword=…`, `keyAlias=bureau`, `keyPassword=…`) e rode `./gradlew assembleRelease`. No GitHub, cadastre os segredos `KEYSTORE_BASE64`, `KEYSTORE_PASSWORD`, `KEY_ALIAS` e `KEY_PASSWORD` — o workflow [`android.yml`](../.github/workflows/android.yml) passa a publicar o APK assinado a cada tag.

### iOS

Requer um Mac com Xcode: `cd web && npm run ios:sync && npx cap open ios`. Com um Apple ID gratuito o app roda no seu iPhone por 7 dias a cada instalação; a conta paga da Apple (US$ 99/ano) remove esse limite. O push **nativo** no iOS ainda não está implementado no app — no iPhone, use o Web Push (PWA) ou o ntfy, que funcionam sem custo.

## Como funciona (para quem for mexer)

- `web/src/lib/platform.ts` — detecta o app (`Capacitor.isNativePlatform()`), guarda servidor + token (Preferences).
- `web/src/lib/sse.ts` — stream em tempo real via `fetch` (aceita `Authorization`, reconecta com backoff, watchdog de 10 s).
- `web/src/lib/push.ts` — registro FCM (app) ou Web Push (navegador).
- `server/Modules/AlertEngine.cs` — regras e transições; `server/Push/` — FCM (HTTP v1), Web Push (RFC 8291/8292) e ntfy.
- Autenticação do app: `POST /api/auth/token` → token Bearer (só o hash SHA-256 fica no servidor). O CORS só libera as origens do Capacitor (`https://localhost`, `capacitor://localhost`).
- Com ninguém olhando, os coletores continuam rodando em ritmo lento (15–30 s) para que os alertas funcionem 24 h.
