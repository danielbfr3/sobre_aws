# O mesmo worker em Python, Go e .NET

Os capítulos anteriores explicaram os mecanismos com exemplos em .NET. Este capítulo faz a mesma coisa **três vezes** — e o objetivo não é ensinar três linguagens, é mostrar o que **não muda** quando você troca de linguagem, e as poucas coisas que mudam.

A tese em uma frase:

> **A resolução de credencial não é assunto da sua linguagem. Os três SDKs implementam a mesma cadeia, com nomes diferentes.**

Pré-requisito: [`01-iam-explicado.md`](01-iam-explicado.md) e [`03-credenciais-no-dotnet.md`](03-credenciais-no-dotnet.md). Este capítulo é a versão poliglota do 03.

---

## 1. O que foi feito com o lab

Antes, os três consumers rodavam a mesma imagem .NET. Agora rodam **três imagens, em três linguagens**:

| Serviço no compose | Linguagem | SDK | Fila |
|---|---|---|---|
| `consumer-registro` | Python 3.12 | boto3 / botocore | `cobranca-registro` |
| `consumer-baixa` | Go 1.25 | `aws-sdk-go-v2` | `cobranca-baixa` |
| `consumer-rejeicao` | .NET 10 | `AWSSDK` 3.7 | `cobranca-rejeicao` |
| `publisher` | Python 3.12 | boto3 | — publica no SNS |

O ponto é que **nada mais no lab precisou saber disso**. O `bootstrap.sh` é o mesmo, as filas são as mesmas, o volume é o mesmo, e o `./run.sh verify` roda as mesmas 23 checagens sem uma linha condicional:

```
--- 5. Comprovantes no volume (README secao 3)
  PASSOU  46 comprovante(s) .txt gravado(s) no volume
  PASSOU  roteamento OK: os 18 comprovantes de 'registro' sao todos de cobranca.registrada
  PASSOU  roteamento OK: os 20 comprovantes de 'baixa' sao todos de cobranca.baixada
  PASSOU  roteamento OK: os 8 comprovantes de 'rejeicao' sao todos de cobranca.rejeitada
  PASSOU  volume compartilhado: consumer-registro enxerga 3 pastas de workers
  PASSOU  idempotencia: nenhum comprovante duplicado
```

Repare na linha do volume compartilhado: o worker **Python** está lendo arquivos que o worker **Go** e o worker **.NET** escreveram. É o mesmo `/data`, o mesmo layout, o mesmo formato.

O código está em [`examples/multilinguagem/`](../examples/multilinguagem/):

```
examples/multilinguagem/
├── comparar.sh              ← prova que as 3 geram o MESMO comprovante
├── python/
│   ├── comprovante.py       ← IMPLEMENTAÇÃO DE REFERÊNCIA
│   ├── log.py               ← log estruturado + trace_id
│   ├── aws.py               ← como se constrói um client
│   ├── publisher.py  consumer.py
│   ├── diagnostico.py       ← capítulo 03, em Python
│   ├── segredos.py          ← capítulo 04, em Python
│   ├── amostra.py           ← comprovante determinístico
│   └── Dockerfile  requirements.txt
├── go/
│   ├── comprovante/comprovante.go
│   ├── logx/logx.go
│   ├── awsx/awsx.go
│   ├── cmd/{publisher,consumer,diagnostico,segredos,amostra}/main.go
│   ├── Dockerfile  go.mod
└── dotnet/
    ├── Comprovante.cs  Log.cs  Aws.cs
    ├── Publisher.cs  Consumer.cs  Diagnostico.cs  Segredos.cs  Amostra.cs
    ├── Program.cs           ← um binário, cinco subcomandos
    └── Dockerfile  Lab.csproj
```

Nenhum dos três precisa da linguagem instalada na sua máquina: os `Dockerfile` são multi-estágio e compilam dentro do build.

---

## 2. A cadeia de credenciais nos três SDKs

Esta é a tabela que vale a leitura do capítulo. A **ordem é a mesma nos três**, o nome de cada elo é que muda:

