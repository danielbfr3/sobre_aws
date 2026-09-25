# Como o SDK .NET descobre a role

Os guias anteriores explicaram *o que* é uma role e *quem* pode assumi-la. Este explica o que acontece **dentro do processo .NET** entre você escrever `new AmazonSQSClient()` e a primeira chamada sair autenticada.

Pré-requisito: [`01-iam-explicado.md`](01-iam-explicado.md) e [`02-assume-role-cross-account.md`](02-assume-role-cross-account.md).

---

## 1. A linha que faz tudo

```csharp
var sqs = new AmazonSQSClient();
```

Nenhuma credencial. Nenhum `if (ambiente == "producao")`. E funciona na sua máquina, no EKS, no ECS e na Lambda — cada um com um mecanismo completamente diferente por baixo.

O que acontece: o SDK monta uma **cadeia de provedores de credenciais** e percorre em ordem, parando no primeiro que responder. No AWS SDK for .NET essa cadeia vive na `FallbackCredentialsFactory`.

---

## 2. A ordem da cadeia

Do mais específico para o mais genérico, **no AWS SDK for .NET**:

| # | Provedor | O que procura | Onde costuma vencer |
|---|---|---|---|
| 1 | Explícito no código | `new AmazonSQSClient(credenciais)` | quando alguém forçou |
| 2 | `app.config` / `web.config` | seção `<aws>` | .NET Framework legado |
| 3 | **Web identity token** | `AWS_WEB_IDENTITY_TOKEN_FILE` + `AWS_ROLE_ARN` | **EKS / IRSA** |
| 4 | Perfil compartilhado | `AWS_PROFILE`, `~/.aws/credentials`, `~/.aws/config` (inclui `role_arn` + `source_profile` e SSO) | máquina de desenvolvedor |
| 5 | **Variáveis de ambiente** | `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` | Lambda, docker-compose do lab |
| 6 | **Credenciais de container** | `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI` ou `..._FULL_URI` | **ECS**, EKS Pod Identity |
| 7 | Metadados da instância (IMDS) | `169.254.169.254` | EC2 puro, role do nó |

Leia as linhas 3, 4 e 5 com atenção, porque elas contrariam o que quase todo
mundo repete sobre a cadeia de credenciais — inclusive o que os SDKs de Python
e Go de fato fazem:

> **No .NET, o IRSA vence a variável de ambiente, e o perfil `~/.aws` também vence.**

Isso é o **oposto** do botocore e do `aws-sdk-go-v2`, onde a variável de ambiente
é o primeiro elo depois do explícito. O [capítulo 07](07-implementacoes-python-go-dotnet.md) §2
põe as três cadeias lado a lado e mostra como medir isso você mesmo, em vez de
acreditar nesta tabela.

### As duas consequências práticas

**Num pod do EKS, um `AWS_ACCESS_KEY_ID` esquecido não cala o IRSA — no .NET.**
Cala em Python e em Go. Um time poliglota que corrige o ConfigMap "porque o IRSA
não funciona" pode estar consertando um sintoma que o worker .NET nunca teve, e
deixando o worker Python quebrado do mesmo jeito.

**Na sua máquina, o `~/.aws/credentials` vence o `AWS_ACCESS_KEY_ID` que você
acabou de exportar.** É a causa de um bug de desenvolvimento que consome tempo:
você exporta a credencial no shell, roda o programa, e ele continua usando o
perfil `[default]` que estava lá desde o ano passado. Em Python e em Go o
`export` teria funcionado.

### O bug que a ordem causa — e onde ele realmente mora

Um `AWS_ACCESS_KEY_ID` esquecido no Deployment, num ConfigMap ou num `.env`
**cala o IRSA por completo em Python e em Go**: a cadeia responde no elo da
variável de ambiente e nunca chega no de web identity. O pod tem a anotação
certa, a role está certa, a trust policy está certa, e nada disso é usado.

