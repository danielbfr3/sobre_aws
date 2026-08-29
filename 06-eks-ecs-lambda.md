# O mesmo lab em EKS, ECS e Lambda

Os três serviços resolvem o mesmo problema — *como este código ganha permissão na AWS, e onde ele guarda arquivo* — de formas bem diferentes. Entender as três ajuda a ler o código de outros times e a participar de discussões de arquitetura sem ficar só na parte que você já conhece.

Este guia assume que você leu [`01-iam-explicado.md`](01-iam-explicado.md).

---

## 1. Panorama

| | **EKS** | **ECS (Fargate)** | **Lambda** |
|---|---|---|---|
| Quantas roles por workload | 1 | **2** | 1 |
| Onde a role é amarrada | anotação no ServiceAccount | task definition | a própria função |
| Granularidade natural | ServiceAccount | task definition | função |
| Como a credencial chega | arquivo com token JWT + STS | endpoint HTTP local | variáveis de ambiente |
| Principal na trust policy | `Federated` (OIDC do cluster) | `ecs-tasks.amazonaws.com` | `lambda.amazonaws.com` |
| Armazenamento persistente | PVC → EFS ou EBS | EFS ou EBS, inline na task def | EFS (só isso) |
| Armazenamento efêmero | filesystem do container | 20 GiB (até 200 GiB) | `/tmp`, 512 MB (até 10 GB) |
| Precisa de IAM para montar volume | **não** | **sim, se IAM auth ligado** | **sim** |

Repare nas duas linhas em negrito — são exatamente onde a intuição vinda do EKS falha.

---

## 2. Roles: as três mecânicas

### EKS — uma role, via ServiceAccount

Já visto em detalhe no outro guia. Resumindo o essencial para comparar:

```
ServiceAccount anotado
   → webhook injeta AWS_ROLE_ARN + AWS_WEB_IDENTITY_TOKEN_FILE
   → SDK lê o JWT do arquivo
   → sts:AssumeRoleWithWebIdentity
   → credenciais temporárias
```

A identidade é do **ServiceAccount**, e a trust policy cita `system:serviceaccount:<ns>:<sa>`.

---

### ECS — duas roles, e confundi-las é o erro nº 1

Esta é a diferença que mais pega quem vem do EKS. No ECS existem **duas roles com propósitos completamente distintos**, e as duas aparecem na mesma task definition:

#### Task execution role — usada pelo *agente*, antes do seu código existir

Quem usa: a infraestrutura do ECS, não a sua aplicação. Serve para:

- puxar a imagem do ECR
- escrever os logs do container no CloudWatch Logs
- resolver secrets do Secrets Manager / SSM para injetar como variável de ambiente

Se faltar permissão aqui, **a task nem inicia**. Os sintomas são característicos e não mencionam IAM de forma óbvia:

```
CannotPullContainerError: ... denied
ResourceInitializationError: unable to pull secrets or registry auth
```

#### Task role — usada pelo *seu código*

É o equivalente direto da role do IRSA. É daqui que sai o `sqs:ReceiveMessage` do consumer.

#### O erro clássico

Colocar `sqs:ReceiveMessage` na **execution role** em vez da **task role**. O resultado é cruel: a task sobe normalmente, o container fica saudável, os logs aparecem no CloudWatch — e o código toma `AccessDenied` na primeira chamada à SQS. Tudo parece certo, menos o que importa.

Regra mental: *execution = antes do seu código; task = o seu código.*

#### Como a credencial chega no container

O agente do ECS injeta uma variável e expõe um endpoint HTTP local:

```
AWS_CONTAINER_CREDENTIALS_RELATIVE_URI=/v2/credentials/abc-123-def
```

O SDK monta `http://169.254.170.2$AWS_CONTAINER_CREDENTIALS_RELATIVE_URI`, faz um GET, e recebe as credenciais temporárias em JSON. Renova sozinho antes de expirar. É outro elo da mesma cadeia padrão de credenciais — por isso o `Program.cs` do lab **não muda uma linha** ao migrar de EKS para ECS.