| Elo | Python (botocore) | Go (`aws-sdk-go-v2`) | .NET (`AWSSDK`) |
|---|---|---|---|
| ponto de entrada | `Session().get_credentials()` | `config.LoadDefaultConfig()` | `FallbackCredentialsFactory.GetCredentials()` |
| 1 — explícito no código | argumentos do `client()` | `config.WithCredentialsProvider` | construtor com `AWSCredentials` |
| 2 — **variáveis de ambiente** | `method="env"` | `Source="EnvConfigCredentials"` | `EnvironmentVariablesAWSCredentials` |
| 3 — perfil compartilhado | `shared-credentials-file` | `SharedConfigCredentials` | `SharedCredentialsFile` |
| 4 — **web identity (IRSA)** | `assume-role-with-web-identity` | `WebIdentityCredentials` | `AssumeRoleWithWebIdentityCredentials` |
| 5 — container (ECS / Pod Identity) | `container-role` | `EndpointCredentialsProvider` | `URIBasedRefreshingCredentialHelper` |
| 6 — IMDS (role do nó) | `iam-role` | `EC2RoleProvider` | `DefaultInstanceProfileAWSCredentials` |

Três consequências práticas, e as três valem para os três SDKs:

**Variável de ambiente vence IRSA.** É o bug do capítulo 03 §2 e ele não é específico do .NET. Um `AWS_ACCESS_KEY_ID` esquecido num ConfigMap cala o IRSA em Python, em Go e em .NET exatamente da mesma forma — a cadeia para no elo 2 e nunca chega no 4.

**A resolução é preguiçosa nos três.** Nenhum deles chama o STS na construção do client. O `AssumeRoleWithWebIdentity` sai na primeira chamada de verdade, e o arquivo do token é **relido a cada renovação** (o kubelet rotaciona aquele arquivo).

**Nenhum dos três recebe credencial no construtor** — em nenhum arquivo deste lab. É isso que faz o mesmo binário rodar na sua máquina, no EKS, no ECS e na Lambda sem um `if`.

### Vendo com os próprios olhos

```bash
./run.sh diagnostico python
./run.sh diagnostico go
./run.sh diagnostico dotnet
```

Cada um imprime o elo vencedor no vocabulário do seu SDK. Rodando no compose, os três dizem a mesma coisa com palavras diferentes:

```
--- 2. Qual provedor venceu                  (Python)
  method .......: env
  significa ....: variaveis de ambiente (AWS_ACCESS_KEY_ID...)

--- 2. Qual provedor venceu                  (Go)
  Source .......: EnvConfigCredentials
  significa ....: variaveis de ambiente (AWS_ACCESS_KEY_ID...)

--- 2. Qual provedor venceu                  (.NET)
  tipo .........: EnvironmentVariablesAWSCredentials
  significa ....: variaveis de ambiente (AWS_ACCESS_KEY_ID...)
```

E o experimento que importa — provocar o bug do IRSA silenciado, em qualquer uma das três:

```bash
docker compose run --rm \
  -e AWS_ROLE_ARN=arn:aws:iam::111122223333:role/qualquer \
  -e AWS_WEB_IDENTITY_TOKEN_FILE=/tmp/token-falso \
  consumer-baixa /app/diagnostico
```

```
  (!) AWS_ACCESS_KEY_ID e AWS_ROLE_ARN presentes ao mesmo tempo.
      Variavel de ambiente vence IRSA na cadeia. O IRSA esta sendo IGNORADO.
```

O alerta é idêntico nos três porque **o problema é da cadeia, não da linguagem**.

---

## 3. Reuso de client: a mesma regra, três formas de errar

> **Reutilize o client. Não crie um por mensagem, por request ou por método.**

O cache de credenciais mora dentro do objeto de client (ou da config). Criar um novo joga o cache fora e força um `AssumeRoleWithWebIdentity` novo: latência extra em toda operação, throttling do STS e um CloudTrail com milhares de eventos por hora.

Onde cada linguagem esconde a armadilha:

| | Onde mora o cache | O erro típico | O jeito certo |
|---|---|---|---|
| **Python** | dentro do objeto de credenciais da `Session` | `boto3.client("sqs")` dentro do laço — cada chamada monta uma `Session` nova | um client no escopo do processo (é o que `aws.py` faz) |
| **Go** | no `CredentialsCache` dentro do `aws.Config` | `config.LoadDefaultConfig` por requisição | carregar a config **uma vez** no `main` e passar adiante |
| **.NET** | dentro do `AWSCredentials`, dentro do client | `using var sqs = new AmazonSQSClient()` num método chamado por mensagem | singleton no DI |

Um detalhe que só aparece em Python: **os clients boto3 são thread-safe, as `Session` não são.** Compartilhar um client entre threads é seguro; compartilhar uma `Session` para criar clients em paralelo não é. Em Go e .NET a resposta é mais simples — client e config são seguros para uso concorrente.

---

## 4. O comprovante: uma regra, três implementações

O formato do comprovante (capítulo `README` §3) é a parte que **precisa** ser idêntica, porque o nome do arquivo é a chave de idempotência. Se as três linguagens divergissem no hash ou no nome, trocar a linguagem de um worker faria ele reprocessar tudo que a versão anterior já tinha gravado.

Isso é afirmação verificável, e o lab verifica:

```bash
./run.sh comparar
```

```
  payload : {"nossoNumero":"4827193","tipoEvento":"cobranca.baixada",
             "valorCentavos":123456,"ocorridoEm":"2026-08-08T17:42:03Z"}
  hash    : 299E3E228C2D1224528490FAAE4B8C27
  caminho : /data/comprovantes/baixa/2026-08-08/4827193-299E3E22.txt
  ---
  ================================================================
    COMPROVANTE DE BAIXA DE COBRANÇA
  ================================================================
  Nosso número......: 4827193
  Valor.............: R$ 1.234,56
  ...

  IGUAL   python == go   (1086 bytes)
  IGUAL   python == dotnet   (1086 bytes)
```

Ele roda o subcomando `amostra` nas três linguagens — payload, host, message id e horários fixos — e faz `diff`. É offline, não publica nada e não suja o `verify`.

### As quatro regras que tiveram que ser escritas três vezes

**A serialização é compacta e com as chaves em ordem fixa.** Não é estilo: o hash sai desses bytes. Um espaço a mais e a mesma mensagem republicada deixa de ser duplicata. Python precisa de `separators=(",",":")` explícito (o padrão do `json.dumps` põe espaços); Go e .NET já emitem compacto e na ordem de declaração da struct.

**MD5, hexadecimal maiúsculo, dos bytes brutos do `Body`.** A escolha do MD5 é deliberada e vale explicar: isto **não é segurança, é deduplicação** — o hash só responde "já vi exatamente estes bytes?". Ele cabe nos 32 caracteres que o comprovante mostra, e ninguém está tentando forjar colisão com um boleto de laboratório. Se o seu caso for antifraude, use SHA-256 — e troque nas três ao mesmo tempo.

**O alinhamento conta caracteres, não bytes.** O rótulo `Nosso número` tem acento: 12 caracteres, 13 bytes em UTF-8. Em Python (`str.ljust`) e em .NET (`PadRight`) isso sai de graça. **Em Go, `len()` é bytes** — o código usa `utf8.RuneCountInString`. Usar `len()` desalinha essa linha, e só ela, o que é justamente o tipo de bug que passa despercebido em revisão.

**Dinheiro é formatado na mão, sem `locale`.** As imagens `slim` não trazem dados de cultura, e o .NET roda em globalization-invariant mode. `CultureInfo("pt-BR")` estoura ou devolve formato errado *dentro do container* — funciona na sua máquina e quebra no cluster. As três implementações montam `R$ 12.345,67` a partir do inteiro.

### A partição por data vem do evento, não do relógio

```
/data/comprovantes/<worker>/<AAAA-MM-DD>/<nossoNumero>-<hash8>.txt
                            └── ocorridoEm do evento
```

Se a data viesse de `now()`, uma mensagem reentregue depois da meia-noite cairia noutro diretório, o teste de existência não acharia o arquivo anterior e a idempotência falharia **uma vez por dia**. Idempotência não pode depender de "que horas são agora".

---

