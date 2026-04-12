# CCEE PLD API Gateway

Este projeto automatiza a consulta ao PLD horário da CCEE — a Câmara de Comercialização de Energia Elétrica do Brasil — e expõe os dados via uma API HTTP simples.

O PLD (Preço de Liquidação das Diferenças) é o sinal de preço usado para liquidar diferenças entre energia contratada e energia efetivamente consumida no mercado de curto prazo, calculado diariamente pela CCEE com base no Custo Marginal de Operação (CMO).

A CCEE costuma proteger seu portal contra acessos automatizados por meio de WAF, controle de protocolos e outras camadas de segurança. Por isso este gateway utiliza um browser automatizado para consultar o site de forma semelhante a um usuário real e, em seguida, persiste os resultados em cache SQLite para evitar novas consultas desnecessárias.

## Arquitetura

- `Ccee.PldApp.Api`: entrada HTTP, auth do painel admin e mapeamento de erros.
- `Ccee.PldApp.Application`: caso de uso principal.
- `Ccee.PldApp.Domain`: modelos de domínio (`PldQuery`, `PldRecord`, `PldQueryResult`).
- `Ccee.PldApp.Infrastructure`: cliente CCEE via browser, parser e SQLite.
- `Ccee.PldApp.Tests`: testes unitários.

## Fluxo funcional

1. A API recebe os parâmetros.
2. A consulta é validada e normalizada.
3. O caso de uso tenta responder pelo cache SQLite.
4. Se não houver cache, a aplicação consulta a CCEE **via browser**.
5. O payload é convertido para registros de domínio.
6. O resultado é salvo no cache.
7. A resposta informa a origem: `Cache` ou `Ccee`.

## Regra de submercado

A API aceita apenas valores exatos:

- `SUDESTE`
- `SUL`
- `NORDESTE`
- `NORTE`

Sem aliases. Valores fora dessa lista retornam erro de validação.

## Endpoints

### `GET /`
Status e exemplos de uso.

### `GET /health`
Health check simples.

### `GET /api/pld`
Endpoint central de dados.

Parâmetros suportados:

- `date` (formato `YYYY-MM-DD`)
- `submercado` (`SUDESTE|SUL|NORDESTE|NORTE`)
- `limit` (inteiro entre `1` e `10000`)

Parâmetros antigos removidos:

- `resourceId`
- `forceRefresh`

Exemplos:

```powershell
Invoke-RestMethod "http://localhost:5000/api/pld?date=2026-04-11&submercado=SUL&limit=24"
Invoke-RestMethod "http://localhost:5000/api/pld?submercado=SUDESTE&limit=24"
```

### `GET /login`
Tela de login do painel admin.

### `GET /admin`
Painel admin web (protegido por sessão).

### `POST /api/auth/login`
Autentica usuário do painel admin e cria cookie de sessão.

Body JSON:

```json
{
  "username": "admin",
  "password": "admin123"
}
```

### `POST /api/auth/logout`
Encerra sessão do painel admin.

### `GET /api/auth/me`
Retorna usuário autenticado da sessão atual.

### Endpoints administrativos (`/api/admin/*`)

- `GET /api/admin/overview`: visão geral do sistema (uptime, banco, browser, sessão, etc.).
- `GET /api/admin/metrics`: métricas de requisições e tempos de resposta.
  Parâmetros opcionais: `fromUtc`, `toUtc`, `method`, `pathContains`, `statusCode`.
  Sem parâmetros, retorna por padrão a janela das últimas 24 horas.
- `GET /api/admin/cache`: estatísticas e consultas recentes do cache SQLite.
- `GET /api/admin/configs`: leitura de arquivos de configuração com mascaramento de campos sensíveis.
- `GET /api/admin/logs/files`: lista arquivos de log disponíveis.
- `GET /api/admin/logs?file=<arquivo>&lines=<n>`: últimas linhas do log selecionado.

Exemplo de filtro de métricas:

```powershell
Invoke-RestMethod "http://localhost:5000/api/admin/metrics?fromUtc=2026-04-12T00:00:00Z&toUtc=2026-04-13T00:00:00Z&method=GET&pathContains=/api/pld"
```

## Configuração