#### Trust policy

Muito mais simples que a do IRSA, porque não há OIDC no meio:

```json
{
  "Effect": "Allow",
  "Principal": { "Service": "ecs-tasks.amazonaws.com" },
  "Action": "sts:AssumeRole"
}
```

A contrapartida: essa trust policy sozinha permite que **qualquer task da conta** assuma a role. No EKS o `sub` do ServiceAccount dava esse escopo de graça. Aqui, para conseguir o equivalente, você adiciona uma condição:

```json
"Condition": {
  "ArnLike": {
    "aws:SourceArn": "arn:aws:ecs:us-east-1:111122223333:*"
  },
  "StringEquals": {
    "aws:SourceAccount": "111122223333"
  }
}
```

Isso é o mínimo. Amarrar a uma task definition específica não é possível na trust policy — o isolamento no ECS vem de **quem pode fazer `ecs:RunTask` com aquela task definition e `iam:PassRole` daquela role**, o que é uma amarração no pipeline de deploy, não no runtime. Diferença conceitual relevante: no EKS o cluster te impede; no ECS é o processo de deploy que te impede.

Ver `examples/ecs/`.

---

### Lambda — uma role só, mas com dois usuários

A Lambda tem uma **execution role**, e ela acumula os dois papéis que o ECS separa:

- o **serviço Lambda** a usa para escrever logs no CloudWatch e, no caso de event source mapping, para **fazer o polling da fila SQS**
- o **seu código** a usa para chamar S3, DynamoDB, o que for

A consequência que confunde: com Lambda + SQS, a execution role precisa de `sqs:ReceiveMessage`, `sqs:DeleteMessage` e `sqs:GetQueueAttributes` — **mas o seu handler nunca chama nenhuma dessas operações**. Quem chama é o serviço Lambda, por baixo, e entrega as mensagens já prontas no parâmetro do handler. Você concede uma permissão que seu código não usa diretamente.

#### Como a credencial chega

Aqui vem a parte que parece contradizer tudo que foi dito antes: **na Lambda as credenciais são variáveis de ambiente**.

```
AWS_ACCESS_KEY_ID=ASIA...
AWS_SECRET_ACCESS_KEY=...
AWS_SESSION_TOKEN=...
```

Isso não é uma regressão de segurança. A regra "credencial em variável de ambiente é ruim" sempre foi sobre credencial **permanente**. Essas são temporárias, emitidas pelo STS, rotacionadas pelo serviço, e existem só dentro daquele ambiente de execução. A presença do `AWS_SESSION_TOKEN` é o sinal de que a credencial é temporária — se você vir esse trio em qualquer lugar, é STS, não usuário IAM.

#### Trust policy

```json
{
  "Effect": "Allow",
  "Principal": { "Service": "lambda.amazonaws.com" },
  "Action": "sts:AssumeRole"
}
```

#### O terceiro documento que só a Lambda tem

Além de trust policy e permission policy, a Lambda tem uma **resource-based policy própria**: quem pode *invocar* a função. É o que você configura ao dar permissão para o API Gateway, o EventBridge ou o S3 chamarem a função:

```bash
aws lambda add-permission \
  --function-name consumer-registro \
  --principal events.amazonaws.com \
  --action lambda:InvokeFunction \
  --statement-id permite-eventbridge
```

Detalhe: para SQS via event source mapping **isso não é necessário**, porque quem puxa é a Lambda, não a SQS que empurra. Direção do fluxo importa.

---

## 3. Volumes: as três mecânicas

### Antes das três: o que é POSIX

O termo aparece o tempo todo daqui em diante — nesta seção, no guia 07, e como tese central do guia 08. Vale gastar um minuto nele.

