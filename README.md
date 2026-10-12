# tech-challenge-billing

Serviço **Billing** da Fase 4 do Tech Challenge (oficina mecânica). É dono do orçamento da OS, da decisão do cliente e, a partir do CARD-39b, do pagamento pelo Mercado Pago. Neste estágio (CARD-39a) contém o **orçamento**: versões, validade, aprovação e recusa.

Decisões e contratos ficam em [tech-challenge-docs](https://github.com/LucazDenadai/tech-challenge-docs): limites e ownership ([ADR-014](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-014-limites-microsservicos-fase4.md), [ADR-015](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-015-ownership-e-infraestrutura-fase4.md)), bancos ([ADR-016](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-016-bancos-sql-nosql-fase4.md)), Saga ([ADR-017](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-017-saga-orquestrada-os-fase4.md)) e contratos assíncronos ([ADR-018](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-018-contratos-assincronos-asyncapi-fase4.md)). Escopo e decisões de implementação: [CARD-39](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-39-servico-billing-pagamentos.md) e [CARD-39a](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-39a-orcamento-aprovacao.md).

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
| `RabbitMq__Enabled`, `__Host`, `__Port`, `__VirtualHost`, `__Username`, `__Password` | Consumidor do `QuoteRequested` e despachante da outbox. Habilitado, exige usuário e senha. |
| `RabbitMq__IntervaloOutboxMs` | Intervalo de leitura da outbox (padrão 500 ms). |
| `Orcamento__ValidadeHoras` | Validade de cada versão a partir da publicação (padrão 24, ADR-017). |
| `Orcamento__ExpiracaoHabilitada`, `__IntervaloExpiracaoSegundos` | Serviço que expira versões vencidas (padrão ligado, a cada 60 s). |
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

Respostas da decisão:

| Status | Quando |
|---|---|
| `200` | Decisão registrada, ou a mesma decisão repetida (sem novo evento) |
| `404` | Orçamento inexistente, de outro cliente, ou versão inexistente |
| `409` | Versão substituída por uma mais nova, ou decisão oposta à já gravada. O corpo traz `versaoVigente`. |
| `410` | Versão vencida sem decisão. A expiração é gravada e `QuoteExpired` sai uma vez. |

Não há rota para criar orçamento: ele nasce só do comando `QuoteRequested` da Saga. Todas as respostas devolvem o cabeçalho `X-Correlation-Id`, recebido ou gerado.

Estados da versão: `AguardandoDecisao` → `Aprovada`, `Recusada`, `Expirada` ou `Substituida`. Todos, menos o primeiro, são terminais.

## Mensageria

Billing é participante da Saga orquestrada pelo OS (ADR-017). Para cada comando que consome, declara a fila `billing.<canal>` e a DLQ `billing.<canal>.dlq` (ADR-018).

| Origem | Efeito | Evento publicado |
|---|---|---|
| `QuoteRequested.v1` | Cria a versão 1 do orçamento da OS, ou a próxima se a vigente ainda aguarda decisão | `QuoteReady.v1` com valor, moeda e validade, ou `QuoteRejected.v1` |
| `POST .../aprovar`, `.../recusar` | Registra a decisão da versão vigente | `QuoteApproved.v1` ou `QuoteDeclined.v1` |
| Serviço de expiração | Expira versões pendentes vencidas | `QuoteExpired.v1` |

O valor é a soma de quantidade × preço do snapshot do diagnóstico. Billing não consulta Operações.

- **`QuoteRejected`:** item sem preço, preço não positivo ou com mais de duas casas, lista vazia, quantidade menor que 1, moeda ausente ou diferente de `BRL`, cliente diferente do orçamento existente, ou pedido depois de uma decisão (revisão fora do MVP). A spec exige alguns desses campos, mas Billing os lê para responder ao OS em vez de perder a mensagem na DLQ (ADR-017).
- **Duplicata:** a mesma mensagem é confirmada sem novo efeito nem novo evento. A mesma `idempotencyKey` em outra mensagem devolve a versão já criada.
- **Fora do contrato:** envelope inválido, `clienteId`, `itemId`, tipo ou descrição inválidos vão para a DLQ com o cabeçalho `x-motivo`.
- **Falha transitória** (incluindo conflito de versão): 3 novas tentativas (1 s, 5 s, 10 s) antes da DLQ. Cada tentativa relê o estado.

Inbox, efeito e evento na outbox ficam em uma transação do PostgreSQL (ADR-016). O despachante publica na exchange do canal com confirmação do broker e marca a mensagem depois. A entrega é ao menos uma vez; o OS deduplica pelo `messageId`. O `traceparent` do comando segue no evento.

Concorrência: o `xmin` do orçamento é o token de versão. Duas decisões simultâneas: uma grava; a outra relê e responde `200` (mesma decisão) ou `409` (decisão oposta).

## Porta para o pagamento (CARD-39b)

`IOrcamentoParaPagamento.ObterVersaoAprovadaAsync(quoteId, versao)` devolve valor, moeda, OS, cliente e filial só de uma versão `Aprovada`. Pendente, recusada, expirada ou substituída devolve `null`: não gera cobrança.

## Testes

```sh
dotnet test tests/OficinaMecanica.Billing.UnitTests          # domínio, casos de uso e contratos AsyncAPI
dotnet test tests/OficinaMecanica.Billing.IntegrationTests   # PostgreSQL e RabbitMQ via Testcontainers (Docker)
```

Com `EXPORTAR_OPENAPI=<caminho>` definido, o teste de Swagger grava o OpenAPI gerado (fonte de `docs/openapi/billing-v1.json`).