Sintoma: `AccessDenied` mencionando um principal que você não reconhece, ou
pior, um usuário IAM antigo que ainda tinha permissão e mascarou o problema por
meses.

Primeiro comando quando o IRSA "não funciona", em qualquer linguagem:

```bash
kubectl exec -n cash deploy/consumer-registro -- env | grep AWS_
```

Se aparecer `AWS_ACCESS_KEY_ID` junto com `AWS_ROLE_ARN`, você achou **uma**
coisa errada — a variável não deveria estar lá de qualquer forma. Se ela é *a*
causa do problema depende da linguagem, e é isso que o `./run.sh diagnostico`
responde sem você ter que adivinhar.

> **Por que este guia não manda você decorar a ordem.** Ela muda entre SDKs,
> muda entre versões maiores do mesmo SDK, e a documentação oficial nem sempre
> acompanha. O hábito que vale é o do §6: rodar o diagnóstico dentro do
> ambiente em questão e **ler qual provedor venceu**. Um minuto de medição
> ganha de uma tarde de dedução a partir de uma tabela.

---

## 3. O que acontece no IRSA, passo a passo dentro do processo

```
1. new AmazonSQSClient()
     └─ não recebeu credencial → FallbackCredentialsFactory.GetCredentials()

2. A cadeia chega no provedor de web identity, que encontra:
       AWS_ROLE_ARN=arn:aws:iam::111122223333:role/asa-dev-...-registro
       AWS_WEB_IDENTITY_TOKEN_FILE=/var/run/secrets/eks.amazonaws.com/serviceaccount/token

3. Instancia AssumeRoleWithWebIdentityCredentials.
   Repare: instancia, não chama a AWS ainda. É preguiçoso.

4. Primeira chamada de verdade (ReceiveMessageAsync):
       - LÊ O ARQUIVO DO TOKEN NESTE MOMENTO
       - chama sts:AssumeRoleWithWebIdentity
       - guarda o resultado em memória com a data de expiração

5. Chamadas seguintes reaproveitam as credenciais em memória.
   Zero chamada ao STS.

6. Perto de expirar (o SDK renova ANTES, não no vencimento),
   volta ao passo 4: relê o arquivo e assume de novo.
```

Dois detalhes que só aparecem em produção:

**O token é relido a cada renovação, não cacheado.** Isso é essencial: o kubelet rotaciona aquele arquivo periodicamente. Um SDK que lesse o token uma vez só quebraria depois de algumas horas. Se você um dia implementar isso na mão — não faça —, releia o arquivo sempre.

**A renovação é preemptiva.** O SDK não espera a credencial vencer; renova com uma folga. Por isso você nunca vê `ExpiredToken` num worker saudável.

---

## 4. A consequência mais importante para o seu código

> **Reutilize o client. Não crie um `AmazonSQSClient` por mensagem, por request ou por método.**

O cache de credenciais mora **dentro do objeto de credenciais**, que mora dentro do client. Criar um client novo joga esse cache fora e força um `AssumeRoleWithWebIdentity` novo. Num worker que processa milhares de mensagens, isso vira:

- latência extra em toda operação
- throttling do STS (que tem limite de requisições)
- ruído enorme no CloudTrail — milhares de eventos `AssumeRoleWithWebIdentity` por hora

Os clients do SDK são **thread-safe e feitos para viver o processo inteiro**. É exatamente por isso que o lab registra como singleton:

```csharp
builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(config));
```

Se você usa `AWSSDK.Extensions.NETCore.Setup`, o equivalente idiomático é:

```csharp
builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonSQS>();   // já registra como singleton
```

O mesmo raciocínio vale para `HttpClient`, e pela mesma razão: objeto caro de construir, barato de reusar.

---

## 5. A região é uma segunda cadeia, independente

Erro comum de quem acha que credencial e região vêm juntas. São duas resoluções separadas:

