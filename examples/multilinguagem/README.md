# O mesmo worker em Python, Go e .NET

Código de apoio do [guia 07](../../docs/07-implementacoes-python-go-dotnet.md).
Não é um exemplo à parte: **é o que o `./run.sh up` sobe**.

| Pasta | Serviço no compose | SDK |
|---|---|---|
| [`python/`](python/) | `consumer-registro` e `publisher` | boto3 / botocore |
| [`go/`](go/) | `consumer-baixa` | `aws-sdk-go-v2` |
| [`dotnet/`](dotnet/) | `consumer-rejeicao` | `AWSSDK` 3.7 |

## Os cinco programas, nas três linguagens

| | Python | Go | .NET |
|---|---|---|---|
| publica no SNS | `publisher.py` | `cmd/publisher` | `Publisher.cs` |
| consome a fila | `consumer.py` | `cmd/consumer` | `Consumer.cs` |
| cadeia de credenciais (guia 03) | `diagnostico.py` | `cmd/diagnostico` | `Diagnostico.cs` |
| cache de segredo (guia 04) | `segredos.py` | `cmd/segredos` | `Segredos.cs` |
| comprovante determinístico | `amostra.py` | `cmd/amostra` | `Amostra.cs` |
| **as regras do comprovante** | **`comprovante.py`** | **`comprovante/`** | **`Comprovante.cs`** |
| **log estruturado + `trace_id`** | **`log.py`** | **`logx/`** | **`Log.cs`** |
| como se constrói um client | `aws.py` | `awsx/` | `Aws.cs` |

As duas linhas em negrito são as que precisam ficar sincronizadas entre as
três. O comprovante, porque o nome do arquivo é a chave de idempotência — se
as regras divergirem, trocar a linguagem de um worker faz ele reprocessar
tudo. E o log, porque os quatro processos escrevem no **mesmo** formato e o
`./run.sh trace` junta os arquivos com um `sort` de texto puro: basta uma
linguagem mudar a largura do campo `ts` para a ordenação da cadeia quebrar.

`python/comprovante.py` é a **implementação de referência** — as outras duas são
portes dela.

## Comandos

```bash
./run.sh comparar                  # prova que as 3 geram o MESMO comprovante
./run.sh diagnostico python|go|dotnet
./run.sh segredos    python|go|dotnet
./run.sh trace [id|--dlq]          # a cadeia de uma mensagem, pelo trace_id
./run.sh exportar-logs             # junta os NDJSON para o visualizador
./run.sh build                     # constrói as 3 imagens
```

Nenhum deles exige Python, Go ou dotnet na sua máquina: os `Dockerfile` são
multi-estágio e o toolchain fica só no estágio de build.

## Por onde começar a ler

1. `python/comprovante.py` — as regras do documento e a escrita atômica.
   O comentário de `gravar_atomico` explica por que `os.link` e não `os.rename`.
2. `python/consumer.py` — as cinco decisões do laço de consumo, no cabeçalho.
3. `go/comprovante/comprovante.go` — o mesmo, com as duas armadilhas de Go
   (`len()` em bytes, `os.Rename` sobrescrevendo).
4. `dotnet/Aws.cs` — por que `ServiceURL` sozinho não basta.
5. `python/log.py` — por que o `ts` vem primeiro e com 6 casas decimais, e
   por que o `traceId` de uma mensagem sem atributo é derivado do `MessageId`
   em vez de sorteado.
