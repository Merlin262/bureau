# BUREAU

**Sistema de controle doméstico** — um painel para administrar o seu servidor Linux caseiro (aquele notebook antigo), os serviços que rodam nele e a sua rede.

```
 ■ BUREAU // velho-thinkpad                                   ■ SINAL ESTÁVEL  23:14:07
 ─────────────────────────────────────────────────────────────────────────────────────
  01  PAINEL          01 // VISÃO GERAL
  02  SERVIÇOS     !  PAINEL
  03  CONTÊINERES
  04  PI-HOLE         PROCESSADOR 21%   MEMÓRIA 42%   TRÁFEGO 57 KB/s ↓   48°C · 2d 4h
  05  REDE
  06  SISTEMA
```

## O que faz

| Módulo | Recursos |
|---|---|
| **Painel** | CPU (total e por núcleo), RAM/swap, tráfego, temperatura, uptime, **bateria do notebook como nobreak** (alerta de queda de energia), resumo de tudo com alertas |
| **Serviços** | Serviços systemd monitorados: estado, memória, tempo ativo; iniciar/parar/reiniciar; logs do journal ao vivo; escolha quais monitorar pela interface |
| **Contêineres** | Docker: estado, portas, iniciar/parar/reiniciar, logs |
| **Pi-hole** | Estatísticas (v6), pausar bloqueio (5 min / 30 min / 1 h / indefinido), gráfico de 24 h, domínios mais bloqueados |
| **Rede** | Dispositivos da LAN (ARP + nomes do Pi-hole + DNS reverso), online/offline, **Wake-on-LAN** |
| **Sistema** | Informações do host, discos, reiniciar/desligar (segure para confirmar), notificações e aparelhos conectados, sons de interface |
| **Alertas** | Push quando um serviço ou contêiner cai, falta energia (bateria do notebook), o disco enche, a CPU esquenta ou o Pi-hole para — com registro de ocorrências no painel |

**App mobile:** o mesmo painel como app Android (APK nas releases), com push nativo. No iPhone, funciona como PWA com Web Push (o projeto iOS nativo também está incluído). Guia completo: [docs/app-mobile.md](docs/app-mobile.md).

Atalhos de teclado: `1`–`6` seções · `Q`/`E` anterior/próxima · `Esc` fecha.

## Instalação

No servidor (Debian 12+, Ubuntu 22.04+ ou derivados, x64 ou ARM64):

```bash
curl -fsSL https://raw.githubusercontent.com/Merlin262/bureau/main/install.sh | sudo bash
```

O instalador:

1. baixa o binário da última release;
2. cria o usuário de serviço `bureau` (sem login, nos grupos `docker` e `systemd-journal`);
3. detecta Pi-hole e serviços comuns e gera `/etc/bureau/bureau.json`;
4. pede a senha de acesso;
5. instala regras polkit (para gerenciar serviços **sem rodar como root**) e o serviço systemd com sandbox;
6. opcionalmente instala o **Tailscale** e publica o painel com HTTPS.

Para **atualizar**, rode o mesmo comando — a configuração é preservada. Para remover: `sudo ./uninstall.sh`.

> Fez um fork? Instale do seu repositório com `sudo bash install.sh --repo seu-usuario/bureau` (ou troque `Merlin262` no README e no `install.sh`). Cada tag `v*` faz o GitHub Actions compilar e publicar os binários e o APK.

### Acesso pelo celular e de fora de casa

