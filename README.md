# Itaú Extrato · busca de transações com Redis 8

> PoV (Proof of Value) em .NET 8 + Redis 8 para mostrar busca de extrato bancário cobrindo **3 modos** (keyword puro, NL→LLM rewrite, vector semantic) com **uma única consulta `FT.SEARCH` determinística** atrás de cada um — sem orquestrador, sem cluster, sem fila.

🌐 **Demo ao vivo:** https://extrato.platformengineer.io
🧠 **Modelo:** OpenAI `gpt-4o-mini` (rewrites) + `text-embedding-3-small` 1536d (semantic)
💾 **Backend:** Redis 8 oficial (Search, JSON, Streams, Bloom, VectorSet — todos nativos, sem RedisStack)

> 🎭 **100% dados sintéticos.** Nenhuma transação real do Itaú ou de qualquer outro banco. Os perfis (Gabriel, Miller, Camila, Pedro) e suas transações são geradas determinísticamente por seed, com nomes/CPFs/contas fictícios para ilustrar capacidades de busca.

---

## Por que isso importa

Bancos brasileiros hoje fazem busca de extrato com Elasticsearch. Funciona — mas:

- **Stack separada** do cache, da fila, do feature store. Cada um com seu cluster, sua observabilidade, seu time on-call.
- **Latência variável** entre 50-500ms dependendo da query + tamanho do shard.
- **LLM rewrite + cache** virou requisito de UX (cliente quer perguntar "quanto torrei com uber esse mês" em PT-BR), e isso normalmente exige adicionar Redis na frente do Elastic mesmo assim.

A proposta desse PoV: **Redis 8 sozinho** entrega o mesmo (ou melhor) com um único cluster.

| Capability                          | Como na demo                                                          |
| ----------------------------------- | --------------------------------------------------------------------- |
| Full-text search (PT-BR)            | `FT.SEARCH` com TEXT fields + sinônimos + filtros TAG/NUMERIC         |
| Filtros estruturados                | `@type:{pix} @direction:{outbound} @date:[from to]`                   |
| Vector semantic                     | KNN sobre embedding 1536d, pre-filter via TAG/NUMERIC                 |
| LLM-rewrite cache                   | Redis JSON com TTL — **second call: ~4ms vs 4000ms first call**       |
| Autocomplete                        | `FT.SUGADD` + `FT.SUGGET` — abaixo de 5ms                             |
| Sinônimos                           | `FT.SYNUPDATE` (`luz` → `energia`, `cpfl`, `enel`, `eletropaulo`…)    |
| Texto livre do Pix (memo)           | TEXT field `pix_message` indexado, peso 1.2                           |

---

## A money shot

Tente isso na demo ao vivo:

> **quantos piques eu fiz pro Morenão da Redis esse mês?**

O que rola:

1. **0ms** — `QueryClassifier` regex classifica como linguagem natural (tem "piques", "Morenão", "esse mês")
2. **300ms** — Cache lookup no Redis JSON. Miss na 1ª vez.
3. **2500ms** — LLM rewrite (`gpt-4o-mini` + Structured Outputs) devolve:
   ```json
   {
     "type": "pix",
     "direction": "outbound",
     "counterparty_match": "Moreno",
     "date_from": "2026-05-01",
     "date_to": "2026-05-22",
     "interpretation": "O cliente quer saber quantos pix enviou para o Morenão da Redis neste mês."
   }
   ```
4. **1ms** — `FT.SEARCH idx:transactions @user_id:{gabriel} @type:{pix} @direction:{outbound} @date:[1777604400 1779505199] (Moreno)`
5. **Resultado:** 6 Pix engraçados com memo (texto livre que o usuário digita no app):
   - R$ -100,00 · *"vai render — bonus do PoV da Redis, te garanto"*
   - R$ -67,80  · *"almoço de terça, tu deixou a carteira em casa kkkk"*
   - R$ -50,00  · *"aposta do brasileirão — paguei como combinamos 🤝"*
   - R$ -18,40  · *"café e pão de queijo do food truck que você gostou"*
   - R$ -54,00  · *"cerveja do happy hour de quinta"*
   - R$ -28,50  · *"racha do uber ontem rapa, valeu"*

