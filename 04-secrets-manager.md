# Secrets Manager na prática

Como a aplicação busca uma senha de banco, uma chave de API ou qualquer segredo — e por que quase toda primeira implementação erra no mesmo ponto.

Pré-requisito: [`03-credenciais-no-dotnet.md`](03-credenciais-no-dotnet.md), porque buscar um segredo é só mais uma chamada AWS autenticada pela role.

---

## 1. Secrets Manager ou Parameter Store?

Os dois guardam valor sensível. A escolha costuma ser decidida por dois critérios:

| | **Secrets Manager** | **SSM Parameter Store** (`SecureString`) |
|---|---|---|
| Custo | ~US$ 0,40 por segredo/mês + chamadas | tier padrão **gratuito**; advanced é pago |
| Rotação automática | **nativa**, via Lambda | não tem — você constrói |
| Replicação entre regiões | nativa | manual |
| Resource policy | sim (cross-account) | só no advanced |
| Tamanho | até 64 KB | 4 KB padrão / 8 KB advanced |
| Versionamento | estágios (`AWSCURRENT`, `AWSPENDING`, `AWSPREVIOUS`) | versões numeradas |

Regra prática: **credencial que precisa rodar automaticamente** (senha de banco, chave de API de parceiro) → Secrets Manager. **Configuração sensível que não rotaciona** (endpoint interno, flag, ID de conta) → Parameter Store, que é grátis.

Um erro comum de custo: colocar 300 parâmetros de configuração no Secrets Manager porque "é o serviço de segredo". São US$ 120/mês em algo que o Parameter Store faria de graça.

---

## 2. Buscando um segredo em .NET

```csharp
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

var client = new AmazonSecretsManagerClient();   // credencial vem da role, como sempre

var resposta = await client.GetSecretValueAsync(new GetSecretValueRequest
{
    SecretId = "asa/prd/cash-cobranca/postgres"
});

// SecretString normalmente é um JSON com vários campos
var segredo = JsonSerializer.Deserialize<CredenciaisBanco>(resposta.SecretString);
```

Simples assim — e é aqui que quase todo mundo erra o passo seguinte.

---

## 3. O erro que quase toda primeira implementação comete

> **Chamar `GetSecretValueAsync` toda vez que precisa do segredo.**

Por que dói:

- **É chamada de API paga.** Num worker que abre conexão por mensagem, isso vira milhares de chamadas/dia.
- **Tem limite de taxa.** Sob carga, você toma `ThrottlingException` — e o worker cai por não conseguir *ler uma senha*.
- **É latência inútil.** Uma ida à rede antes de cada operação.
- **Polui o CloudTrail.** Milhares de eventos `GetSecretValue` afogam a trilha justamente onde você iria procurar acesso indevido.

A solução é cache com TTL. Duas formas:

### Opção A — o pacote oficial

```
dotnet add package AWSSDK.SecretsManager.Caching
```

```csharp
var cache = new SecretsManagerCache(client);
var json = await cache.GetSecretString("asa/prd/cash-cobranca/postgres");
```

Faz cache, renova em background e é a resposta certa na maioria dos casos.

### Opção B — manual, quando você precisa de controle

Está em [`examples/secrets/SegredoProvider.cs`](../examples/secrets/SegredoProvider.cs), com dois cuidados que o exemplo oficial não cobre:

**TTL menor que o intervalo de rotação.** Se a senha rotaciona a cada 30 dias e seu cache tem TTL de 1 hora, a janela de credencial velha é de no máximo 1 hora. Um cache eterno (`static` populado uma vez na subida) significa que o worker quebra na rotação e **só volta com restart** — que é o modo de falha mais comum e mais irritante desse tema.

**Invalidar em caso de falha de autenticação.** Se o banco recusar a senha, invalide o cache e tente **uma vez** de novo antes de propagar o erro. Isso cobre a janela entre a rotação acontecer e o TTL expirar. Uma tentativa só — repetir indefinidamente com senha errada bloqueia a conta no banco.

