# Lab: SNS → SQS → workers no EKS

Laboratório local para entender três coisas que aparecem juntas em quase toda aplicação AWS corporativa:

1. **Fan-out** — um evento publicado uma vez, entregue em várias filas.
2. **IAM Role** — como um pod ganha permissão de falar com a AWS sem ter senha nenhuma.
3. **Volume persistente** — como um worker acessa um diretório que sobrevive ao pod.

> **Comece pelos guias em [`docs/`](docs/README.md)** se a parte de infra AWS é nova para você.
> São nove capítulos numa ordem pensada: do que é uma role, passando por como o SDK .NET
> a descobre sozinho, até o que muda quando há criptografia no caminho — e, no fim, o que
> muda quando o diretório de rede é SMB em vez de NFS (08) ou EFS de verdade (09). O índice em
> [`docs/README.md`](docs/README.md) tem uma trilha rápida por problema
> ("quero debugar um `AccessDenied`" → tal capítulo).
>
> O resto deste README pressupõe pelo menos o capítulo 01.
>
> **Três documentos, três papéis:**
> [`ROTEIRO.md`](ROTEIRO.md) é o passo a passo — *o que fazer, em que ordem*.
> Este README é referência — *como o lab é montado*.
> [`docs/`](docs/README.md) são as apostilas — *por que funciona assim*.
>
> **Se você quer começar agora, vá direto para o [ROTEIRO](ROTEIRO.md).**
>
> **Para ver o desenho antes de ler**, abra
> [`docs/assets/visualizador.html`](docs/assets/visualizador.html) no navegador:
> ele anima uma mensagem atravessando o pipeline e deixa você quebrar
> permissões para ver exatamente onde o fluxo para.

```
aws-eks-lab/
├── ROTEIRO.md                   ← O PASSO A PASSO, do zero ao fim
├── run.sh                       ← orquestra o lab na ordem correta
├── docs/                        ← os nove guias (índice em docs/README.md)
├── docker-compose.yml           ← Floci + publisher + 3 consumers
├── infra/
│   ├── local/bootstrap.sh       ← cria SNS, SQS, DLQs e subscriptions no Floci
│   ├── local/verify.sh          ← checa se os resultados esperados aconteceram
│   ├── aws/create-roles.sh      ← cria UMA ROLE POR WORKER
│   ├── iam/                     ← 5 roles (trust + permission) + 1 exemplo Pod Identity
│   └── k8s/                     ← ServiceAccounts, Deployments, PVC
├── infra/local/trace.sh         ← remonta a cadeia de uma mensagem pelo trace_id
├── src/
│   ├── Publisher/               ← publica eventos no SNS
│   └── Consumer/                ← consome a fila e grava o comprovante .txt
└── examples/                    ← credenciais, secrets, ECS, Lambda
    └── multilinguagem/          ← O MESMO worker em Python, Go e .NET (guia 07)
        ├── comparar.sh          ← prova que as 3 geram o mesmo comprovante
        ├── python/  go/  dotnet/
```

> **Os três consumers que sobem com `./run.sh up` estão em três linguagens
> diferentes** — `consumer-registro` em Python, `consumer-baixa` em Go e
> `consumer-rejeicao` em .NET. É o assunto do
> [guia 07](docs/07-implementacoes-python-go-dotnet.md), e é proposital: nada no
> resto do lab precisa saber disso.

---

## 1. A topologia

```
                        ┌─────────────────────────┐
                        │  SNS: eventos-cobranca  │
                        └────────────┬────────────┘
                                     │  filtro por MessageAttribute "eventType"
              ┌──────────────────────┼──────────────────────┐
              │                      │                      │
   eventType=cobranca.registrada  =cobranca.baixada   =cobranca.rejeitada
              │                      │                      │
        ┌─────▼──────┐        ┌──────▼─────┐         ┌──────▼──────┐
        │ SQS        │        │ SQS        │         │ SQS         │
        │ registro   │        │ baixa      │         │ rejeicao    │
        └─────┬──────┘        └──────┬─────┘         └──────┬──────┘
              │  (DLQ)               │  (DLQ)               │  (DLQ)
        ┌─────▼──────┐        ┌──────▼─────┐         ┌──────▼──────┐
        │ consumer   │        │ consumer   │         │ consumer    │
        │ x2 pods    │        │ x2 pods    │         │ x1 pod      │
        │            │        │            │         │             │
        │ role A     │        │ role B     │         │ role C      │
        └────────────┘        └────────────┘         └─────────────┘
             ↑                      ↑                       ↑
        cada uma enxerga APENAS a própria fila e a própria DLQ
              │                      │                       │
              └──────────────────────┼───────────────────────┘
                                     ▼
                   PVC ReadWriteMany (EFS) montado em /data
                   /data/comprovantes/<worker>/<data>/*.txt
```

Três decisões de projeto que importam:

**Por que SNS na frente e não publicar direto nas 3 filas?**
Porque o produtor não deve saber quem consome. Quando amanhã surgir um quarto consumidor (antifraude, BI, auditoria), ninguém mexe no publisher — só se cria mais uma subscription. É o desacoplamento que justifica o SNS existir.

**Por que filas e não o SNS entregando direto no worker?**
Porque a fila é o amortecedor. Se o worker cair por 40 minutos, as mensagens ficam paradas na SQS esperando (até 14 dias) e são reprocessadas quando ele voltar. Sem a fila, a mensagem some.

**Por que `RawMessageDelivery=true`?**
Sem isso o SNS embrulha seu JSON dentro de outro JSON (o envelope do SNS) e você precisa desembrulhar no código do consumer. Com raw delivery, o `Body` da mensagem SQS é exatamente o que você publicou.