**POSIX** (*Portable Operating System Interface*) é um **padrão** — IEEE 1003, mantido junto com o The Open Group — que define a interface que um sistema operacional oferece aos programas: quais chamadas existem, como se chamam e o que exatamente cada uma faz. O "P" é de portabilidade: escreva contra o padrão e o mesmo código roda em Linux, macOS, BSD e Solaris sem alteração. **Windows não é POSIX** — e é por isso que o [guia 08](08-fsx-smb.md) existe.

O padrão é amplo, mas quando estes guias dizem "POSIX" eles quase sempre falam da parte de **sistema de arquivos**:

| | O que o POSIX define |
|---|---|
| **A árvore** | arquivos e diretórios num caminho hierárquico único, `/data/comprovantes/…` |
| **As chamadas** | `open`, `read`, `write`, `close`, `stat`, `link`, `rename`, `unlink` — com comportamento especificado |
| **As permissões** | dono (uid), grupo (gid) e os bits `rwx` |
| **O modelo** | tudo é arquivo; você recebe um descritor e opera nele |

**É isso que o driver CSI promete entregar.** Quando a próxima subseção diz que `/data` é "um diretório comum com permissão POSIX", a afirmação é: o driver apresenta a interface POSIX, e por isso o `open()` do seu código funciona sem saber que existe rede atrás dele. É o motivo de o worker das três linguagens não ter SDK nenhum para volume — diferente de SQS, SNS ou KMS, que aparecem no código como chamada explícita.

Duas consequências que voltam adiante:

- **"É POSIX, não IAM."** Quando a escrita em `/data` falha, quem negou foi o modelo de permissão do filesystem — uid e gid —, não a AWS. Não existe `AccessDenied`, não há role envolvida. É a regra de diagnóstico que a tabela de sintomas desta seção usa, e procurar na policy custa uma tarde.
- **POSIX "de verdade" vs. "emulado".** No EFS as chamadas acima têm a semântica especificada. Sobre SMB, um cliente traduz POSIX para um protocolo que não foi desenhado para isso, e a tradução vaza — é o assunto do [guia 08](08-fsx-smb.md).

> **Sobre a notação `link(2)`**, que estes guias usam bastante: o número é a **seção do manual do Unix**. A seção 2 é a das chamadas de sistema, a 1 é a dos comandos de shell e a 3 é a das funções de biblioteca. Então `link(2)` quer dizer "a chamada de sistema `link`", e não o comando `link` que você digitaria no terminal. `man 2 link` abre exatamente essa página.

---

### EKS

`StorageClass → PVC → PV → volumeMount`. Provisionamento dinâmico via driver CSI. EFS para `ReadWriteMany`, EBS para `ReadWriteOnce`.

**O pod não precisa de nenhuma permissão IAM para montar.** Quem precisa de role é o driver CSI. Do ponto de vista do container, `/data` é um diretório comum com permissão POSIX.

---

### ECS

Não existe abstração de PVC. O volume é declarado **inline na task definition**, o que é mais simples e menos portável.

#### Efêmero
<cite index="14-1">Tasks Fargate a partir da plataforma 1.40 vêm com 20 GiB de armazenamento efêmero</cite>, e <cite index="14-1">é possível configurar até 200 GiB pela opção de ephemeral storage — que continua sendo armazenamento não persistente</cite>. Some quando a task para.

#### EFS
Declarado com `efsVolumeConfiguration`, com suporte a Access Point e TLS em trânsito. E aqui está a diferença que importa em relação ao EKS:

```json
"authorizationConfig": { "accessPointId": "fsap-0123", "iam": "ENABLED" }
```

Com `iam: ENABLED`, **a task role precisa de permissão IAM para montar**:

```json
{
  "Effect": "Allow",
  "Action": ["elasticfilesystem:ClientMount", "elasticfilesystem:ClientWrite"],
  "Resource": "arn:aws:elasticfilesystem:us-east-1:111122223333:file-system/fs-0123"
}
```

