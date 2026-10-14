/**
 * Cliente de Server-Sent Events sobre fetch: permite cabeçalho Authorization (o EventSource não),
 * detecta 401 diretamente e reconecta com backoff. Um watchdog derruba conexões "zumbis"
 * (comuns no celular ao trocar de rede): o servidor envia dados a cada segundo.
 */
export type StreamState = 'open' | 'closed' | 'unauthorized'

export function openStream(
  url: string,
  headers: Record<string, string>,
  onEvent: (event: string, data: string) => void,
  onState: (s: StreamState) => void,
): () => void {
  let stopped = false
  let ctrl: AbortController | undefined
  let delay = 500

  async function run() {
    while (!stopped) {
      ctrl = new AbortController()
      const c = ctrl
      let watchdog: ReturnType<typeof setTimeout> | undefined
      const kick = () => {
        clearTimeout(watchdog)
        watchdog = setTimeout(() => c.abort(), 10_000)
      }
      try {
        kick()
        const res = await fetch(url, { headers, signal: c.signal, cache: 'no-store' })
        if (res.status === 401) {
          clearTimeout(watchdog)
          onState('unauthorized')
          return
        }
        if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)
        onState('open')
        delay = 500

        const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
        let buf = ''
        for (;;) {
          const { value, done } = await reader.read()
          if (done) break
          kick()
          buf += value
          let i: number
          while ((i = buf.indexOf('\n\n')) >= 0) {
            const block = buf.slice(0, i)
            buf = buf.slice(i + 2)
            let event = 'message'
            let data = ''
            for (const line of block.split('\n')) {
              if (line.startsWith('event: ')) event = line.slice(7)
              else if (line.startsWith('data: ')) data += line.slice(6)
            }
            if (data) onEvent(event, data)
          }
        }
      } catch {
        /* rede caiu, abortado ou servidor reiniciando */
      }
      clearTimeout(watchdog)
      if (stopped) return
      onState('closed')
      await new Promise((r) => setTimeout(r, delay))
      delay = Math.min(delay * 2, 8000)
    }
  }

  run()
  return () => {
    stopped = true
    ctrl?.abort()
  }
}