**Um tópico com filtro, ou três tópicos?**
Um tópico com `FilterPolicy` por subscription é o padrão: o roteamento vira configuração de infra, não código. Três tópicos separados também funciona e vale quando os eventos são de domínios realmente diferentes. O script suporta os dois: `TOPIC_MODE=single` (padrão) ou `TOPIC_MODE=multi`.

---

## 2. Uma role por worker

### O mapa

Cinco workloads, cinco ServiceAccounts, cinco roles, cinco pares de policies:

| Workload | ServiceAccount | Role | Pode fazer |
|---|---|---|---|
| consumer-registro | `sa-consumer-registro` | `asa-dev-cash-cobranca-consumer-registro` | ler/apagar em `cobranca-registro` + DLQ |
| consumer-baixa | `sa-consumer-baixa` | `asa-dev-cash-cobranca-consumer-baixa` | ler/apagar em `cobranca-baixa` + DLQ |
| consumer-rejeicao | `sa-consumer-rejeicao` | `asa-dev-cash-cobranca-consumer-rejeicao` | ler/apagar em `cobranca-rejeicao` + DLQ |
| publisher | `sa-publisher` | `asa-dev-cash-cobranca-publisher` | `sns:Publish` no tópico — **não lê fila nenhuma** |
| worker-arquivos | `sa-arquivos` | `asa-dev-cash-cobranca-arquivos` | `s3:GetObject` no bucket de entrada |

No cluster, os três consumers rodariam a **mesma imagem**. O que difere entre eles é o `QUEUE_URL` e o ServiceAccount — e é o ServiceAccount que determina o que cada um consegue fazer de fato. (Localmente eles rodam três imagens diferentes, uma por linguagem, pelo motivo explicado no [guia 07](docs/07-implementacoes-python-go-dotnet.md) — o que não muda nada nesta seção: a role continua amarrada ao ServiceAccount, não ao runtime.) Se alguém trocar o `QUEUE_URL` do consumer de registro para a fila de baixa num pull request, o pod sobe e toma `AccessDenied` na primeira chamada, em vez de silenciosamente processar mensagens que não são dele.

### Por que a granularidade é o ServiceAccount

A trust policy de cada role cita literalmente:

```json
"oidc.eks.../id/EXAMPLE...:sub": "system:serviceaccount:cash:sa-consumer-registro"
```

Ou seja: **a unidade real de identidade não é "o serviço", é o ServiceAccount**. Um pod em outro namespace, ou usando outro SA, não assume essa role nem sabendo o ARN. A amarração existe nos dois lados — no Kubernetes (`serviceAccountName`) e na AWS (a `Condition` da trust policy).

### A regra de bolso

> **Uma role por unidade deployável, por ambiente.**

Deployments que sobem separadamente e falham separadamente merecem roles distintas. E nunca compartilhe role entre ambientes — em banco isso geralmente já vem resolvido de graça, porque dev/hml/prod são contas AWS diferentes e role não atravessa conta.

A alternativa é **uma role por domínio**: um SA só, com as três filas no `Resource`. Menos arquivo, menos ticket para a plataforma, raio de alcance maior. Pode ser aceitável quando os workers são do mesmo time, mesmo repositório e mesma criticidade. O que não compensa é começar compartilhado "só para destravar" e nunca separar — separar depois exige descobrir empiricamente quem usa o quê, lendo CloudTrail por semanas.

### Criando as roles

Todos os JSONs usam valores de exemplo (`111122223333`, `us-east-1`, `EXAMPLED539...`). O script troca por valores reais antes de enviar:

```bash
chmod +x infra/aws/create-roles.sh

# --- praticando contra o Floci, sem custo e sem acesso à conta da empresa ---
AWS_ENDPOINT_URL=http://localhost:4566 ./infra/aws/create-roles.sh

# --- contra a AWS de verdade ---
ACCOUNT_ID=123456789012 \
OIDC_ID=$(aws eks describe-cluster --name meu-cluster \
           --query 'cluster.identity.oidc.issuer' --output text | rev | cut -d/ -f1 | rev) \
ENVIRONMENT=dev \
./infra/aws/create-roles.sh
```

Leia o script — ele mostra que a role é criada em **dois comandos separados**, um para cada documento:

```bash
aws iam create-role     --assume-role-policy-document file://trust.json   # QUEM pode assumir
aws iam put-role-policy --policy-document              file://perm.json   # O QUE pode fazer
```

Em produção isso vira um módulo Terraform parametrizado recebendo uma lista de workers, em vez de cinco blocos parecidos escritos na mão. O script é a versão didática.

### Quando o recurso está em outra conta

Em banco, dev/hml/prod costumam ser **contas AWS diferentes** — e role não atravessa conta. Se o worker precisar gravar num bucket do time de dados ou publicar numa fila de auditoria que vive em outra conta, entra o mecanismo de `AssumeRole`.

A regra que explica quase toda falha: **os dois lados precisam permitir**. Dentro da mesma conta, um `Allow` na identity policy costuma bastar. Entre contas, a conta dona do recurso precisa concordar explicitamente *e* a conta dona do principal também. Nenhuma consegue se auto-conceder acesso à outra — é o que faz da fronteira de conta a fronteira de segurança mais forte da AWS.

Há três caminhos, e escolher errado dá retrabalho:

| Caminho | Use quando | Custo |
|---|---|---|
| **Assume role** | o serviço de destino não tem resource policy (DynamoDB, EC2, RDS) | um hop a mais; no CloudTrail do destino aparece a role de destino |
| **Resource policy direta** | S3, SQS, SNS, KMS, Secrets Manager, Lambda | só funciona nesses serviços |
| **IRSA cross-account** | pod no EKS acessando outra conta, e você pode registrar o OIDC lá | provedor OIDC a manter em cada conta de destino |

