# Assume role e acesso entre contas

Continuação de [`01-iam-explicado.md`](01-iam-explicado.md). Duas coisas que aparecem juntas o tempo todo em ambiente bancário, onde dev/hml/prod costumam ser contas AWS separadas.

---

## 1. O que `AssumeRole` faz de verdade

### O STS é uma máquina de credenciais temporárias

Toda vez que alguém assume uma role, quem responde é o **STS** (Security Token Service). Ele recebe uma prova de identidade, confere contra a trust policy da role, e devolve um trio de credenciais com prazo de validade:

```json
{
  "AccessKeyId": "ASIAXXXXXXXXXXXXXXXX",
  "SecretAccessKey": "wJalr...",
  "SessionToken": "IQoJb3JpZ2luX2VjE...",
  "Expiration": "2026-08-08T18:42:00Z"
}
```

**Dica de diagnóstico que vale ouro:** olhe o prefixo do `AccessKeyId`.

- `AKIA...` → credencial **permanente**, de usuário IAM. Se você achar isso num pod, tem algo errado.
- `ASIA...` → credencial **temporária**, do STS. É o que você espera ver.

A presença do `SessionToken` diz a mesma coisa. Credencial permanente não tem session token.

### As quatro portas de entrada

Todas devolvem a mesma coisa; o que muda é a prova de identidade que você apresenta:

| API | Prova apresentada | Onde você já viu |
|---|---|---|
| `sts:AssumeRole` | credencial de outro principal IAM | cross-account, ECS, Lambda, role chaining |
| `sts:AssumeRoleWithWebIdentity` | JWT de um provedor OIDC | **IRSA**, GitHub Actions, Cognito |
| `sts:AssumeRoleWithSAML` | asserção SAML | login corporativo via IdP |
| `sts:GetSessionToken` | MFA de um usuário IAM | acesso humano pontual |

O IRSA do lab é a segunda linha. O cross-account da Parte 2 é a primeira.

### Parâmetros que importam

```bash
aws sts assume-role \
  --role-arn arn:aws:iam::444455556666:role/asa-prd-dados-gravador-comprovantes \
  --role-session-name cobranca-registro \
  --duration-seconds 3600 \
  --external-id abc-123
```

**`--role-session-name`** — obrigatório, e não é decorativo. Ele aparece no CloudTrail e na identidade resultante. Colocar `sessao1` ali é jogar fora a rastreabilidade; colocar o nome do workload (ou o ID da requisição) faz o log responder "quem foi" sozinho.

**`--duration-seconds`** — padrão 1h. O teto é o `MaxSessionDuration` da role, que vai até 12h. **Exceção importante:** em *role chaining* (uma role assumindo outra), o máximo é **1 hora**, independentemente do que a role permita. Isso pega gente que configurou 12h e não entende por que a sessão morre em 60 minutos.

**`--external-id`** — para quando um **terceiro** assume sua role (fornecedor SaaS, ferramenta de observabilidade). Resolve o *confused deputy problem*: sem ele, se o fornecedor atende você e o seu concorrente, um cliente poderia induzir o fornecedor a assumir a role do outro. O ExternalId é um segredo compartilhado que só você e ele conhecem. **Regra: se é terceiro, exija ExternalId. Se é conta sua, não precisa.**

**`--policy` (session policy)** — restringe ainda mais no momento de assumir. A permissão efetiva é a **interseção** entre a permission policy da role e a session policy. Serve para um mesmo serviço assumir a mesma role com escopos diferentes por requisição. Session policy nunca *amplia* nada.

**`--tags` (session tags)** — exige `sts:TagSession` na trust policy. As tags viram condições utilizáveis via `aws:PrincipalTag/...`, o que habilita ABAC (controle por atributo em vez de por ARN).

### A identidade que sai do outro lado — e a pegadinha

Depois de assumir, você **não é** a role. Você é uma *sessão* daquela role:

