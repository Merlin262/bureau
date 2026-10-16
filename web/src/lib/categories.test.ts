import { describe, expect, it } from 'vitest'
import { CATEGORY_LABEL, baseDomain, categorize } from './categories'

describe('categorias de domínio', () => {
  it('domínio principal', () => {
    expect(baseDomain('rr3---sn-x1x.googlevideo.com')).toBe('googlevideo.com')
    expect(baseDomain('ads.mercadolivre.com.br')).toBe('mercadolivre.com.br')
    expect(baseDomain('WWW.Example.COM.')).toBe('example.com')
    expect(baseDomain('localhost')).toBe('localhost')
  })

  it('classifica pelo nome completo e pelos sufixos', () => {
    expect(categorize('rr3---sn-x1x.googlevideo.com')).toBe('streaming')
    expect(categorize('scontent.cdninstagram.com')).toBe('social')
    expect(categorize('teams.microsoft.com')).toBe('work') // mais específico vence
    expect(categorize('telemetry.microsoft.com')).toBe('system')
    expect(categorize('store.steampowered.com')).toBe('games')
    expect(categorize('www.nubank.com.br')).toBe('shopping')
    expect(categorize('stats.g.doubleclick.net')).toBe('ads')
    expect(categorize('algum-site.xyz')).toBe('other')
    expect(categorize('dns.google', new Set(['dns.google']))).toBe('dns')
    expect(categorize('DNS.Google.', new Set(['dns.google']))).toBe('dns')
  })

  it('todas as categorias têm rótulo', () => {
    for (const c of ['streaming', 'social', 'games', 'work', 'shopping', 'ads', 'system', 'dns', 'other'] as const)
      expect(CATEGORY_LABEL[c]).toBeTruthy()
  })
})
