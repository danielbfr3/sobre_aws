# IAM explicado do zero

Guia para quem escreve a aplicação e precisa entender a parte de infra que encosta nela. Não pressupõe nada além de saber o que é uma aplicação que chama uma API.

---

## 1. O modelo mental

Toda chamada que sua aplicação faz para a AWS — `sqs:ReceiveMessage`, `s3:GetObject`, qualquer uma — passa por uma pergunta antes de ser executada:

> **Quem** está pedindo para fazer **o quê**, em **qual recurso**, sob **quais condições**?

O IAM é o serviço que responde essa pergunta. Quatro peças:

| Peça | O que é | Exemplo |
|---|---|---|
| **Principal** | quem está pedindo | a role `asa-dev-cash-cobranca-consumer-registro` |
| **Action** | a operação | `sqs:ReceiveMessage` |
| **Resource** | em que | `arn:aws:sqs:us-east-1:111122223333:cobranca-registro` |
| **Condition** | sob que circunstância | só se vier via TLS, só se o IP for X |

Uma **policy** (política) é um documento JSON que combina essas quatro peças em uma lista de statements. É isso e nada mais.

### Como a AWS decide

A regra é curta e vale sempre:

```
1. Existe um Deny explícito em algum lugar?  →  NEGA. Fim. Nada reverte isso.
2. Existe um Allow explícito?                →  PERMITE.
3. Nenhum dos dois?                          →  NEGA (deny implícito).
```

O terceiro item é o que mais surpreende quem vem de outros sistemas: **por padrão, nada é permitido**. Uma role recém-criada sem nenhuma policy anexada não consegue fazer absolutamente nada. Você não "tira" permissões, você "dá".

O primeiro item é o motivo de existirem SCPs e permission boundaries (seção 9): um `Deny` no nível da organização não pode ser desfeito por ninguém dentro da conta, nem pelo administrador.

---

## 2. Anatomia de um statement

Pegue `infra/iam/policy-consumer-registro.json` e leia campo a campo:

```json
{
  "Sid": "ConsumirApenasAPropriaFila",
  "Effect": "Allow",
  "Action": ["sqs:ReceiveMessage", "sqs:DeleteMessage"],
  "Resource": [
    "arn:aws:sqs:us-east-1:111122223333:cobranca-registro",
    "arn:aws:sqs:us-east-1:111122223333:cobranca-registro-dlq"
  ]
}
```

- **`Sid`** — identificador livre, só para humanos. Não afeta nada. Use para explicar a intenção.
- **`Effect`** — `Allow` ou `Deny`. Só isso.
- **`Action`** — sempre no formato `serviço:Operação`. Aceita curinga (`sqs:*`), e é justamente isso que você quer evitar.
- **`Resource`** — o ARN do recurso. Curinga `*` aqui significa "todos os recursos desse tipo na conta".
- **`Condition`** — opcional, restringe ainda mais.
- **`Principal`** — **só existe em trust policies e resource policies**, nunca em identity policies. Se o documento diz *quem sou eu*, ele não precisa dizer quem é o principal.

### Lendo um ARN

ARN é o "endereço" de qualquer coisa na AWS. Sempre no mesmo formato:

```
arn : aws : sqs : us-east-1 : 111122223333 : cobranca-registro
 │     │     │        │            │              │
 │     │     │        │            │              └── nome do recurso
 │     │     │        │            └── ID da conta AWS (12 dígitos)
 │     │     │        └── região
 │     │     └── serviço
 │     └── partição (aws, aws-cn, aws-us-gov)
 └── literal fixo
```

Alguns serviços deixam campos vazios porque não se aplicam. S3 é global e sem dono no ARN: `arn:aws:s3:::meu-bucket`. Repare nos três `:` seguidos — região e conta em branco. IAM também é global: `arn:aws:iam::111122223333:role/minha-role`.

Saber ler ARN resolve metade dos erros de policy, porque a maioria é ARN escrito errado.

---

## 3. Os dois lugares onde uma permissão pode morar

Esta é a distinção que mais confunde, e ela aparece duas vezes no nosso lab.

### Identity-based policy — colada em *quem*

Fica anexada a uma role (ou usuário, ou grupo). Diz o que **essa identidade** pode fazer.

```
role asa-dev-...-consumer-registro  →  pode fazer sqs:ReceiveMessage na fila X
```

São os arquivos `infra/iam/policy-*.json`.

### Resource-based policy — colada *no recurso*

Fica anexada ao recurso. Diz **quem** pode chamá-lo.