A forma recomendada é o **[Tailscale](https://tailscale.com)** (grátis para uso pessoal):

- nenhuma porta aberta no roteador — o painel não fica exposto à internet;
- `tailscale serve` dá um endereço `https://seu-servidor.sua-tailnet.ts.net` com certificado válido;
- com HTTPS, o BUREAU pode ser **instalado como app** no celular (Android: menu → *Instalar app*; iPhone: Compartilhar → *Adicionar à Tela de Início*). Ele abre em tela cheia e carrega instantaneamente do cache.

Dentro de casa, também funciona direto pelo IP: `http://192.168.x.x:8420`.

### App Android e notificações

Baixe o `bureau-android.apk` da release, informe o endereço do servidor e a senha. Para receber push, configure ao menos um canal (Firebase, Web Push ou ntfy) — passo a passo em [docs/app-mobile.md](docs/app-mobile.md).

## Configuração

`/etc/bureau/bureau.json` (exemplo completo em [`deploy/bureau.example.json`](deploy/bureau.example.json)):

| Chave | Padrão | Descrição |
|---|---|---|
| `Port` | `8420` | Porta HTTP |
| `PasswordHash` | — | Gerado por `bureau set-password` |
| `Services` | `[]` | Serviços monitorados inicialmente (depois edite pela interface) |
| `AllowPower` | `true` | Permite reiniciar/desligar pela interface |
| `Docker.Enabled` / `Docker.Socket` | `true` / `/var/run/docker.sock` | |
| `PiHole.Enabled` / `Url` / `Password` | `false` / `http://127.0.0.1` / — | Use uma *app password* do Pi-hole v6 |
| `Alerts.Enabled` | `true` | Liga/desliga o envio de push |
| `Alerts.DiskPercent` / `TempCelsius` / `BatteryCritical` | `90` / `85` / `20` | Limites dos alertas |
| `Push.FcmServiceAccount` | `/etc/bureau/firebase.json` | Conta de serviço do Firebase (push no app Android) |
| `Push.WebPushSubject` | `mailto:bureau@example.com` | Seu e-mail (exigido pelo Web Push/Apple) |
| `Push.NtfyUrl` / `NtfyToken` | — | Tópico do ntfy, alternativa sem Firebase |

Trocar a senha:

```bash
sudo BUREAU_CONFIG=/etc/bureau/bureau.json /opt/bureau/bureau set-password && sudo systemctl restart bureau
```

## Por que é rápido

- **Backend em C# / .NET 10 com Native AOT** — binário nativo, sem JIT: inicia quase instantaneamente e consome pouca memória. Métricas lidas direto de `/proc` e `/sys` (sem processos externos); Docker via socket unix; JSON por source generator (sem reflexão).
- **Tudo empurrado em tempo real (SSE)** — cada módulo publica seu estado num barramento em memória. Ao conectar, o navegador recebe *todo* o estado de uma vez; depois, só mudanças. Trocar de tela **nunca** espera a rede: os dados já estão lá.
- **Coletores sob demanda** — systemd/Docker/Pi-hole só são consultados enquanto há alguém olhando; quando um cliente conecta, os coletores acordam na hora.
- **UI otimista** — ao clicar em uma ação, o estado muda imediatamente; o servidor confirma em seguida.
- **Frontend em Svelte 5** — compilado, sem runtime pesado: ~35 KB de JS comprimido. Assets pré-comprimidos com cache imutável e service worker: a segunda abertura é instantânea.

## Segurança

- Senha com PBKDF2-SHA256 (210 mil iterações), sessão em cookie `HttpOnly` + `SameSite=Strict` (30 dias).
- Limite de 5 tentativas de login por minuto por IP; proteção CSRF por cabeçalho; CSP restritiva.
- O processo roda como usuário sem privilégios, com `ProtectSystem=strict`; a escalada é limitada pelo polkit a gerenciar unidades systemd e reiniciar/desligar.
- Ações só aceitam serviços que estão na lista de monitorados; nada é passado a um shell.
- O app mobile usa um token por aparelho (só o hash fica no servidor), revogável em *Sistema → Aparelhos*; o CORS só libera as origens do app.
- Não exponha a porta 8420 diretamente na internet — use o Tailscale (ou outro túnel/VPN).

## Desenvolvimento

Requisitos: .NET 10 SDK e Node 20+. Funciona em Windows/macOS: fora do Linux, o backend gera **dados simulados**.

```bash
cd server && dotnet run          # API em http://localhost:8420  (senha de dev: bureau)
```

```bash
cd web && npm install && npm run dev   # interface com hot reload em http://localhost:5173
```

Build de produção para Linux (requer `clang` e `zlib1g-dev` para o Native AOT):

```bash
./build.sh && sudo ./install.sh --local dist/linux-x64
```

### Estrutura

```
server/                  Backend C# (ASP.NET Core minimal API, Native AOT)
  Api.cs                 Endpoints REST + stream SSE
  Infra/                 EventHub (barramento), segurança, arquivos estáticos, processos
  Modules/               SystemMonitor/LinuxProbe, Services (systemd), Docker, PiHole, Network, AlertEngine
  Push/                  Envio de push: Firebase (FCM), Web Push (VAPID) e ntfy
web/                     Frontend Svelte 5 + Vite → server/wwwroot (web) e dist-app (app)
  android/ · ios/        Projetos nativos do app (Capacitor)
docs/app-mobile.md       App mobile, push e como compilar
  src/lib/store.svelte.ts  Estado global alimentado pelo SSE
  src/views/             Telas · src/components/ peças da interface
install.sh / uninstall.sh / build.sh
.github/workflows/       CI e release (binários x64 e ARM64)
```

### Adicionando um módulo

1. Crie `server/Modules/MeuModulo.cs` com um `RefreshAsync()` que chama `hub.Publish("meutopico", estado, AppJson.Default.MeuEstado)`.
2. Registre o tipo em `Models.cs` (`[JsonSerializable]`), o singleton em `Program.cs` e um loop em `Collectors`.
3. No frontend, adicione `on('meutopico', ...)` em `store.svelte.ts` e uma tela em `views/`.

## Roteiro

- [x] App mobile (Android; iOS via Capacitor/PWA)
- [x] Notificações push (serviço caiu, queda de energia, disco cheio…)
- [ ] Push nativo no iOS (APNs)
- [ ] Mais integrações: Home Assistant, Jellyfin, backups, atualizações do apt
- [ ] Histórico de métricas persistido (dias/semanas)

## Licença

MIT
