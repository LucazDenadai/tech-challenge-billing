# tech-challenge-billing

Serviço **Billing** da Fase 4 do Tech Challenge (oficina mecânica). É dono do **orçamento** da OS (versões, validade, aprovação e recusa, CARD-39a) e do **pagamento** Pix pelo Mercado Pago (cobrança, webhook, reconciliação e estorno, CARD-39b).

Decisões e contratos ficam em [tech-challenge-docs](https://github.com/LucazDenadai/tech-challenge-docs): limites e ownership ([ADR-014](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-014-limites-microsservicos-fase4.md), [ADR-015](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-015-ownership-e-infraestrutura-fase4.md)), bancos ([ADR-016](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-016-bancos-sql-nosql-fase4.md)), Saga ([ADR-017](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-017-saga-orquestrada-os-fase4.md)) contratos assíncronos ([ADR-018](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-018-contratos-assincronos-asyncapi-fase4.md)) e Mercado Pago ([ADR-019](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-019-integracao-mercado-pago-fase4.md)). Escopo e decisões de implementação: [CARD-39](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-39-servico-billing-pagamentos.md), [CARD-39a](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-39a-orcamento-aprovacao.md) e [CARD-39b](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-39b-mercado-pago-webhook.md).

## Estrutura

```
src/OficinaMecanica.Billing.Domain          orçamento, versões, itens e decisões
src/OficinaMecanica.Billing.Application     casos de uso, portas e contratos da Saga
src/OficinaMecanica.Billing.Infrastructure  EF Core/PostgreSQL, inbox, outbox, consumidor e despachante RabbitMQ, expiração
src/OficinaMecanica.Billing.API             controllers, autenticação, Swagger, health checks
tests/                                      testes unitários, de contrato e de integração
contratos/asyncapi-saga-os.yaml             cópia da spec AsyncAPI usada nos testes de contrato
docs/openapi/billing-v1.json                OpenAPI gerado pela API
docker-compose.yml                          PostgreSQL, RabbitMQ e Jaeger para desenvolvimento
```

## Configuração

Nenhum segredo é versionado. Em execução, todos chegam por variável de ambiente (`Secao__Chave`) ou Secret do Kubernetes.

| Chave | Uso |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL exclusivo de Billing. Migrations rodam no start. |
| `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience` | Validação do JWT. Mesma chave e issuer do OS (funcionários) e da Lambda de CPF (clientes). |
| `RabbitMq__Enabled`, `__Host`, `__Port`, `__VirtualHost`, `__Username`, `__Password` | Consumidor dos comandos da Saga e despachante da outbox. Habilitado, exige usuário e senha. |
| `RabbitMq__IntervaloOutboxMs` | Intervalo de leitura da outbox (padrão 500 ms). |
| `Orcamento__ValidadeHoras` | Validade de cada versão a partir da publicação (padrão 24, ADR-017). |
| `Orcamento__ExpiracaoHabilitada`, `__IntervaloExpiracaoSegundos` | Serviço que expira versões vencidas (padrão ligado, a cada 60 s). |
| `MercadoPago__Modo` | `Simulado` (padrão: provedor em memória, sem credenciais) ou `MercadoPago` (API real; sandbox ou produção pelas credenciais). |
| `MercadoPago__AccessToken`, `MercadoPago__WebhookSecret` | Access Token da aplicação e assinatura secreta do webhook, do painel "Suas integrações". Obrigatórios no modo `MercadoPago`. Nunca entram em log. |
| `MercadoPago__UrlNotificacao` | URL pública do webhook enviada em cada cobrança. Vazia: vale a URL do painel. |
| `MercadoPago__ExpiracaoMinutos` | Validade do Pix (padrão 30, o mínimo do Mercado Pago). |
| `MercadoPago__TimeoutSegundos`, `__TentativasPorChamada` | Timeout (10 s) e tentativas imediatas (3) por chamada ao provedor. |
| `MercadoPago__ToleranciaAssinaturaMinutos` | Diferença máxima entre o `ts` do `x-signature` e o relógio (padrão 30). |
| `MercadoPago__ReconciliacaoHabilitada`, `__IntervaloReconciliacaoSegundos`, `__IntervaloVerificacaoMinutos`, `__MaxTentativasReconciliacao` | Reconciliação em segundo plano (padrão ligada, a cada 60 s, verifica cada pagamento a cada 2 min, ação manual depois de 10 falhas técnicas seguidas). |
| `Cors__Origins__0` | Origem permitida. Obrigatória fora de `Development`/`Test`. |
| `Jaeger__Endpoint` | Coletor OTLP/HTTP dos traces. |

## Executar

```sh
docker compose up -d

ConnectionStrings__DefaultConnection="Host=localhost;Port=5434;Database=oficina_billing;Username=postgres;Password=postgres" \
RabbitMq__Enabled=true RabbitMq__Port=5673 RabbitMq__Username=billing RabbitMq__Password=billing-local \
Jaeger__Endpoint=http://localhost:4319 \
Jwt__Key="<mesma chave do OS, 32+ caracteres>" \
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/OficinaMecanica.Billing.API
```

As portas do Compose (`5434`, `5673`, `4319`) não colidem com as de Operações. No fluxo integrado da Saga (CARD-40), os três serviços usam o mesmo broker.

Sem `MercadoPago__*`, o serviço sobe no modo `Simulado`: a cobrança Pix é criada em memória e o pagamento é aprovado pela rota do simulador.

Swagger em `/billing/swagger`, liveness em `/billing/health` e readiness (PostgreSQL e, se habilitado, RabbitMQ) em `/billing/ready`.

## Quem decide o orçamento

Só o **cliente dono da OS**. A aprovação e a recusa exigem token com `role=Cliente` e a claim `cliente_id` igual ao cliente do orçamento, que chega no `QuoteRequested`.

- Token de funcionário (`Admin`, `Atendente`, `Mecanico`) recebe `403`, mesmo `Admin`.
- Um funcionário que traz o próprio veículo entra pelo CPF na Lambda e decide como cliente.
- Orçamento de outro cliente responde `404`, para não revelar que existe.

## API

| Rota | Quem | O que faz |
|---|---|---|
| `GET /billing/orcamentos/meus` | Cliente | Orçamentos do cliente do token, com a versão vigente |
| `GET /billing/orcamentos/{quoteId}` | Cliente dono, Admin, Atendente | Versões, itens e histórico de decisão |
| `GET /billing/orcamentos/os/{osId}` | Admin, Atendente | Orçamento de uma OS |
| `POST /billing/orcamentos/{quoteId}/aprovar` | Cliente dono | Corpo `{ "versao": 1 }` |
| `POST /billing/orcamentos/{quoteId}/recusar` | Cliente dono | Corpo `{ "versao": 1, "motivo": "..." }` (motivo opcional) |
| `GET /billing/pagamentos/meus` | Cliente | Pagamentos do cliente do token, com o código Pix "copia e cola" e a URL de pagamento |
| `GET /billing/pagamentos/os/{osId}` | Admin, Atendente | Pagamentos de uma OS |
| `POST /billing/pagamentos/webhook/mercadopago` | Mercado Pago (anônimo, `x-signature`) | Notificação de pagamento. Ver [Pagamento](#pagamento-mercado-pago). |
| `POST /billing/simulador/pagamentos/{referencia}/status` | Admin, só no modo `Simulado` | Corpo `{ "status": "approved" }`. Simula o cliente pagando, recusando ou o Pix expirando. |

Respostas da decisão:

| Status | Quando |
|---|---|
| `200` | Decisão registrada, ou a mesma decisão repetida (sem novo evento) |
| `404` | Orçamento inexistente, de outro cliente, ou versão inexistente |
| `409` | Versão substituída por uma mais nova, ou decisão oposta à já gravada. O corpo traz `versaoVigente`. |
| `410` | Versão vencida sem decisão. A expiração é gravada e `QuoteExpired` sai uma vez. |

Não há rota para criar orçamento nem cobrança: eles nascem só dos comandos `QuoteRequested` e `PaymentCreationRequested` da Saga. Todas as respostas devolvem o cabeçalho `X-Correlation-Id`, recebido ou gerado.

Estados da versão: `AguardandoDecisao` → `Aprovada`, `Recusada`, `Expirada` ou `Substituida`. Todos, menos o primeiro, são terminais.

## Mensageria

Billing é participante da Saga orquestrada pelo OS (ADR-017). Para cada comando que consome, declara a fila `billing.<canal>` e a DLQ `billing.<canal>.dlq` (ADR-018).

| Origem | Efeito | Evento publicado |
|---|---|---|
| `QuoteRequested.v1` | Cria a versão 1 do orçamento da OS, ou a próxima se a vigente ainda aguarda decisão | `QuoteReady.v1` com valor, moeda e validade, ou `QuoteRejected.v1` |
| `POST .../aprovar`, `.../recusar` | Registra a decisão da versão vigente | `QuoteApproved.v1` ou `QuoteDeclined.v1` |
| Serviço de expiração | Expira versões pendentes vencidas | `QuoteExpired.v1` |
| `PaymentCreationRequested.v1` | Registra o pagamento e, depois do commit, cria o Pix no provedor | `PaymentPending.v1` com `providerReference`, ou `PaymentOutcomeUnknown.v1` se o provedor não respondeu |
| Webhook, simulador ou reconciliação | Consulta o pagamento no provedor e aplica a mudança | `PaymentApproved.v1` ou `PaymentDeclined.v1` |
| `PaymentRefundRequested.v1` | Pede o estorno total, depois do commit | `PaymentRefunded.v1`, `PaymentRefundPending.v1` ou `PaymentRefundFailed.v1` |

O valor é a soma de quantidade × preço do snapshot do diagnóstico. Billing não consulta Operações.

- **`QuoteRejected`:** item sem preço, preço não positivo ou com mais de duas casas, lista vazia, quantidade menor que 1, moeda ausente ou diferente de `BRL`, cliente diferente do orçamento existente, ou pedido depois de uma decisão (revisão fora do MVP). A spec exige alguns desses campos, mas Billing os lê para responder ao OS em vez de perder a mensagem na DLQ (ADR-017).
- **Duplicata:** a mesma mensagem é confirmada sem novo efeito nem novo evento. A mesma `idempotencyKey` em outra mensagem devolve a versão já criada.
- **Fora do contrato:** envelope inválido, `clienteId`, `itemId`, tipo ou descrição inválidos vão para a DLQ com o cabeçalho `x-motivo`.
- **Falha transitória** (incluindo conflito de versão): 3 novas tentativas (1 s, 5 s, 10 s) antes da DLQ. Cada tentativa relê o estado.

Inbox, efeito e evento na outbox ficam em uma transação do PostgreSQL (ADR-016). O despachante publica na exchange do canal com confirmação do broker e marca a mensagem depois. A entrega é ao menos uma vez; o OS deduplica pelo `messageId`. O `traceparent` do comando segue no evento.

Concorrência: o `xmin` do orçamento é o token de versão. Duas decisões simultâneas: uma grava; a outra relê e responde `200` (mesma decisão) ou `409` (decisão oposta).

## Pagamento (Mercado Pago)

Pix pela API de Pagamentos do Mercado Pago ([ADR-019](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-019-integracao-mercado-pago-fase4.md)). Billing nunca recebe dados de cartão.

- **Cobrança:** só para a versão `Aprovada` do orçamento, com o mesmo valor, moeda, OS e filial. O comando precisa de `payerEmail`. Fora disso, o comando vai para a DLQ e o provedor não é chamado. O `paymentId` é o `external_reference` e o `X-Idempotency-Key`: todo retry usa a mesma chave, então não há segunda cobrança.
- **Chamada fora da transação:** o comando grava o pagamento em `Solicitado`; a chamada ao provedor roda depois do commit. Se o processo cair entre os dois, a reconciliação busca pelo `external_reference` e, se não achar, cria de novo com a mesma chave.
- **Webhook:** valida o `x-signature` (HMAC-SHA256 do manifesto `id:<data.id>;request-id:<x-request-id>;ts:<ts>;`) e responde `401` se falhar. Notificação válida só dispara `GET /v1/payments/{id}`; o corpo não muda estado. O mesmo `x-request-id` não é processado de novo. Cada notificação autenticada fica em `NotificacoesPagamento` com o resultado.
- **Conferência:** antes de qualquer mudança, o pagamento consultado precisa ter o `external_reference`, a referência, o valor e a moeda da tentativa. Divergência ou pagamento desconhecido não muda nada.
- **Estados:** `Solicitado` → `Pendente` → `Aprovado` ou `Recusado`; `Aprovado` → `EstornoPendente` → `Estornado` ou `EstornoFalhou`. Só avançam: notificação atrasada não regride. Cada evento sai uma vez.
- **Falha técnica** (timeout, rede, `5xx`, `429`, resposta ilegível): nunca é recusa. Até 3 tentativas imediatas; depois o resultado fica desconhecido e a reconciliação tenta de novo. Depois de 10 falhas seguidas, o pagamento fica marcado para ação manual (`ReconciliacaoManual`), com log de erro e métrica. No estorno, o limite gera `PaymentRefundFailed`.
- **Dados pessoais:** o e-mail do pagador fica só até a cobrança existir no provedor. CPF e nome não chegam a Billing. Nenhum evento leva dado do pagador.
- **Métricas:** `billing_mercadopago_chamadas` (operação e resultado), `billing_webhook_notificacoes` (resultado) e `billing_pagamentos_reconciliacao_manual`, em `/metrics`.

Para usar o Mercado Pago de verdade: crie a aplicação em "Suas integrações", copie o Access Token das credenciais de teste, configure o webhook com o evento "Pagamentos" e copie a assinatura secreta. Defina `MercadoPago__Modo=MercadoPago`, `MercadoPago__AccessToken` e `MercadoPago__WebhookSecret` como variáveis de ambiente.

## Testes

```sh
dotnet test tests/OficinaMecanica.Billing.UnitTests          # domínio, casos de uso e contratos AsyncAPI
dotnet test tests/OficinaMecanica.Billing.IntegrationTests   # PostgreSQL e RabbitMQ via Testcontainers (Docker); Mercado Pago com respostas gravadas
```

Com `EXPORTAR_OPENAPI=<caminho>` definido, o teste de Swagger grava o OpenAPI gerado (fonte de `docs/openapi/billing-v1.json`).