```
fila cobranca-registro  →  aceita sqs:SendMessage vindo do serviço SNS
```

É o bloco que o `bootstrap.sh` aplica com `sqs set-queue-attributes --attributes Policy=...`. Sem ele, em AWS real, o SNS não consegue entregar na fila — e o sintoma é cruel: a subscription aparece como confirmada, o publish retorna sucesso, e a mensagem simplesmente nunca chega. Não há erro em lugar nenhum.

**Quando você precisa dos dois?** Dentro da mesma conta, um `Allow` de qualquer um dos lados normalmente basta. Entre contas diferentes, você precisa de **allow nos dois lados** — a identity policy da role na conta A *e* a resource policy do recurso na conta B. Essa é a regra que faz gente perder uma tarde, e está detalhada em [`02-assume-role-cross-account.md`](02-assume-role-cross-account.md).

Nem todo serviço tem resource policy. SQS, SNS, S3, KMS, Secrets Manager e Lambda têm. DynamoDB e EC2 não.

---

## 4. Usuário vs Role

### Usuário IAM

Uma identidade permanente com credencial permanente: `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY`. A chave nunca expira sozinha.

Para aplicação, isso é considerado falha grave em ambiente bancário, por motivos concretos:

- Vazou uma vez, vale para sempre até alguém revogar manualmente.
- Aparece em `kubectl describe pod`, em dump de env, em log de crash, em `git log` mais cedo ou mais tarde.
- Rotacionar exige redeploy coordenado de tudo que usa aquela chave.
- No CloudTrail tudo aparece como "usuário X". Três serviços compartilhando a chave = você não sabe qual fez o quê durante um incidente.

Usuário IAM ainda faz sentido para pessoas (e mesmo assim o padrão hoje é SSO) e para sistemas fora da AWS que não têm outro jeito de se autenticar.

### Role

Uma identidade **sem credencial permanente**. Ninguém faz login numa role. Quem for autorizado **assume** a role e recebe de volta credenciais temporárias emitidas pelo **STS** (Security Token Service): um trio `AccessKeyId` + `SecretAccessKey` + `SessionToken`, válido por 1 hora por padrão, renovado automaticamente pelo SDK.

O ganho não é abstrato:

| | Usuário | Role |
|---|---|---|
| Credencial expira | Não | Sim, ~1h |
| Aparece no YAML/env | Sim | Não |
| Rotação | Manual, com redeploy | Automática, invisível |
| Rastreio no CloudTrail | "usuário X" | "role Y, sessão Z" |
| Vazou o log | Comprometido | Expirado antes de ser útil |

### Os dois documentos de toda role

Confundir estes dois é o erro nº 1 de quem está começando:

| Documento | Pergunta | Onde no console | Onde no lab |
|---|---|---|---|
| **Trust policy** | *Quem* pode assumir esta role? | aba "Relações de confiança" | `trust-policy-*.json` |
| **Permission policy** | O que quem assumiu *pode fazer*? | aba "Permissões" | `policy-*.json` |

Uma role sem trust policy é inútil (ninguém consegue assumir). Uma role sem permission policy é assumível mas impotente. As duas coisas são necessárias e são gerenciadas separadamente — inclusive por comandos diferentes da CLI, como você vê em `infra/aws/create-roles.sh`:

```bash
aws iam create-role      --assume-role-policy-document file://trust.json   # trust
aws iam put-role-policy  --policy-document              file://perm.json   # permissão
```

---

## 5. IRSA: como o pod prova quem é

No EKS, o mecanismo clássico chama-se **IRSA** — *IAM Roles for Service Accounts*. O problema que ele resolve: a AWS não faz ideia do que é um pod. Ela entende OIDC. Então o EKS expõe o cluster como um provedor de identidade OIDC, e a AWS passa a confiar nos tokens que aquele cluster assina.

Passo a passo do que acontece quando seu pod sobe:

