# Roteiro do lab

Passo a passo do zero ao fim. Cada etapa tem **o que fazer**, **o que esperar**, **o que você acabou de aprender** e **onde ler mais**.

O `README.md` é referência (como o lab é montado). As [apostilas](docs/README.md) são teoria. **Este arquivo é a ordem em que fazer as coisas.**

Tempo total: cerca de 2h45 se você fizer tudo. Dá para parar depois do Bloco 2 e voltar outro dia — o `docker compose down` preserva o volume.

| Bloco | Etapas | Tempo | O que você sai sabendo |
|---|---|---|---|
| 0 — Preparação | 1 | 5 min | ambiente pronto |
| 1 — Ver antes de ler | 2 | 10 min | o desenho inteiro na cabeça |
| 2 — Rodar | 3–5 | 20 min | o pipeline funcionando e conferido |
| 3 — Experimentos | 6–10 | 45 min | por que cada peça existe |
| 4 — IAM | 11–13 | 30 min | roles, cadeia de credenciais, isolamento |
| 5 — Três linguagens | 14–16 | 40 min | o que **não** depende da linguagem, e como rastrear |
| 6 — Levar adiante | 17–18 | 20 min | Kubernetes de verdade e o seu trabalho |

---

# Bloco 0 — Preparação

## Etapa 1 — Conferir os pré-requisitos

**Fazer:**

```bash
docker --version      # qualquer versão recente com "docker compose"
aws --version         # aws-cli v2
dotnet --version      # 10.x — opcional, só para os projetos .NET soltos
```

**Esperar:** as duas primeiras respondem. O `dotnet` é opcional.

> **Não é preciso ter Python, Go nem .NET instalados.** Os três workers do lab
> são compilados dentro do Docker, em build multi-estágio.

> **Se faltar o aws-cli:** ele é obrigatório. O `bootstrap.sh` e o `verify.sh` falam com o Floci por ele.

**Ler mais:** nada ainda.

---

# Bloco 1 — Ver antes de ler

## Etapa 2 — Abrir o visualizador

Cinco minutos aqui poupam meia hora de leitura depois.

**Fazer:** abra [`docs/assets/visualizador.html`](docs/assets/visualizador.html) no navegador.

```bash
open docs/assets/visualizador.html        # macOS
xdg-open docs/assets/visualizador.html    # Linux
```

1. Clique em **Publicar 10** e observe o caminho da mensagem.
2. Marque **uma** caixinha de "quebrar permissões" por vez, publique de novo, e leia o log.
3. Preste atenção especial em **"Tirar a resource policy da fila de baixa"**.

**Esperar:** com a resource policy quebrada, o publish dá **sucesso**, a mensagem some, e **nenhum erro aparece**. Esse é o sintoma mais cruel do fan-out e o que você vai reconhecer em produção.

**O que você aprendeu:** existem pelo menos três tipos de documento que precisam concordar — a policy da role, a policy do recurso, e (quando há criptografia) a key policy. Falhar em cada um produz um sintoma diferente.

**Ler mais:** [`docs/00-diagramas.md`](docs/00-diagramas.md) — os mesmos caminhos em diagrama.

---

# Bloco 2 — Rodar

## Etapa 3 — Subir o lab

**Fazer:**

```bash
./run.sh up
```

**Esperar:** três etapas numeradas. A primeira build do .NET demora alguns minutos.

```
==> ETAPA 1/3 - subindo o emulador
  aguardando o Floci responder......
  OK   Floci respondendo em http://localhost:4566

==> ETAPA 2/3 - criando topico, filas, DLQs e subscriptions
    topico: arn:aws:sns:us-east-1:000000000000:eventos-cobranca
    fila: cobranca-registro  <-  cobranca.registrada
    fila: cobranca-baixa  <-  cobranca.baixada
    fila: cobranca-rejeicao  <-  cobranca.rejeitada
    .env gerado com TOPIC_ARN=...

==> ETAPA 3/3 - subindo publisher e os 3 consumers
```

> Os três consumers estão em **três linguagens diferentes** — `consumer-registro`
> em Python, `consumer-baixa` em Go, `consumer-rejeicao` em .NET. Por enquanto
> ignore isso: o ponto do bloco 5 é justamente que não faz diferença nenhuma
> daqui até a etapa 13.