Duas armadilhas que vale conhecer antes de topar com elas:

- **O ARN da sessão não serve em policy.** Depois de assumir, sua identidade é `arn:aws:sts::...:assumed-role/Role/Sessao`. Copiar isso do `get-caller-identity` para uma bucket policy não funciona — policy sempre referencia `arn:aws:iam::...:role/Role`.
- **KMS tem um terceiro documento.** Se o recurso é criptografado, a **key policy** também precisa mencionar o principal de origem. Ter `kms:Decrypt` na role não basta, e o erro resultante aponta para a role, não para a chave.

Detalhamento completo, com as policies dos dois lados escritas por inteiro, em [`docs/02-assume-role-cross-account.md`](docs/02-assume-role-cross-account.md).

---

## 3. Os comprovantes: o que os workers gravam no volume

Cada consumer grava **um arquivo `.txt` por evento processado** dentro do volume compartilhado. É o que torna o PVC verificável em vez de teórico — dá para abrir, contar, e ver o que sobrevive a um restart.

### Layout no volume

```
/data/comprovantes/
├── registro/
│   └── 2026-08-08/
│       ├── 4827193-A1B2C3D4.txt
│       └── 5910284-7F3E9A01.txt
├── baixa/
│   └── 2026-08-08/
│       └── 3374018-B2C4E6F8.txt
└── rejeicao/
    └── 2026-08-08/
        └── 8821457-D9E0F1A2.txt
```

Particionar por data evita um diretório com milhões de entradas — no EFS, que é NFS por baixo, um `ls` num diretório assim é doloroso. Mesmo raciocínio de prefixo que você usaria no S3.

### Como fica o arquivo

```
================================================================
  COMPROVANTE DE BAIXA DE COBRANÇA
================================================================
Nosso número......: 4827193
Evento............: cobranca.baixada
Valor.............: R$ 1.234,56
Ocorrido em.......: 08/08/2026 17:42:03 UTC
----------------------------------------------------------------
Processado por....: baixa
Pod / host........: consumer-baixa-7d9f8b4c2-x2k4l
Message ID........: 9f3a1c22-5e88-4b1d-a0f7-1c9e2b6d4a51
Trace ID..........: a1b2c3d4e5f6071829304a5b6c7d8e9f
Tentativa.........: 1
Registrado em.....: 08/08/2026 17:42:04 UTC
Hash do payload...: A1B2C3D4E5F60718293A4B5C6D7E8F90
================================================================
Documento gerado automaticamente para fins de laboratório.
Não possui valor fiscal ou probatório.
```

O campo **Pod / host** é `Environment.MachineName`, que dentro de um pod é o nome do pod. Rodando com 2 réplicas você vê os comprovantes se dividindo entre elas — é a prova visual de que a SQS distribui sozinha, sem coordenação entre réplicas.

O campo **Tentativa** vem do `ApproximateReceiveCount` da SQS. Se aparecer `2` ou `3`, aquela mensagem já falhou antes.

O campo **Trace ID** é o que fecha o circuito. Ele nasceu no publisher, viajou como *MessageAttribute* do SNS até o worker, e está gravado aqui — então de um comprovante no volume você volta para a cadeia de log inteira:

```bash
./run.sh trace a1b2c3d4e5f6071829304a5b6c7d8e9f
```

Detalhes em [`docs/07-implementacoes-python-go-dotnet.md`](docs/07-implementacoes-python-go-dotnet.md) §7.

### Três decisões no código que valem atenção

**O nome do arquivo é a chave de idempotência.** Ele deriva do hash do payload (`{nossoNumero}-{hash8}.txt`), então a mesma mensagem entregue duas vezes gera o mesmo nome, e a segunda vez encontra o arquivo já existente e desiste. Vantagem sobre o `HashSet` em memória da versão anterior: **sobrevive a restart do pod e vale entre réplicas diferentes**, porque o volume é compartilhado. Idempotência que mora no estado, não na memória.

**A escrita é atômica** — grava num `.tmp` e faz `File.Move(..., overwrite: false)`. Sem isso, um pod morto no meio do write deixa um `.txt` truncado; e como o nome já existe, a lógica de idempotência nunca mais tenta de novo. O `overwrite: false` também resolve a corrida entre duas réplicas: quem perder recebe `IOException`, verifica que o destino existe, e trata como duplicata.

**O `DeleteMessage` vem depois de gravar o comprovante.** A ordem é deliberada: se o processo morrer entre as duas coisas, a mensagem volta pela expiração do visibility timeout e o comprovante sai na próxima tentativa. A ordem inversa criaria a janela oposta — mensagem sumindo sem comprovante nenhum.

Tem também um `VerificarVolume` na subida do worker que grava e apaga um arquivo-sonda. Parece paranoia e não é: no EFS o diretório pode existir e ainda assim negar escrita se o `fsGroup` do pod não bater com o `gid` do Access Point. Sem essa checagem, o worker sobe feliz, processa mensagens, grava tudo no filesystem efêmero do container, e você só descobre quando o pod morre.

### Conferindo localmente

