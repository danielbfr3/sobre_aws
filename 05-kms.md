# KMS: o fio que atravessa todos os capítulos

Este guia é diferente dos outros. Em vez de apresentar um assunto novo, ele **volta em cada capítulo anterior** e mostra onde o KMS aparece — porque em ambiente bancário quase tudo é criptografado, e o KMS é a causa de uma classe inteira de erros que *parecem* ser de IAM e não são.

Se você só ler uma frase deste documento, que seja esta:

> **Quando um `AccessDenied` mencionar `kms:`, a permissão que falta quase nunca está na role. Está na key policy.**

---

## 1. O que o KMS faz (e o que ele não faz)

O KMS **não criptografa seus dados**. Ele gerencia chaves.

Para dados acima de 4 KB — ou seja, praticamente tudo — o que acontece é **envelope encryption**:

```
1. O serviço (S3, SQS, EBS...) chama kms:GenerateDataKey
2. O KMS devolve DUAS coisas:
     - a chave de dados em texto claro
     - a mesma chave, criptografada com a chave mestra
3. O serviço criptografa seus dados com a chave em texto claro
4. Descarta a chave em texto claro da memória
5. Guarda o dado criptografado + a chave criptografada, juntos

Para ler de volta:
6. Chama kms:Decrypt passando a chave criptografada
7. Recebe a chave de dados em texto claro
8. Descriptografa o dado
```

Isso explica duas coisas que confundem:

**Por que existem duas permissões diferentes.** Quem **escreve** precisa de `kms:GenerateDataKey`. Quem **lê** precisa de `kms:Decrypt`. Uma role de escrita com só `Decrypt` falha — e vice-versa. No lab, o publisher e o consumer precisariam de permissões KMS *diferentes* sobre a mesma chave.

**Por que a chave mestra nunca sai do KMS.** Ela nunca é exportada. O que circula é sempre uma chave de dados derivada.

---

## 2. A key policy é diferente de tudo que você viu

Em [`01-iam-explicado.md`](01-iam-explicado.md) vimos que uma permissão pode morar na identity policy ou na resource policy, e que dentro da mesma conta **um `Allow` de qualquer um dos lados normalmente basta**.

**O KMS quebra essa regra.** A key policy é obrigatória e é a raiz da confiança:

> Uma role sem menção na key policy **não usa a chave**, por mais `kms:Decrypt` que tenha na identity policy.

### Então por que funciona dentro da mesma conta?

Porque a key policy **padrão**, criada junto com a chave, contém este bloco:

```json
{
  "Effect": "Allow",
  "Principal": { "AWS": "arn:aws:iam::111122223333:root" },
  "Action": "kms:*",
  "Resource": "*"
}
```

Isso significa: *"delego para as identity policies desta conta decidirem"*. É por isso que na mesma conta você só escreve a permissão na role e tudo funciona — a key policy já delegou.

E é exatamente por isso que **cross-account não funciona sem alterar a key policy**: a delegação vale só para a própria conta.

Se alguém "endureceu" a key policy removendo esse bloco (acontece em ambiente regulado), até dentro da mesma conta cada role precisa ser citada nominalmente. Isso costuma pegar times inteiros de surpresa.

---

## 3. Chave gerenciada pela AWS vs chave própria

| | **AWS managed** (`aws/sqs`, `aws/s3`, `aws/secretsmanager`) | **Customer managed** (CMK) |
|---|---|---|
| Custo | grátis | ~US$ 1/mês + chamadas |
| Key policy editável | **não** | sim |
| Cross-account | **impossível** | sim |
| Rotação | automática, anual, sem controle | configurável |
| Aparece no CloudTrail como sua | não | sim |

A linha decisiva é a terceira:

> **Chave gerenciada pela AWS não atravessa conta. Nunca.**

Consequência prática, que amarra com [`02-assume-role-cross-account.md`](02-assume-role-cross-account.md): se um recurso precisa ser acessado por outra conta e é criptografado, ele **tem** que usar chave própria. Não há policy que resolva — a key policy da chave gerenciada não é editável.