Segunda chamada da mesma query: **~4ms total** (cache hit + FT.SEARCH).

---

## Arquitetura · 30s

```
                                 ┌────────────────────────────────────┐
                                 │  Redis 8 (Search, JSON, Bloom…)    │
                                 │                                    │
                                 │   idx:transactions  (FT.SEARCH)    │
   Cliente (browser)             │   idx:suggest       (FT.SUGGET)    │
       │                         │   cache:rewrite:<hash>  (JSON)     │
       │ POST /api/extrato/search│                                    │
       ▼                         └────────────────────────────────────┘
   ┌────────────────────┐                  ▲     ▲
   │  ASP.NET Core 8    │──────────────────┘     │
   │  Minimal API       │   FT.SEARCH /          │
   │                    │   FT.SUGGET / JSON.SET │
   │  QueryClassifier   │                        │
   │  RewriteCache      │                        │
   │  LlmRewriter ─────────────── OpenAI ────────┘
   │  SearchService      (gpt-4o-mini + Structured Outputs JSON Schema)
   └────────────────────┘
```

3 modos, escolhidos por `QueryClassifier` regex (0ms triage), com override via admin panel:

| Modo       | Trigger                                | Pipeline                                                                       |
| ---------- | -------------------------------------- | ------------------------------------------------------------------------------ |
| `keyword`  | "uber", "magalu", "salário"            | direto pro `FT.SEARCH` com TEXT escape + filtros mínimos                       |
| `natural`  | "quanto torrei com uber esse mês"      | cache → (miss) LLM rewrite com Structured Outputs → `FT.SEARCH` determinístico |
| `semantic` | "presentes pro meu pai" (opt-in admin) | embedding → KNN com pre-filter TAG/NUMERIC                                     |

Note that em modo `natural` o LLM **nunca formula a query Redis** — ele apenas devolve **filtros estruturados** que viram um `FT.SEARCH` que a gente controla. Sem `eval`, sem `string interpolation`, sem prompt injection abrir SELECT.

---

## Estrutura do repo

```
.
├── src/
│   ├── Itau.Extrato.Api/         # ASP.NET Core minimal API (5218)
│   │   ├── Program.cs             # rotas + DI
│   │   └── wwwroot/               # UI single-page (HTML+CSS+vanilla JS)
│   ├── Itau.Extrato.Search/      # Lib de search: cache, LLM rewrite, classifier
│   │   ├── SearchService.cs       # 3 modos: keyword / natural / semantic
│   │   ├── LlmRewriter.cs         # OpenAI Structured Outputs → RewrittenFilter
│   │   ├── QueryClassifier.cs     # regex 0ms pra decidir modo
│   │   ├── Cache/RewriteCache.cs  # Redis JSON com TTL configurável
│   │   ├── Schemas/               # FT.CREATE pra idx:transactions + idx:suggest
│   │   └── Models/Transaction.cs  # record do extrato
│   └── Itau.Extrato.Seed/        # Gera 1.3k transações realistas determinísticas
│       ├── TransactionFactory.cs  # PIX + cartão + boleto + salário…
│       ├── Seeder.cs              # orquestra geração + JSON.SET + FT.SYNUPDATE
│       └── UserProfile.cs         # 4 perfis demo
├── seeds/
│   ├── autocomplete_corpus.json   # alimenta FT.SUGADD
│   └── synonyms.json              # grupos pro FT.SYNUPDATE
├── Dockerfile                     # multi-stage .NET 8 SDK → aspnet runtime
├── docker-compose.yml             # api + redis
├── docker-compose.cloud.yml       # overlay pra VM (bind 127.0.0.1)
├── deploy/nginx-extrato.conf      # reverse proxy template
├── scripts/
│   ├── ssh.sh                     # gcloud compute ssh atalho
│   └── bootstrap-vm.sh            # instala docker numa VM limpa
├── deploy.sh                      # pipeline: buildx → scp → ssh → up → certbot
└── .env.example                   # copie pra .env e preencha OPENAI_API_KEY
```