No EKS isso não existia. Se você migrar o worker de comprovantes do lab para ECS e esquecer essas duas ações, o container falha no mount e a task morre antes de logar qualquer coisa útil.

#### EBS
Novidade de 2024: <cite index="16-1">ECS e Fargate passaram a integrar com EBS, permitindo provisionar e anexar volumes EBS a tasks tanto no Fargate quanto no EC2 através das APIs do ECS</cite>. Os atributos do volume (tamanho, tipo, IOPS, throughput, chave KMS, snapshot de origem) vão no `RunTask`, `CreateService` ou `UpdateService`.

Duas restrições que definem o uso: <cite index="10-1">é 1 volume EBS por task, suportado para tasks Linux no Fargate</cite>, e <cite index="11-1">por padrão o ECS apaga o volume quando a task encerra</cite>. É o análogo do EBS no EKS — disco rápido de uma task só, não compartilhamento.

O ECS também precisa de uma role de infraestrutura própria para criar e anexar esses volumes em seu nome. Mais uma role no mapa.

#### EC2 launch type
Ganha também bind mounts, volumes Docker e FSx. Mais opções, mais coisa para operar.

---

### Lambda

#### `/tmp`
512 MB por padrão, configurável até 10 GB. Duas propriedades que geram bug com frequência:

- **Persiste entre invocações do mesmo ambiente de execução.** Lambda "quente" reaproveita o container, e o `/tmp` vem com o que a invocação anterior deixou. Você *pode* usar como cache — e *não pode* assumir que está vazio. Código que faz `File.AppendAllText` no `/tmp` sem pensar acaba acumulando dados de execuções anteriores.
- **Some quando o ambiente é reciclado**, sem aviso e sem garantia de quando.

Ou seja: bom para arquivo temporário dentro de uma invocação, péssimo para qualquer coisa que precise existir depois.

#### EFS
A Lambda monta EFS num caminho (`/mnt/comprovantes`, por exemplo). Requisitos:

- a função precisa estar **numa VPC** — o que traz ENI, cold start maior e a necessidade de NAT ou VPC endpoints para falar com outros serviços AWS
- a execution role precisa de `elasticfilesystem:ClientMount` e `ClientWrite`
- o Access Point define o uid/gid, então não há `fsGroup` como no Kubernetes

#### Não existe EBS
Não há armazenamento de bloco em Lambda. Se o requisito é disco de bloco, a Lambda está fora da conversa.

#### A recomendação honesta
Para o caso de gravar comprovantes, **S3 é quase sempre melhor que EFS em Lambda**. Sem VPC, sem ENI, sem cold start extra, com versionamento e lifecycle de graça, e o objeto fica acessível para qualquer outro sistema. EFS em Lambda existe para quando algo exige semântica POSIX de verdade — biblioteca legada que só sabe abrir arquivo, lock, append. Se der para escolher, escolha S3.

Isso vale, aliás, para o próprio lab: `/data/comprovantes/*.txt` num PVC é ótimo para *aprender* PVC. Em produção, esses comprovantes provavelmente deveriam ir para o S3.

---

## 4. Como o lab ficaria em cada um

### Em ECS

Três task definitions, mesma imagem, cada uma com sua **task role**. A execution role pode ser compartilhada entre as três — é prática comum e aceitável, porque ela só puxa imagem e escreve log. Separe se cada serviço ler secrets diferentes.

```
task-definition consumer-registro
├── executionRoleArn: asa-dev-ecs-task-execution        (compartilhada)
├── taskRoleArn:      asa-dev-cash-cobranca-registro    (exclusiva)
└── volumes[0]: EFS fs-0123 + accessPoint + iam: ENABLED
```

O código não muda. Nem uma linha. `Program.cs` continua sem credencial no construtor, e a cadeia padrão do SDK descobre o endpoint de credenciais do ECS sozinha.

Ver `examples/ecs/task-definition-consumer-registro.json`.

