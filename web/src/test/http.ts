/**
 * fetch falso: cada teste registra respostas por "MÉTODO /caminho" (sem a query).
 * O valor pode ser um objeto (vira JSON 200), uma Response, ou uma função que recebe o corpo enviado.
 */
type Reply = unknown | Response | ((body: unknown, url: URL) => unknown | Response | Promise<unknown>)

export interface Call { method: string; path: string; query: string; body: unknown; headers: Record<string, string> }

class FakeHttp {
  routes = new Map<string, Reply>()
  calls: Call[] = []

  reset() {
    this.routes.clear()
    this.calls = []
  }

  on(route: string, reply: Reply) {
    this.routes.set(route, reply)
    return this
  }

  sent(route: string) {
    const [method, path] = route.split(' ')
    return this.calls.filter((c) => c.method === method && c.path === path)
  }

  fetch = async (input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> => {
    const url = new URL(String(input), 'http://localhost')
    const method = (init.method ?? 'GET').toUpperCase()
    const body = typeof init.body === 'string' ? JSON.parse(init.body) : init.body
    this.calls.push({ method, path: url.pathname, query: url.search, body, headers: { ...(init.headers as Record<string, string>) } })
    const key = `${method} ${url.pathname}`
    if (!this.routes.has(key)) return json({ ok: false, message: `sem rota: ${key}` }, 404)
    let r = this.routes.get(key)
    if (typeof r === 'function') r = await (r as (b: unknown, u: URL) => unknown)(body, url)
    if (r instanceof Response) return r
    if (r instanceof Error) throw r
    return json(r)
  }
}

export const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } })

export const http = new FakeHttp()
