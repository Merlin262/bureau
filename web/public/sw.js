// Service worker: o app abre instantaneamente (cache do "shell"), a API nunca é cacheada.
const CACHE = 'bureau-v2'

self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  )
})

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url)
  if (e.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api/')) return

  // Assets com hash: cache primeiro (nunca mudam).
  if (url.pathname.startsWith('/assets/')) {
    e.respondWith(
      caches.match(e.request).then((hit) => hit || fetch(e.request).then((res) => {
        const copy = res.clone()
        caches.open(CACHE).then((c) => c.put(e.request, copy))
        return res
      })),
    )
    return
  }

  // Navegação/demais: rede primeiro, cache como reserva (offline).
  e.respondWith(
    fetch(e.request).then((res) => {
      if (res.ok) {
        const copy = res.clone()
        caches.open(CACHE).then((c) => c.put(e.request, copy))
      }
      return res
    }).catch(() => caches.match(e.request).then((hit) => hit || caches.match('/'))),
  )
})

// ── Notificações push (Web Push) ──────────────────────────
self.addEventListener('push', (e) => {
  let a = { title: 'BUREAU', body: '', view: 'painel', kind: 'info', level: 'warn' }
  try { a = { ...a, ...e.data.json() } } catch { /* payload vazio */ }
  e.waitUntil(
    self.registration.showNotification(a.title, {
      body: a.body,
      icon: '/icon-192.png',
      badge: '/badge-96.png',
      tag: a.kind,
      renotify: true,
      requireInteraction: a.level === 'crit',
      data: { view: a.view },
    }),
  )
})

self.addEventListener('notificationclick', (e) => {
  e.notification.close()
  const url = `/#/${e.notification.data?.view ?? 'painel'}`
  e.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((list) => {
      for (const c of list) {
        if ('focus' in c) {
          c.navigate(url)
          return c.focus()
        }
      }
      return self.clients.openWindow(url)
    }),
  )
})