### Em Lambda

Aqui o código **muda de forma**, e é a mudança mais interessante das três.

O `while (true)` com `ReceiveMessageAsync` **desaparece**. Um *event source mapping* liga a fila à função, e o serviço Lambda faz o polling por você, invocando o handler com um lote já pronto:

```csharp
public async Task<SQSBatchResponse> Handler(SQSEvent evt, ILambdaContext context)
```

O que some do seu código: long polling, `DeleteMessage`, tratamento de erro do receive, o loop, o `BackgroundService`. O que aparece:

**Falha parcial de lote.** Se 1 mensagem de um lote de 10 falha, o comportamento padrão é devolver as 10 para a fila — as 9 que deram certo são reprocessadas à toa. Ligando `ReportBatchItemFailures` no event source mapping e devolvendo os IDs que falharam, só a mensagem problemática volta. Quase todo mundo esquece disso na primeira Lambda de SQS.

**Visibility timeout ≥ 6× o timeout da função.** É a recomendação da AWS. Se não seguir, a mensagem reaparece na fila enquanto a invocação ainda está rodando, e você processa em duplicidade.

**A DLQ continua sendo a da fila**, via `RedrivePolicy` — a mesma que o `bootstrap.sh` configura. A configuração de DLQ da própria Lambda é para invocação assíncrona, não para event source mapping de SQS. Confundir as duas leva a uma DLQ que nunca recebe nada.

**Idempotência continua obrigatória.** Event source mapping também é entrega *pelo menos uma vez*. A estratégia do lab — nome de arquivo derivado do hash do payload — continua valendo, com a ressalva de que em Lambda esse arquivo deveria estar no S3, não no `/tmp`.

Ver `examples/lambda/Function.cs`.

---

## 5. Qual escolher

Sem torcida, e reconhecendo que a resposta depende do que você já tem rodando:

| | Escolha quando | Cuidado com |
|---|---|---|
| **Lambda** | consumidor de fila com processamento curto; volume irregular; quer escalar a zero | limite de 15 min por invocação; cold start; sem estado entre invocações; custo alto em volume constante e alto |
| **ECS Fargate** | worker de longa duração; quer controle do container sem operar cluster | menos flexível que Kubernetes; ecossistema de ferramentas menor |
| **EKS** | você **já tem** o cluster; precisa de recursos de Kubernetes de verdade | custo operacional alto se for só por causa de três workers |

Para um consumidor de SQS que faz um trabalho curto por mensagem, **Lambda costuma ser o default correto** — não há worker ocioso queimando dinheiro em madrugada de domingo. O que mais empurra times para ECS ou EKS é: processamento que passa de 15 minutos, dependência de estado local, ou simplesmente o cluster já existir e ter esteira de deploy pronta.

Esse último motivo é legítimo, aliás. "Já temos EKS e todo o tooling em cima dele" é uma razão de engenharia válida, não preguiça. O custo de manter uma segunda plataforma de deploy é real.

---

## 6. Tabela de sintomas

Quando algo falha, o serviço muda o formato do erro. Vale reconhecer:

| Sintoma | Onde | Causa provável |
|---|---|---|
| `CannotPullContainerError` | ECS | falta permissão de ECR na **execution role** |
| `ResourceInitializationError: unable to pull secrets` | ECS | falta `secretsmanager:GetSecretValue` na **execution role** |
| task saudável mas código com `AccessDenied` | ECS | permissão foi para a execution role em vez da **task role** |
| falha no mount do EFS sem log da aplicação | ECS | falta `elasticfilesystem:ClientMount` na task role, com `iam: ENABLED` |
| pod em `Pending` sem motivo claro | EKS | PVC não existe, ou CSI sem permissão |
| `permission denied` ao escrever no volume | EKS | `fsGroup` não bate com o gid do Access Point |
| `UnauthorizedAccessException` no .NET, sem `AccessDenied` | EKS/ECS | é POSIX, não IAM — procurar na policy é o caminho errado |
| lock de arquivo se comportando de forma instável | EKS/ECS | EFS é NFS; prefira um arquivo por escritor a disputar lock |
| env sem `AWS_ROLE_ARN` dentro do pod | EKS | webhook não injetou; SA errado ou pod anterior à anotação |
| mensagens reprocessadas em duplicidade | Lambda | visibility timeout menor que 6× o timeout da função |
| lote inteiro reprocessado por causa de 1 erro | Lambda | falta `ReportBatchItemFailures` |
| DLQ da função vazia, mensagens sumindo | Lambda | confundiu DLQ de invocação assíncrona com redrive policy da fila |
| cold start de vários segundos | Lambda | função em VPC (provavelmente por causa do EFS) |

