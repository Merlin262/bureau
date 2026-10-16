/**
 * Categoria de um domínio pelo "domínio principal" (ex.: rr3---sn.googlevideo.com → googlevideo.com).
 * Lista curta e local: cobre o que mais aparece numa casa; o resto fica em "Outros".
 */
export type Category = 'streaming' | 'social' | 'games' | 'work' | 'shopping' | 'ads' | 'system' | 'dns' | 'other'

export const CATEGORY_LABEL: Record<Category, string> = {
  streaming: 'Vídeo e música',
  social: 'Redes sociais e mensagens',
  games: 'Jogos',
  work: 'Trabalho e estudo',
  shopping: 'Compras e bancos',
  ads: 'Anúncios e rastreamento',
  system: 'Sistema e atualizações',
  dns: 'DNS seguro',
  other: 'Outros',
}

const MAP: [Category, string[]][] = [
  ['streaming', ['youtube.com', 'googlevideo.com', 'ytimg.com', 'youtu.be', 'netflix.com', 'nflxvideo.net', 'nflximg.net', 'nflxso.net',
    'primevideo.com', 'aiv-cdn.net', 'disneyplus.com', 'disney-plus.net', 'bamgrid.com', 'max.com', 'hbomax.com', 'globoplay.globo.com',
    'globo.com', 'spotify.com', 'scdn.co', 'spotifycdn.com', 'deezer.com', 'twitch.tv', 'ttvnw.net', 'jtvnw.net', 'crunchyroll.com',
    'paramountplus.com', 'pluto.tv', 'appletv.apple.com', 'music.apple.com', 'soundcloud.com', 'kick.com', 'jellyfin.org']],
  ['social', ['instagram.com', 'cdninstagram.com', 'facebook.com', 'fbcdn.net', 'facebook.net', 'fb.com', 'messenger.com', 'whatsapp.com',
    'whatsapp.net', 'tiktok.com', 'tiktokcdn.com', 'tiktokv.com', 'byteoversea.com', 'ibytedtos.com', 'twitter.com', 'x.com', 'twimg.com',
    't.co', 'snapchat.com', 'sc-cdn.net', 'reddit.com', 'redd.it', 'redditmedia.com', 'discord.com', 'discord.gg', 'discordapp.com',
    'discordapp.net', 'telegram.org', 't.me', 'linkedin.com', 'licdn.com', 'pinterest.com', 'pinimg.com', 'threads.net', 'kwai.com', 'bsky.app']],
  ['games', ['steampowered.com', 'steamcommunity.com', 'steamstatic.com', 'steamserver.net', 'epicgames.com', 'unrealengine.com',
    'xboxlive.com', 'xbox.com', 'playstation.com', 'playstation.net', 'sonyentertainmentnetwork.com', 'riotgames.com', 'leagueoflegends.com',
    'roblox.com', 'rbxcdn.com', 'ea.com', 'origin.com', 'ubisoft.com', 'ubi.com', 'blizzard.com', 'battle.net', 'nintendo.net',
    'nintendo.com', 'minecraft.net', 'mojang.com', 'garena.com', 'freefiremobile.com', 'supercell.com', 'gamepass.com']],
  ['work', ['office.com', 'office.net', 'office365.com', 'microsoftonline.com', 'sharepoint.com', 'teams.microsoft.com', 'outlook.com',
    'live.com', 'onedrive.com', 'zoom.us', 'slack.com', 'slack-edge.com', 'notion.so', 'github.com', 'githubusercontent.com', 'gitlab.com',
    'atlassian.net', 'atlassian.com', 'docs.google.com', 'drive.google.com', 'meet.google.com', 'classroom.google.com', 'webex.com',
    'figma.com', 'dropbox.com', 'stackoverflow.com', 'chatgpt.com', 'openai.com', 'claude.ai', 'anthropic.com', 'canva.com', 'gov.br']],
  ['shopping', ['mercadolivre.com.br', 'mercadolibre.com', 'mlstatic.com', 'mercadopago.com', 'amazon.com', 'amazon.com.br', 'media-amazon.com',
    'shopee.com.br', 'susercontent.com', 'aliexpress.com', 'alicdn.com', 'magazineluiza.com.br', 'magalu.com', 'americanas.com.br',
    'ifood.com.br', 'nubank.com.br', 'nu.com.br', 'itau.com.br', 'bradesco.com.br', 'bb.com.br', 'caixa.gov.br', 'santander.com.br',
    'inter.co', 'picpay.com', 'paypal.com', 'uber.com', '99app.com', 'rappi.com.br', 'shein.com', 'temu.com']],
  ['ads', ['doubleclick.net', 'googlesyndication.com', 'googleadservices.com', 'google-analytics.com', 'googletagmanager.com',
    'googletagservices.com', 'app-measurement.com', 'adservice.google.com', 'scorecardresearch.com', 'criteo.com', 'criteo.net',
    'taboola.com', 'outbrain.com', 'adnxs.com', 'amazon-adsystem.com', 'adsrvr.org', 'rubiconproject.com', 'pubmatic.com', 'appsflyer.com',
    'adjust.com', 'branch.io', 'crashlytics.com', 'hotjar.com', 'mixpanel.com', 'segment.io', 'unity3d.com', 'applovin.com', 'moatads.com',
    'clarity.ms', 'bat.bing.com', 'samsungads.com', 'ads.tiktok.com']],
  ['system', ['windowsupdate.com', 'update.microsoft.com', 'microsoft.com', 'msftconnecttest.com', 'msftncsi.com', 'windows.com',
    'windows.net', 'msn.com', 'bing.com', 'apple.com', 'icloud.com', 'mzstatic.com', 'aaplimg.com', 'apple-dns.net', 'gstatic.com',
    'googleapis.com', 'android.com', 'gvt1.com', 'gvt2.com', 'play.googleapis.com', 'samsung.com', 'samsungcloud.com', 'samsungosp.com',
    'xiaomi.com', 'miui.com', 'mi.com', 'tcl.com', 'lgtvcommon.com', 'lgsmartad.com', 'roku.com', 'ubuntu.com', 'canonical.com',
    'snapcraft.io', 'docker.com', 'docker.io', 'pool.ntp.org', 'ntp.org', 'akamaiedge.net', 'akamaihd.net', 'cloudfront.net', 'tuya.com',
    'tuyaus.com', 'amazonaws.com']],
]

const INDEX = new Map<string, Category>()
for (const [cat, list] of MAP) for (const d of list) INDEX.set(d, cat)

/** Domínio principal: últimas 2 partes, ou 3 para domínios de país com 2º nível (com.br, gov.br, co.uk…). */
export function baseDomain(d: string): string {
  const p = d.toLowerCase().replace(/\.$/, '').split('.')
  const n = p.length >= 3 && p[p.length - 1].length === 2 && p[p.length - 2].length <= 3 ? 3 : 2
  return p.slice(-n).join('.')
}

/** Categoria do domínio: tenta o nome completo e depois os sufixos (teams.microsoft.com antes de microsoft.com). */
export function categorize(domain: string, doh: ReadonlySet<string> = new Set()): Category {
  const d = domain.toLowerCase().replace(/\.$/, '')
  if (doh.has(d)) return 'dns'
  const parts = d.split('.')
  for (let i = 0; i < parts.length - 1; i++) {
    const hit = INDEX.get(parts.slice(i).join('.'))
    if (hit) return hit
  }
  return 'other'
}