```bash
# quantos comprovantes cada worker gerou
docker compose exec consumer-registro sh -c \
  'find /data/comprovantes -name "*.txt" | cut -d/ -f4 | sort | uniq -c'

# ler um comprovante
docker compose exec consumer-baixa sh -c \
  'cat "$(find /data/comprovantes/baixa -name "*.txt" | head -1)"'

# provar que o volume é compartilhado: o consumer de registro
# enxerga os comprovantes que o de baixa escreveu
docker compose exec consumer-registro ls /data/comprovantes/

# provar que sobrevive ao container
docker compose restart consumer-registro
docker compose exec consumer-registro sh -c 'find /data/comprovantes -name "*.txt" | wc -l'
```

No EKS o equivalente é `kubectl exec -n cash deploy/consumer-registro -- ls /data/comprovantes/`.

---

## 4. O worker que precisa acessar um volume

No Kubernetes, o pod nunca fala com o disco diretamente. A cadeia é:

```
StorageClass  →  PersistentVolumeClaim  →  PersistentVolume  →  volumeMount no pod
 (que tipo       (quanto eu quero e       (o disco de           (onde aparece
  de disco)       de que jeito)            verdade)              dentro do container)
```

Você escreve a **StorageClass** (uma vez, geralmente é a plataforma que faz) e o **PVC**. O PV é criado dinamicamente pelo driver CSI. Manifests em `infra/k8s/02-volume.yaml` — inclusive o PVC `comprovantes-cobranca` que os três consumers montam.

```bash
# ordem importa: PVC antes do Deployment que o consome
kubectl apply -f infra/k8s/02-volume.yaml
kubectl apply -f infra/k8s/01-workers-irsa.yaml
```

Um Deployment cujo PVC ainda não existe deixa o pod em `Pending` com uma mensagem pouco óbvia (`persistentvolumeclaim not found`).

A escolha real é entre dois modos:

| | **EFS** (`efs.csi.aws.com`) | **EBS** (`ebs.csi.aws.com`) |
|---|---|---|
| Access mode | `ReadWriteMany` | `ReadWriteOnce` |
| Vários pods juntos | Sim | Não — um nó por vez |
| Zonas de disponibilidade | Multi-AZ | Preso a uma AZ |
| Protocolo | NFS | Bloco |
| Latência | Maior | Menor |
| Custo | Por GB armazenado + throughput | Por GB provisionado |
| Uso típico | Diretório compartilhado de arquivos | Disco de um StatefulSet |

Para o seu caso — worker que lê arquivos que outro sistema depositou — o que se usa é **EFS**, porque múltiplas réplicas precisam enxergar o mesmo diretório. Com EBS, subir a segunda réplica faz o pod ficar `Pending` para sempre esperando um volume já montado em outro nó. Esse é o sintoma clássico.

### O ponto que confunde: volume e IAM quase não se falam

O **pod que monta o EFS não precisa de nenhuma permissão IAM para isso**. Do ponto de vista do container, `/data` é só um diretório; o controle de acesso ali é POSIX (uid/gid), não IAM.

Quem precisa de role é o **driver CSI**, que roda em outro namespace com ServiceAccount próprio, porque ele é que chama `elasticfilesystem:CreateAccessPoint`, `ec2:CreateVolume`, `ec2:AttachVolume`. Se um PVC fica preso em `Pending` sem motivo aparente, os logs do controller do CSI costumam revelar um `AccessDenied`.

No lab, a role `sa-arquivos` existe por outro motivo: esse worker também lê arquivos de entrada no S3.

Duas armadilhas práticas:

- **`fsGroup`** — sem `securityContext.fsGroup` batendo com o `gid` do Access Point do EFS, o container sobe e dá *permission denied* no primeiro write.
- **`volumeBindingMode: WaitForFirstConsumer`** no EBS — sem isso o volume é criado numa AZ antes de o scheduler decidir onde o pod vai, e às vezes as duas decisões não batem.

### O que muda no seu código: quase nada, e as exceções

Do lado da aplicação, um volume EFS montado é **só um diretório**. `File.WriteAllTextAsync`, `Directory.GetFiles`, `File.Exists` — `System.IO` normal. É exatamente o que o `ComprovanteWriter.cs` faz: nenhuma linha ali sabe que `/data` é NFS, e essa ignorância é proposital.

Quatro coisas, porém, mudam de comportamento:

**O caminho vem de configuração, não hardcoded.** O lab lê `DATA_PATH` do ambiente com `/data` como padrão:

```csharp
var dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data";
```

Não é preciosismo: localmente você monta um volume Docker comum, no cluster é um PVC EFS, e em teste unitário é um diretório temporário. Um `const string "/data"` te obriga a rodar em container para testar qualquer coisa.

**Falha de permissão vem como `UnauthorizedAccessException`, não como erro de IAM.** Se o `fsGroup` do pod não bate com o `gid` do Access Point, o .NET lança `UnauthorizedAccessException` no primeiro write. Nenhum `AccessDenied`, nenhuma menção a role — porque não é IAM, é POSIX. Procurar a causa na policy é o caminho errado e custa tempo. É por isso que o `QueueConsumer` grava um arquivo-sonda na subida: transforma esse erro tardio e confuso numa falha imediata com mensagem clara.

**Lock de arquivo se comporta diferente sobre NFS.** `FileStream` com `FileShare.None`, `FileStream.Lock()` e afins dependem de lock advisory do NFS, que tem semântica distinta da de um disco local — e fica mais frágil quando várias réplicas disputam o mesmo arquivo. A saída prática é não depender de lock: o lab usa **um arquivo por evento, com nome determinístico**, e escrita atômica via `File.Move(..., overwrite: false)`. Quem perde a corrida recebe `IOException` e trata como duplicata. Desenhar para "cada escritor tem seu próprio arquivo" evita o problema em vez de tentar resolvê-lo.