**O que você aprendeu:** a ordem importa. O `bootstrap.sh` precisa rodar com o emulador de pé, e grava o `.env` com o ARN do tópico que só existe depois dele. Subir os workers antes não quebra (eles têm retry), mas enche o log de erro.

**Ler mais:** `README.md` §6.

---

## Etapa 4 — Conferir que funcionou

Aguarde uns 30 segundos antes de rodar.

**Fazer:**

```bash
./run.sh verify
```

**Esperar:** todas as checagens em `PASSOU`. A mais importante:

```
  PASSOU  roteamento OK: os 9 comprovantes de 'baixa' sao todos de cobranca.baixada
```

**Como ler:** essa linha abre os comprovantes do worker `baixa` e confirma que nenhum é de outro tipo. É a `FilterPolicy` validada de ponta a ponta.

> **Se falhar:** cada mensagem de falha diz qual capítulo revisar. Se a falha for de `FilterPolicy`, o emulador não a suporta — rode `./run.sh reset` e depois `TOPIC_MODE=multi ./run.sh up`.

**O que você aprendeu:** o roteamento acontece no **broker**, não no seu código. Nenhum dos três consumers tem uma linha de filtro.

---

## Etapa 5 — Abrir um comprovante

**Fazer:**

```bash
./run.sh comprovantes
```

**Esperar:** a contagem por worker e um comprovante completo.

```
================================================================
  COMPROVANTE DE BAIXA DE COBRANÇA
================================================================
Nosso número......: 4827193
Valor.............: R$ 1.234,56
----------------------------------------------------------------
Processado por....: baixa
Pod / host........: 8f3a1c22b4d1        <-- guarde este valor
Tentativa.........: 1                   <-- e este
```

**O que você aprendeu:** `Pod / host` é `Environment.MachineName`, que dentro de um pod é o nome do pod. `Tentativa` vem do `ApproximateReceiveCount` da SQS — se aparecer `2` ou `3`, aquela mensagem já falhou antes. Os dois campos voltam nas etapas 7 e 8.

**Ler mais:** `README.md` §3.

---

# Bloco 3 — Experimentos

Daqui em diante você provoca falhas de propósito. Prepare o terminal:

```bash
export AWS_ENDPOINT_URL=http://localhost:4566
export AWS_ACCESS_KEY_ID=test AWS_SECRET_ACCESS_KEY=test AWS_DEFAULT_REGION=us-east-1
export TOPICO=arn:aws:sns:us-east-1:000000000000:eventos-cobranca
export FILA=http://localhost:4566/000000000000/cobranca-registro
```

## Etapa 6 — Roteamento: só uma fila recebe

**Fazer:** publique um evento de um tipo específico e olhe as três filas.

```bash
aws sns publish --topic-arn "$TOPICO" \
  --message '{"nossoNumero":"7777777","tipoEvento":"cobranca.baixada","valorCentavos":50000,"ocorridoEm":"2026-08-09T12:00:00Z"}' \
  --message-attributes '{"eventType":{"DataType":"String","StringValue":"cobranca.baixada"}}'

sleep 3
docker compose logs --tail=5 consumer-registro consumer-baixa consumer-rejeicao
```

**Esperar:** só o `consumer-baixa` reagiu. Os outros dois nem viram a mensagem.

**O que você aprendeu:** um `Publish`, três subscriptions, uma entrega. O produtor não sabe quem consome — é isso que permite acrescentar um quarto consumidor amanhã sem tocar no publisher.

---

## Etapa 7 — Idempotência: publique a mesma coisa duas vezes

**Fazer:** rode o **mesmo comando duas vezes** (payload idêntico, de propósito).

```bash
PAYLOAD='{"nossoNumero":"1234567","tipoEvento":"cobranca.registrada","valorCentavos":9900,"ocorridoEm":"2026-08-09T12:00:00Z"}'
ATTR='{"eventType":{"DataType":"String","StringValue":"cobranca.registrada"}}'

aws sns publish --topic-arn "$TOPICO" --message "$PAYLOAD" --message-attributes "$ATTR"
sleep 2
aws sns publish --topic-arn "$TOPICO" --message "$PAYLOAD" --message-attributes "$ATTR"
sleep 3

docker compose logs --tail=10 consumer-registro
```

**Esperar:** a segunda vez gera um aviso, não um arquivo novo.