```
arn:aws:sts::444455556666:assumed-role/asa-prd-dados-gravador-comprovantes/cobranca-registro
    │                                   │                                   │
    └── sts, não iam                    └── nome da role                    └── o session name
```

E aqui está a pegadinha que consome tardes inteiras:

> **Em policies você referencia a role (`arn:aws:iam::...:role/X`), nunca a sessão (`arn:aws:sts::...:assumed-role/X/Y`).**

Se você copiar o ARN que aparece no `get-caller-identity` e colar numa bucket policy, ela não vai funcionar. A mensagem de erro mostra o ARN da sessão, mas a policy precisa do ARN da role.

A exceção é dentro de `Condition`, onde `aws:userid` e `aws:PrincipalArn` operam sobre a sessão.

Comando de primeira parada, sempre:

```bash
aws sts get-caller-identity
```

### Como isso aparece no código

Você quase nunca chama `AssumeRole` na mão. O SDK tem um provider que assume e **renova sozinho** antes de expirar:

```csharp
using Amazon.Runtime;
using Amazon.S3;

// credenciais da conta de origem: no EKS, vêm do IRSA sem você fazer nada
var origem = FallbackCredentialsFactory.GetCredentials();

// AssumeRoleAWSCredentials cuida do refresh. Nunca guarde o resultado
// de um AssumeRole em variável: ele expira e você precisa renovar.
var destino = new AssumeRoleAWSCredentials(
    origem,
    "arn:aws:iam::444455556666:role/asa-prd-dados-gravador-comprovantes",
    "cobranca-registro");

var s3 = new AmazonS3Client(destino);
```

Montado por completo, com os pontos de atenção:

```csharp
using Amazon.Runtime;
using Amazon.S3;

public sealed class CrossAccountUploader
{
    private readonly IAmazonS3 _s3;

    public CrossAccountUploader(string roleArnDestino, string sessionName, string? externalId = null)
    {
        // 1. Credenciais da conta de ORIGEM. No EKS isso resolve o IRSA
        //    sozinho — o SDK acha AWS_ROLE_ARN e AWS_WEB_IDENTITY_TOKEN_FILE.
        var origem = FallbackCredentialsFactory.GetCredentials();

        // 2. Assume a role da conta de DESTINO.
        //    AssumeRoleAWSCredentials RENOVA SOZINHO antes de expirar. É por
        //    isso que se usa esta classe em vez de chamar AssumeRoleAsync na
        //    mão: o resultado de um AssumeRole manual expira em 1h, e um
        //    worker que roda por dias começaria a tomar ExpiredToken.
        var destino = new AssumeRoleAWSCredentials(origem, roleArnDestino, sessionName,
            new AssumeRoleAWSCredentialsOptions
            {
                DurationSeconds = 3600,
                // Obrigatório SOMENTE quando quem assume é um terceiro.
                // Entre contas da mesma empresa, deixe nulo.
                ExternalId = externalId,
            });

        _s3 = new AmazonS3Client(destino);
    }
}
```

Três coisas que o snippet esconde e valem repetir:

- Em *role chaining* a sessão dura **no máximo 1 hora**, mesmo que a role permita 12h.
- O `sessionName` aparece no CloudTrail da conta de destino — é o que responde "quem foi".
- Se o bucket exigir SSE-KMS, a **key policy** da chave também precisa citar o principal de origem. Ter `kms:GenerateDataKey` na role não basta.

Localmente, o equivalente vive no `~/.aws/config` e o SDK entende sozinho:

```ini
[profile cash-prd]
region = us-east-1
sso_session = corp

[profile dados-prd]
role_arn = arn:aws:iam::444455556666:role/asa-prd-dados-gravador-comprovantes
source_profile = cash-prd
role_session_name = daniel-local
```

Com isso, `aws s3 ls --profile dados-prd` já faz o assume role por baixo.

---

## 2. Cross-account

### Origem e destino: dois papéis, não dois rótulos

Todo o resto desta seção usa "conta de origem" e "conta de destino" o tempo todo. Vale fixar o significado antes, porque é uma daquelas palavras que parecem óbvias e depois atrapalham.