## 5. A escrita atômica: onde a tradução ingênua quebra

Esta é a diferença mais importante entre as três implementações, e ela não aparece em nenhum tutorial de SDK.

O requisito: gravar o comprovante de forma que (a) um processo morto no meio não deixe um `.txt` truncado, e (b) duas réplicas correndo pelo mesmo nome não sobrescrevam uma à outra.

O padrão é sempre o mesmo — escreve num temporário, depois publica o nome definitivo. **O que muda é a chamada que publica o nome:**

| | Chamada | Se o destino já existe |
|---|---|---|
| **.NET** | `File.Move(tmp, dst, overwrite: false)` | lança `IOException` ✅ |
| **Go** | `os.Link(tmp, dst)` | erro `fs.ErrExist` ✅ |
| **Go** | ~~`os.Rename(tmp, dst)`~~ | **sobrescreve em silêncio** ❌ |
| **Python** | `os.link(tmp, dst)` | `FileExistsError` ✅ |
| **Python** | ~~`os.rename(tmp, dst)`~~ | **sobrescreve em silêncio** ❌ |

`os.rename` é a tradução óbvia de `File.Move` — e é a errada. Em POSIX, `rename(2)` substitui o destino sem avisar. Com ele, as duas réplicas "ganham" a corrida, a última sobrescreve a primeira, e o mecanismo de idempotência vira decoração: ele nunca detecta duplicata, porque nunca falha.

`link(2)` é o oposto: falha se o destino existe. É exatamente a semântica que o `overwrite: false` do .NET oferece. Quem perde a corrida trata como duplicata e segue.

```python
tmp.write_text(conteudo, encoding="utf-8")
try:
    os.link(tmp, destino)      # link(2) FALHA se o destino existir - rename NÃO
    return True
except FileExistsError:
    return False               # outra réplica chegou antes; o comprovante existe
finally:
    tmp.unlink(missing_ok=True)
```

> **Restrição de `link(2)`:** o temporário precisa estar no **mesmo filesystem** do destino. Por isso os três geram o `.tmp` no próprio diretório do comprovante, e não em `/tmp`.

---

## 6. As diferenças que sobraram

Fora a escrita atômica, o que realmente difere entre as três implementações é pouco — e é bom que seja pouco.

**Como pedir o `ApproximateReceiveCount`.** O campo `Tentativa` do comprovante vem daí, e ele **só chega se você pedir** no `ReceiveMessage`. Os três SDKs têm dois parâmetros para isso: o legado (`AttributeNames`, que gera `AttributeName.N` no protocolo) e o novo (`MessageSystemAttributeNames`). O lab usa o **legado de propósito**: é o que todo emulador entende. Contra a AWS de verdade, prefira o novo — no .NET o legado já emite `CS0618`.

**Erro estruturado.** Python usa `ClientError` com `e.response["Error"]["Code"]`; Go usa `errors.As` com tipos como `*types.ResourceExistsException`; .NET usa `catch (ResourceExistsException)`. Mesma informação, três ergonomias.

**Campo ausente vs. valor zero.** Em Python o `dict` diz se a chave existe. Em Go e .NET o zero-value não distingue "veio 0" de "não veio" — as duas implementações usam ponteiro / nullable no tipo de desserialização só para poder recusar um payload incompleto. É o que faz `isto-nao-e-json` ir parar na DLQ em vez de virar um comprovante de R$ 0,00.

**Sinais.** O `Program.cs` do .NET registra `PosixSignalRegistration` para SIGTERM em vez de `AppDomain.ProcessExit` — o `ProcessExit` dispara **depois** que os `using` do escopo já rodaram, então cancelar um `CancellationTokenSource` ali estoura `ObjectDisposedException` durante o encerramento e **trava o processo em vez de derrubá-lo**. Vale saber: num pod, um worker que não morre no SIGTERM é morto no SIGKILL depois do `terminationGracePeriodSeconds`, e a mensagem que ele estava processando volta para a fila.

---

## 7. Log estruturado e `trace_id`: seguir uma mensagem do início ao fim