```
1. O cluster EKS tem um provedor OIDC registrado no IAM da conta.
   (feito uma vez por cluster, geralmente pela equipe de plataforma)
       aws eks describe-cluster --name meu-cluster \
         --query 'cluster.identity.oidc.issuer'
       → https://oidc.eks.us-east-1.amazonaws.com/id/EXAMPLED539D46...

2. Você anota o ServiceAccount:
       eks.amazonaws.com/role-arn: arn:aws:iam::111122223333:role/asa-dev-...-registro

3. O pod é criado. Um webhook de admissão do EKS intercepta e injeta:
       AWS_ROLE_ARN=arn:aws:iam::111122223333:role/asa-dev-...-registro
       AWS_WEB_IDENTITY_TOKEN_FILE=/var/run/secrets/eks.amazonaws.com/serviceaccount/token
   e monta um projected volume contendo um JWT assinado pelo cluster.
   Esse JWT tem validade curta e é renovado pelo kubelet.

4. Seu código chama new AmazonSQSClient(). Sem credencial nenhuma.
   O SDK percorre a cadeia padrão de credenciais, encontra aquelas duas
   variáveis, lê o token do arquivo e chama:
       sts:AssumeRoleWithWebIdentity(role_arn, web_identity_token)

5. O STS:
       - valida a assinatura do JWT contra o provedor OIDC do cluster
       - lê os claims do token, principalmente:
             "aud": "sts.amazonaws.com"
             "sub": "system:serviceaccount:cash:sa-consumer-registro"
       - confere esses claims contra a Condition da trust policy da role
       - se bater, devolve credenciais temporárias

6. O SDK usa as credenciais e renova sozinho antes de expirarem.
```

O ponto que costuma demorar a cair: **a trust policy amarra a role a um ServiceAccount específico de um namespace específico**, através do claim `sub`. Um pod no namespace `default`, ou usando outro SA, não consegue assumir a role — mesmo sabendo o ARN, mesmo estando no mesmo cluster. É isso que impede um serviço de usar as permissões do outro.

Repare também no `aud`. Ele parece burocrático e não é: sem checar a audiência, um token emitido para outro destino poderia ser reapresentado ao STS. Sempre inclua.

### A alternativa mais nova: EKS Pod Identity

Desde o fim de 2023 existe o **EKS Pod Identity**, que faz o mesmo com menos cerimônia: um agente roda como DaemonSet, você cria uma *association* entre ServiceAccount e role pela API do EKS, e a trust policy vira apenas:

```json
{
  "Effect": "Allow",
  "Principal": { "Service": "pods.eks.amazonaws.com" },
  "Action": ["sts:AssumeRole", "sts:TagSession"]
}
```

Sem OIDC por cluster, sem trust policy diferente para cada cluster, a mesma role reutilizável em vários clusters. Ver `infra/iam/trust-policy-pod-identity.json`.

**Qual dos dois usar?** IRSA é o que você mais encontra em ambiente existente. Pod Identity é o que costuma ser escolhido para coisas novas. Vale perguntar no seu time qual o cluster usa — a resposta muda o formulário que você preenche para pedir uma role nova.

### O anti-padrão que você vai encontrar

Colocar as permissões na **role do nó** (a instance role do nodegroup EC2). Funciona, e é por isso que sobrevive em muitos clusters. O problema: **todos os pods daquele nó** herdam essas permissões, incluindo o sidecar de log, o agente de métricas e qualquer container comprometido. Se você vir isso, é dívida técnica, não decisão de arquitetura.

---

## 6. Uma role por worker

### Por que a granularidade é o ServiceAccount

Já vimos que a trust policy cita `system:serviceaccount:<namespace>:<sa>`. Então a unidade real de identidade não é "o serviço", é **o ServiceAccount**. A pergunta prática vira: quantos ServiceAccounts eu crio?

### As duas filosofias

**Uma role por worker** — 5 SAs, 5 roles, o que este lab faz agora.

Cada consumer enxerga só a própria fila e a própria DLQ. O publisher só publica. O worker de arquivos só lê S3. Se o worker de rejeição for comprometido, ou se alguém trocar o `QUEUE_URL` por engano num PR, ele simplesmente não consegue.

**Uma role por domínio** — 1 SA, uma policy com as 3 filas no `Resource`.

Menos YAML, menos Terraform, menos ticket para a plataforma. O raio de alcance passa a ser "todo o domínio de cobrança". Pode ser aceitável se os workers são do mesmo time, do mesmo repositório e do mesmo nível de criticidade.

### A regra de bolso

> **Uma role por unidade deployável, por ambiente.**

Deployments que sobem separadamente e falham separadamente merecem roles distintas. E **nunca compartilhe role entre ambientes** — em banco isso geralmente já vem resolvido de graça, porque dev/hml/prod são contas AWS diferentes e role não atravessa conta.

### O custo real de fazer isso

Honestamente: é mais arquivo, mais Terraform, mais coisa para manter sincronizada. O que torna sustentável é gerar as roles a partir de um módulo parametrizado em vez de escrever cinco blocos parecidos na mão. O `create-roles.sh` deste lab é a versão didática disso; em produção seria um módulo Terraform recebendo uma lista.