> **Origem é a conta onde o *workload* roda. Destino é a conta onde o *recurso* está.**

O importante é que **são papéis relativos a uma chamada, não propriedades da conta**. A mesma conta é origem numa chamada e destino em outra. No lab, `cash-prd` é origem quando o consumer grava no S3 do time de dados; se amanhã uma Lambda de `dados-prd` passasse a ler a fila `cobranca-registro`, `dados-prd` viraria a origem daquela chamada. Ninguém "é" a conta de origem — cada conta é origem da direção em que está chamando.

### O que mora de cada lado, neste lab

Antecipando o desenho de "Cenário do lab", logo abaixo:

| | Conta | O que vive lá |
|---|---|---|
| **Origem** | `cash-prd` — `111122223333` | cluster EKS, os pods dos três consumers, o provedor OIDC do cluster, as ServiceAccounts, as roles IRSA |
| **Destino** | `dados-prd` — `444455556666` | bucket `asa-prd-comprovantes-cobranca`, a chave KMS que o cifra, a fila SQS de auditoria |

No EKS, "origem" é concretamente uma **cadeia de identidade**, não uma conta abstrata:

```
ServiceAccount sa-consumer-registro   (no namespace cash)
  └── anotação eks.amazonaws.com/role-arn
        └── role IRSA em 111122223333
              └── credencial ASIA... vinda do STS
                    └── é ESTA credencial que assume a role de 444455556666
```

É por isso que no código a variável se chama exatamente isso (§1, "Como isso aparece no código"): `origem` é o que o `FallbackCredentialsFactory` devolve — a credencial que o IRSA já resolveu — e `destino` é o `AssumeRoleAWSCredentials` construído em cima dela. O SDK torna a distinção literal.

### O mapa que faz o resto da seção fazer sentido

Cada lado responde por documentos diferentes, e é isso que dá conteúdo à regra dos dois lados:

| Documento | Mora na conta | Responde à pergunta |
|---|---|---|
| **Identity policy** da role do worker | **origem** | esta role pode chamar `sts:AssumeRole` (ou a ação direta) naquele ARN? |
| **Trust policy** da role de destino | **destino** | quem, lá de fora, eu aceito que me assuma? |
| **Permission policy** da role de destino | **destino** | depois de assumida, o que ela faz? |
| **Resource policy** (bucket, fila, tópico) | **destino** | aceito um principal de outra conta chamando direto? |
| **Key policy** do KMS | **destino** | este principal pode usar a chave? |
| **SCP / permission boundary** | **qualquer um dos dois** | a organização proíbe isso de um dos lados? |

Repare que a origem tem **um** documento e o destino tem de dois a quatro. Daí a assimetria prática: a maioria dos `AccessDenied` cross-account é falha do lado do destino, e o checklist de §4 está ordenado por isso.

### Onde cada padrão cruza a fronteira

Os três padrões que vêm a seguir diferem justamente em *onde* a travessia acontece:

| Padrão | Quem faz a chamada final | Identidade que o destino vê |
|---|---|---|
| **1 — Assume role** | uma role **do destino**, assumida pela origem | role de destino; a origem só aparece no evento `AssumeRole` |
| **2 — Resource policy direta** | a própria role **da origem** | role de origem — auditoria melhor |
| **3 — IRSA cross-account** | uma role **do destino**, assumida direto pelo pod | role de destino, mas com o `sub` do ServiceAccount no evento |

### A pegadinha do Padrão 3

O padrão 3 embaralha o vocabulário de propósito, e é onde mais gente se perde:

> O **provedor OIDC é registrado na conta de destino** (`arn:aws:iam::444455556666:oidc-provider/...`), mas o *issuer* que ele aponta é o do cluster que roda na **origem**.

Ou seja: existe um objeto IAM na conta de destino cujo nome contém a URL de um cluster que não é dela. Isso não move o cluster de conta e não faz de `444455556666` a origem — só significa que o destino passou a confiar diretamente no OIDC da origem, dispensando o hop intermediário. A origem continua sendo quem hospeda o cluster e emite o token.