Esse é o motivo real por trás de muita migração de chave em projeto de integração entre times.

---

## 4. Onde o KMS aparece em cada capítulo anterior

### No fan-out SNS → SQS (README §1)

Se a fila usa SSE-KMS, dois pontos silenciosos:

**O consumer precisa de `kms:Decrypt`** — já está em `infra/iam/policy-consumer-*.json`.

**A key policy precisa permitir o principal de serviço do SNS.** Sem isso o SNS não consegue criptografar a mensagem ao entregar na fila, e o comportamento é o pior possível: o `Publish` retorna sucesso, a subscription aparece confirmada, e a mensagem **simplesmente não chega**. Nenhum erro em lugar nenhum.

```json
{
  "Sid": "PermiteSnsEntregarNaFilaCriptografada",
  "Effect": "Allow",
  "Principal": { "Service": "sns.amazonaws.com" },
  "Action": ["kms:GenerateDataKey*", "kms:Decrypt"],
  "Resource": "*"
}
```

Se você já perdeu horas com "a mensagem sumiu entre o SNS e a SQS", a resposta é essa ou a queue policy.

### Nas roles por worker (README §2)

Uma chave por domínio, e não uma chave por worker. Motivo prático: chave custa por mês e por chamada, e os três consumers do lab leem da mesma fila lógica de negócio. A granularidade fina fica na condição, não em chaves separadas:

```json
"Condition": { "StringEquals": { "kms:ViaService": "sqs.us-east-1.amazonaws.com" } }
```

Isso limita a role a usar a chave **através da SQS**. Se a role for comprometida, ela não consegue usar a chave para descriptografar um objeto S3 ou um snapshot EBS que use a mesma chave.

`kms:ViaService` é barato de escrever e restringe bastante. Use sempre que souber por qual serviço a chave será usada.

### Nos comprovantes em volume (README §3 e §4)

Aqui há uma assimetria que vale memorizar:

| Recurso | Criptografia em repouso | O pod/task precisa de permissão KMS? |
|---|---|---|
| **EFS** | transparente, gerenciada pelo serviço | **não** |
| **EBS** | por volume | **não** — quem precisa é o driver CSI |
| **S3** | por objeto | **sim** — `GenerateDataKey` para escrever |

EFS e EBS criptografados não exigem nada da aplicação: a descriptografia acontece abaixo do sistema de arquivos. Se você migrar os comprovantes do volume para o S3, **passa a precisar** de permissão KMS que antes não precisava. É a diferença que surpreende na migração.

E amarrando com [`06-eks-ecs-lambda.md`](06-eks-ecs-lambda.md): no EKS, quem precisa de KMS para o EBS é o **driver CSI**; no ECS com a integração EBS, é a **role de infraestrutura** do ECS. Em ambos os casos, não é a role da sua aplicação — e o PVC ou a task ficam presos sem erro claro se faltar.

### No Secrets Manager (capítulo 04)

Todo segredo é criptografado. Com a chave gerenciada, dentro da conta, funciona sem permissão extra. Com chave própria, a role precisa de `kms:Decrypt` com condição `kms:ViaService: secretsmanager.<região>.amazonaws.com`.

E, pelo que vimos na seção 3: **segredo lido de outra conta exige chave própria**.

### No cross-account (capítulo 02)

O terceiro documento a acertar, além da identity policy e da resource policy. O guia [02](02-assume-role-cross-account.md), seção "KMS: onde o cross-account mais falha", tem a key policy escrita por inteiro.

Isso transforma a regra "os dois lados precisam permitir" em **três lugares** quando há criptografia:

```
conta origem: identity policy da role
       ↓
conta destino: resource policy do recurso (bucket, fila)
       ↓
conta destino: KEY POLICY da chave     ← o mais esquecido
```

### Nas credenciais do SDK (capítulo 03)

Nenhum impacto — a resolução de credenciais não passa por KMS. Mas o erro aparece na mesma tela: `AccessDenied` mencionando `kms:GenerateDataKey` significa que a role já foi resolvida com sucesso e a falha é de autorização, não de identidade. Ler o principal na mensagem confirma que o IRSA funcionou.