O erro que compensa evitar desde o começo é o oposto: começar com uma role compartilhada "só para destravar" e nunca mais separar. Separar depois exige descobrir empiricamente quem usa o quê, e isso se faz lendo CloudTrail por semanas.

---

## 7. Convenção de nomes

Parece detalhe e não é. Com 200 roles numa conta, o nome é a única coisa que permite auditar em escala.

```
asa - dev - cash-cobranca - consumer-registro
 │     │         │                 │
 │     │         │                 └── worker específico
 │     │         └── domínio / squad
 │     └── ambiente
 └── organização
```

O que um prefixo consistente habilita:

- `aws iam list-roles --query "Roles[?starts_with(RoleName, 'asa-prd-')]"` — inventário instantâneo.
- SCPs e permission boundaries escritas por padrão de nome: *"roles com prefixo `asa-dev-` não podem tocar em recursos de produção"*.
- Descobrir o dono de uma role vendo um `AccessDenied` no CloudTrail, sem precisar de planilha.

Essa é a decisão que ninguém quer tomar no começo e todo mundo se arrepende de não ter tomado.

---

## 8. Cotas que existem

Raramente travam, mas vale saber que existem antes de propor "uma role por pod":

| Limite | Valor padrão | Elevável? |
|---|---|---|
| Roles por conta | 1.000 | sim, até 5.000 |
| Managed policies anexadas por role | 10 | sim, até 20 |
| Tamanho de uma policy inline | 10.240 caracteres | não |
| Tamanho de uma managed policy | 6.144 caracteres | não |
| Duração máxima de sessão | 1h (padrão) / 12h (máximo) | configurável por role |

O limite de caracteres é o que morde primeiro na prática, quando alguém tenta listar 300 ARNs explícitos numa policy só.

### Inline vs managed policy

- **Inline** — nasce e morre com a role. Use quando a policy só faz sentido para aquela role. É o que o `create-roles.sh` faz.
- **Managed** — objeto independente, anexável a várias roles. Use quando várias roles compartilham exatamente o mesmo conjunto de permissões, ou quando você quer versionar a policy separadamente.

---

## 9. Duas coisas que você vai encontrar em banco

**SCP (Service Control Policy)** — vive no AWS Organizations, acima da conta. Define o teto máximo de permissões da conta inteira. Um `Deny` numa SCP não pode ser desfeito por ninguém dentro da conta, nem pelo root. É como bancos garantem coisas do tipo "nenhuma conta desta OU pode criar recurso fora de `sa-east-1`".

**Permission boundary** — anexada a uma role individual. Define o teto daquela role específica. A permissão efetiva é a **interseção** entre a permission policy e a boundary. Serve para delegar: o time de plataforma deixa você criar suas próprias roles, mas com uma boundary que impede que você crie uma role de administrador.

O sintoma de ambos é o mesmo e é desconcertante: sua policy tem um `Allow` explícito, você lê o JSON dez vezes, está certo, e a chamada continua sendo negada. Se isso acontecer, pergunte pela boundary e pela SCP antes de duvidar da sua sanidade.

---

## 10. Debugando `AccessDenied`

### Leia a mensagem de erro — ela diz tudo

A AWS é generosa aqui e quase ninguém aproveita:

```
User: arn:aws:sts::111122223333:assumed-role/asa-dev-cash-cobranca-consumer-registro/botocore-session-1234
is not authorized to perform: sqs:ReceiveMessage
on resource: arn:aws:sqs:us-east-1:111122223333:cobranca-baixa
because no identity-based policy allows the sqs:ReceiveMessage action
```

Três coisas de graça: **quem** (a role, confirmando que o IRSA funcionou), **o quê**, e **onde**. Neste exemplo o diagnóstico é imediato — a role do worker de registro está tentando ler a fila de *baixa*. É bug de configuração, não de permissão. A policy está fazendo exatamente o trabalho dela.

Repare no formato do principal: `assumed-role/NOME-DA-ROLE/NOME-DA-SESSÃO`. Se aparecer `assumed-role`, o AssumeRole deu certo. Se aparecer o nome do nó EC2, o IRSA **não** pegou e o pod está usando a role do nó.

### Confirme a identidade de dentro do pod

O primeiro comando a rodar, sempre:

```bash
kubectl exec -n cash deploy/consumer-registro -- env | grep AWS_
# esperado:
#   AWS_ROLE_ARN=arn:aws:iam::111122223333:role/asa-dev-cash-cobranca-consumer-registro
#   AWS_WEB_IDENTITY_TOKEN_FILE=/var/run/secrets/eks.amazonaws.com/serviceaccount/token
```

