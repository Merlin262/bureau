import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/svelte'
import { createRawSnippet } from 'svelte'
import Sq from './Sq.svelte'
import Meter from './Meter.svelte'
import Spark from './Spark.svelte'
import Panel from './Panel.svelte'
import Title from './Title.svelte'
import Modal from './Modal.svelte'
import HoldButton from './HoldButton.svelte'
import Toasts from './Toasts.svelte'
import LogViewer from './LogViewer.svelte'
import HistoryChart from './HistoryChart.svelte'
import LockScreen from './LockScreen.svelte'
import { store } from '../lib/store.svelte'
import { flush } from '../test/util'

const html = (s: string) => createRawSnippet(() => ({ render: () => s }))

describe('componentes simples', () => {
  it('Sq', () => {
    const { container } = render(Sq, { state: 'fail' })
    expect(container.querySelector('.sq.fail')).toBeTruthy()
  })

  it('Meter limita o valor e marca quente', () => {
    const { container, rerender } = render(Meter, { value: 150, hot: 90 })
    const m = container.querySelector('[role=meter]')!
    expect(m.getAttribute('aria-valuenow')).toBe('100')
    expect(m.classList.contains('hot')).toBe(true)
    rerender({ value: Number.NaN })
    expect(m.getAttribute('aria-valuenow')).toBe('0')
  })

  it('Spark desenha a partir de 2 pontos', async () => {
    const { container, rerender } = render(Spark, { values: [5] })
    expect(container.querySelector('.line')!.getAttribute('d')).toBe('')
    await rerender({ values: [1, 5, 200], max: 100, red: true })
    expect(container.querySelector('.line')!.getAttribute('d')).toMatch(/^M.*L.*L/)
    expect(container.querySelector('svg')!.classList.contains('red')).toBe(true)
    await rerender({ values: [0, 0], max: undefined })
    expect(container.querySelector('.area')!.getAttribute('d')).toContain('Z')
  })

  it('Panel com rótulo, lado e alerta', () => {
    const { container } = render(Panel, { label: 'CPU', alert: true, class: 'x', aside: html('<b>lado</b>'), children: html('<p>corpo</p>') })
    expect(container.querySelector('.panel.alert.x')).toBeTruthy()
    expect(screen.getByText('CPU')).toBeTruthy()
    expect(screen.getByText('lado')).toBeTruthy()
    expect(screen.getByText('corpo')).toBeTruthy()

    const plain = render(Panel, { children: html('<p>só</p>') })
    expect(plain.container.querySelector('header')).toBeNull()
  })

  it('Title', () => {
    render(Title, { index: '01', code: 'X', label: 'Painel', children: html('<button>ação</button>') })
    expect(screen.getByRole('heading').textContent).toBe('Painel')
    expect(screen.getByText('ação')).toBeTruthy()
  })

  it('Modal fecha por ESC, fundo e botão', async () => {
    const onclose = vi.fn()
    const { container } = render(Modal, { title: 'Teste', onclose, children: html('<p>dentro</p>') })
    expect(screen.getByRole('dialog', { name: 'Teste' })).toBeTruthy()
    await fireEvent.keyDown(window, { key: 'a' })
    expect(onclose).not.toHaveBeenCalled()
    await fireEvent.keyDown(window, { key: 'Escape' })
    await fireEvent.click(screen.getByText('dentro'))
    expect(onclose).toHaveBeenCalledTimes(1)
    await fireEvent.click(container.querySelector('.backdrop')!)
    await fireEvent.click(screen.getByText('[ESC] FECHAR'))
    expect(onclose).toHaveBeenCalledTimes(3)
  })

  it('Toasts', () => {
    store.toasts = [{ id: 1, kind: 'ok', text: 'feito' }, { id: 2, kind: 'err', text: 'falhou' }, { id: 3, kind: 'info', text: 'oi' }]
    render(Toasts)
    expect(screen.getByText('[OK]')).toBeTruthy()
    expect(screen.getByText('[FALHA]')).toBeTruthy()
    expect(screen.getByText('[INFO]')).toBeTruthy()
  })
})