---

## 4. Rotação: o que muda de verdade

Rotação é o motivo de o Secrets Manager existir, e é o que exige código diferente.

Durante a rotação existem **estágios** simultâneos:

| Estágio | Significa |
|---|---|
| `AWSCURRENT` | a versão válida agora — é o que você lê por padrão |
| `AWSPENDING` | a nova versão sendo criada e testada |
| `AWSPREVIOUS` | a anterior, mantida por um tempo |

A consequência para você: **a senha pode mudar enquanto o processo está no ar**. Uma aplicação que lê o segredo uma vez no `Startup` e guarda numa `static readonly string` funciona por semanas e quebra numa madrugada de rotação, com erro de autenticação que parece problema do banco.

A implicação prática: leia o segredo **através de um provider com TTL** — nunca uma constante capturada na subida.

Para pool de conexão (Npgsql no seu caso), lembre que conexões já abertas continuam válidas; o problema aparece só quando o pool tenta abrir uma nova. Isso torna o sintoma intermitente e ainda mais difícil de diagnosticar.

---

## 5. As permissões — e o KMS de novo

```json
{
  "Effect": "Allow",
  "Action": "secretsmanager:GetSecretValue",
  "Resource": "arn:aws:secretsmanager:us-east-1:111122223333:secret:asa/prd/cash-cobranca/postgres-??????"
}
```

**O ARN tem um sufixo aleatório de 6 caracteres** que a AWS acrescenta na criação. Você não sabe qual é ao escrever a policy no Terraform. Por isso o `-??????` no final (`?` casa um caractere) — ou `-*`, que é mais frouxo mas aceito.

Escrever o ARN sem sufixo simplesmente não casa com nada, e o erro não explica isso.

### E o KMS

Todo segredo é criptografado com uma chave KMS. Duas situações:

- **Chave gerenciada pela AWS (`aws/secretsmanager`)**: dentro da mesma conta funciona sem permissão extra.
- **Chave própria (CMK)**: a role precisa também de `kms:Decrypt`.

```json
{
  "Effect": "Allow",
  "Action": "kms:Decrypt",
  "Resource": "arn:aws:kms:us-east-1:111122223333:key/EXAMPLE-KEY-ID",
  "Condition": {
    "StringEquals": { "kms:ViaService": "secretsmanager.us-east-1.amazonaws.com" }
  }
}
```

E para **cross-account** existe uma regra dura: a chave gerenciada pela AWS **não pode ser compartilhada entre contas**. Segredo acessado de outra conta *exige* chave própria, com a key policy citando o principal externo. Detalhes em [`05-kms.md`](05-kms.md).

---

## 6. Ler no código ou injetar como variável de ambiente?

Há duas escolas, e vale conhecer as duas porque você vai encontrar as duas no trabalho.

### Injetar na subida

**ECS** faz isso nativamente na task definition — e quem busca o segredo é a **execution role**, não a task role:

```json
"secrets": [
  { "name": "PG_PASSWORD",
    "valueFrom": "arn:aws:secretsmanager:us-east-1:111122223333:secret:asa/prd/...-AbCdEf" }
]
```

**EKS** não tem equivalente nativo. Usa-se o *External Secrets Operator* (sincroniza para um `Secret` do Kubernetes) ou o *Secrets Store CSI Driver* (monta como arquivo).

**Vantagem:** a aplicação não conhece o Secrets Manager. Portável, testável, zero código.

**Desvantagem decisiva:** **a rotação não chega até o processo sem restart.** A variável de ambiente foi resolvida uma vez, na criação do container. Se o segredo rotaciona, você precisa de um redeploy — o que anula boa parte do motivo de usar Secrets Manager.

### Ler no código

**Vantagem:** rotação funciona de verdade, e você controla o TTL.

