# fiap-esperanca-solidaria-lambda-authorizer

**Lambda Authorizer** do API Gateway da plataforma Esperança Solidária (FIAP 11NETT). Toda rota `CUSTOM`
do gateway invoca esta função, que valida o JWT emitido pelo Firebase e decide, por
**método + rota + papel**, se a requisição segue para a `campanha-api`/`usuario-api` ou é negada.

> Documentação técnica completa (fluxo interno, tabela de regras, claims, variáveis, testes no
> LocalStack): **[`lambda-authorizer/README.md`](lambda-authorizer/README.md)**.
> Para subir o ambiente inteiro, siga o README do repositório **`fiap-esperanca-solidaria-infra`**.

---

## Visão geral

```
Cliente ── Authorization: Bearer <idToken> ──▶ API Gateway (LocalStack)
                                                   │ rota CUSTOM
                                                   ▼
                                  AuthorizerFunction.FunctionHandler
                                   1. extrai método e path do methodArn
                                   2. valida o JWT (JWKS do Firebase, issuer/audience do projeto)
                                   3. lê os papéis da claim "roles"
                                   4. procura a primeira regra que casa (método + path)
                                   5. devolve policy IAM Allow/Deny + context {userId, roles}
                                                   │
                                  Allow ──▶ integração HTTP_PROXY para a API no k8s
                                  Deny  ──▶ 403 direto do gateway
```

- Papéis: **`Doador`** e **`GestorONG`** (custom claim `roles` gravada pelo `usuario-api` no Firebase).
- **Rota sem regra é negada.** Ao criar uma rota `CUSTOM` no `terraform/k8s/main.tf` da infra, adicione a
  regra correspondente em `lambda-authorizer/Infrastructure/AuthorizationRulesService.cs`.
- Rotas públicas (`authorization = NONE` no gateway) nem chegam aqui; elas aparecem na tabela só como
  defesa em profundidade.

---

## Estrutura do repositório

```
.
├── README.md                          # Este arquivo (visão geral)
└── lambda-authorizer/
    ├── Program.cs                     # AuthorizerFunction.FunctionHandler (entry point da Lambda)
    ├── Application/Queries/           # AuthorizeTokenQuery + handler (orquestra validação, papéis e regra)
    ├── Domain/                        # AuthorizationRule, AuthorizationResult
    ├── Infrastructure/
    │   ├── AuthorizationRulesService.cs  # ★ tabela de regras (método, path, papéis, anônimo)
    │   ├── JwtTokenService.cs            # validação do JWT via JWKS do Firebase
    │   ├── IamPolicyBuilder.cs           # monta a policy Allow/Deny
    │   └── MinimalJwtDecoder.cs, RequestProxyFunction.cs  # código legado, sem uso
    ├── FiapEsperancaSolidaria.Lambda.Authorizer.csproj   # net10.0
    ├── lambda-authorizer.sln
    ├── build-and-deploy.ps1           # empacota e copia o function.zip para o repo de infra
    └── README.md                      # documentação técnica detalhada
```

---

## Regras resumidas

| Quem | Pode |
|---|---|
| Qualquer um | listar/ver campanhas, `health`, cadastro de doador, upload de foto, login, refresh |
| `Doador` | doar, ver as próprias doações, atualizar o próprio cadastro, consultar/encerrar sessão |
| `GestorONG` | CRUD e cancelamento de campanhas, listagem de gestão, criar/promover gestores, ver doações, sessão |

A tabela completa, rota a rota, está no [README técnico](lambda-authorizer/README.md#regras-de-autorização).

---

## Como compilar e publicar

Pré-requisitos: .NET SDK 10 e `Amazon.Lambda.Tools` (`dotnet tool install -g Amazon.Lambda.Tools`).
Clone este repo **ao lado** do `fiap-esperanca-solidaria-infra` (ou informe o caminho).

```powershell
cd lambda-authorizer
.\build-and-deploy.ps1
# ou: .\build-and-deploy.ps1 -InfraRepoPath "C:\caminho\fiap-esperanca-solidaria-infra"
```

O script gera o pacote (`linux-x64`) e copia para
`fiap-esperanca-solidaria-infra/terraform/lambda-auth/function.zip`. Depois, no repo de infra (com o
`kubectl port-forward -n localstack svc/localstack 4566:4566` aberto):

```powershell
cd terraform/k8s
terraform apply -replace="terraform_data.api_gateway_custom"
```

Commite o `function.zip` atualizado no repo de infra para que o time use a mesma versão.

| Item | Valor |
|---|---|
| Nome da função | `fiap-api-authorizer` |
| Runtime | `dotnet10` |
| Handler | `FiapEsperancaSolidaria.Lambda.Authorizer::FiapEsperancaSolidaria.Lambda.Authorizer.AuthorizerFunction::FunctionHandler` |
| Variáveis | `FIREBASE_PROJECT_ID` (padrão `esperancasolidaria`), `JWKS_METADATA_ADDRESS` (opcional) |

---

## Testando rapidamente

```powershell
$id = aws apigateway get-rest-apis --endpoint-url http://localhost:30466 --region us-east-1 `
        --query "items[?name=='local-api-gateway-v1'].id" --output text
$gw = "http://localhost:30466/restapis/$id/dev/_user_request_"

curl -i "$gw/api/v1/campanhas"                                          # público: 200, sem invocar a Lambda
curl -i "$gw/api/v1/doacoes/me"                                         # sem token: 401
curl -i "$gw/api/v1/doacoes/me" -H "Authorization: Bearer invalido"     # token inválido: 403
curl -i "$gw/api/v1/doacoes/me" -H "Authorization: Bearer <idToken de um Doador>"   # 200
```

Logs da função no LocalStack (k8s):

```powershell
kubectl exec -n localstack deploy/localstack -- awslocal logs tail /aws/lambda/fiap-api-authorizer
```

---

## Troubleshooting

| Sintoma | Causa provável |
|---|---|
| Rotas `CUSTOM` dão 500/timeout | Port-forward `4566` fechado, ou `function.zip` corrompido/de outro projeto (confira o handler dentro do zip). |
| 403 com token válido | Papel ausente na claim `roles`, ou rota sem regra correspondente. |
| 403 após promover alguém a gestor | O token antigo não tem a claim nova — faça login/refresh de novo. |
| 401 sem invocar a Lambda | Header `Authorization` ausente (o gateway barra antes). |