---

## Rodando localmente

### Pré-requisitos

- Docker Desktop (Windows/Mac) ou Docker Engine + Compose v2 (Linux)
- Chave OpenAI (`sk-…`) com acesso a `gpt-4o-mini` e `text-embedding-3-small`

### 3 comandos

```bash
git clone https://github.com/gacerioni/gabs-redis-banco-extrato.git
cd gabs-redis-banco-extrato
cp .env.example .env && $EDITOR .env   # preencher OPENAI_API_KEY

docker compose up -d
```

Boot leva ~30s (Redis + .NET start + seed inicial de ~1.3k transações).

- **UI:** http://localhost:5218
- **Admin panel:** http://localhost:5218/admin.html (toggle modos, TTL, mostrar JSON crú)
- **Health:** `curl http://localhost:5218/api/health`

Para re-seed (drop + rebuild do índice — necessário após mudança de schema):

```bash
curl -X POST http://localhost:5218/api/seed
```

---

## Deploy em VM (GCP / qualquer Linux com nginx)

```bash
# 1. Configurar variáveis do seu setup
export VM_NAME=minha-vm
export VM_ZONE=us-east1-c
export VM_PROJECT=meu-projeto-gcp
export PUBLIC_DOMAIN=extrato.meusite.io
export IMAGE_NAME=docker-hub-user/redis-banco-extrato
export IMAGE_TAG=0.1.0

# 2. DNS: A record PUBLIC_DOMAIN → IP da VM (manual)

# 3. Subir
./deploy.sh
```

O `deploy.sh` cobre:

1. ✓ Buildx multi-arch (linux/amd64 + linux/arm64) → push pro Docker Hub
2. ✓ scp + ssh pra VM
3. ✓ Instala Docker + Compose se faltar
4. ✓ Copia nginx config + reload
5. ✓ `docker compose up -d`
6. ✓ `certbot --nginx` pra emitir cert LE (idempotente — skip se já tem)
7. ✓ Smoke test final em `https://$PUBLIC_DOMAIN/api/health`

Opções:

```bash
./deploy.sh --skip-build   # usa image que já tá no Hub
./deploy.sh --build-only   # só push, não toca na VM
./deploy.sh --logs         # ssh + docker compose logs -f
```

---

## API · principais rotas

| Método | Rota                        | Pra que                                                  |
| ------ | --------------------------- | -------------------------------------------------------- |
| GET    | `/`                         | UI single-page                                           |
| GET    | `/admin.html`               | Admin panel (toggle modos, TTL cache, debug JSON)        |
| GET    | `/api/health`               | Liveness                                                 |
| GET    | `/api/redis/info`           | Version, used_memory, ops/sec — pra header pill          |
| GET    | `/api/account`              | Saldo + perfil do Gabriel                                |
| GET    | `/api/extrato`              | Últimas N transações cronológicas                        |
| POST   | `/api/extrato/search`       | Busca multi-modo: `{query, mode?, limit?}` → resultados + metrics |
| GET    | `/api/extrato/suggest?q=…`  | Autocomplete via `FT.SUGGET`                             |
| POST   | `/api/seed`                 | Drop + reseed (use após mudança de schema)               |
| GET    | `/api/admin/settings`       | Config corrente                                          |
| PUT    | `/api/admin/settings`       | Toggle `defaultMode`, `cacheTtlSec`, `vssEnabled`        |

Cada response de search tem um bloco `metrics`:

