# Lambda Authorizer - Esperança Solidária

Lambda Authorizer (tipo **TOKEN**) do API Gateway. Valida o JWT emitido pelo Firebase
(projeto `esperancasolidaria`) e decide, por **rota + método + papel**, se a requisição é liberada.

Papéis existentes:

| Papel | Quem é | Como chega no authorizer |
|-------|--------|--------------------------|
| deslogado | visitante | sem token (só acessa rotas `AllowAnonymous`) |
| `Doador` | usuário logado | claim `role` do JWT |
| `GestorONG` | administrador da ONG | claim `role` do JWT |

## Arquitetura

```
lambda-authorizer/
├── Domain/
│   ├── AuthorizationRule.cs          # Regra (método, path, papéis permitidos, anônimo)
│   └── AuthorizationResult.cs        # Resultado da decisão
├── Application/Queries/
│   ├── AuthorizeTokenQuery.cs        # Token + método + path + apiId + stage
│   └── AuthorizeTokenQueryHandler.cs # Decodifica token, extrai papéis, consulta as regras
├── Infrastructure/
│   ├── JwtTokenService.cs            # Valida o JWT via JWKS do Firebase (IJwtTokenService)
│   ├── AuthorizationRulesService.cs  # Tabela de regras por rota/papel (IAuthorizationRulesService)
│   └── IamPolicyBuilder.cs           # Monta a policy Allow/Deny + context (IIamPolicyBuilder)
├── Program.cs                        # Entry point da Lambda (AuthorizerFunction.FunctionHandler)
└── build-and-deploy.ps1              # Gera o function.zip e envia para o repo de infra
```

> `Infrastructure/MinimalJwtDecoder.cs` e `Infrastructure/RequestProxyFunction.cs` não são
> usados por nenhum código e podem ser removidos.

## Fluxo

```
API Gateway (methodArn + Authorization)
    ↓
Program.cs extrai apiId, stage, httpMethod e resourcePath do methodArn
    ↓
AuthorizeTokenQueryHandler
    ├─ JwtTokenService        → claims (ou null se token ausente/inválido)
    ├─ papéis dos claims "role"/"roles"
    └─ AuthorizationRulesService.IsAuthorized("<METHOD> <path>", papéis)
    ↓
IamPolicyBuilder → policy Allow/Deny (+ context com userId e roles)
    ↓
API Gateway libera ou responde 403
```

Sem regra correspondente = **Deny** (default deny). Token ausente/inválido só passa em rota `AllowAnonymous`.

## Regras de autorização

Definidas em `AuthorizationRulesService.InitializeRulesStatic()`. O `path` é o do recurso no
API Gateway, **sem barra inicial** (ex.: `api/v1/campanhas`), e o `*` final casa por prefixo.

| Método | Path | Quem acessa | Chega no Lambda? |
|--------|------|-------------|------------------|
| GET | `api/v1/campanhas` e `api/v1/campanhas/*` | deslogado | não (`authorization = NONE`) |
| GET | `health` | deslogado | não (`NONE`) |
| POST | `users/api/v1/User` (cadastro) | deslogado | não (`NONE`) |
| POST | `users/api/v1/User/Login` | deslogado | não (`NONE`) |
| POST | `api/v1/campanhas` | `GestorONG` | **sim** (`CUSTOM`) |
| PUT | `api/v1/campanhas/*` | `GestorONG` | **sim** |
| GET / DELETE | `users/api/v1/User/Session/*` | `Doador`, `GestorONG` | **sim** |
| PUT | `users/api/v1/User/MakeGestorONG` | `GestorONG` | **sim** |

As linhas marcadas "não" já são liberadas direto no API Gateway; ficam na tabela só como defesa em profundidade.

**Ao adicionar uma rota `CUSTOM` no `main.tf` do repo de infra, adicione a regra correspondente aqui**
(hoje `Donation` ainda não tem recurso no API Gateway).

### Claims esperados

```json
{
  "sub": "id-do-usuario",
  "role": "GestorONG"
}
```

`role` (ou `roles`) pode ser string ou lista. A comparação de papel ignora maiúsculas/minúsculas.
O `context` devolvido ao API Gateway contém `userId` e `roles` (separados por vírgula).

## Configuração (variáveis de ambiente da Lambda)

| Variável | Descrição |
|----------|-----------|
| `FIREBASE_PROJECT_ID` | Projeto Firebase. **Issuer** `https://securetoken.google.com/<id>` e **audience** `<id>`. Fallback no código: `esperancasolidaria` |
| `JWKS_METADATA_ADDRESS` | (opcional) sobrescreve a URL do OpenID configuration usada para buscar as chaves |

No Terraform (repo de infra) essas variáveis vêm de `firebase_project_id` e `jwks_metadata_address`
do `terraform.tfvars` (não versionado). `ALLOW_DEV_STAGE_BYPASS` é lido apenas pelo Terraform, não pelo código C#.

## Build e deploy

Pré-requisitos: .NET SDK 10 e a ferramenta `Amazon.Lambda.Tools` (`dotnet tool install -g Amazon.Lambda.Tools`).

```powershell
cd lambda-authorizer
.\build-and-deploy.ps1
# ou, se o repo de infra não estiver ao lado deste:
.\build-and-deploy.ps1 -InfraRepoPath "C:\caminho\fiap-esperanca-solidaria-infra"
```

O script empacota a Lambda (`dotnet lambda package`, target `net10.0`/`linux-x64`) e copia o
`function.zip` para `fiap-esperanca-solidaria-infra/terraform/lambda-auth/function.zip`, lido pelas duas
árvores Terraform. Em seguida, no repo de infra:

```powershell
cd terraform\docker-compose   # ou terraform\k8s
terraform apply
```

Handler: `FiapEsperancaSolidaria.Lambda.Authorizer::FiapEsperancaSolidaria.Lambda.Authorizer.AuthorizerFunction::FunctionHandler` (runtime `dotnet10`).

## Como testar (LocalStack)

```bash
BASE="http://localhost.localstack.cloud:4566/_aws/execute-api/<api_id>/dev"

# rota pública: não invoca o authorizer
curl -i "$BASE/health"

# rota protegida sem token: 401 (o API Gateway barra antes de invocar a Lambda)
curl -i -X POST "$BASE/api/v1/campanhas" -H "Content-Type: application/json" -d '{}'

# rota protegida com token inválido: 403 (Lambda invocada e negou)
curl -i -X POST "$BASE/api/v1/campanhas" -H "Authorization: Bearer invalido" -H "Content-Type: application/json" -d '{}'
```

Logs da Lambda no LocalStack (`Authorization refused: POST api/v1/campanhas (principal=anonymous).`):

```bash
docker exec localstack sh -lc "awslocal logs describe-log-streams --log-group-name /aws/lambda/fiap-api-authorizer --order-by LastEventTime --descending --query 'logStreams[0].logStreamName' --output text"
```

## Próximos passos

- [ ] Testes unitários do `AuthorizationRulesService` e do `AuthorizeTokenQueryHandler`
- [ ] Remover código morto (`MinimalJwtDecoder`, `RequestProxyFunction`)
- [ ] Adicionar regras de `Donation` quando o recurso for exposto no API Gateway
- [ ] Log estruturado