### "Conta de origem" não é `aws:SourceAccount`

Colisão de termo que vale desarmar agora, porque as duas coisas aparecem em JSONs parecidos. Em [`06-eks-ecs-lambda.md`](06-eks-ecs-lambda.md) você vê isto numa trust policy:

```json
"Condition": {
  "StringEquals": { "aws:SourceAccount": "111122223333" },
  "ArnLike":      { "aws:SourceArn": "arn:aws:ecs:us-east-1:111122223333:*" }
}
```

Isso **não** tem a ver com assume role entre contas. `aws:SourceAccount` e `aws:SourceArn` são para o caso em que um **serviço da AWS** (ECS, S3, SNS, CloudWatch) assume uma role ou chama outro serviço *em seu nome*. A condição amarra a chamada ao recurso específico que a originou, resolvendo o confused deputy no lado do serviço — é o primo do `ExternalId` de §1, que resolve o mesmo problema no lado do terceiro.

Regra para não confundir:

- **conta de origem / destino** → duas contas suas, um principal atravessando a fronteira. Vocabulário deste guia.
- **`aws:SourceAccount` / `aws:SourceArn`** → um serviço AWS agindo em seu nome. Vocabulário de trust policy de serviço.

Um `Principal` de serviço (`"Service": "ecs-tasks.amazonaws.com"`) é o sinal de que você está no segundo caso, não no primeiro.

### Descobrindo em qual lado você está

Na prática, `aws sts get-caller-identity` (§1) responde em uma linha, e o número da conta no ARN é a resposta. Vale lembrar que uma chamada cross-account deixa rastro no CloudTrail das **duas** contas — o `AssumeRole` na origem, a chamada efetiva no destino —, detalhe que fecha §4. Para praticar o isolamento sem AWS de verdade, §5 mostra como o Floci separa contas por `AWS_ACCESS_KEY_ID`.

Se preferir ver acontecer em vez de ler, **`./run.sh cross-account`** roda esta seção inteira contra o Floci: cria a fila numa conta, prova que a outra não a enxerga, monta os três documentos de §2, atravessa com `AssumeRole` e mostra o número da conta mudando no `get-caller-identity`. Termina com seis perguntas e com a lista honesta do que o exercício prova e do que não prova.

E a aba **Cross-account** do [visualizador](assets/visualizador.html) é o mesmo percurso em forma de desenho: duas contas lado a lado, os três padrões, e a cada passo o painel mostra qual identidade está em ação e qual dos documentos acima está decidindo. É o caminho mais rápido para ver *onde* cada quebra desta seção interrompe a cadeia.

---

### A regra que explica 90% das falhas

> **Entre contas, os dois lados precisam permitir. Um `Allow` só de um lado não basta.**

Dentro da mesma conta, um `Allow` na identity policy costuma ser suficiente. Entre contas, não: a conta que possui o recurso precisa concordar explicitamente, e a conta que possui o principal também. Nenhuma conta consegue se auto-conceder acesso à outra — é isso que torna a fronteira de conta a fronteira de segurança mais forte da AWS.

Quando algo não funciona entre contas, a pergunta certa é sempre: *qual dos dois lados eu esqueci?*

### Cenário do lab

Imagine o desenho que você encontraria num banco:

```
   conta cash-prd  (111122223333)          conta dados-prd  (444455556666)
   ┌──────────────────────────┐            ┌──────────────────────────┐
   │  cluster EKS             │            │  S3 comprovantes         │
   │  consumer-registro       │  ────────► │  KMS chave de dados      │
   │  role: ...consumer-      │            │  SQS fila de auditoria   │
   │        registro          │            │                          │
   └──────────────────────────┘            └──────────────────────────┘
```

Os workers rodam numa conta; os dados consolidados moram em outra. Há **três formas** de atravessar essa fronteira, e escolher errado dá trabalho depois.

---

### Padrão 1 — Assume role (delegação)