```
[12:00:03] registro 60eac105  comprovante gravado: /data/comprovantes/registro/2026-08-09/1234567-A1B2C3D4.txt
[12:00:05] registro 9b3034b3  comprovante ja existe, duplicata ignorada: 1234567-A1B2C3D4.txt
```

Repare nos dois códigos depois do nome do worker: são `trace_id` **diferentes**.
Cada publicação ganhou o seu. Mas o comprovante não foi reescrito — então o
arquivo guarda para sempre o trace da **primeira** gravação. Está correto: o
documento registra quem o criou, não quem tentou recriá-lo. Você volta a isso
na etapa 16.

**O que você aprendeu:** SQS entrega **pelo menos uma vez**, então duplicata não é exceção, é rotina. O nome do arquivo deriva do hash do payload — a idempotência mora no estado compartilhado, não em memória. Ela sobrevive a restart do pod e vale entre réplicas.

---

## Etapa 8 — Escala: a SQS distribui sozinha

**Fazer:**

```bash
docker compose up -d --scale consumer-registro=3
sleep 20

docker compose exec -T consumer-baixa sh -c \
  'grep -h "Pod / host" /data/comprovantes/registro/*/*.txt | sort | uniq -c'
```

**Esperar:** três hosts diferentes, com contagens parecidas.

```
   6 Pod / host........: 3f2a1b4c5d6e
   5 Pod / host........: 7a8b9c0d1e2f
   7 Pod / host........: 1c2d3e4f5a6b
```

**O que você aprendeu:** duas coisas ao mesmo tempo. A SQS distribuiu entre as réplicas **sem nenhuma coordenação entre elas** — é assim que se escala um consumidor. E o comando rodou dentro do `consumer-baixa` lendo arquivos que o `consumer-registro` escreveu: o volume é `ReadWriteMany` de verdade.

**Voltar ao normal:**

```bash
docker compose up -d --scale consumer-registro=1
```

---

## Etapa 9 — DLQ: onde vai parar o que não dá para processar

Nenhuma edição de código. Basta mandar algo que o parser não entende.

**Fazer:** acelere o ciclo de retry primeiro, senão leva 3 minutos.

```bash
aws sqs set-queue-attributes --queue-url "$FILA" --attributes VisibilityTimeout=5

aws sqs send-message --queue-url "$FILA" --message-body 'isto-nao-e-json'

sleep 30
docker compose logs --tail=15 consumer-registro

aws sqs get-queue-attributes \
  --queue-url http://localhost:4566/000000000000/cobranca-registro-dlq \
  --attribute-names ApproximateNumberOfMessages
```

**Esperar:** três tentativas no log e depois `"ApproximateNumberOfMessages": "1"` na DLQ.

**O que você aprendeu:** a mensagem falhou, **não foi deletada**, voltou pela expiração do visibility timeout, e após `maxReceiveCount` foi para a DLQ. É por isso que o `DeleteMessage` vem **depois** de gravar o comprovante: a ordem inversa criaria a janela oposta, com mensagem sumindo sem comprovante.

> A partir daqui `./run.sh verify` vai acusar mensagem na DLQ. É esperado. `./run.sh reset` limpa.

**Restaurar:**

```bash
aws sqs set-queue-attributes --queue-url "$FILA" --attributes VisibilityTimeout=60
```

---

## Etapa 10 — Durabilidade: o volume sobrevive ao container

**Fazer:**

```bash
docker compose exec -T consumer-registro sh -c 'find /data/comprovantes -name "*.txt" | wc -l'
docker compose restart consumer-registro
sleep 10
docker compose exec -T consumer-registro sh -c 'find /data/comprovantes -name "*.txt" | wc -l'
```

**Esperar:** a segunda contagem é maior ou igual à primeira. Nada se perdeu.

**O que você aprendeu:** o filesystem do container é efêmero; o volume não. É o ensaio local do PVC. E como a idempotência mora nos nomes de arquivo, ela sobreviveu ao restart junto — coisa que um `HashSet` em memória não faria.

**Ler mais:** `README.md` §4, sobretudo "O que muda no seu código".

---

# Bloco 4 — IAM

## Etapa 11 — Criar as roles

Até aqui o lab funcionou **sem nenhuma role**, porque o emulador não exige. Agora você cria as roles de verdade para ver a estrutura.

**Fazer:**

