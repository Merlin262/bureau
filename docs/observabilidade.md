# Observabilidade e entrega de alertas

## Outbox de alertas

O `AlertEngine` não envia push diretamente. Ele grava o alerta na **outbox** (`DataDir/outbox.json`) e um
worker em segundo plano (`AlertOutbox`) faz a entrega:

```
coletor ──► AlertEngine.Raise ──► AlertOutbox.Enqueue ──(arquivo)──► worker ──► Notifier ──► FCM / Web Push / APNs / ntfy
             (dentro do lock)       grava e acorda o worker            retry com backoff
```

- **Durável:** o alerta é gravado antes de qualquer chamada de rede; se o processo reiniciar, a entrega continua.
- **Retry com backoff exponencial e jitter:** 15 s, 30 s, 1 min… até 30 min, no máximo 10 tentativas
  (depois disso o alerta é descartado e conta em `bureau.outbox.dead_letters`).
- **Sem duplicar:** cada item guarda os destinos que já receberam; o retry só tenta os que falharam.
- **Fila limitada:** no máximo 500 itens; acima disso descarta os mais antigos.
- O botão *Enviar teste* continua síncrono (a pessoa quer o resultado na hora).

Motivação: o alerta "internet caiu" é disparado justamente quando o FCM não está acessível. Antes, o envio era
*fire-and-forget* e o alerta se perdia; agora ele chega quando a conexão volta.

## OpenTelemetry

Com um endpoint OTLP configurado, o BUREAU exporta **traces, métricas e logs**:

```json
"Telemetry": { "OtlpEndpoint": "http://127.0.0.1:4317" }
```

(ou a variável `OTEL_EXPORTER_OTLP_ENDPOINT`). Sem endpoint, nada é coletado.

Para testar com Grafana (Tempo, Mimir/Prometheus e Loki num contêiner só):

```bash
docker run -d --name lgtm -p 3000:3000 -p 4317:4317 -p 4318:4318 grafana/otel-lgtm
```

Abra `http://localhost:3000` (admin/admin) → *Explore*.

### Traces

```
collect services                       (coletor)
└─ alert.enqueue        [producer]     (alerta gravado na outbox)
   └─ alert.deliver     [consumer]     (worker, possivelmente minutos depois, em outra tentativa)
      ├─ push.send  channel=fcm
      │  └─ POST https://fcm.googleapis.com/...
      └─ push.send  channel=ntfy
```

O contexto W3C (`traceparent`) é salvo junto com o item da outbox, então o trace atravessa a fila e o retry
— o mesmo padrão usado com headers em mensagens Kafka/RabbitMQ. Requisições HTTP da API também viram spans
(exceto o SSE `/api/stream` e o terminal, que são conexões longas).

### Métricas

| Métrica | Tipo | Tags |
|---|---|---|
| `bureau.alerts.raised` | counter | `kind`, `level` |
| `bureau.push.deliveries` | counter | `channel`, `result` (ok/failed/gone) |
| `bureau.push.duration` | histogram (s) | `channel`, `result` |
| `bureau.outbox.pending` | gauge | |
| `bureau.outbox.retries` | counter | |
| `bureau.outbox.dead_letters` | counter | `reason` (max_attempts/capacity) |
| `bureau.outbox.lag` | histogram (s) | tempo entre disparar e entregar |

Além de `System.Runtime` (GC, threads, memória) e das métricas HTTP do ASP.NET Core e do HttpClient.

Exemplos de PromQL no Grafana:

```promql
# taxa de falha de push por canal
sum by (channel) (rate(bureau_push_deliveries_total{result="failed"}[5m]))
  / sum by (channel) (rate(bureau_push_deliveries_total[5m]))

# p95 do tempo até o alerta chegar
histogram_quantile(0.95, sum by (le) (rate(bureau_outbox_lag_seconds_bucket[15m])))
```