Até aqui, cada um dos quatro processos escrevia no seu próprio log. Isso funciona para ver *se* algo aconteceu, e é inútil para responder a pergunta que se faz num incidente: **o que aconteceu com aquela mensagem?**

O lab resolve isso com duas peças pequenas: um evento estruturado e um identificador que atravessa o pipeline.

### O evento

Cada linha de log é um JSON com nome de evento — não só texto:

```json
{"ts":"2026-08-09T17:11:00.672431Z","nivel":"info","servico":"publisher",
 "worker":"publisher","linguagem":"python","host":"122326fde94d",
 "traceId":"396806d777684edbb42e48df284ce9d2","evento":"publish.ok",
 "msg":"#4 cobranca.rejeitada nossoNumero=2001661 messageId=603595e8-…",
 "tipoEvento":"cobranca.rejeitada","nossoNumero":"2001661","duracaoMs":6.4}
```

O `msg` serve para humano; o **`evento`** serve para máquina. É ele que permite ao `./run.sh trace` e ao visualizador remontarem a cadeia sem interpretar texto livre. Os nomes usados: `publish.iniciado`, `publish.ok`, `mensagem.recebida`, `comprovante.gravado`, `comprovante.duplicado`, `mensagem.falhou`, `mensagem.deletada`.

Duas decisões de formato que parecem detalhe e economizam código depois:

**`ts` é a primeira chave, com largura fixa e 6 casas decimais.** Isso faz a ordenação *de texto* coincidir com a cronológica — e é por isso que o `trace.sh` junta os quatro arquivos com um `sort` comum, sem `jq`, sem parser. Com 3 casas decimais, dois eventos do mesmo processo caem no mesmo instante com frequência e o `sort` desempata pelo resto da linha, ou seja, em ordem alfabética: a cadeia aparece mostrando `mensagem.falhou` **antes** de `mensagem.recebida`. Microssegundos resolvem.

**Um arquivo por escritor:** `/data/logs/<servico>-<host>.ndjson`. É o mesmo raciocínio da §5 — em vez de quatro processos disputarem append no mesmo arquivo (que sobre NFS tem semântica frouxa), cada um escreve no seu e a junção acontece na leitura.

> **Ressalva honesta:** em produção o worker escreve **só no stdout**, e um coletor (fluent-bit, CloudWatch agent, Datadog) leva dali para onde precisa. O arquivo no volume é o substituto de laboratório para esse coletor — é o que permite juntar, num lugar só, o que quatro processos em três linguagens escreveram, sem subir um Elasticsearch para aprender sobre IAM.

### O `trace_id`, e por que ele não vai no corpo

O trace nasce no publisher e viaja como **MessageAttribute** do SNS:

```python
MessageAttributes={
    "eventType": {"DataType": "String", "StringValue": evento.tipo_evento},
    "traceId":   {"DataType": "String", "StringValue": trace},
}
```

> **Nunca dentro do corpo.** O nome do comprovante deriva do hash do **corpo** (§4). Se o `traceId` entrasse ali, cada republicação do "mesmo" evento geraria um hash diferente, um nome de arquivo diferente, e a idempotência deixaria de funcionar — silenciosamente, que é o pior modo. Atributo é metadado de transporte; corpo é o fato de negócio. O hash só pode cobrir o segundo.

Do outro lado, o consumer precisa **pedir** os atributos. Nenhum dos três SDKs os traz por padrão:

```python
sqs.receive_message(..., MessageAttributeNames=["All"])
```
```go
MessageAttributeNames: []string{"All"},
```
```csharp
MessageAttributeNames = ["All"],
```

Esquecer essa linha não dá erro. Só parte a cadeia no meio — exatamente no ponto mais importante dela.

O formato é o do W3C Trace Context: 32 caracteres hexadecimais minúsculos. Em produção isso viria do OpenTelemetry, pelo cabeçalho `traceparent`; aqui está na mão para deixar o mecanismo à vista.

### Quando não veio trace nenhum

Se a mensagem foi publicada direto na fila — `aws sqs send-message`, que é o que a etapa 9 do ROTEIRO faz para exercitar a DLQ — não há atributo. O trace então é **derivado do `MessageId`**, nunca sorteado:

```python
def trace_do_message_id(message_id): return md5(message_id).hexdigest()
```

A diferença importa e é o detalhe mais fácil de errar no tema. O `MessageId` é estável entre reentregas; um id sorteado a cada `receive` não é. Com o id sorteado, as três tentativas da mesma mensagem viram três "cadeias" desconexas e o caminho até a DLQ — o único lugar onde rastrear paga o investimento — fica invisível. Com o derivado:

```
$ ./run.sh trace --dlq
  TRACE                              WORKER   LING   EVENTO FINAL      TENTATIVAS
  4e3dc021f91d8fc495c6b5a233b82252   baixa    go     mensagem.falhou   3

$ ./run.sh trace 4e3dc021f91d8fc495c6b5a233b82252
  17:12:33.605  mensagem.recebida   baixa/go    mensagem recebida (tentativa 1)
  17:12:33.605  mensagem.falhou     baixa/go    corpo nao e JSON valido: invalid character 'i'…
  17:12:38.975  mensagem.recebida   baixa/go    mensagem recebida (tentativa 2)
  17:12:38.975  mensagem.falhou     baixa/go    corpo nao e JSON valido: …
  17:12:43.988  mensagem.recebida   baixa/go    mensagem recebida (tentativa 3)
  17:12:43.989  mensagem.falhou     baixa/go    corpo nao e JSON valido: …

  3 tentativas falharam. Com maxReceiveCount=3 na RedrivePolicy,
  esta mensagem ja foi para a DLQ.
```

### A cadeia inteira, atravessando linguagens

```
$ ./run.sh trace 396806d777684edbb42e48df284ce9d2

  17:11:00.665  publish.iniciado      publisher/python
  17:11:00.672  publish.ok            publisher/python
                  #4 cobranca.rejeitada nossoNumero=2001661 messageId=603595e8-…
  17:11:00.698  mensagem.recebida     rejeicao/dotnet
  17:11:00.716  comprovante.gravado   rejeicao/dotnet
                  comprovante gravado: /data/comprovantes/rejeicao/2026-08-09/2001661-8D943550.txt
  17:11:00.720  mensagem.deletada     rejeicao/dotnet
```

Publisher em Python, consumer em .NET, um `trace_id` só. É a §2 deste capítulo mostrada em vez de afirmada: a cadeia atravessa o SNS, a SQS e uma fronteira de linguagem sem se partir.

### O circuito fechado: do artefato de volta para o log

O comprovante gravado no volume carrega o `Trace ID`:

```
Message ID........: b3f2d569-147b-4f42-af18-2bc759147e12
Trace ID..........: 396806d777684edbb42e48df284ce9d2
Tentativa.........: 1
```

Isso é o que transforma um arquivo num ponto de partida. Você acha um comprovante estranho no volume, lê o Trace ID, roda `./run.sh trace <id>` e tem a história inteira — quem publicou, quando, em que tentativa, quanto demorou cada salto. Sem esse campo, o artefato é um beco sem saída.

**Uma consequência que parece bug e não é:** republicar o mesmo payload gera um trace **novo**, mas o comprovante **não é reescrito** (é o que a idempotência garante). Então o arquivo guarda para sempre a cadeia da *primeira* gravação, e o segundo trace termina em `comprovante.duplicado` apontando para um arquivo cujo Trace ID é outro. Está correto: o documento registra quem o criou, não quem tentou recriá-lo.

### Visualizar

O `visualizador.html` ganhou uma aba **"Logs reais"**. Ele lê o NDJSON exportado e desenha os traces sobre o mesmo diagrama da simulação — só que cada seta acesa corresponde a um evento que aconteceu de verdade, com o tempo que ele levou.

```bash
./run.sh exportar-logs          # junta os 4 NDJSON em logs-do-lab.ndjson
open docs/assets/visualizador.html
# aba "Logs reais" → arraste o arquivo → escolha um trace → "Reproduzir o trace"
```

A lista mostra worker, linguagem, duração e desfecho (gravado / duplicata / falhou), com filtro por tipo. Clicar num trace abre a cadeia com o delta de tempo de cada evento. A leitura é toda no navegador — nada sai da sua máquina, e a página continua sendo um arquivo só, sem servidor.