describe('HoldButton', () => {
  it('confirma só segurando até o fim', async () => {
    const onconfirm = vi.fn()
    render(HoldButton, { label: 'Desligar', onconfirm, ms: 200 })
    const b = screen.getByRole('button')
    await fireEvent.pointerDown(b)
    await new Promise((r) => setTimeout(r, 20))
    await fireEvent.pointerUp(b)
    await new Promise((r) => setTimeout(r, 100))
    expect(onconfirm).not.toHaveBeenCalled()
    expect(b.textContent!.trim()).toBe('Desligar')

    await fireEvent.pointerDown(b)
    await new Promise((r) => setTimeout(r, 100))
    expect(b.textContent).toMatch(/SEGURE… \d+%/)
    await new Promise((r) => setTimeout(r, 250))
    expect(onconfirm).toHaveBeenCalledTimes(1)

    await fireEvent.keyDown(b, { key: 'Enter' })
    await new Promise((r) => setTimeout(r, 300))
    await fireEvent.keyUp(b)
    expect(onconfirm).toHaveBeenCalledTimes(2)
    await fireEvent.keyDown(b, { key: 'x' })
    await fireEvent.contextMenu(b)
  })

  it('desativado não faz nada', async () => {
    const onconfirm = vi.fn()
    render(HoldButton, { label: 'X', onconfirm, ms: 10, disabled: true })
    await fireEvent.pointerDown(screen.getByRole('button'))
    await new Promise((r) => setTimeout(r, 60))
    expect(onconfirm).not.toHaveBeenCalled()
  })
})

describe('LogViewer', () => {
  it('carrega, filtra, atualiza e ao vivo', async () => {
    vi.useFakeTimers()
    let n = 0
    const load = vi.fn(async () => ({ text: n++ === 0 ? 'linha erro\nlinha ok' : '' }))
    const { container } = render(LogViewer, { load })
    expect(container.querySelector('pre')!.textContent).toBe('CARREGANDO REGISTROS…')
    await vi.advanceTimersByTimeAsync(20)
    expect(container.querySelector('pre')!.textContent).toBe('linha erro\nlinha ok')

    await fireEvent.input(screen.getByPlaceholderText('Filtrar…'), { target: { value: 'ERRO' } })
    expect(container.querySelector('pre')!.textContent).toBe('linha erro')
    await fireEvent.input(screen.getByPlaceholderText('Filtrar…'), { target: { value: '' } })

    await fireEvent.click(screen.getByText('ATUALIZAR'))
    await vi.advanceTimersByTimeAsync(20)
    expect(container.querySelector('pre')!.textContent).toBe('(vazio)')

    await fireEvent.click(screen.getByText('○ AO VIVO'))
    await vi.advanceTimersByTimeAsync(5100)
    expect(load.mock.calls.length).toBeGreaterThanOrEqual(4)
    await fireEvent.click(screen.getByText('● AO VIVO'))
  })

  it('mostra o erro', async () => {
    const { container } = render(LogViewer, { load: async () => { throw new Error('sem acesso') } })
    await flush()
    expect(container.querySelector('pre')!.textContent).toBe('Error: sem acesso')
  })
})

describe('HistoryChart', () => {
  const fmt = (v: number) => `${v.toFixed(0)}%`

  it('média, pico, lacunas e valor sob o cursor', async () => {
    const t = [1700000000, 1700000060, 1700000120, 1700000180]
    const { container } = render(HistoryChart, { label: 'CPU', t, values: [10, null, 30, 95], format: fmt, max: 100, hot: 90 })
    expect(container.textContent).toContain('MÉD 45%')
    expect(container.textContent).toContain('PICO 95%')
    expect(container.querySelector('path')!.getAttribute('d')!.match(/M/g)).toHaveLength(2) // lacuna no null
    const svg = container.querySelector('svg')!
    expect(svg.classList.contains('red')).toBe(true)

    svg.getBoundingClientRect = () => ({ left: 0, width: 300, top: 0, height: 80, right: 300, bottom: 80, x: 0, y: 0, toJSON() {} })
    await fireEvent.pointerMove(svg, { clientX: 300 })
    expect(container.textContent).toContain('95%')
    expect(container.querySelector('.cursor')).toBeTruthy()
    await fireEvent.pointerDown(svg, { clientX: 100 })
    await fireEvent.pointerLeave(svg)
    expect(container.querySelector('.cursor')).toBeNull()
  })

  it('sem dados e períodos longos', async () => {
    const day = 86400
    const { container } = render(HistoryChart, { label: 'TEMP', t: [0, 3 * day], values: [null, null], format: fmt })
    expect(container.textContent).toContain('SEM DADOS')
    const single = render(HistoryChart, { label: 'X', t: [0], values: [], format: fmt })
    const svg = single.container.querySelector('svg')!
    await fireEvent.pointerMove(svg, { clientX: 1 })
    expect(single.container.querySelector('.axis')).toBeNull()
  })
})

describe('LockScreen', () => {
  it('botão de desbloquear', async () => {
    render(LockScreen)
    await fireEvent.click(screen.getByText('Desbloquear ▸'))
    await flush()
    store.lockError = 'Sensor indisponível'
    await flush()
    expect(screen.getByText('▲ Sensor indisponível')).toBeTruthy()
    store.locked = true
    store.authed = true
    await fireEvent.click(screen.getByText('Entrar com a senha'))
    expect(store.locked).toBe(false)
    expect(store.authed).toBe(false)
  })
})