O worker assume uma role na conta de destino e passa a agir **como principal da conta de destino**.

**Lado origem** — identity policy da role do worker, na conta `111122223333`:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "AssumirRoleDaContaDeDados",
    "Effect": "Allow",
    "Action": "sts:AssumeRole",
    "Resource": "arn:aws:iam::444455556666:role/asa-prd-dados-gravador-comprovantes"
  }]
}
```

**Lado destino** — trust policy da role, na conta `444455556666`:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "ConfiaApenasNaRoleDoWorkerDeRegistro",
    "Effect": "Allow",
    "Principal": {
      "AWS": "arn:aws:iam::111122223333:role/asa-prd-cash-cobranca-consumer-registro"
    },
    "Action": "sts:AssumeRole"
  }]
}
```

**Lado destino** — permission policy, o que a role assumida pode fazer:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "GravarComprovantesNoBucketDeDados",
      "Effect": "Allow",
      "Action": ["s3:PutObject"],
      "Resource": "arn:aws:s3:::asa-prd-comprovantes-cobranca/registro/*"
    },
    {
      "Sid": "UsarAChaveDoBucket",
      "Effect": "Allow",
      "Action": ["kms:GenerateDataKey", "kms:Encrypt"],
      "Resource": "arn:aws:kms:us-east-1:444455556666:key/EXAMPLE-KEY-ID"
    }
  ]
}
```

**Quando usar:** sempre que o serviço de destino **não tem resource policy**. DynamoDB, EC2, RDS, a maioria dos serviços. Aí não há alternativa — assume role é o único caminho.

**Vantagem:** funciona para tudo, uniformemente.

**Desvantagem:** um hop a mais, e no CloudTrail da conta de destino a chamada aparece como a role de destino. A origem só é rastreável olhando o evento de `AssumeRole`.

#### Duas formas de escrever o `Principal`, e a diferença é grande

```json
"Principal": { "AWS": "arn:aws:iam::111122223333:root" }
```
Confia na **conta inteira**. Não significa "qualquer um da conta entra" — significa "delego para os administradores IAM daquela conta decidirem quem". O acesso só acontece se lá do outro lado alguém também conceder `sts:AssumeRole` na identity policy. É a forma mais comum e mais robusta.

```json
"Principal": { "AWS": "arn:aws:iam::111122223333:role/asa-prd-...-consumer-registro" }
```
Confia numa **role específica**. Mais apertado. Mas tem uma armadilha operacional pouco conhecida:

> Quando você nomeia uma role no `Principal`, a AWS guarda internamente o **ID único** daquela role, não o texto do ARN. Se a role for **apagada e recriada com o mesmo nome**, o ID muda e a trust policy quebra silenciosamente — você verá o principal virar algo como `AROA1234...` no JSON.

Se a sua esteira recria roles (Terraform com `replace`, por exemplo), prefira `:root` mais uma condição, ou esteja preparado para reaplicar a trust policy.

#### Confiando na organização inteira

Em empresa com dezenas de contas, listar ARN por ARN não escala:

```json
"Condition": {
  "StringEquals": { "aws:PrincipalOrgID": "o-exampleorgid" }
}
```

Confia em qualquer principal da sua organização AWS. Combinado com `:root` no principal, dá uma trust policy que sobrevive à criação de contas novas.

---

### Padrão 2 — Resource policy direta (sem assume role)

Alguns serviços aceitam um principal de outra conta diretamente na resource policy. Aí **não há AssumeRole nenhum** — o worker chama com as credenciais dele mesmo.

Bucket policy, na conta de destino:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "PermiteRoleDaOutraContaGravar",
    "Effect": "Allow",
    "Principal": {
      "AWS": "arn:aws:iam::111122223333:role/asa-prd-cash-cobranca-consumer-registro"
    },
    "Action": "s3:PutObject",
    "Resource": "arn:aws:s3:::asa-prd-comprovantes-cobranca/registro/*",
    "Condition": {
      "StringEquals": { "s3:x-amz-server-side-encryption": "aws:kms" }
    }
  }]
}
```