---

## 8. O endpoint local: onde os SDKs ainda não concordam

Para falar com o Floci em vez da AWS, o cliente precisa apontar para `http://floci:4566`. Os três SDKs suportam a variável `AWS_ENDPOINT_URL`, mas **passaram a suportar em versões diferentes** — e o .NET foi o último.

Por isso as três implementações leem a variável na mão e passam explicitamente:

```python
boto3.client(servico, endpoint_url=os.environ.get("AWS_ENDPOINT_URL") or None)
```
```go
sqs.NewFromConfig(cfg, func(o *sqs.Options) { o.BaseEndpoint = endpointOuNil() })
```
```csharp
config.ServiceURL = url;
config.AuthenticationRegion = regiao;   // sem regiao não há assinatura SigV4
```

Em produção a variável não existe e as três resolvem o endpoint real da região. **Note o que continua fora do código nos três casos: a credencial.** Endpoint é configuração de destino; credencial é identidade. Só o primeiro aparece no código.

Repare também na linha do .NET: `ServiceURL` sozinho não basta, é preciso `AuthenticationRegion`. É o capítulo 03 §5 aparecendo de novo — **região e credencial são duas cadeias independentes**, e faltar região produz `No RegionEndpoint or ServiceURL configured`, que parece erro de IAM e não é.

---

## 9. Qual linguagem escolher

Sem torcida. Para um consumidor de SQS, os três funcionam e a escolha real quase nunca é técnica:

| | A favor | Contra |
|---|---|---|
| **Python** | menos código para o mesmo resultado; boto3 é o SDK mais completo e o primeiro a receber serviços novos; ótimo para ferramenta e script de operação | mais lento por mensagem; imagem maior que a de Go; sem tipo em tempo de compilação, o payload malformado só aparece em runtime |
| **Go** | binário estático, imagem mínima, subida instantânea (importa em escala-a-zero e em cold start); concorrência barata | mais verboso; o SDK v2 exige montar as coisas na mão; `len()` em bytes é uma pegadinha real com texto acentuado |
| **.NET** | ecossistema forte para aplicação de negócio; DI, configuração e testabilidade prontos; é o que a maior parte do código bancário já é | imagem e consumo de memória maiores que os de Go; runtime a manter |

O critério honesto é: **a linguagem que o seu time mantém**. Um worker de SQS é ~200 linhas; o custo de vida dele não está em escrevê-lo, está em quem vai ser acordado às 3h para depurá-lo.

O que **não** varia com a escolha — e é o motivo deste capítulo — é tudo que os capítulos 01 a 06 ensinam. A role, a trust policy, o `sub` do ServiceAccount, a resource policy da fila, a key policy do KMS, as duas roles do ECS: nada disso muda de linguagem.

---

## 10. Experimentos

| Experimento | Comando | O que observar |
|---|---|---|
| **As três são a mesma** | `./run.sh comparar` | mesmo hash, mesmo nome de arquivo, mesmos 1086 bytes |
| **Cadeia de credenciais** | `./run.sh diagnostico {python,go,dotnet}` | três vocabulários, uma cadeia |
| **IRSA silenciado** | o `docker compose run` da seção 2 | o alerta é idêntico nos três |
| **Cache de segredo** | `./run.sh segredos {python,go,dotnet}` | 5 leituras, 1 ida à AWS, nos três |
| **DLQ** | mande `isto-nao-e-json` para as três filas | as três recusam o payload e a mensagem vai para a DLQ após 3 tentativas |
| **Volume compartilhado** | `docker compose exec consumer-registro ls /data/comprovantes/` | o worker Python enxerga o que Go e .NET escreveram |
| **Seguir uma mensagem** | `./run.sh trace` e depois `./run.sh trace <id>` | a cadeia atravessa Python → .NET sem se partir |
| **A cadeia até a DLQ** | `./run.sh trace --dlq` | 3 tentativas, **um** trace — porque ele é derivado do MessageId |
| **Do artefato ao log** | leia o `Trace ID` de um comprovante e passe para `./run.sh trace` | o circuito fecha |
| **Ver no desenho** | `./run.sh exportar-logs` → aba "Logs reais" do visualizador | os mesmos caminhos, com tempo real |
| **Trocar de linguagem** | veja a seção 10 | o `verify` continua passando |