---

## 7. Na prática: os comandos

### ECS: criando as duas roles

#### As duas roles

```bash
# 1. execution role — puxa imagem, escreve log. Usada ANTES do seu código existir.
#    Pode ser compartilhada entre os três consumers: ela não toca em nada de negócio.
aws iam create-role \
  --role-name asa-dev-ecs-task-execution \
  --assume-role-policy-document file://../examples/ecs/trust-policy-ecs-task.json

aws iam put-role-policy \
  --role-name asa-dev-ecs-task-execution \
  --policy-name execucao \
  --policy-document file://../examples/ecs/policy-task-execution.json

# 2. task role — usada pelo SEU CÓDIGO. Uma por worker, como no lab do EKS.
aws iam create-role \
  --role-name asa-dev-cash-cobranca-consumer-registro \
  --assume-role-policy-document file://../examples/ecs/trust-policy-ecs-task.json

aws iam put-role-policy \
  --role-name asa-dev-cash-cobranca-consumer-registro \
  --policy-name permissoes-consumer-registro \
  --policy-document file://../examples/ecs/policy-task-role-consumer-registro.json

# 3. registrar a task definition
aws ecs register-task-definition \
  --cli-input-json file://../examples/ecs/task-definition-consumer-registro.json
```

As duas roles usam **a mesma trust policy**. O que as distingue é só o que cada uma permite fazer — e quem as usa.

#### O que olhar na task definition

| Campo | Por que importa |
|---|---|
| `executionRoleArn` | agente do ECS: ECR + CloudWatch. Falha aqui = task nem inicia |
| `taskRoleArn` | seu código. Falha aqui = task saudável, código com `AccessDenied` |
| `volumes[].efsVolumeConfiguration` | não há PVC; o volume é declarado inline |
| `authorizationConfig.iam: ENABLED` | liga a exigência de IAM para montar — **isso não existe no EKS** |
| `ephemeralStorage.sizeInGiB` | eleva os 20 GiB padrão do Fargate, até 200 GiB |

#### Diferença de isolamento em relação ao EKS

No EKS, a trust policy cita o ServiceAccount e o namespace — o próprio cluster impede um workload de assumir a role do outro.

No ECS não há equivalente. `ecs-tasks.amazonaws.com` com condição de conta permite que **qualquer task da conta** assuma a role. O isolamento real vem de quem tem permissão de `ecs:RunTask` com aquela task definition e `iam:PassRole` daquela role — ou seja, é uma amarração no **pipeline de deploy**, não no runtime.

Vale saber disso antes de afirmar que "no ECS é igual, só muda o YAML".

---

### Lambda: role e event source mapping

#### A role única

```bash
aws iam create-role \
  --role-name asa-dev-cash-cobranca-lambda-registro \
  --assume-role-policy-document file://../examples/lambda/trust-policy-lambda.json

aws iam put-role-policy \
  --role-name asa-dev-cash-cobranca-lambda-registro \
  --policy-name permissoes-lambda-registro \
  --policy-document file://../examples/lambda/policy-lambda-consumer-registro.json
```

#### Ligando a fila à função