E a identity policy da role, na conta de origem, precisa permitir `s3:PutObject` naquele ARN. **Os dois lados, de novo.**

**Serviços que suportam:** S3, SQS, SNS, KMS, Secrets Manager, Lambda, ECR, EventBridge, API Gateway. **Não suportam:** DynamoDB, EC2, RDS, a maioria dos demais.

**Vantagem:** menos um hop, e no CloudTrail da conta de destino aparece a identidade real da origem — bem melhor para auditoria.

**Desvantagem:** só funciona para alguns serviços, então você acaba com dois padrões diferentes convivendo.

#### A pegadinha específica do S3

Objeto gravado por um principal de outra conta continua sendo **propriedade da conta que gravou**, não da dona do bucket. O resultado clássico: a equipe de dados não consegue ler os próprios comprovantes no próprio bucket.

Duas saídas, e a segunda é a certa hoje:

1. Exigir `bucket-owner-full-control` na condição da bucket policy.
2. Ligar **S3 Object Ownership = BucketOwnerEnforced** no bucket. Isso desativa ACLs de vez e faz o dono do bucket ser dono de tudo. É a recomendação atual da AWS e resolve o problema na raiz.

---

### Padrão 3 — IRSA direto entre contas

Menos conhecido e às vezes o mais elegante: você pode registrar o **provedor OIDC do cluster da conta A dentro da conta B**, e escrever a trust policy da role de B apontando diretamente para o ServiceAccount do cluster de A.

Trust policy da role na conta de destino:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "OidcDoClusterDaContaDeCash",
    "Effect": "Allow",
    "Principal": {
      "Federated": "arn:aws:iam::444455556666:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXAMPLED539D4633E53DE1B71EXAMPLE"
    },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {
        "oidc.eks.us-east-1.amazonaws.com/id/EXAMPLED539D4633E53DE1B71EXAMPLE:aud": "sts.amazonaws.com",
        "oidc.eks.us-east-1.amazonaws.com/id/EXAMPLED539D4633E53DE1B71EXAMPLE:sub": "system:serviceaccount:cash:sa-consumer-registro"
      }
    }
  }]
}
```

Repare: o `oidc-provider` está registrado na conta **444455556666**, apontando para o issuer do cluster que roda na conta **111122223333**.

O pod assume a role da conta de destino **diretamente**, sem hop intermediário, e mantém a granularidade de ServiceAccount que o role chaining perde. Anota-se o SA com o ARN da role da outra conta e pronto.

**Custo:** o provedor OIDC precisa ser registrado em cada conta de destino, e a plataforma precisa saber disso quando trocar o cluster.

---

### KMS: onde o cross-account mais falha

Se o recurso é criptografado com KMS — e em banco quase tudo é — existe **um terceiro documento** para acertar: a **key policy**.

A key policy é especial: diferente de outras resource policies, ela é *obrigatória*. Uma role sem menção na key policy não usa a chave, por mais `kms:Decrypt` que tenha na identity policy.

Formato, na conta de destino:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "AdministracaoDaChavePelaPropriaConta",
      "Effect": "Allow",
      "Principal": { "AWS": "arn:aws:iam::444455556666:root" },
      "Action": "kms:*",
      "Resource": "*"
    },
    {
      "Sid": "UsoPelaRoleDaOutraConta",
      "Effect": "Allow",
      "Principal": {
        "AWS": "arn:aws:iam::111122223333:role/asa-prd-cash-cobranca-consumer-registro"
      },
      "Action": ["kms:GenerateDataKey", "kms:Encrypt", "kms:DescribeKey"],
      "Resource": "*",
      "Condition": {
        "StringEquals": { "kms:ViaService": "s3.us-east-1.amazonaws.com" }
      }
    }
  ]
}
```

Repare no primeiro bloco: é a delegação padrão para a própria conta. Removê-lo "para endurecer" faz cada role da própria conta precisar ser citada nominalmente — o que costuma pegar times inteiros de surpresa.

