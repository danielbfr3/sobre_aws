# Guias

Nove capítulos, na ordem em que fazem sentido ler. Cada um pressupõe o anterior, mas todos funcionam sozinhos se você já conhece o tema.

| # | Guia | Responde |
|---|---|---|
| 00 | [Diagramas](00-diagramas.md) | O mapa visual de tudo. Tem um **[visualizador interativo](assets/visualizador.html)**. |
| 01 | [IAM explicado do zero](01-iam-explicado.md) | Como a AWS decide se permite uma chamada. O que é uma role, de verdade. |
| 02 | [Assume role e cross-account](02-assume-role-cross-account.md) | O que o STS faz. Como acessar recursos de outra conta AWS. |
| 03 | [Credenciais no .NET](03-credenciais-no-dotnet.md) | Como `new AmazonSQSClient()` descobre a role sozinho. |
| 04 | [Secrets Manager](04-secrets-manager.md) | Como buscar senhas e chaves sem quebrar na rotação. |
| 05 | [KMS](05-kms.md) | O que muda em **todos** os capítulos acima quando há criptografia. |
| 06 | [EKS vs ECS vs Lambda](06-eks-ecs-lambda.md) | As mesmas ideias nas outras plataformas de compute. |
| 07 | [Python, Go e .NET](07-implementacoes-python-go-dotnet.md) | O mesmo worker em três linguagens. O que **não** muda. Log estruturado e `trace_id`. |
| 08 | [FSx e SMB](08-fsx-smb.md) | Quando o diretório de rede não é NFS. Active Directory no lugar do IAM. |

## Por que essa ordem

**00 é o mapa.** Cinco diagramas e um visualizador interativo onde você quebra permissões e vê onde o fluxo para. Vale abrir antes e voltar depois de cada capítulo.

**01 → 02** vai do conceito ao mecanismo: primeiro *o que é* uma role, depois *como* alguém a assume e o que muda quando há uma fronteira de conta no caminho.

**03 → 04** desce para o código. O 03 mostra o que acontece dentro do processo .NET entre você instanciar um client e a chamada sair autenticada. O 04 aplica isso ao caso mais comum de uso — buscar um segredo — que é só mais uma chamada autenticada pela role.

**05 é o capítulo transversal.** Ele não introduz assunto novo: volta em cada um dos anteriores e mostra onde a criptografia muda a resposta. Fica no fim de propósito, porque só faz sentido depois que você tem os outros na cabeça.

**06 é opcional** e existe para quando a pergunta for "e se isso rodasse em outro lugar".

**07 é o teste da tese.** Ele reimplementa o worker do lab em Python, Go e .NET e roda os três
lado a lado, contra as mesmas filas e o mesmo volume. Serve para duas coisas: mostrar que
nada dos capítulos 01 a 06 depende da linguagem, e dar o vocabulário de cada SDK a quem
precisa ler código de outro time. Leia depois do 03 — é a versão poliglota dele.

**08 é o capítulo da vida real corporativa.** Todos os anteriores assumem que o volume é EFS,
ou seja, NFS e POSIX. Em muita empresa o diretório de rede é **SMB**, e aí a autenticação sai
do IAM e vai para o Active Directory. Leia se o seu worker precisa de FSx — e leia antes de
abrir o chamado para a infra, porque metade do capítulo é a lista de perguntas a fazer.
Diferente dos outros, ele **não é exercitado pelo lab**; ele mesmo diz o que é fato e o que
é hipótese a testar.

## Trilha rápida por problema

| Você quer | Leia |
|---|---|
| Ver o desenho inteiro antes de ler | 00 |
| Entender o que é "uma role" | 01, seções 4 e 5 |
| Debugar um `AccessDenied` | 01 §10, e 05 §6 se a mensagem citar `kms:` |
| Descobrir por que o IRSA não pegou | 03 §2 e §6 |
| Acessar um bucket de outra conta | 02, seção 2 |
| Buscar uma senha de banco | 04 |
| Entender por que a mensagem some entre SNS e SQS | 05 §4 |
| Propor uma role nova para a plataforma | 01 §12 |
| Comparar com ECS ou Lambda | 06 |
| Traduzir o worker para Python ou Go | 07 |
| Achar o equivalente de `FallbackCredentialsFactory` no seu SDK | 07 §2 |
| Entender por que `os.rename` quebra a idempotência | 07 §5 |
| Seguir uma mensagem do publish ao comprovante | 07 §7 |
| Descobrir por que a cadeia de trace se parte no meio | 07 §7 |
| Montar um diretório de rede SMB (FSx) num pod | 08 §4 |
| Saber o que perguntar para a infra antes de pedir FSx | 08 §6 |
| Entender por que o `fsGroup` não resolve permissão no SMB | 08 §4.4 |
| Descobrir por que o mount do SMB nem chega a tentar | 08 §3.2 (quase sempre é DNS) |

## Código de apoio

Cada guia aponta para os artefatos que usa, em [`../examples/`](../examples/):

| Pasta | Guia | Status |
|---|---|---|
| `dotnet-credenciais/` | [03](03-credenciais-no-dotnet.md) | **executável**: `./run.sh diagnostico` |
| `secrets/` | [04](04-secrets-manager.md) | **executável** contra o Floci: `./run.sh segredos` |
| `lambda/` | [06](06-eks-ecs-lambda.md) | compila; publicar exige conta AWS |
| `ecs/` | [06](06-eks-ecs-lambda.md) | JSON de configuração, com valores de exemplo |
| `multilinguagem/python/` | [07](07-implementacoes-python-go-dotnet.md) | **roda no lab** — é o `consumer-registro` |
| `multilinguagem/go/` | [07](07-implementacoes-python-go-dotnet.md) | **roda no lab** — é o `consumer-baixa` |
| `multilinguagem/dotnet/` | [07](07-implementacoes-python-go-dotnet.md) | **roda no lab** — é o `consumer-rejeicao` |

Os `.cs` são projetos de verdade, com `.csproj` próprio — `./run.sh build` compila todos.
As três pastas de `multilinguagem/` compilam **dentro do Docker** (build multi-estágio):
não é preciso ter Python, Go nem dotnet instalados para rodar o lab.
Os `.json` de policy têm `111122223333` e `EXAMPLED539...` no lugar da sua conta e do seu
OIDC; não existe "rodar" uma trust policy, ela é substituída e aplicada.

O lab que roda de verdade está na raiz: o [README](../README.md) descreve como ele é montado, e o **[ROTEIRO](../ROTEIRO.md)** dá o passo a passo de execução, etapa por etapa.