**Toda operação de I/O é uma ida à rede.** EFS é NFS: um `File.Exists` custa um round-trip, não um acesso a disco. Isso não quebra nada, mas muda o que é caro. Um loop que faz `Directory.GetFiles` a cada iteração, ou que verifica existência de mil arquivos um a um, tem um custo em EFS que não teria em disco local. Vale por isso o particionamento por data (`/comprovantes/<worker>/<AAAA-MM-DD>/`): um `ls` num diretório com milhões de entradas sobre NFS é doloroso de um jeito que em disco local não seria.

**Se o diretório de rede for SMB e não NFS** — FSx for Windows File Server, o caso comum em empresa com legado Windows — quase tudo desta seção muda de lugar: a autenticação sai do IAM e vai para o Active Directory, o `fsGroup` deixa de ser a resposta para *permission denied*, e o `link(2)` em que a idempotência se apoia pode não existir. Está em [`docs/08-fsx-smb.md`](docs/08-fsx-smb.md).

**Ressalva honesta:** se o worker só precisa *ler arquivos*, S3 normalmente é melhor que EFS — mais barato, sem NFS no caminho, com eventos nativos. EFS existe para quando algo exige semântica POSIX de verdade (biblioteca legada, lock de arquivo, append). Se der para escolher, escolha S3.

**O capítulo dedicado ao EFS** está em [`docs/09-efs.md`](docs/09-efs.md): as características que mudam uma decisão (Regional vs. One Zone, os três modos de throughput, classes de armazenamento), o fluxo de provisionamento na AWS — filesystem, mount target, Access Point, driver CSI, PVC — e o que muda no código das três linguagens, que é menos do que parece e mais do que "nada".

---

## 5. E se isso estivesse em ECS ou Lambda?

Os três serviços resolvem as mesmas duas perguntas — *como este código ganha permissão* e *onde ele guarda arquivo* — de formas bem diferentes. Vale conhecer as três mesmo trabalhando só com EKS: ajuda a ler o código de outros times e a participar de discussões de arquitetura.

O aprofundamento está em [`docs/06-eks-ecs-lambda.md`](docs/06-eks-ecs-lambda.md), com os artefatos em [`examples/`](examples/). O essencial:

| | **EKS** | **ECS (Fargate)** | **Lambda** |
|---|---|---|---|
| Roles por workload | 1 | **2** | 1 |
| Amarrada em | anotação no ServiceAccount | task definition | a própria função |
| Credencial chega via | arquivo com token JWT + STS | endpoint HTTP `169.254.170.2` | variáveis de ambiente |
| Principal na trust policy | `Federated` (OIDC do cluster) | `ecs-tasks.amazonaws.com` | `lambda.amazonaws.com` |
| Persistência | PVC → EFS ou EBS | EFS ou EBS, inline na task def | EFS (só) |
| Efêmero | filesystem do container | 20 GiB (até 200 GiB) | `/tmp`, 512 MB (até 10 GB) |
| IAM para montar volume | **não** | **sim, se `iam: ENABLED`** | **sim** |

### As três diferenças que mais pegam

**No ECS são duas roles, e trocá-las é o erro nº 1.** A *task execution role* é usada pelo agente do ECS **antes do seu código existir** — puxa a imagem do ECR, escreve logs, resolve secrets. A *task role* é usada pelo seu código, e é o equivalente direto da role do IRSA. Colocar `sqs:ReceiveMessage` na execution role produz o sintoma mais cruel possível: a task sobe, fica saudável, os logs aparecem no CloudWatch — e o código toma `AccessDenied` na primeira chamada. Regra mental: *execution = antes do seu código; task = o seu código.*

**No ECS o volume exige IAM; no EKS não.** Aqui a intuição vinda do Kubernetes falha direto. Com `authorizationConfig.iam: ENABLED` no `efsVolumeConfiguration`, a task role precisa de `elasticfilesystem:ClientMount` e `ClientWrite`. Se você migrar o worker de comprovantes e esquecer essas duas ações, a task morre no mount antes de logar qualquer coisa útil. No EKS, lembre, o pod não precisa de IAM nenhum para montar — quem precisa é o driver CSI.

**Na Lambda a credencial *é* variável de ambiente — e tudo bem.** `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` e `AWS_SESSION_TOKEN` ficam no ambiente de execução. Isso não contradiz o que foi dito antes: a regra "credencial em env var é ruim" sempre foi sobre credencial **permanente**. A presença do `AWS_SESSION_TOKEN` é justamente o sinal de que é STS. Outra sutileza: com event source mapping de SQS, a execution role precisa de `sqs:ReceiveMessage` — **mas o seu handler nunca chama isso**. Quem faz o polling é o serviço Lambda, e entrega o lote pronto no parâmetro.

### O que muda no código

Em ECS, **nada**. Nem uma linha. O `Program.cs` continua sem credencial no construtor, e a cadeia padrão do SDK descobre o endpoint de credenciais do ECS sozinha. Essa portabilidade é o retorno concreto de nunca passar `AWSCredentials` explicitamente.

Em Lambda, o `QueueConsumer.cs` **muda de forma**: o `while(true)`, o `ReceiveMessageAsync`, o long polling e o `DeleteMessageAsync` desaparecem — o serviço Lambda faz tudo isso. Em troca aparecem duas preocupações novas: **falha parcial de lote** (`ReportBatchItemFailures`, senão um erro em uma mensagem devolve o lote inteiro à fila) e **visibility timeout ≥ 6× o timeout da função**. A idempotência continua obrigatória, porque event source mapping também é entrega *pelo menos uma vez*.

Compare `src/Consumer/QueueConsumer.cs` com [`examples/lambda/Function.cs`](examples/lambda/Function.cs) — os comentários apontam o que sumiu e por quê.

