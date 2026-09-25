# Diagramas

Os mesmos conceitos dos outros capítulos, em forma visual. Serve como mapa: leia daqui e vá para o capítulo que explica cada pedaço.

Há também um **[visualizador interativo](assets/visualizador.html)** — abra o arquivo no navegador (`open docs/assets/visualizador.html`). Ele tem três abas:

- **Simulação** — anima uma mensagem atravessando o pipeline e deixa você **quebrar permissões** para ver onde o fluxo para. A mais instrutiva é a resource policy: o publish retorna sucesso, a subscription aparece confirmada, e a mensagem simplesmente não chega.
- **Logs reais** — carrega o NDJSON que o lab gravou (`./run.sh exportar-logs`) e desenha os traces de verdade sobre o mesmo diagrama, com o tempo que cada salto levou. Ver [guia 07](07-implementacoes-python-go-dotnet.md) §7.
- **Cross-account** — troca o palco por **duas contas lado a lado** e percorre a travessia passo a passo, nos três padrões do [guia 02](02-assume-role-cross-account.md) §2. A cada salto ele mostra o que está em trânsito, **qual identidade você é naquele instante** (a conta muda no meio do caminho no padrão 1, e nunca muda no padrão 2) e **qual documento decide** — com o JSON e a conta em que ele mora. As duas quebras mais instrutivas são silenciosas: a role recriada com o mesmo nome, que faz o principal virar um `AROA…` morto, e o ARN da sessão colado numa policy.

---

## 1. O caminho de uma mensagem

Cada seta é uma chamada de API que o IAM precisa autorizar.

```mermaid
flowchart LR
    P["publisher<br/><small>sa-publisher</small>"]
    T{{"SNS<br/>eventos-cobranca"}}
    Q1["SQS<br/>registro"]
    Q2["SQS<br/>baixa"]
    Q3["SQS<br/>rejeicao"]
    D1[("DLQ")]
    W1["consumer registro<br/><small>sa-consumer-registro</small>"]
    W2["consumer baixa<br/><small>sa-consumer-baixa</small>"]
    W3["consumer rejeicao<br/><small>sa-consumer-rejeicao</small>"]
    V[("PVC EFS<br/>ReadWriteMany<br/>/data/comprovantes")]

    P -->|"sns:Publish<br/>identity policy"| T
    T -->|"eventType=<br/>cobranca.registrada"| Q1
    T -->|"eventType=<br/>cobranca.baixada"| Q2
    T -->|"eventType=<br/>cobranca.rejeitada"| Q3
    Q1 -.->|"apos 3 falhas"| D1
    Q1 -->|"sqs:ReceiveMessage"| W1
    Q2 -->|"sqs:ReceiveMessage"| W2
    Q3 -->|"sqs:ReceiveMessage"| W3
    W1 --> V
    W2 --> V
    W3 --> V
```

Dois pontos que o diagrama esconde e valem lembrar:

- A entrega **SNS → SQS** depende da *resource policy* da fila, não da role de ninguém. É o tipo de policy que mora no recurso (guia [01](01-iam-explicado.md) §3).
- O **roteamento acontece no broker**, pela `FilterPolicy` da subscription. Nenhum dos três consumers tem código de filtro.

---

## 2. Como o pod prova quem é (IRSA)

```mermaid
sequenceDiagram
    participant K as kubelet
    participant P as pod (worker)
    participant S as AWS STS
    participant Q as SQS

    Note over K,P: na criação do pod
    K->>P: injeta AWS_ROLE_ARN
    K->>P: injeta AWS_WEB_IDENTITY_TOKEN_FILE
    K->>P: monta projected volume com o JWT

    Note over P: primeira chamada de verdade
    P->>P: lê o arquivo do token AGORA
    P->>S: AssumeRoleWithWebIdentity(role, token)
    S->>S: valida assinatura contra o provedor OIDC
    S->>S: confere sub = system:serviceaccount:cash:sa-consumer-registro
    S-->>P: credenciais temporárias (ASIA..., ~1h)

    P->>Q: ReceiveMessage (assinado)
    Q-->>P: mensagens

    Note over P,S: perto de expirar
    P->>P: RELÊ o arquivo (o kubelet rotaciona)
    P->>S: AssumeRoleWithWebIdentity de novo
```