```bash
AWS_ENDPOINT_URL=http://localhost:4566 ./infra/aws/create-roles.sh

aws iam get-role --role-name asa-dev-cash-cobranca-consumer-registro \
  --query 'Role.AssumeRolePolicyDocument'

aws iam get-role-policy \
  --role-name asa-dev-cash-cobranca-consumer-registro \
  --policy-name permissoes-consumer-registro --query 'PolicyDocument'
```

**Esperar:** dois documentos bem diferentes. O primeiro diz **quem pode assumir**, o segundo diz **o que pode fazer**.

**Fazer também** — compare as três permission policies:

```bash
diff infra/iam/policy-consumer-registro.json infra/iam/policy-consumer-baixa.json
```

**Esperar:** a diferença é só o nome da fila.

**O que você aprendeu:** confundir trust policy com permission policy é o erro nº 1 de quem começa. São documentos separados, criados por comandos diferentes (`create-role` vs `put-role-policy`). E "uma role por worker" na prática significa três arquivos quase idênticos — o que muda é justamente o escopo.

**Ler mais:** [`docs/01-iam-explicado.md`](docs/01-iam-explicado.md) §4 e §6.

---

## Etapa 12 — Achar os três pontos onde a role aparece

Este é um exercício de leitura, sem comando. Vale mais que os anteriores.

**Fazer:** abra os três arquivos e localize, **para o worker de registro**:

| Onde | Arquivo | O que procurar |
|---|---|---|
| 1 | `infra/k8s/01-workers-irsa.yaml` | o `serviceAccountName` do Deployment |
| 2 | o mesmo arquivo | a anotação `eks.amazonaws.com/role-arn` daquele ServiceAccount |
| 3 | `infra/iam/trust-policy-consumer-registro.json` | o claim `:sub` |

**Esperar:** os três se encaixam:

```
Deployment  →  serviceAccountName: sa-consumer-registro
     ↓
ServiceAccount sa-consumer-registro  →  role-arn: ...consumer-registro
     ↓
trust policy dessa role  →  sub: system:serviceaccount:cash:sa-consumer-registro
```

**Pergunta para você responder:** o que acontece se alguém mudar o `serviceAccountName` de um Deployment sem mudar a trust policy?

<details><summary>resposta</summary>

O pod sobe normalmente, mas o `AssumeRoleWithWebIdentity` falha: o claim `sub` do token não bate com a condição da trust policy. É o comportamento desejado — a amarração existe nos dois lados, e é o que impede um serviço de usar as permissões de outro.
</details>

**Ler mais:** [`docs/01-iam-explicado.md`](docs/01-iam-explicado.md) §5.

---

## Etapa 13 — A cadeia de credenciais e o Secrets Manager

Sem pré-requisito: roda dentro do container. (Se você tiver `dotnet` e a pasta
`examples/dotnet-credenciais/` existir, `./run.sh diagnostico` sem argumento usa
ela; caso contrário cai no worker Python.)

**Fazer:**

```bash
./run.sh diagnostico python
```

**Esperar:** o programa diz qual provedor venceu e quem você é. Dentro do
compose, `env` / `EnvironmentVariablesAWSCredentials` — as variáveis do
`docker-compose.yml`.

**Fazer agora o experimento que importa:**

```bash
docker compose run --rm \
  -e AWS_ROLE_ARN=arn:aws:iam::111122223333:role/qualquer \
  -e AWS_WEB_IDENTITY_TOKEN_FILE=/tmp/token-falso \
  consumer-registro python -u diagnostico.py
```

As variáveis vão com `-e` porque o programa roda **dentro do container** —
exportá-las no seu shell não as levaria para lá. É a mesma lição do
`kubectl exec ... env | grep AWS_` do guia 01: o ambiente que importa é o do
processo, não o seu.

**Esperar:** o alerta dispara.

```
  (!) AWS_ACCESS_KEY_ID e AWS_ROLE_ARN presentes ao mesmo tempo.
      Variavel de ambiente vence IRSA na cadeia. O IRSA esta sendo IGNORADO.
```

**O que você aprendeu:** um `AWS_ACCESS_KEY_ID` esquecido num ConfigMap **cala o IRSA por completo**. O SDK nunca chega no provedor de web identity porque o de env vars respondeu antes. É a causa mais comum de "o IRSA não funciona" — e não aparece em nenhuma policy.

**Fazer em seguida:**

```bash
./run.sh segredos python
```

**Esperar:** cinco leituras, mas só uma ida à AWS. No fim, a simulação de rotação mostra o cache quente devolvendo a **senha velha** até você invalidar.