**Desvantagem:** a aplicação passa a depender da AWS, o que exige um `ISegredoProvider` com implementação alternativa para teste local.

### A recomendação honesta

**Segredo que rotaciona → leia no código, com cache e TTL.** **Configuração que não rotaciona → injete como variável de ambiente**, é mais simples e não há o que ganhar com a complexidade.

Note que o mesmo serviço acaba usando os dois modelos, e tudo bem.

---

## 7. Integrando com `IConfiguration`

O jeito idiomático em .NET é fazer o segredo aparecer como configuração normal, sem espalhar chamada do Secrets Manager pelo código:

```csharp
// Program.cs
builder.Services.AddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient());
builder.Services.AddSingleton<ISegredoProvider, SecretsManagerProvider>();

// e onde precisa da conexão:
public sealed class ConexaoFactory(ISegredoProvider segredos)
{
    public async Task<NpgsqlConnection> AbrirAsync(CancellationToken ct)
    {
        // Passa pelo cache. Só vai à AWS quando o TTL expira.
        var cred = await segredos.ObterAsync<CredenciaisBanco>("asa/prd/cash-cobranca/postgres", ct);
        var conexao = new NpgsqlConnection(cred.MontarConnectionString());
        await conexao.OpenAsync(ct);
        return conexao;
    }
}
```

O ganho da interface `ISegredoProvider`: em teste local você registra uma implementação que lê de `appsettings.Development.json` e o resto do código não muda. Mesma lógica de manter a resolução de credenciais fora do código de negócio.

---

## 8. Convenção de nomes

Mesma razão do capítulo de roles: com centenas de segredos, o nome é o que permite escrever policy por prefixo.

```
asa/prd/cash-cobranca/postgres
 │   │        │           │
 │   │        │           └── qual segredo
 │   │        └── domínio
 │   └── ambiente
 └── organização
```

Isso habilita policies por padrão:

```json
"Resource": "arn:aws:secretsmanager:us-east-1:111122223333:secret:asa/prd/cash-cobranca/*"
```

Uma role que enxerga os segredos do próprio domínio e de nenhum outro — sem precisar listar cada ARN, e sem precisar mexer na policy quando surge um segredo novo do mesmo domínio.

---

## 9. Testando localmente

<cite index="3-1">O Floci emula Secrets Manager, com versionamento, resource policies e tagging.</cite> Dá para criar e ler segredos exatamente como na AWS:

```bash
export AWS_ENDPOINT_URL=http://localhost:4566
export AWS_ACCESS_KEY_ID=test AWS_SECRET_ACCESS_KEY=test AWS_DEFAULT_REGION=us-east-1

aws secretsmanager create-secret \
  --name asa/dev/cash-cobranca/postgres \
  --secret-string '{"host":"localhost","port":5432,"username":"app","password":"trocar"}'

aws secretsmanager get-secret-value --secret-id asa/dev/cash-cobranca/postgres
```

Serve para exercitar o SDK, o cache e o parsing. Não serve para validar se a policy concede o acesso certo — o Floci não age como ponto de decisão de autorização.

---

## 10. Tabela de sintomas

| Sintoma | Causa |
|---|---|
| `AccessDeniedException` no `GetSecretValue` | ARN da policy sem o sufixo `-??????` |
| `AccessDeniedException` mencionando `kms:Decrypt` | chave própria sem `kms:Decrypt` na role, ou key policy sem o principal |
| funciona por semanas e quebra de madrugada | segredo lido uma vez na subida; rotação aconteceu |
| `ThrottlingException` sob carga | sem cache; `GetSecretValue` por operação |
| falha só ao abrir conexão nova, intermitente | rotação + pool de conexões; as antigas seguem válidas |
| cross-account não funciona nem com policy certa | chave gerenciada pela AWS não atravessa conta; precisa de CMK |
| CloudTrail cheio de `GetSecretValue` | mesma causa do throttling; cache resolve os dois |