### Na Lambda (capítulo 06)

Variáveis de ambiente de Lambda são criptografadas com KMS por padrão, mas o serviço cuida disso sozinho. Só vira problema se você usar chave própria e esquecer de dar acesso à execution role — e o sintoma é a função nem iniciar.

Log group do CloudWatch criptografado com chave própria: a key policy precisa permitir `logs.<região>.amazonaws.com`. Sintoma: função executa mas não loga nada.

---

## 5. Custo, porque isso aparece na fatura

- **US$ 1 por chave por mês** (chaves gerenciadas pela AWS são grátis)
- **~US$ 0,03 por 10.000 requisições**

O segundo item é o que surpreende. Um worker de alto volume que grava um objeto S3 por mensagem faz uma chamada KMS por objeto. Um milhão de comprovantes por dia = 1 milhão de `GenerateDataKey` = ~US$ 3/dia só de KMS.

Duas mitigações:

**S3 Bucket Keys.** Reduz drasticamente as chamadas ao KMS para objetos no mesmo bucket, reaproveitando uma chave de nível de bucket. Ligar é uma configuração do bucket, sem mudança de código. Para gravação em volume, é dinheiro de graça.

**Data key caching** no SDK de criptografia, quando você criptografa na aplicação. Tem trade-off de segurança: quanto mais tempo a chave de dados fica em memória, maior a janela de exposição. Só vale em volume realmente alto.

---

## 6. Checklist quando aparece um erro de KMS

Na ordem:

1. A mensagem diz `GenerateDataKey` ou `Decrypt`? Isso diz se falta permissão de **escrita** ou de **leitura**.
2. A role tem essa ação na identity policy?
3. **A key policy menciona o principal?** (mesma conta: existe o bloco `root` com `kms:*`? Cross-account: o ARN da role está lá?)
4. Se é a chave gerenciada pela AWS e a chamada é cross-account: **não vai funcionar**. Migre para CMK.
5. A condição `kms:ViaService` bate com o serviço que está realmente sendo usado?
6. Se é entrega SNS→SQS: a key policy permite `sns.amazonaws.com`?
7. Se é log group: a key policy permite `logs.<região>.amazonaws.com`?
8. A chave está na mesma região do recurso? (chave KMS é regional)
9. A chave está habilitada e não agendada para exclusão?

O passo 3 resolve a maioria dos casos, e é o que quase ninguém checa primeiro, porque a mensagem de erro aponta para a role.

---

## 7. Tabela de sintomas

| Sintoma | Causa |
|---|---|
| `AccessDenied` em `kms:Decrypt` com a role aparentemente correta | key policy não menciona o principal |
| mensagem publicada com sucesso mas nunca chega na fila | key policy da fila não permite `sns.amazonaws.com` |
| cross-account não funciona nem com policies corretas dos dois lados | chave gerenciada pela AWS; precisa de CMK |
| gravar funciona, ler falha | tem `GenerateDataKey` mas falta `Decrypt` |
| Lambda não inicia | chave própria nas variáveis de ambiente sem acesso da execution role |
| Lambda executa mas não loga | log group com CMK sem permissão para `logs.<região>.amazonaws.com` |
| PVC preso em `Pending` sem erro claro | driver CSI sem acesso à chave do EBS |
| fatura de KMS inesperada | uma chamada por objeto; ligue S3 Bucket Keys |
| funciona em dev, falha em prod | chaves são regionais e por conta; a de prod é outra |

---

## Referências

- Key policies — https://docs.aws.amazon.com/kms/latest/developerguide/key-policies.html
- Envelope encryption — https://docs.aws.amazon.com/kms/latest/developerguide/concepts.html#enveloping
- Condição `kms:ViaService` — https://docs.aws.amazon.com/kms/latest/developerguide/conditions-kms.html#conditions-kms-via-service
- SSE-KMS na SQS — https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-key-management.html
- S3 Bucket Keys — https://docs.aws.amazon.com/AmazonS3/latest/userguide/bucket-key.html