```json
"metrics": {
  "total_ms": 4013,
  "stages": {
    "rewrite.cache_get": 312,
    "llm.rewrite":       2449,
    "rewrite.cache_set": 26,
    "ft.search":         1199
  },
  "redis_total_ms": 1537,
  "redis_ops":      3,
  "mode":           "nl_llm_rewrite",
  "llm_rewrite_json": "..."
}
```

Useful pra demonstrar onde o tempo vai e onde o cache vira ouro.

---

## Receitas Redis usadas

Resumo do que rola dentro do Redis no boot e em runtime:

### Schema do índice principal (`idx:transactions`)

```
FT.CREATE idx:transactions
  ON JSON
  PREFIX 1 tx:
  SCHEMA
    $.user_id           AS user_id           TAG
    $.type              AS type              TAG
    $.direction         AS direction         TAG
    $.category          AS category          TAG
    $.channel           AS channel           TAG
    $.installment.is_installment AS is_installment TAG
    $.date_unix         AS date              NUMERIC SORTABLE
    $.amount_brl        AS amount            NUMERIC SORTABLE
    $.description       AS description       TEXT
    $.counterparty_name AS counterparty      TEXT
    $.pix_message       AS pix_message       TEXT WEIGHT 1.2
    $.embedding         AS embedding         VECTOR FLAT 6 DIM 1536 TYPE FLOAT32 DISTANCE_METRIC COSINE
```

### Sinônimos (alimenta o text matching dentro do `FT.SEARCH`)

```
FT.SYNUPDATE idx:transactions luz energia cpfl enel eletropaulo
FT.SYNUPDATE idx:transactions transporte uber 99 cabify corrida
FT.SYNUPDATE idx:transactions delivery ifood rappi uber-eats comida
```

### Autocomplete

```
FT.SUGADD dict:autocomplete "Miller Moreno" 8.0
FT.SUGADD dict:autocomplete "Don Ramón Cerioni" 6.0
FT.SUGGET dict:autocomplete "morena" → ["Miller Moreno", "Morenão", "Moreno Junior"]
```

### Cache de LLM rewrite

```
JSON.SET cache:rewrite:<sha256_da_query_normalizada> $ '{"filter":..., "interpretation":...}'
EXPIRE cache:rewrite:<hash> 300   # TTL configurável pelo admin
```

---

## Observação técnica · FTS vs Hybrid

Importante: em RediSearch, fazer `FT.SEARCH "(@type:{pix} @date:[..]) =>[KNN K @embedding]"` **não funde rankings** — o TEXT atua como **pré-filtro** que reduz o conjunto sobre o qual o KNN ranqueia.

Quem funde rankings (BM25 + cosine via Reciprocal Rank Fusion) é o **`FT.HYBRID`** (Redis 8.4+), comando dedicado pra hybrid search. Não está usado aqui no PoV — o objetivo era mostrar os 3 modos como **alternativas controláveis** pelo admin, não fusão automática.

Versão futura: substituir o modo `semantic` por `FT.HYBRID` puro com RRF.

---

## Stack

- **.NET 8** · ASP.NET Core Minimal API
- **Redis 8** · oficial (não RedisStack). Search/JSON/Bloom/Streams/VectorSet todos nativos
- **OpenAI** · `gpt-4o-mini` (chat com Structured Outputs `json_schema`) + `text-embedding-3-small` (1536d)
- **NRedisStack** · `FT.SEARCH`, `FT.SUGGET`, `FT.SYNUPDATE`, `JSON.SET`
- **Docker** · multi-stage, multi-arch (amd64+arm64) via buildx
- **nginx + certbot** · TLS Let's Encrypt em prod

---

## Licença

MIT.

## Autor

Gabriel Cerioni · Solutions Architect @ Redis · gabriel.cerioni@redis.com

PoV construído pra demonstrar Redis 8 como single-engine para busca de extrato bancário. Pull requests bem-vindas se você quiser estender (sugestões: trocar modo `semantic` por `FT.HYBRID`, adicionar fontes de transação de outros bancos abertos via Open Finance, etc.).