### Provar que a linguagem é intercambiável

Este é o experimento que amarra o capítulo. Troque o consumer de rejeição de .NET para Go, sem apagar o volume:

```bash
# no docker-compose.yml, em consumer-rejeicao:
#   context: ./examples/multilinguagem/dotnet   →   ./examples/multilinguagem/go
#   command: ["dotnet","Lab.dll","consumer"]    →   ["/app/consumer"]

docker compose up -d --build consumer-rejeicao
./run.sh verify
```

**Esperar:** todas as checagens continuam passando, inclusive a de idempotência. O worker Go reconhece como duplicata os comprovantes que o worker .NET gravou — porque o nome do arquivo deriva do hash do payload, e o hash é o mesmo. A idempotência sobreviveu à troca de **linguagem**, não só a um restart.

---

## 11. Tabela de sintomas

| Sintoma | Linguagem | Causa |
|---|---|---|
| comprovante duplicado com o mesmo nome | Python / Go | `os.rename` / `os.Rename` no lugar de `link` — sobrescreve em silêncio |
| `.tmp` acumulando no volume | qualquer | falta o `finally` que apaga o temporário |
| `link(2)` falhando com `EXDEV` | qualquer | o temporário está em outro filesystem (`/tmp`); gere-o no diretório do destino |
| linha `Nosso número` desalinhada | Go | `len()` conta bytes; use `utf8.RuneCountInString` |
| `R$ 1234.56` em vez de `R$ 1.234,56` | qualquer | dependeu de `locale` que a imagem `slim` não tem |
| `Tentativa` sempre 1 | qualquer | não pediu `ApproximateReceiveCount` no `ReceiveMessage` |
| comprovante com BOM, `diff` acusando 3 bytes | .NET | `File.WriteAllText` sem `new UTF8Encoding(false)` |
| processo não morre no `docker stop` | .NET | `ProcessExit` cancelando um `CancellationTokenSource` já descartado |
| `permission denied` em `/data` | qualquer | uid da imagem diferente do dono do volume — é POSIX, não IAM (guia 06) |
| cadeia do trace pára no `publish.ok` | qualquer | faltou `MessageAttributeNames` no `ReceiveMessage` |
| cada tentativa da mesma mensagem tem trace diferente | qualquer | trace sorteado no receive em vez de derivado do `MessageId` |
| eventos fora de ordem na cadeia | qualquer | `ts` com 3 casas decimais; o `sort` desempata em ordem alfabética |
| idempotência parou de funcionar ao adicionar trace | qualquer | o `traceId` foi para o corpo da mensagem, e o hash é do corpo |
| `/data/logs` vazio | qualquer | o serviço não monta o volume (o publisher precisa montar) |
| `No RegionEndpoint or ServiceURL configured` | .NET | `ServiceURL` sem `AuthenticationRegion` — é região, não IAM |
| `ObjectDisposedException` no encerramento | .NET | mesmo caso do `ProcessExit` acima |

---

## Referências

- Cadeia de credenciais do botocore — https://boto3.amazonaws.com/v1/documentation/api/latest/guide/credentials.html
- `config.LoadDefaultConfig` (Go v2) — https://aws.github.io/aws-sdk-go-v2/docs/configuring-sdk/
- Cadeia de credenciais do .NET — https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/creds-assign.html
- `rename(2)` — substitui o destino: https://man7.org/linux/man-pages/man2/rename.2.html
- `link(2)` — falha com `EEXIST`: https://man7.org/linux/man-pages/man2/link.2.html
- SQS `ReceiveMessage` — https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
- Atributos de mensagem no SNS — https://docs.aws.amazon.com/sns/latest/dg/sns-message-attributes.html
- W3C Trace Context (formato do `trace-id`) — https://www.w3.org/TR/trace-context/
- OpenTelemetry para AWS SDK — https://opentelemetry.io/docs/languages/