| # | Origem da região |
|---|---|
| 1 | `RegionEndpoint` explícito no config |
| 2 | `AWS_REGION` |
| 3 | `AWS_DEFAULT_REGION` |
| 4 | `region` no perfil do `~/.aws/config` |
| 5 | IMDS (a região da própria instância) |

Sintoma clássico: credenciais resolvem perfeitamente e a aplicação estoura com *"No RegionEndpoint or ServiceURL configured"*. Não é problema de IAM — é região faltando. No EKS, o `AWS_REGION` não é injetado pelo webhook do IRSA; você declara no Deployment (o lab declara).

---

## 6. Diagnosticando

O [`examples/multilinguagem/dotnet/Diagnostico.cs`](../examples/multilinguagem/dotnet/Diagnostico.cs) é um programa curto que imprime **qual provedor venceu** e **quem você é**. Vale copiar para dentro de um container quando algo não fecha. Rode com:

```bash
./run.sh diagnostico dotnet      # e também: python, go
```

Rodar nas três linguagens é o que revela a divergência da §2 — e é o exercício da etapa 15 do [ROTEIRO](../ROTEIRO.md).

O essencial dele:

```csharp
var credenciais = FallbackCredentialsFactory.GetCredentials();

// O TIPO do objeto já diz qual provedor venceu:
//   EnvironmentVariablesAWSCredentials      → env vars
//   AssumeRoleWithWebIdentityCredentials    → IRSA
//   URIBasedRefreshingCredentialHelper      → ECS / Pod Identity
//   DefaultInstanceProfileAWSCredentials    → IMDS (role do nó!)
Console.WriteLine(credenciais.GetType().Name);

using var sts = new AmazonSecurityTokenServiceClient(credenciais);
var eu = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest());
Console.WriteLine($"{eu.Account} {eu.Arn}");
```

Como ler o resultado do `GetCallerIdentity`:

| ARN devolvido | Significa |
|---|---|
| `arn:aws:sts::...:assumed-role/asa-dev-...-registro/...` | IRSA funcionou |
| `arn:aws:sts::...:assumed-role/eks-node-group-...` | **IRSA não pegou** — você está na role do nó |
| `arn:aws:iam::...:user/alguem` | credencial permanente de usuário — não deveria estar aí |

O segundo caso é traiçoeiro porque a role do nó costuma ter permissões, então parte das coisas funciona e você não desconfia.

Se precisar ver a cadeia decidindo, ligue o log do SDK:

```csharp
AWSConfigs.LoggingConfig.LogTo = LoggingOptions.Console;
AWSConfigs.LoggingConfig.LogResponses = ResponseLoggingOption.OnError;
```

---

## 7. Tabela de sintomas

| Sintoma | Causa provável |
|---|---|
| `AccessDenied` citando a role do nó | `serviceAccountName` faltando; em **Python/Go**, também env var sobrescrevendo o IRSA (§2) |
| IRSA funciona em .NET e falha em Python/Go, mesmo cluster | env var no ConfigMap: ela vence o IRSA nesses dois SDKs e não no .NET (§2) |
| `export AWS_ACCESS_KEY_ID` ignorado na sua máquina | .NET: o perfil `~/.aws/credentials` vence a variável de ambiente (§2) |
| `AccessDenied` citando usuário IAM | credencial permanente esquecida no ambiente |
| `No RegionEndpoint or ServiceURL configured` | falta `AWS_REGION` — não é IAM |
| `ExpiredToken` num worker de longa duração | alguém guardou o resultado de um `AssumeRole` em variável em vez de usar `AssumeRoleAWSCredentials` |
| Throttling do STS, CloudTrail cheio de `AssumeRole` | client sendo criado por requisição em vez de singleton |
| `AWS_ROLE_ARN` ausente dentro do pod | webhook não injetou: SA errado, anotação com typo, ou pod anterior à anotação (precisa de `rollout restart`) |
| Funciona local, falha no cluster | local usa env vars; no cluster a role real é mais restrita — compare as permissões, não o código |