### `Ccee.PldApp.Api/appsettings.json` (seção `PldGateway`)

- `BaseUrl`
- `DatabasePath`
- `UserAgent`
- `DownloadBrowserIfMissing`
- `BrowserExecutablePath`
- `HttpTimeoutSeconds`
- `HttpMaxAttempts`
- `EnableResourceIdAutoDiscovery`
- `ResourceCatalogUrl`
- `ResourceIdsByYear`

Exemplo de `ResourceIdsByYear`:

```json
"PldGateway": {
  "ResourceIdsByYear": {
    "2025": "2a180a6b-f092-43eb-9f82-a48798b803dc",
    "2026": "3f279d6b-1069-42f7-9b0a-217b084729c4"
  }
}
```

Regra:

- A API escolhe o `resource_id` pelo ano da `date`.
- Se `date` não for informada, usa o ano atual.
- Se não existir `resource_id` para o ano solicitado, a API tenta descobrir automaticamente no catálogo CKAN da CCEE.
- Quando a descoberta automática encontra um `resource_id`, o valor é gravado automaticamente no `appsettings.json` em `PldGateway:ResourceIdsByYear`.
- Se a descoberta automática falhar, retorna erro de validação.

### HTML do admin/login

Arquivo de configuração:

- `Ui:PagesPath` (padrão: `Configuration/Ui`)

Arquivos esperados:

- `Configuration/Ui/login.html`
- `Configuration/Ui/admin.html`

Esses arquivos são lidos em tempo de execução a cada requisição de `/login` e `/admin`.

### Autenticação do painel admin

Arquivo: `Ccee.PldApp.Api/Configuration/SecurityConfig.json`

```json
{
  "Security": {
    "Dashboard": {
      "RequireAuthentication": true,
      "SessionTimeoutMinutes": 60,
      "Users": [
        {
          "Username": "admin",
          "Password": "admin123"
        }
      ]
    }
  }
}
```

## Execução

### API

```powershell
dotnet run --project .\Ccee.PldApp.Api\
```

No `cmd` (Windows):

```cmd
dotnet run --project .\Ccee.PldApp.Api\
```

## Build e testes

```powershell
dotnet build .\CceePld.slnx
dotnet test .\Ccee.PldApp.Tests\
```

## Observações

- O endpoint da CCEE exige uso de browser para consultas automáticas neste projeto.
- A consulta por data usa `MES_REFERENCIA` + `DIA` como estratégia principal (formato real retornado pelo recurso atual da CCEE) e tenta formato de data completa apenas como compatibilidade.
- O cache não expira automaticamente (comportamento intencional).
- As métricas administrativas são persistidas em `logs/metrics/metrics-YYYYMMDD.jsonl` e mantidas por até 30 dias.
- A leitura de logs no `/api/admin/logs` usa acesso compartilhado; quando o arquivo estiver temporariamente indisponível, a API pode responder `423 Locked`.

## Aviso legal e uso responsável

Este é um projeto pessoal desenvolvido apenas para estudo técnico.

Embora o portal de Dados Abertos da CCEE disponibilize APIs para consumo automatizado, o uso dos dados e da plataforma também é condicionado aos Termos de Uso da CCEE. Isso pode gerar uma ambiguidade jurídica/operacional sobre como determinadas automações devem ser conduzidas na prática.

Antes de uso em produção, recomenda-se:

- Revisar internamente os aspectos de compliance e jurídico.
- Utilizar a API oficial (`/api/3/action/...`) com limites de requisição e monitoramento.
- Manter atribuição da fonte CCEE conforme licença aplicável.
- Em caso de dúvida, solicitar orientação formal da própria CCEE.

Fontes oficiais consultadas:

- [Portal Dados Abertos CCEE (sobre)](https://dadosabertos.ccee.org.br/about)
- [Dataset PLD_HORARIO (licença e termo legal)](https://dadosabertos.ccee.org.br/dataset/pld_horario)
- [Lista de datasets com referência à API CKAN](https://dadosabertos.ccee.org.br/dataset/)
- [Termo de Acesso e Uso da CCEE](https://www.ccee.org.br/-/termo-de-acesso-e-uso)
- [Documentação da API CKAN](https://docs.ckan.org/en/2.10/api/)