```bash
aws lambda create-event-source-mapping \
  --function-name consumer-registro \
  --event-source-arn arn:aws:sqs:us-east-1:111122223333:cobranca-registro \
  --batch-size 10 \
  --maximum-batching-window-in-seconds 5 \
  --function-response-types ReportBatchItemFailures
```

O `--function-response-types ReportBatchItemFailures` é metade do par. A outra metade é o `Function.cs` devolver `SQSBatchResponse` com os IDs que falharam. **Sem os dois juntos, não funciona** — e o sintoma é silencioso: um erro em uma mensagem devolve o lote inteiro para a fila.

#### Três configurações que causam bug

| Configuração | Regra | O que acontece se ignorar |
|---|---|---|
| Visibility timeout da fila | ≥ 6× o timeout da função | mensagem reaparece enquanto ainda está sendo processada → duplicidade |
| DLQ | usar o `RedrivePolicy` **da fila** | a DLQ configurada na função é para invocação assíncrona; com event source mapping ela nunca recebe nada |
| Idempotência | continua obrigatória | event source mapping também é entrega *pelo menos uma vez* |

#### Por que S3 e não EFS

O `Function.cs` grava o comprovante no S3, não num volume. A Lambda até monta EFS, mas isso exige colocar a função numa VPC — o que traz ENI, cold start maior e necessidade de NAT ou VPC endpoints.

Para gravar um comprovante, S3 ganha em quase tudo: sem VPC, versionamento e lifecycle de graça, e o objeto fica acessível para outros sistemas.

E o `/tmp`? Existe (512 MB, até 10 GB), mas não serve aqui: **persiste entre invocações do mesmo ambiente quente**, então você não pode assumir que está vazio, nem que vai continuar existindo. Serve para arquivo temporário *dentro* de uma invocação, e só.

---

## 8. O que não muda em lugar nenhum

O `Program.cs` do lab, com `new AmazonSQSClient()` sem credencial no construtor, **funciona nos três serviços sem alteração**. A cadeia padrão de credenciais do SDK resolve:

| Onde | O que a cadeia encontra |
|---|---|
| local | `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` das env vars |
| EKS | `AWS_WEB_IDENTITY_TOKEN_FILE` → `sts:AssumeRoleWithWebIdentity` |
| ECS | `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI` → GET em `169.254.170.2` |
| Lambda | `AWS_ACCESS_KEY_ID` + `AWS_SESSION_TOKEN` injetados pelo serviço |

Manter a resolução de credenciais fora do seu código é o que torna essa portabilidade possível. É a razão prática de nunca passar `AWSCredentials` no construtor.

---

## Arquivos de apoio

Em [`../examples/`](../examples/):

| Arquivo | Mostra |
|---|---|
| `ecs/task-definition-consumer-registro.json` | as duas roles e o volume EFS inline |
| `ecs/trust-policy-ecs-task.json` | `ecs-tasks.amazonaws.com` com condição de conta |
| `ecs/policy-task-execution.json` | ECR + logs — usada pelo **agente** |
| `ecs/policy-task-role-consumer-registro.json` | SQS + EFS — usada pelo **seu código** |
| `lambda/Function.cs` | o handler; compare com `src/Consumer/QueueConsumer.cs` |
| `lambda/Lambda.csproj` | linka `src/Consumer/Comprovante.cs` em vez de duplicar |

---

## Referências

- ECS task IAM roles — https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html
- ECS task execution role — https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_execution_IAM_role.html
- EBS volumes com ECS — https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ebs-volumes.html
- EFS volumes com ECS — https://docs.aws.amazon.com/AmazonECS/latest/developerguide/efs-volumes.html
- Lambda execution role — https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html
- Lambda com SQS — https://docs.aws.amazon.com/lambda/latest/dg/with-sqs.html
- Lambda com EFS — https://docs.aws.amazon.com/lambda/latest/dg/configuration-filesystem.html