**O que você aprendeu:** cache não é otimização, é requisito — sem ele você toma throttling. Mas cache **eterno** é pior: o worker funciona por semanas e quebra numa madrugada de rotação, voltando só com restart.

**Ler mais:** [`docs/03-credenciais-no-dotnet.md`](docs/03-credenciais-no-dotnet.md) §2 e [`docs/04-secrets-manager.md`](docs/04-secrets-manager.md) §3.

---

# Bloco 5 — Três linguagens

Este bloco existe para responder a uma pergunta que costuma aparecer tarde:
*"tudo isso vale se o meu time não usa .NET?"*

Você já rodou o lab inteiro sem reparar, mas os três consumers estão em
linguagens diferentes:

| Serviço | Linguagem | SDK |
|---|---|---|
| `consumer-registro` | Python 3.12 | boto3 / botocore |
| `consumer-baixa` | Go 1.25 | `aws-sdk-go-v2` |
| `consumer-rejeicao` | .NET 10 | `AWSSDK` |

Confirme antes de continuar:

```bash
docker compose ps --format 'table {{.Service}}\t{{.Command}}'
```

**Esperar:** `python -u consumer.py`, `/app/consumer` e `dotnet Lab.dll consumer`.

**O que você aprendeu:** as etapas 3 a 13 funcionaram sem que você soubesse
disso. Nada do que você fez até aqui — filas, DLQ, roles, trust policy,
volume compartilhado — dependia da linguagem.

---

## Etapa 14 — A mesma cadeia de credenciais, três vocabulários

**Fazer:**

```bash
./run.sh diagnostico python
./run.sh diagnostico go
./run.sh diagnostico dotnet
```

**Esperar:** os três dizem a mesma coisa com palavras diferentes.

```
  method .......: env                                   (Python)
  Source .......: EnvConfigCredentials                  (Go)
  tipo .........: EnvironmentVariablesAWSCredentials    (.NET)
```

**Fazer agora o experimento da etapa 13, mas em Go:**

```bash
docker compose run --rm \
  -e AWS_ROLE_ARN=arn:aws:iam::111122223333:role/qualquer \
  -e AWS_WEB_IDENTITY_TOKEN_FILE=/tmp/token-falso \
  consumer-baixa /app/diagnostico
```

**Esperar:** o mesmo alerta, palavra por palavra.

```
  (!) AWS_ACCESS_KEY_ID e AWS_ROLE_ARN presentes ao mesmo tempo.
      Variavel de ambiente vence IRSA na cadeia. O IRSA esta sendo IGNORADO.
```

**O que você aprendeu:** o bug do IRSA silenciado **não é do .NET**. Os três
SDKs implementam a mesma cadeia, na mesma ordem, e param no mesmo elo. Quando
alguém do time disser "isso é coisa do SDK de vocês", esta é a resposta.

**Ler mais:** [`docs/07-implementacoes-python-go-dotnet.md`](docs/07-implementacoes-python-go-dotnet.md) §2.

---

## Etapa 15 — Trocar a linguagem de um worker sem perder a idempotência

Primeiro, prove que as três geram o mesmo arquivo:

**Fazer:**

```bash
./run.sh comparar
```

**Esperar:**

```
  hash    : 299E3E228C2D1224528490FAAE4B8C27
  caminho : /data/comprovantes/baixa/2026-08-08/4827193-299E3E22.txt
  ...
  IGUAL   python == go   (1086 bytes)
  IGUAL   python == dotnet   (1086 bytes)
```

**Agora troque de verdade.** No `docker-compose.yml`, em `consumer-rejeicao`:

```yaml
    build:
      context: ./examples/multilinguagem/go        # era .../dotnet
    command: ["/app/consumer"]                     # era ["dotnet","Lab.dll","consumer"]
```

```bash
docker compose up -d --build consumer-rejeicao
./run.sh verify
```

**Esperar:** todas as checagens continuam passando — **sem apagar o volume**.

**Fazer a prova direta.** Abra um comprovante que o worker .NET gravou *antes*
da troca e republique aquele mesmo evento:

```bash
docker compose exec -T consumer-registro sh -c \
  'ls -t /data/comprovantes/rejeicao/*/*.txt | tail -1 | xargs cat'
# anote nossoNumero, valor (em centavos) e "Ocorrido em"

aws sns publish --topic-arn "$TOPICO" \
  --message '{"nossoNumero":"9758171","tipoEvento":"cobranca.rejeitada","valorCentavos":44370,"ocorridoEm":"2026-08-09T16:50:09Z"}' \
  --message-attributes '{"eventType":{"DataType":"String","StringValue":"cobranca.rejeitada"}}'

docker compose logs --tail=5 consumer-rejeicao
```

**Esperar:** o worker **Go** recusa um comprovante que o worker **.NET** gravou.

```
rejeicao: comprovante ja existe, duplicata ignorada: 9758171-45592964.txt
```

**O que você aprendeu:** a idempotência do lab mora no *nome do arquivo*, que
deriva do hash do payload. Por isso ela sobreviveu à troca de **linguagem**, e
não só a um restart de pod. Um `HashSet` em memória teria perdido tudo duas
vezes: no restart e na troca.

**Desfazer:** volte as duas linhas e rode `docker compose up -d --build consumer-rejeicao`.

**Ler mais:** [`docs/07-implementacoes-python-go-dotnet.md`](docs/07-implementacoes-python-go-dotnet.md) §4 e §5 — sobretudo a parte de por que `os.rename` é a tradução errada de `File.Move(..., overwrite: false)`.

---

## Etapa 16 — Seguir uma mensagem do início ao fim

Até aqui você leu logs soltos. Agora vai seguir **uma** mensagem atravessando
quatro processos e três linguagens.

**Fazer:**

```bash
./run.sh trace
```

**Esperar:** a lista dos traces recentes, com worker, linguagem e desfecho.

```
  TRACE                              WORKER     LING      EVENTO FINAL       TENTATIVAS
  396806d777684edbb42e48df284ce9d2   rejeicao   dotnet    mensagem.deletada  1
  4167114333ed4101aa59d90b472c2732   baixa      go        mensagem.deletada  1
```

**Fazer** — escolha um e abra a cadeia:

```bash
./run.sh trace 396806d777684edbb42e48df284ce9d2
```

**Esperar:**

```
  17:11:00.665  publish.iniciado      publisher/python
  17:11:00.672  publish.ok            publisher/python
  17:11:00.698  mensagem.recebida     rejeicao/dotnet
  17:11:00.716  comprovante.gravado   rejeicao/dotnet
  17:11:00.720  mensagem.deletada     rejeicao/dotnet
```

**O que você aprendeu:** o `trace_id` nasceu no publisher (Python), viajou como
*MessageAttribute* do SNS, e o consumer (.NET) o encontrou do outro lado. A
cadeia atravessou o broker e uma fronteira de linguagem sem se partir.

**Fazer** — feche o circuito no sentido inverso. O próprio `trace` imprime, no
fim, o comando para abrir o comprovante daquela cadeia:

```
  O comprovante gravado por esta cadeia:
      docker compose exec consumer-registro cat /data/comprovantes/rejeicao/…/….txt
```

Copie e rode. Ache o campo `Trace ID`.

**Esperar:** o mesmo id que você acabou de rastrear.

```
Message ID........: b3f2d569-147b-4f42-af18-2bc759147e12
Trace ID..........: 396806d777684edbb42e48df284ce9d2
Tentativa.........: 1
```

**O que você aprendeu:** de um arquivo no volume você volta para a história
inteira. Sem esse campo, o artefato seria um beco sem saída.

**Fazer agora o caso que mais ensina** — a mensagem que não dá para processar:

```bash
export FILA=http://localhost:4566/000000000000/cobranca-baixa
aws sqs set-queue-attributes --queue-url "$FILA" --attributes VisibilityTimeout=5
aws sqs send-message --queue-url "$FILA" --message-body 'isto-nao-e-json'
sleep 30
./run.sh trace --dlq
aws sqs set-queue-attributes --queue-url "$FILA" --attributes VisibilityTimeout=60
```

**Esperar:** **um** trace com **três** tentativas.

```
  4e3dc021f91d8fc495c6b5a233b82252   baixa   go   mensagem.falhou   3
```

**Como ler:** essa mensagem nunca passou pelo SNS, então não tinha atributo
`traceId`. O worker derivou o trace do `MessageId` — que é estável entre
reentregas. Se ele tivesse sorteado um id a cada `receive`, as três tentativas
virariam três cadeias desconexas e o caminho até a DLQ ficaria invisível,
justamente no caso em que rastrear vale mais.