### Qual escolher

| | Escolha quando | Cuidado com |
|---|---|---|
| **Lambda** | consumidor de fila com processamento curto; volume irregular; quer escalar a zero | limite de 15 min; cold start; custo alto em volume constante |
| **ECS Fargate** | worker de longa duração; quer controle do container sem operar cluster | menos flexível que Kubernetes; ecossistema menor |
| **EKS** | você **já tem** o cluster; precisa de Kubernetes de verdade | custo operacional alto se for só por três workers |

Para um consumidor de SQS com trabalho curto por mensagem, **Lambda costuma ser o default correto** — não há worker ocioso queimando dinheiro em madrugada de domingo. O que empurra times para ECS ou EKS é: processamento acima de 15 minutos, dependência de estado local, ou o cluster já existir com esteira de deploy pronta. Esse último motivo é legítimo — o custo de manter uma segunda plataforma de deploy é real.

---

## 6. Rodando localmente com Floci

O Floci é um emulador AWS local, MIT, drop-in do LocalStack (mesma porta 4566, mesmos env vars, mesmo `/_localstack/health`). Cobre SNS, SQS, IAM, STS e Secrets Manager em processo.

### Como ele entra no `docker-compose.yml`

```yaml
services:
  floci:
    image: floci/floci:latest
    ports:
      - "4566:4566"
    environment:
      FLOCI_HOSTNAME: floci          # URLs estáveis para quem chama de dentro da rede
      FLOCI_STORAGE_MODE: memory     # o estado morre com o container
```

Duas decisões que valem explicar:

**`FLOCI_HOSTNAME: floci`.** Sem isso, a URL que o Floci devolve em `get-queue-url` carrega o host que veio no cabeçalho da requisição — que muda conforme quem perguntou (o container diz `floci`, o seu terminal diz `localhost`). Fixar o hostname torna a URL estável para todo mundo.

**`FLOCI_STORAGE_MODE: memory`.** O estado morre junto com o container, que é o que se quer num lab: `./run.sh reset` volta ao zero. Trocando para `persistent` e montando `./data:/app/data`, o tópico e as filas sobrevivem ao `down`.

Não montamos o `/var/run/docker.sock`. Ele só é necessário para os serviços do Floci que sobem containers (EKS/k3s, Lambda); SNS, SQS, IAM, STS e Secrets Manager rodam em processo, e expor o socket num lab didático é dar mais poder do que o exercício precisa.

Os workers recebem `AWS_ENDPOINT_URL=http://floci:4566` pelo ambiente — nenhuma das três implementações tem endereço de emulador escrito no código.

### O caminho curto

```bash
./run.sh up        # sobe na ordem certa: Floci → bootstrap → workers
# aguarde ~30s
./run.sh verify    # checa se os resultados esperados aconteceram
./run.sh comprovantes
```

**A ordem importa.** O `run.sh` existe justamente para tirar essa pegadinha do caminho:

| Etapa | O que faz | Por que a ordem importa |
|---|---|---|
| 1 | sobe o Floci e **espera ficar saudável** | os passos seguintes falham contra um emulador que ainda está subindo |
| 2 | `bootstrap.sh`: tópico, filas, DLQs, resource policies, subscriptions | e grava o `.env` com o `TOPIC_ARN` gerado |
| 3 | sobe publisher e os 3 consumers | os workers têm retry, mas subir antes enche o log de erro e confunde |

Se preferir rodar etapa por etapa: `./run.sh bootstrap` e `./run.sh workers`.

### Resultados esperados

`./run.sh verify` checa 20+ afirmações e diz **qual capítulo revisar** quando alguma falha:

```
--- 2. Filas e DLQs
  PASSOU  fila cobranca-registro existe
  PASSOU  cobranca-registro tem RedrivePolicy apontando para a DLQ
  PASSOU  cobranca-registro tem resource policy permitindo o SNS

--- 5. Comprovantes no volume
  PASSOU  27 comprovante(s) .txt gravado(s) no volume
  PASSOU  roteamento OK: os 9 comprovantes de 'baixa' sao todos de cobranca.baixada
  PASSOU  volume compartilhado: consumer-registro enxerga 3 pastas de workers
  PASSOU  idempotencia: nenhum comprovante duplicado
```

A checagem mais valiosa é a de **roteamento**: ela lê os comprovantes do worker `baixa` e confirma que nenhum é de outro tipo de evento. Isso valida a `FilterPolicy` de ponta a ponta — se o emulador estiver ignorando o filtro, essa é a checagem que denuncia, e a mensagem sugere `TOPIC_MODE=multi`.

### Inspecionando na mão

```bash
export AWS_ENDPOINT_URL=http://localhost:4566
export AWS_ACCESS_KEY_ID=test AWS_SECRET_ACCESS_KEY=test AWS_DEFAULT_REGION=us-east-1

aws sqs get-queue-attributes \
  --queue-url http://localhost:4566/000000000000/cobranca-registro \
  --attribute-names ApproximateNumberOfMessages ApproximateNumberOfMessagesNotVisible

aws sns list-subscriptions-by-topic \
  --topic-arn arn:aws:sns:us-east-1:000000000000:eventos-cobranca

# as roles, se você rodou o create-roles.sh
aws iam get-role --role-name asa-dev-cash-cobranca-consumer-registro \
  --query 'Role.AssumeRolePolicyDocument'
```

### Experimentos que ensinam mais que ler