O sintoma típico de esquecer o segundo bloco:

```
AccessDenied: ... is not authorized to perform: kms:GenerateDataKey on resource: ...
```

E o mais frustrante: **a permissão na role está lá, correta**. Falta a menção na key policy. Se você lembrar de checar a key policy sempre que aparecer `kms:` numa mensagem de erro cross-account, economiza horas.

Vale para: SQS com SSE-KMS, S3 com SSE-KMS, Secrets Manager, RDS criptografado, EBS criptografado.

---

## 3. Escolhendo

| Situação | Padrão |
|---|---|
| Serviço sem resource policy (DynamoDB, EC2, RDS) | Assume role — não há alternativa |
| S3, SQS, SNS entre contas, chamada simples | Resource policy direta — menos hop, melhor auditoria |
| Pod no EKS acessando recurso de outra conta | IRSA cross-account, se der para registrar o OIDC lá |
| Terceiro/fornecedor acessando sua conta | Assume role **com ExternalId**, sempre |
| Muitas contas na mesma organização | `:root` + condição `aws:PrincipalOrgID` |

Regra prática: **prefira resource policy quando o serviço suportar**, e use assume role quando não suportar ou quando precisar que a chamada apareça com identidade da conta de destino.

---

## 4. Checklist de debug cross-account

Na ordem em que costuma dar errado:

1. `aws sts get-caller-identity` — você é quem pensa que é?
2. A identity policy na conta de **origem** permite a ação (ou o `sts:AssumeRole`)?
3. A trust policy ou a resource policy na conta de **destino** cita o principal certo?
4. Você usou o ARN da **role** (`:role/X`) e não o da **sessão** (`assumed-role/X/Y`)?
5. A role foi apagada e recriada depois que a trust policy foi escrita?
6. Se tem KMS envolvido: a **key policy** menciona o principal de origem?
7. Se é S3: o Object Ownership está como `BucketOwnerEnforced`?
8. Há SCP ou permission boundary bloqueando de um dos lados?
9. Se é role chaining: a sessão está expirando em 1h e você esperava 12h?
10. Se é terceiro: o `ExternalId` bate?

Chamadas cross-account aparecem no CloudTrail das **duas** contas — o `AssumeRole` na origem, a chamada efetiva no destino. Ter as duas trilhas à mão encurta muito a investigação.

---

## 5. Praticando o isolamento no Floci

<cite index="3-1">O Floci suporta multi-account: se o `AWS_ACCESS_KEY_ID` tiver exatamente 12 dígitos, ele é usado como ID da conta, e recursos criados por uma conta ficam invisíveis para outra.</cite> Credenciais de `AssumeRole` também resolvem para a conta da role assumida — então o fluxo "assume role e depois provisiona na outra conta" funciona localmente:

```bash
export AWS_ENDPOINT_URL=http://localhost:4566 AWS_DEFAULT_REGION=us-east-1
export AWS_SECRET_ACCESS_KEY=test

# recurso na conta de destino
AWS_ACCESS_KEY_ID=444455556666 aws s3 mb s3://asa-prd-comprovantes-cobranca

# a conta de origem não enxerga o bucket da outra
AWS_ACCESS_KEY_ID=111122223333 aws s3 ls
```

Serve para entender o **isolamento** entre contas e praticar os comandos. Não serve para validar se as policies concedem o acesso certo — para isso, Policy Simulator.

---

## Referências

- Como funciona o AssumeRole — https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_common-scenarios_aws-accounts.html
- Confused deputy e ExternalId — https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html
- Session policies — https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies.html#policies_session
- Acesso cross-account no S3 — https://docs.aws.amazon.com/AmazonS3/latest/userguide/example-walkthroughs-managing-access.html
- Key policies do KMS — https://docs.aws.amazon.com/kms/latest/developerguide/key-policies.html
- IRSA cross-account — https://docs.aws.amazon.com/eks/latest/userguide/cross-account-access.html