**Fazer por último** — veja tudo no desenho:

```bash
./run.sh exportar-logs
open docs/assets/visualizador.html      # xdg-open no Linux
```

Vá na aba **"Logs reais"**, arraste o `logs-do-lab.ndjson`, escolha um trace e
clique em **Reproduzir o trace**.

**Esperar:** o mesmo diagrama da etapa 2, mas agora cada seta que acende
corresponde a um evento que aconteceu de verdade — com o tempo que levou.
Compare um trace verde (gravado) com um vermelho (falhou 3×).

**Ler mais:** [`docs/07-implementacoes-python-go-dotnet.md`](docs/07-implementacoes-python-go-dotnet.md) §7.

---

# Bloco 6 — Levar adiante

## Etapa 17 — Kubernetes de verdade

O Floci não reproduz o webhook do IRSA. Para ver os objetos do Kubernetes funcionando, use um cluster local.

**Fazer:**

```bash
kind create cluster --name cobranca-lab

kubectl apply -f infra/k8s/02-volume.yaml    # PVC ANTES
kubectl apply -f infra/k8s/01-workers-irsa.yaml

kubectl get pods -n cash
kubectl describe pod -n cash -l app=consumer-registro | tail -20
```

**Esperar:** os pods **não** vão subir — as imagens do ECR não existem e não há EFS. É o esperado, e o `describe` é o objetivo: veja o `ServiceAccount`, o `volumeMounts`, o `fsGroup`.

**Fazer** — confirme a ordem que importa, aplicando ao contrário num cluster limpo:

```bash
kubectl delete -f infra/k8s/02-volume.yaml
kubectl describe pod -n cash -l app=consumer-registro | grep -A3 Events
```

**Esperar:** `persistentvolumeclaim "comprovantes-cobranca" not found` e pods em `Pending`.

**O que você aprendeu:** PVC antes do Deployment que o consome. A mensagem de erro é pouco óbvia e vale reconhecer.

**Limpar:**

```bash
kind delete cluster --name cobranca-lab
```

---

## Etapa 18 — Trazer para o seu trabalho

Sem comando. É a etapa que consolida.

1. Peça para ver a **trust policy de uma role real** do seu time. Identifique qual ServiceAccount ela autoriza. É a mesma estrutura da etapa 12, com nomes diferentes.
2. Descubra se o cluster usa **IRSA ou EKS Pod Identity** — muda o formulário que você preenche ao pedir uma role nova. ([guia 01](docs/01-iam-explicado.md) §5)
3. Veja se as filas de vocês usam **SSE-KMS**. Se usarem, confira se a key policy permite `sns.amazonaws.com`. ([guia 05](docs/05-kms.md) §4)
4. Procure um `AWS_ACCESS_KEY_ID` em algum Deployment ou ConfigMap. Se achar, você achou um IRSA desligado sem ninguém saber.

---

## Encerrar

```bash
./run.sh reset      # derruba tudo e APAGA o volume
```

Sem o `-v` (`docker compose down`), os comprovantes ficam para a próxima sessão.

---

## Se algo der errado

| Sintoma | Onde olhar |
|---|---|
| Floci não responde | `./run.sh logs floci` |
| workers em loop de erro | rodou `./run.sh bootstrap` antes? |
| `verify` acusa `FilterPolicy` | `./run.sh reset && TOPIC_MODE=multi ./run.sh up` |
| nenhum comprovante | `./run.sh logs consumer-registro` — provavelmente volume não montado |
| `verify` acusa DLQ | esperado depois da etapa 9; `./run.sh reset` limpa |
| build do .NET falha | `./run.sh build` mostra o erro por projeto |
| `./run.sh comparar` acusa divergência | as regras do comprovante mudaram numa linguagem só — guia 07 §4 |
| `permission denied` em `/data` | uid da imagem diferente do dono do volume; é POSIX, não IAM — guia 07 §11 |
| `./run.sh trace` não acha nada | o lab está no ar? os logs só existem depois que algo é publicado |
| a cadeia pára no `publish.ok` | faltou `MessageAttributeNames` no `ReceiveMessage` — guia 07 §7 |

## Depois do roteiro

As apostilas em [`docs/`](docs/README.md) aprofundam cada assunto. A ordem sugerida está no [índice](docs/README.md), e a trilha rápida por problema ("quero debugar um `AccessDenied`" → tal capítulo) fica no fim dele.