| Experimento | O que fazer | O que observar |
|---|---|---|
| **DLQ** | Force uma exceção no `ProcessarAsync` | A mensagem reaparece 3× e some da fila principal → está na `-dlq` |
| **Visibility timeout** | Baixe para `5` e ponha `Task.Delay(30s)` no processamento | A mesma mensagem é processada em paralelo por dois workers |
| **Idempotência** | Publique o mesmo payload duas vezes | O comprovante não é reescrito; o nome do arquivo é a chave |
| **Comprovante** | `find /data/comprovantes -name '*.txt'` | Um txt por evento, particionado por worker e data |
| **Durabilidade** | `docker compose restart consumer-baixa` | Os comprovantes continuam lá — o volume sobrevive ao container |
| **Escala horizontal** | `docker compose up --scale consumer-registro=3` | A SQS distribui sozinha, sem coordenação entre réplicas |
| **Filtro** | Mande `eventType=cobranca.registrada` e olhe as 3 filas | Só uma recebe — o roteamento é do broker, não seu |
| **Isolamento de role** | Compare os 3 `policy-consumer-*.json` | Ache o único campo que muda entre eles |
| **Quebrar permissões** | Abra [`docs/assets/visualizador.html`](docs/assets/visualizador.html) | Onde o fluxo para quando cada policy some |
| **Três linguagens, um comprovante** | `./run.sh comparar` | Python, Go e .NET geram o mesmo arquivo, byte a byte |
| **Cadeia de credenciais** | `./run.sh diagnostico go` (ou `python`, `dotnet`) | Três vocabulários de SDK, uma cadeia só |
| **Origem × destino** | `./run.sh cross-account` | O número da conta no `get-caller-identity` vira `444455556666` depois do assume role — sem trocar de terminal |
| **Os dois lados, no desenho** | Aba *"Cross-account"* do visualizador | Qual identidade está em ação e qual documento decide a cada salto; quebre a trust policy e veja a cadeia parar |
| **Seguir uma mensagem** | `./run.sh trace` e depois `./run.sh trace <id>` | A cadeia atravessa Python → .NET sem se partir |
| **Rastrear até a DLQ** | `./run.sh trace --dlq` | 3 tentativas, **um** trace — derivado do MessageId |
| **Logs no desenho** | `./run.sh exportar-logs` → aba "Logs reais" do visualizador | Os mesmos caminhos, com tempo real |

### Onde o Floci ajuda e onde não ajuda

**Ajuda muito:** SNS, SQS, S3, DynamoDB. Você exercita o SDK de verdade, o wire protocol de verdade, o comportamento de visibility timeout e DLQ de verdade. Para o pipeline deste lab, a fidelidade é ótima.

**Cuidado com IAM.** O Floci deixa você criar roles e policies e até chamar `AssumeRole` e `AssumeRoleWithWebIdentity` — ótimo para praticar os comandos. Mas ele não age como ponto de decisão de autorização entre serviços (credenciais podem ser quaisquer valores não-vazios, salvo checagens específicas como `FLOCI_SERVICES_S3_ENFORCE_AUTH`). Na prática: **uma policy mal escrita passa no Floci e falha na AWS.** Para validar least privilege, use o IAM Policy Simulator e o Access Analyzer.

**EKS:** o Floci sobe um k3s real via Docker para emular EKS, então o control plane responde de verdade. Mas o que faz IRSA funcionar é o webhook de injeção e o provedor OIDC do EKS gerenciado — isso não é reproduzido. Para praticar Kubernetes puro prefira `kind` ou `k3d`, e trate IRSA como algo que só se valida em cluster real.

O jeito de tirar proveito disso é o que já está no código: manter a resolução de credenciais **fora** do seu código. Nem `Program.cs` do Publisher nem o do Consumer passam credencial no construtor. Local, o SDK pega das env vars; no EKS, ele acha o token do web identity. Mesmo binário, zero `if`.

---

## 7. Três linguagens, o mesmo worker

Os três consumers que o `./run.sh up` sobe não são a mesma imagem:

| Serviço | Linguagem | SDK |
|---|---|---|
| `consumer-registro` | Python 3.12 | boto3 / botocore |
| `consumer-baixa` | Go 1.25 | `aws-sdk-go-v2` |
| `consumer-rejeicao` | .NET 10 | `AWSSDK` |

Isso não é enfeite. É a demonstração de que **nada das seções 1 a 6 depende da linguagem**: as filas, as roles, a resource policy, o volume compartilhado e as 23 checagens do `verify.sh` são exatamente os mesmos. O único acoplamento real entre os três é o **formato do comprovante**, porque o nome do arquivo é a chave de idempotência — e isso o lab prova em vez de afirmar:

```bash
./run.sh comparar
```

```
  IGUAL   python == go   (1086 bytes)
  IGUAL   python == dotnet   (1086 bytes)
```

### Log estruturado e `trace_id`

Os quatro processos (publisher + três consumers) escrevem eventos JSON com um **`trace_id` que atravessa o pipeline inteiro**: ele nasce no publisher, viaja como *MessageAttribute* do SNS — nunca dentro do corpo, porque o corpo é o que gera o hash de idempotência — e reaparece no campo `Trace ID` do comprovante gravado no volume.

```bash
./run.sh trace                 # lista os traces recentes
./run.sh trace <id>            # a cadeia daquela mensagem, do publish ao comprovante
./run.sh trace --dlq           # só as que falharam
./run.sh exportar-logs         # junta tudo num .ndjson para o visualizador
```

```
  17:11:00.665  publish.iniciado      publisher/python
  17:11:00.672  publish.ok            publisher/python
  17:11:00.698  mensagem.recebida     rejeicao/dotnet
  17:11:00.716  comprovante.gravado   rejeicao/dotnet
  17:11:00.720  mensagem.deletada     rejeicao/dotnet
```