Detalhado no guia [03](03-credenciais-no-dotnet.md) §3. O ponto que costuma passar batido: o token é **relido a cada renovação**, nunca cacheado.

---

## 3. Onde cada permissão mora

```mermaid
flowchart TB
    subgraph K8s["Kubernetes"]
        SA["ServiceAccount<br/>sa-consumer-registro"]
        DEP["Deployment<br/>serviceAccountName:"]
        DEP --> SA
    end

    subgraph AWS["AWS IAM"]
        TRUST["TRUST POLICY<br/><i>quem pode assumir</i>"]
        ROLE["Role<br/>asa-dev-...-consumer-registro"]
        PERM["PERMISSION POLICY<br/><i>o que pode fazer</i>"]
        TRUST --> ROLE
        ROLE --> PERM
    end

    subgraph REC["No recurso"]
        RES["RESOURCE POLICY<br/>da fila SQS"]
        KEY["KEY POLICY<br/>da chave KMS"]
    end

    SA -.->|"anotação<br/>eks.amazonaws.com/role-arn"| ROLE
    TRUST -.->|"cita<br/>system:serviceaccount:cash:sa-..."| SA
    PERM --> RES
    PERM --> KEY
```

Os três (ou quatro) documentos que precisam concordar. O guia [01](01-iam-explicado.md) §3 e §4 explica os dois primeiros; o [05](05-kms.md) explica por que a key policy é diferente de todas as outras.

---

## 4. Cross-account: os dois lados

```mermaid
sequenceDiagram
    participant W as worker (conta A)
    participant SA as STS
    participant B as recurso (conta B)

    Note over W: já tem credencial via IRSA
    W->>SA: AssumeRole(role da conta B)
    Note right of SA: 1. identity policy em A permite?<br/>2. trust policy em B aceita?
    SA-->>W: credenciais da conta B
    W->>B: PutObject
    Note right of B: 3. key policy do KMS<br/>menciona o principal?
    B-->>W: 200 OK
```

As três verificações são exatamente onde as coisas falham, e na ordem em que falham. Guia [02](02-assume-role-cross-account.md) §2.

---

## 5. As três plataformas de compute

```mermaid
flowchart LR
    subgraph EKS
        E1["ServiceAccount<br/>anotado"] --> E2["JWT + STS"] --> E3["1 role"]
    end
    subgraph ECS
        C1["task definition"] --> C2["endpoint<br/>169.254.170.2"]
        C1 --> C3["2 roles:<br/>execution + task"]
    end
    subgraph Lambda
        L1["função"] --> L2["variáveis<br/>de ambiente"] --> L3["1 role<br/>(2 usuários)"]
    end
```

Guia [06](06-eks-ecs-lambda.md). A diferença que mais pega: no ECS são **duas** roles, e trocá-las produz uma task saudável cujo código toma `AccessDenied`.

---

## 6. Ordem de execução do lab

```mermaid
flowchart TB
    A["./run.sh up"] --> B["1. docker compose up floci"]
    B --> C{"Floci<br/>respondendo?"}
    C -->|não| B
    C -->|sim| D["2. bootstrap.sh<br/>topico, filas, DLQs,<br/>resource policies, subscriptions"]
    D --> E["grava .env com TOPIC_ARN"]
    E --> F["3. docker compose up<br/>publisher + 3 consumers"]
    F --> G["./run.sh verify"]
    G --> H{"passou?"}
    H -->|sim| I["experimentos<br/>README secao 6"]
    H -->|não| J["a mensagem diz<br/>qual capitulo revisar"]
```

A ordem importa: subir os workers antes do passo 2 não quebra nada (eles têm retry), mas gera uma enxurrada de erro no log que confunde quem está aprendendo. O `run.sh` existe para tirar essa pegadinha do caminho.