Se essas variáveis **não** estiverem lá, o problema é anterior ao IAM: o webhook não injetou. Causas usuais: o pod não usa o SA anotado, a anotação tem erro de digitação, ou o pod já existia antes da anotação ser criada (o webhook só age na criação — precisa de `kubectl rollout restart`).

Se as variáveis estiverem lá, teste a troca de credenciais:

```bash
kubectl exec -n cash deploy/consumer-registro -- aws sts get-caller-identity
```

### Checklist na ordem em que costuma dar errado

1. O pod usa o `serviceAccountName` certo? (`kubectl get pod -o yaml | grep serviceAccountName`)
2. O SA tem a anotação `eks.amazonaws.com/role-arn` sem typo?
3. O pod foi recriado *depois* da anotação existir?
4. A trust policy da role cita o namespace **e** o SA corretos, na ordem `system:serviceaccount:<ns>:<sa>`?
5. A trust policy tem a condição `:aud` = `sts.amazonaws.com`?
6. O ID do provedor OIDC na trust policy é o do cluster certo? (fácil errar entre dev e prod)
7. A permission policy tem o ARN certo — região certa, conta certa, nome certo?
8. Se a fila usa SSE-KMS: a role tem `kms:Decrypt` **e** a key policy da chave permite essa role?
9. Se for cross-account: existe allow nos dois lados?
10. Existe permission boundary ou SCP no caminho?

### Ferramentas

- **CloudTrail** — toda chamada negada fica registrada com o principal e o motivo. É a fonte da verdade.
- **IAM Policy Simulator** — testa uma policy contra uma ação e um recurso sem executar nada.
- **IAM Access Analyzer** — valida a sintaxe e semântica da policy e sugere reduções baseadas no uso real registrado no CloudTrail. É a ferramenta certa para apertar uma policy que hoje é ampla demais.

---

## 11. Sobre validar isso no Floci

O Floci emula IAM e STS: você consegue criar roles, anexar policies e chamar `AssumeRole` e `AssumeRoleWithWebIdentity`. É excelente para **praticar os comandos** — rodar o `create-roles.sh` contra ele e ver os objetos surgindo custa zero e não depende de acesso à conta da empresa.

Mas com uma ressalva que importa: o Floci não age como ponto de decisão de autorização entre serviços — credenciais podem ser quaisquer valores não-vazios. Na prática, **uma policy mal escrita passa no Floci e falha na AWS**. Ele não vai te avisar que você esqueceu o `kms:Decrypt`.

Divisão de trabalho que funciona:

| Para isso | Use |
|---|---|
| Entender a sintaxe, praticar CLI, montar o pipeline | Floci |
| Validar se a policy realmente concede/nega o certo | Policy Simulator, Access Analyzer |
| Validar IRSA de ponta a ponta | só cluster EKS real |
| Praticar os objetos do Kubernetes (SA, PVC, Deployment) | `kind` ou `k3d` |

---

## 12. O que pedir quando você precisar de uma role nova

Se você tiver que abrir um ticket para a plataforma, esta é a informação que evita três idas e voltas:

```
Ambiente:        dev
Cluster:         eks-asa-dev
Namespace:       cash
ServiceAccount:  sa-consumer-registro
Nome sugerido:   asa-dev-cash-cobranca-consumer-registro

Permissões necessárias:
  sqs:ReceiveMessage, sqs:DeleteMessage, sqs:DeleteMessageBatch,
  sqs:GetQueueAttributes, sqs:ChangeMessageVisibility
  em:
    arn:aws:sqs:us-east-1:<conta>:cobranca-registro
    arn:aws:sqs:us-east-1:<conta>:cobranca-registro-dlq

  kms:Decrypt em <arn da chave>, se a fila usar SSE-KMS

Justificativa: worker que consome eventos de registro de cobrança.
Não precisa de acesso às demais filas do domínio.
```

Essa última linha — dizer explicitamente o que você **não** precisa — é o que costuma acelerar a aprovação, porque poupa quem revisa de ter que adivinhar o escopo.

---

## Referências

- IAM roles for service accounts — https://docs.aws.amazon.com/eks/latest/userguide/iam-roles-for-service-accounts.html
- EKS Pod Identity — https://docs.aws.amazon.com/eks/latest/userguide/pod-identities.html
- Lógica de avaliação de policies — https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_evaluation-logic.html
- Referência de ações e recursos por serviço — https://docs.aws.amazon.com/service-authorization/latest/reference/
- Cotas do IAM — https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_iam-quotas.html