Publisher em Python, consumer em .NET, um `trace_id` só — a cadeia atravessa o SNS, a SQS e uma fronteira de linguagem sem se partir.

O **[visualizador](docs/assets/visualizador.html)** tem uma aba *"Logs reais"*: arraste o `.ndjson` exportado e ele desenha os traces sobre o mesmo diagrama da simulação, com o tempo que cada salto levou de verdade.

Detalhes — inclusive por que o trace de uma mensagem sem atributo é **derivado do `MessageId`** e não sorteado — em [`docs/07-implementacoes-python-go-dotnet.md`](docs/07-implementacoes-python-go-dotnet.md) §7.

### Diagnóstico e segredos

Os três também trazem o programa de diagnóstico do capítulo 03 e a demo de cache do capítulo 04:

```bash
./run.sh diagnostico python     # qual provedor da cadeia venceu
./run.sh diagnostico go
./run.sh diagnostico dotnet
./run.sh segredos go            # 5 leituras, 1 ida à AWS
```

A diferença mais instrutiva entre as implementações não está em nenhum SDK: é que `os.rename` (Python) e `os.Rename` (Go) **sobrescrevem o destino em silêncio**, enquanto o `File.Move(..., overwrite: false)` do .NET falha. Traduzir um pelo outro quebra a idempotência sem produzir erro nenhum. O detalhe, e a saída (`link(2)`), estão em [`docs/07-implementacoes-python-go-dotnet.md`](docs/07-implementacoes-python-go-dotnet.md) §5.

Código em [`examples/multilinguagem/`](examples/multilinguagem/). Nenhuma das três exige a linguagem instalada na sua máquina — os `Dockerfile` são multi-estágio.

---

## 8. Por onde começar

O passo a passo completo — 17 etapas, cerca de 2h30, com o que esperar em cada uma — está em **[`ROTEIRO.md`](ROTEIRO.md)**.

Resumo dos blocos:

| Bloco | O que você faz | Tempo |
|---|---|---|
| 0 | confere pré-requisitos | 5 min |
| 1 | abre o visualizador e quebra permissões | 10 min |
| 2 | `./run.sh up` e `./run.sh verify` | 20 min |
| 3 | cinco experimentos: roteamento, idempotência, escala, DLQ, durabilidade | 45 min |
| 4 | cria as roles, lê as trust policies, testa a cadeia de credenciais | 30 min |
| 5 | compara as três linguagens e troca a linguagem de um worker | 25 min |
| 6 | leva para um `kind` local e para o seu trabalho | 20 min |

Se preferir estudar antes de rodar, o índice das apostilas está em [`docs/README.md`](docs/README.md).

## O que foi verificado e o que não foi

Vale saber o que está conferido e o que depende de você rodar:

| Item | Status |
|---|---|
| JSON de todas as policies | validado (parse + estrutura) |
| YAML dos manifests e do compose | validado |
| Coerência `serviceAccountName` ↔ trust policy | validado por script |
| Sintaxe dos shell scripts | `bash -n` em todos |
| Escape JSON do `bootstrap.sh` | testado (round-trip) |
| Links entre os guias | validado |
| Build das três imagens (Python, Go, .NET) | executado |
| `./run.sh up` + `./run.sh verify` | **executado — 23/23 passaram** |
| Equivalência dos três comprovantes | **executada** — `./run.sh comparar`, 1086 bytes idênticos |
| Cadeia de credenciais nas três linguagens | executada — `./run.sh diagnostico {python,go,dotnet}` |
| Cache com TTL nas três linguagens | executado — `./run.sh segredos {python,go,dotnet}` |
| DLQ: payload inválido nas três filas | executado — 3 tentativas e migração para a DLQ, nas três |
| Idempotência com publicação duplicada | executada — o segundo comprovante é recusado pelo nome |
| Troca de linguagem de um worker sem apagar o volume | executada — o worker Go recusou como duplicata um comprovante do .NET |
| `trace_id` propagado do publisher ao comprovante | executado — cadeia Python → .NET num só trace |
| `trace_id` derivado na DLQ | executado — 3 tentativas, um trace, `./run.sh trace --dlq` |
| **Compilação dos projetos `src/` em C#** | **não verificada** — rode `./run.sh build` |

O único item em aberto é a compilação dos projetos `.NET` soltos de `src/` e
`examples/` (que exigem `dotnet` na máquina). Todo o resto foi executado de
ponta a ponta contra o Floci. O `verify.sh` continua sendo a forma de conferir
do seu lado: ele codifica os resultados esperados como checagens.

## Referências

- **Guias deste repositório — [`docs/README.md`](docs/README.md)**
- ECS task IAM roles — https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html
- Lambda com SQS — https://docs.aws.amazon.com/lambda/latest/dg/with-sqs.html
- IAM roles for service accounts — https://docs.aws.amazon.com/eks/latest/userguide/iam-roles-for-service-accounts.html
- EKS Pod Identity — https://docs.aws.amazon.com/eks/latest/userguide/pod-identities.html
- SNS message filtering — https://docs.aws.amazon.com/sns/latest/dg/sns-message-filtering.html
- EFS CSI driver — https://github.com/kubernetes-sigs/aws-efs-csi-driver
- Floci — https://floci.io/aws/ e https://github.com/floci-io/floci
- Cadeia de credenciais do botocore — https://boto3.amazonaws.com/v1/documentation/api/latest/guide/credentials.html
- Configuração do AWS SDK for Go v2 — https://aws.github.io/aws-sdk-go-v2/docs/configuring-sdk/
