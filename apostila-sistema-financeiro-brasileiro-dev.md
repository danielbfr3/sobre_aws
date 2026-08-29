# O Sistema Financeiro Brasileiro para Desenvolvedores

### Como o dinheiro se move entre bancos, o Bacen, o crédito e os pagamentos — uma visão técnica

---

## Sumário

**Parte I — Fundamentos (comece aqui)**
1. [Introdução: por que um dev precisa entender isso](#1-introdução)
2. [Como usar esta apostila: trilha de aprendizado](#2-como-usar-esta-apostila)
3. [Conceitos fundamentais: liquidez, compensação, liquidação e risco](#3-conceitos-fundamentais)
4. [O banco por dentro: produtos, balanço e como o banco ganha dinheiro](#4-o-banco-por-dentro)
5. [Contabilidade para devs: conta, lançamento e transação](#5-contabilidade-para-devs)
6. [Dinheiro no código: representação monetária e matemática financeira](#6-dinheiro-no-código)

**Parte II — A infraestrutura nacional**
7. [Estrutura do Sistema Financeiro Nacional (SFN)](#7-estrutura-do-sfn)
8. [O Bacen como orquestrador técnico: RSFN e ISPB](#8-o-bacen-como-orquestrador-técnico)
9. [Sistema de Pagamentos Brasileiro (SPB): o "barramento" central](#9-sistema-de-pagamentos-brasileiro-spb)

**Parte III — Os trilhos de pagamento**
10. [PIX: arquitetura técnica em detalhe](#10-pix-arquitetura-técnica-em-detalhe)
11. [TED, boleto e CNAB: os trilhos "tradicionais"](#11-ted-boleto-e-cnab)
11-A. [CNAB 240 de pagamento: segmentos, estados e ocorrências](#11-a-cnab-240-de-pagamento-segmentos-estados-e-ocorrências)
12. [Cartões: o arranjo de quatro partes](#12-cartões-o-arranjo-de-quatro-partes)

**Parte IV — Crédito e conformidade**
13. [Área de Crédito: ciclo de vida, garantias, SCR e recebíveis](#13-área-de-crédito)
    - [13.1 Ciclo de vida de uma operação de crédito](#131-ciclo-de-vida-de-uma-operação-de-crédito)
    - [13.2 Limites](#132-limites)
    - [13.3 Garantias](#133-garantias)
    - [13.4 Inadimplência, provisão e write-off](#134-inadimplência-provisão-e-write-off)
    - [13.5 Duplicatas](#135-duplicatas)
    - [13.6 Registradoras de recebíveis: registro, gravame e baixa](#136-registradoras-de-recebíveis)
14. [Compliance: KYC, PLD/FT, sigilo bancário e LGPD](#14-compliance)
15. [Conciliação, estorno, fraude e disputas](#15-conciliação-estorno-fraude-e-disputas)

**Parte V — Fronteira e consolidação**
16. [Open Finance: o dado como novo trilho](#16-open-finance)
17. [Visão consolidada: do sistema do banco até o Bacen](#17-visão-consolidada)
18. [Glossário técnico para devs](#18-glossário-técnico)
19. [Referências](#19-referências)

---

## 1. Introdução

Quando você trabalha em backend de banco — seja num core bancário, num sistema de cobrança, num motor de crédito ou numa integração PIX — está, na prática, escrevendo um dos nós de uma rede regulada que tem o **Banco Central do Brasil (Bacen/BCB)** como liquidante final. Entender essa rede muda a forma como você projeta idempotência, reconciliação, tratamento de erro e SLA, porque cada mensagem que seu serviço envia eventualmente:

- passa por uma **câmara** — uma entidade que centraliza e organiza as trocas financeiras entre instituições (Núclea, B3, SPI, STR);
- é liquidada em **moeda de banco central**, numa conta de Reservas Bancárias no Bacen;
- gera (ou consome) um **registro regulatório** (SCR, DICT, Open Finance) que outras instituições e o próprio Bacen também enxergam.

Esta apostila cobre os fundamentos econômicos e contábeis, a estrutura institucional, os trilhos de pagamento (PIX, boleto/CNAB, TED, cartões), a área de crédito e as obrigações de conformidade — sempre com o olhar de "o que isso significa para o meu sistema".

---

## 2. Como usar esta apostila

Esta apostila vai do **simples ao avançado** e assume que você sabe programar, mas **não** que você conheça o domínio bancário. Nenhum conhecimento prévio de finanças é necessário.

A ordem dos capítulos é intencional — cada parte depende da anterior:

```mermaid
graph LR
    P1["Parte I<br/>Fundamentos<br/>caps. 3-6"] --> P2["Parte II<br/>Infraestrutura<br/>caps. 7-9"]
    P2 --> P3["Parte III<br/>Trilhos de pagamento<br/>caps. 10-12"]
    P3 --> P4["Parte IV<br/>Crédito e conformidade<br/>caps. 13-15"]
    P4 --> P5["Parte V<br/>Fronteira<br/>caps. 16-17"]

    style P1 fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

| Se você… | Comece por |
|---|---|
| Nunca trabalhou com banco | Capítulo 3, na ordem, sem pular |
| Já trabalha com pagamentos, quer entender crédito | Capítulos 3, 5 e 6, depois pule para 13 |
| Já trabalha com crédito, quer entender pagamentos | Capítulos 3, 9, 10, 11 e 12 |
| Precisa de referência rápida | Capítulo 18 (glossário) |

**Como o texto é escrito.** Todo termo novo é definido no momento em que aparece pela primeira vez, em negrito, com a explicação logo em seguida. Você não precisa consultar nada fora deste documento para acompanhar — siglas são expandidas, jargão é traduzido, e o capítulo 18 repete tudo em forma de glossário para consulta rápida. Capítulos terminam com um **checkpoint**: perguntas curtas com gabarito, para você verificar se entendeu antes de seguir.

**Três avisos honestos antes de começar:**

1. **O vocabulário é o obstáculo real, não a complexidade técnica.** Um dev sênior consegue entender qualquer fluxo desta apostila; o que trava é a densidade de siglas. Não decore — volte ao glossário sempre que precisar.
2. **Errar em sistema financeiro custa dinheiro real de pessoas reais.** Um bug de arredondamento vira reclamação no Bacen. Isso muda o padrão de qualidade esperado do seu código — testes, idempotência e auditoria não são opcionais aqui.
3. **A regra muda.** Bacen publica resoluções continuamente. Esta apostila tem data (agosto/2026); antes de implementar, confirme a norma vigente.

---

## 3. Conceitos fundamentais

Antes de entrar em siglas (STR, SPI, SCR), vale fixar o vocabulário econômico que sustenta tudo. Esses conceitos aparecem disfarçados nas suas decisões técnicas — em prazo de conciliação, em desenho de retry, em modelagem de saldo.

### Moeda, escrituração e o que é "dinheiro" num sistema bancário

Aqui vai a primeira quebra de intuição para quem vem de fora do domínio: **quase nenhum dinheiro no sistema bancário é papel-moeda**. O que existe é *escrituração* — linhas em bases de dados de instituições distintas que, por acordo regulatório, representam obrigação de pagamento.

Existem duas "camadas de dinheiro" que convivem:

- **Moeda escritural (bancária)** — o saldo que o cliente vê no app. É uma **obrigação do banco perante o cliente** (passivo do banco). Quando você faz um `UPDATE` num saldo de conta corrente, você está movendo isso.
- **Moeda de banco central (reservas)** — o saldo que o *banco* mantém na sua conta de Reservas Bancárias no Bacen. É o único dinheiro que quita definitivamente uma obrigação **entre instituições**.

Essa distinção é a razão de existir de metade desta apostila: mover dinheiro dentro do mesmo banco é uma transação de banco de dados; mover entre bancos exige tocar a camada de reservas no Bacen.

### Liquidez

**Liquidez** é a rapidez com que um bem vira dinheiro na mão **sem perder valor**.

> **Analogia:** pense em hierarquia de memória. Saldo em conta é RAM — acesso imediato. Um investimento com resgate em D+1 é SSD — rápido, mas não instantâneo. Um imóvel é fita magnética: o dado está lá, só que recuperá-lo leva meses. E, como em cache, quanto mais rápido o acesso, menor tende a ser o retorno.

Liquidez é um espectro, não um booleano:

| Ativo | Liquidez | Observação |
|---|---|---|
| Saldo em conta corrente | Altíssima | Disponível imediatamente |
| Tesouro Selic | Muito alta | Resgate em D+1, baixa oscilação |
| CDB com liquidez diária | Alta | Depende da regra do produto |
| Duplicata a vencer em 90 dias | Média/baixa | Só vira caixa antecipando com deságio |
| Imóvel | Baixa | Meses para vender sem descontar preço |

Dois usos do termo que você vai encontrar:

- **Liquidez de um ativo** (a tabela acima) — quão rápido vira caixa.
- **Liquidez de uma instituição** — se o banco tem recursos disponíveis *agora* para honrar saques e obrigações interbancárias. Um banco pode ser **solvente** (ativos > passivos) e ainda assim quebrar por **falta de liquidez** (os ativos existem, mas estão travados em crédito de longo prazo enquanto os clientes sacam hoje). Foi assim que bancos historicamente colapsaram — não por prejuízo contábil, mas por descasamento de prazo.

> Para o dev: quando você vê regras de **horário de corte** (o horário-limite do dia para que uma operação ainda seja processada naquele dia, também chamado de *cut-off*), limites de valor por janela, ou o Bacen exigindo que reservas fiquem "pré-financiadas" antes de uma liquidação, o motivo é sempre gestão de liquidez do sistema.

### Compensação × Liquidação (a distinção mais importante da apostila)

Esses dois termos são usados como sinônimos no dia a dia e **não são**:

- **Compensação (*clearing*)** — apurar *quem deve quanto a quem*. É cálculo e troca de mensagens. Nenhum dinheiro mudou de mãos ainda.
- **Liquidação (*settlement*)** — a transferência de fato, definitiva e irreversível.

> **Analogia:** é a comanda do bar. Durante a noite, o garçom anota tudo — isso é compensação: a dívida existe, está registrada, mas você não pagou nada. Só quando você passa o cartão na saída é que ocorre a liquidação. E repare no risco embutido: entre anotar e cobrar, o cliente pode ir embora sem pagar.

Entre uma e outra existe **risco de liquidação**: a janela em que a operação foi confirmada comercialmente mas o dinheiro ainda não foi entregue. Se a contraparte quebrar nesse intervalo, alguém perde.

```mermaid
graph LR
    A["Transação iniciada<br/>(cliente clica 'pagar')"] --> B["Compensação<br/>apura saldos líquidos<br/>entre instituições"]
    B -->|"⚠ janela de risco de liquidação"| C["Liquidação<br/>move reservas no Bacen<br/>IRREVOGÁVEL"]
    C --> D["Recursos definitivamente<br/>disponíveis"]

    style C fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

**Erro clássico de arquitetura:** dar baixa definitiva num título com base no evento de compensação. Se o fluxo é reversível até a liquidação, seu sistema precisa de um estado intermediário (`PAGO_A_CONFIRMAR` vs `LIQUIDADO`) — não um booleano `pago`.

### LBTR × LDL: os dois modelos de liquidação

| | **LBTR** (Liquidação Bruta em Tempo Real) | **LDL** (Liquidação Diferida Líquida) |
|---|---|---|
| Como funciona | Cada transação liquidada individualmente, na hora | Transações acumuladas e compensadas em lote; só o saldo líquido é liquidado |
| Risco de liquidação | Praticamente eliminado | Existe até o momento do fechamento |
| Necessidade de liquidez | Alta (banco precisa de reserva a cada operação) | Baixa (netting reduz o valor movimentado) |
| Onde aparece | STR, SPI (PIX) | Boletos e cartões historicamente, via câmaras |

O **netting** (compensação multilateral) é o motivo de o LDL existir: se o banco A deve 100 ao B e o B deve 90 ao A, transfere-se só 10.

> **Analogia:** é o Splitwise. Durante a viagem, ninguém fica passando PIX a cada conta paga — o app só anota quem deve o quê. No fim, ele calcula o líquido e cada um faz uma transferência só. Muito menos dinheiro circula, mas todo mundo carrega o risco de alguém sumir antes do acerto. Essa é exatamente a troca do LDL: economiza liquidez, acumula risco ao longo do dia.

### D+0, D+1, D+2

Notação universal de prazo de liquidação, onde **D** é a data da operação:

- **D+0** — liquida no mesmo dia (PIX, TED)
- **D+1** — no dia útil seguinte (boletos, em boa parte dos arranjos atuais)
- **D+2** — dois dias úteis (ações na B3)
- **D+30** — comum em recebíveis de cartão de crédito

**Atenção:** não existe regra única de contagem. Liquidação interbancária conta **dias úteis**; recebíveis de cartão e encargos de mora normalmente contam **dias corridos**. A regra vem do produto e do contrato — assumir uma delas como padrão global é fonte clássica de divergência.

De qualquer forma, todo sistema financeiro brasileiro precisa de um **calendário de feriados bancários** (nacionais, estaduais e os do município da **praça** — no jargão bancário, "praça" é a localidade de cobrança ou compensação de um título). Hardcodar `data + 1 dia` é bug garantido.

### Float

**Float** é o dinheiro que já saiu de um lado e ainda não chegou no outro — fica "no ar" e rende para quem está segurando.

> **Analogia:** é a mensagem que o *producer* já publicou e o *consumer* ainda não processou. Ela existe, está em trânsito, e alguém precisa respondê-la enquanto isso. A diferença é que aqui a fila rende juros para o dono do broker. Historicamente foi fonte de receita relevante para bancos; o PIX praticamente eliminou float em pagamentos de varejo, o que é uma das razões econômicas da resistência inicial de parte do mercado a ele.

### Spread, deságio e taxa

- **Spread bancário** — diferença entre o custo de captação do banco e a taxa cobrada no empréstimo. É a margem bruta da operação de crédito.
- **Deságio** — desconto aplicado ao antecipar um recebível. Antecipar R$ 10.000 que venceriam em 90 dias por R$ 9.700 significa deságio de 3%. É o "preço da liquidez".
- **Taxa Selic** — taxa básica de juros, definida pelo **Copom** (Comitê de Política Monetária, colegiado do Bacen que se reúne periodicamente para definir a taxa), que ancora o custo do dinheiro no país. **Não confundir com o sistema SELIC** (custódia de títulos públicos) — mesmo nome, coisas diferentes.

### Os tipos de risco (e por que aparecem no seu código)

| Risco | O que é | Como aparece tecnicamente |
|---|---|---|
| **Risco de crédito** | O devedor não pagar | Motor de decisão, consulta ao SCR, limites |
| **Risco de liquidez** | Não ter caixa disponível na hora | Horários de corte, limites por janela |
| **Risco de liquidação** | Contraparte quebrar entre compensação e liquidação | Estados intermediários, estorno |
| **Risco operacional** | Falha de sistema, erro humano, fraude | Idempotência, trilha de auditoria, conciliação |
| **Risco sistêmico** | Quebra de um participante contaminar a cadeia | Razão de existir do STR e da supervisão do Bacen |

O **risco operacional** é literalmente o seu escopo de trabalho. Idempotência não é preciosismo de engenharia num sistema financeiro — é mitigação de risco regulatório: reprocessar um arquivo CNAB sem chave de deduplicação gera cobrança dupla, que gera reclamação no Bacen.

### Conta de Reservas Bancárias

Cada instituição participante direto do SPB mantém uma conta no Bacen. Toda liquidação interbancária, de qualquer trilho, termina num débito/crédito numa conta dessas. Ela precisa ter saldo suficiente **no momento** da liquidação — a conta não pode ficar negativa.

Mas isso não significa que o banco precise "adivinhar" o caixa do dia. O Bacen oferece o **redesconto intradia**: uma operação compromissada em que a instituição vende títulos públicos ao Bacen e os recompra no mesmo dia, **sem custo**, apenas para atravessar descasamentos de horário entre pagamentos e recebimentos. Não é medida de emergência — é rotina de infraestrutura, com centenas de operações e centenas de bilhões de reais por dia. Se a devolução escorregar para o dia útil seguinte, aí sim há cobrança de taxa.

> **Nota importante:** o PIX **não** liquida em Reservas Bancárias. Ele usa uma conta própria, a **Conta PI**, detalhada no capítulo 10.

```mermaid
graph TB
    subgraph BancoA["Banco A"]
        CC1["Conta do cliente João<br/>(moeda escritural = passivo do banco A)"]
    end

    subgraph Bacen["Banco Central"]
        RA["Reservas do Banco A"]
        RB["Reservas do Banco B"]
    end

    subgraph BancoB["Banco B"]
        CC2["Conta da cliente Maria<br/>(moeda escritural = passivo do banco B)"]
    end

    CC1 -->|"1. Débito interno<br/>(base de dados do banco A)"| RA
    RA -->|"2. Liquidação<br/>(moeda de banco central)"| RB
    RB -->|"3. Crédito interno<br/>(base de dados do banco B)"| CC2

    style RA fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
    style RB fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

Repare que são **três movimentações contábeis distintas** para um único PIX do João para a Maria. Se você só modelar a primeira e a terceira, seu sistema não fecha com o extrato do Bacen.

### Débito × Crédito (partidas dobradas)

Todo sistema financeiro sério usa **partidas dobradas**: toda operação gera pelo menos um débito e um crédito de valor igual.

Isso não é burocracia contábil. É um invariante que funciona como *checksum* do seu sistema — se a soma não fecha, existe bug.

E tem consequência direta de modelagem: se você guarda um campo `saldo` mutável em vez de um **ledger append-only** de lançamentos, perde a auditabilidade e a capacidade de reconstruir o estado passado. Que é exatamente o que um regulador vai pedir.

> Padrão recomendado: **event sourcing / ledger imutável**. Saldo é projeção derivada, nunca a fonte da verdade. O capítulo 5 detalha esse modelo — conta contábil, lançamento, partida e as consequências de arquitetura.

### Checkpoint

1. Qual a diferença entre moeda escritural e moeda de banco central?
2. Um banco pode ser solvente e quebrar mesmo assim. Como?
3. Compensação e liquidação: qual delas é irrevogável?
4. Por que o modelo LDL precisa de menos liquidez que o LBTR?
5. Por que `data + 1 dia` é insuficiente para calcular D+1?

*Respostas: (1) escritural é o saldo do cliente, passivo do banco; de banco central são as reservas no Bacen, única forma de quitar obrigação entre instituições; (2) por falta de liquidez — os ativos existem mas estão travados em prazo longo enquanto os saques são hoje; (3) a liquidação; (4) porque o netting compensa obrigações mútuas e só o saldo líquido é movimentado; (5) porque D+1 conta dia útil e depende de calendário de feriados, inclusive municipais.*

---

## 4. O banco por dentro

Este capítulo responde uma pergunta que raramente é feita explicitamente, mas que muda tudo: **como um banco ganha dinheiro?** Sem isso, você implementa requisitos sem entender por que eles existem.

### Intermediação financeira: o modelo de negócio

Um banco é, essencialmente, um **intermediário de prazo e risco**. Ele:

1. **Capta** recursos de quem tem sobra — depósitos e títulos de captação como **CDB** (Certificado de Depósito Bancário), **LCI** (Letra de Crédito Imobiliário) e **LCA** (Letra de Crédito do Agronegócio), nos quais o cliente empresta dinheiro ao banco em troca de juros — pagando pouco por isso;
2. **Empresta** para quem precisa (crédito) — cobrando mais;
3. Fica com a diferença: o **spread**.

O detalhe crucial: o banco capta **curto** (você pode sacar amanhã) e empresta **longo** (financiamento de 30 anos). Esse **descasamento de prazo** é a função econômica do banco — e também sua fragilidade estrutural.

> **Analogia:** é *overbooking* de connection pool. O banco sabe que nem todos os clientes vão sacar no mesmo dia, então mantém em caixa só uma fração do que deve. Funciona bem no caso médio. Se todo mundo pedir conexão ao mesmo tempo — uma corrida bancária —, o pool estoura, mesmo com a instituição sendo perfeitamente saudável no papel.

**As quatro fontes de receita de um banco:**

| Fonte | O que é | Exemplo |
|---|---|---|
| **Spread de crédito** | Margem entre captação e empréstimo | Capta a 10% a.a., empresta a 30% a.a. |
| **Tarifas e serviços** | Cobrança por serviços prestados | Anuidade de cartão, TED, conta PJ, custódia |
| **Float** | Rendimento do dinheiro em trânsito | Fortemente reduzido pelo PIX |
| **Tesouraria** | Aplicação de recursos próprios | Títulos públicos, câmbio, derivativos |

### O balanço de um banco (a parte contraintuitiva)

Aqui está a inversão que confunde todo dev iniciante no domínio:

- **O depósito do cliente é PASSIVO do banco** (o banco *deve* esse dinheiro ao cliente).
- **O empréstimo concedido é ATIVO do banco** (alguém *deve* ao banco).

Ou seja: do ponto de vista do banco, seu saldo em conta é uma dívida dele com você, e a sua dívida de cartão é um bem dele.

```mermaid
graph LR
    subgraph PASSIVO["PASSIVO + PL — de onde o dinheiro veio"]
        P1["Depósitos de clientes<br/>(à vista e a prazo)"]
        P2["Captações no mercado<br/>(CDB, LCI, LCA)"]
        P3["Patrimônio Líquido<br/>(capital dos sócios)"]
    end

    subgraph ATIVO["ATIVO — onde o dinheiro está aplicado"]
        A1["Carteira de crédito<br/>(empréstimos concedidos)"]
        A2["Títulos e valores mobiliários"]
        A3["Reservas no Bacen + caixa"]
    end

    P1 --> A1
    P1 --> A3
    P2 --> A1
    P3 --> A2

    style A3 fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

**Por que isso importa no código:** quando você modela lançamentos, o sinal do valor depende da perspectiva. Um crédito na conta do cliente é um aumento de passivo do banco. Se seu ledger não deixa isso explícito, você vai inverter sinal em algum relatório contábil — e vai demorar para descobrir.

### Reserva compulsória e o multiplicador de crédito

O Bacen obriga cada banco a manter uma fração dos depósitos "parada" na conta de reservas — o **depósito compulsório**. É instrumento de política monetária (aperta o compulsório, reduz o crédito na economia) e de segurança.

O efeito colateral é o **multiplicador de crédito**: quando o banco A empresta, esse dinheiro é depositado no banco B, que empresta parte dele, e assim por diante. O sistema bancário **cria moeda escritural** — a quantidade de "dinheiro" no sistema é múltiplo da base monetária emitida pelo Bacen. Não é truque contábil, é como o sistema funciona por desenho.

### Tipos de conta (e por que a distinção importa)

Esse é um ponto que aparece direto em modelagem de dados:

| Tipo | Quem oferece | Característica |
|---|---|---|
| **Conta corrente (depósito à vista)** | Bancos | Movimentação livre, pode ter cheque especial |
| **Conta poupança** | Bancos | Remuneração pela regra da poupança, aniversário mensal |
| **Conta de pagamento** | IPs e bancos | **Recursos são de terceiros e devem ser segregados** — não entram no balanço da IP e não podem ser emprestados |
| **Conta salário** | Bancos | Destinada a recebimento de salário, portabilidade obrigatória |
| **Conta escrow / vinculada** | Bancos | Recursos bloqueados até condição contratual ser cumprida |

> **Ponto crítico:** a segregação de recursos em conta de pagamento é a razão de uma fintech não poder "usar o float dos clientes". Se você trabalha em IP, isso significa que o dinheiro dos clientes vive em conta separada e seu sistema precisa provar essa segregação a qualquer momento.

### Identificação de contas: agência, conta e dígito

- **Agência** — herança da era física; hoje muitas vezes só um número lógico (`0001`).
- **Conta + dígito verificador** — o DV é calculado por algoritmo específico **de cada banco** (variações de módulo 11). Não existe padrão nacional único. Se você valida conta no cliente, vai precisar da regra de cada instituição.
- **Conta gráfica** — conta interna, sem existência no sistema bancário externo; comum em carteiras digitais e sistemas de subcontas.

### Taxonomia de produtos de crédito

| Produto | Característica |
|---|---|
| **Empréstimo pessoal** | Sem destinação específica, sem garantia real |
| **Financiamento** | Destinação vinculada ao bem (veículo, imóvel), bem costuma ser a garantia |
| **Consignado** | Desconto direto em folha/benefício — risco menor, taxa menor |
| **Cheque especial** | Limite rotativo em conta, taxa alta |
| **Cartão de crédito** | Rotativo + parcelado, prazo de faturamento |
| **Capital de giro (PJ)** | Financia operação corrente da empresa |
| **Antecipação de recebíveis** | Adianta valor de duplicatas/recebíveis de cartão com deságio |

### Checkpoint

1. Do ponto de vista do banco, o saldo do seu cliente é ativo ou passivo?
2. Quais são as quatro fontes de receita de um banco?
3. Por que uma instituição de pagamento não pode emprestar o dinheiro que está na conta de pagamento do cliente?
4. O que é descasamento de prazo e por que ele é a função econômica do banco?

*Respostas: (1) passivo — é uma dívida do banco com o cliente; (2) spread de crédito, tarifas, float e tesouraria; (3) porque são recursos de terceiros e a regulação exige que fiquem segregados, fora do balanço da IP; (4) captar curto e emprestar longo — é o que permite financiar projetos de anos com depósitos sacáveis a qualquer momento, e é também a origem do risco de liquidez.*

---

## 5. Contabilidade para devs

Aqui está a tese central deste capítulo, e talvez de toda a apostila:

> **Em uma instituição financeira, a contabilidade não é um relatório gerado a partir do seu modelo de dados. Ela É o modelo de dados.**

Quem vem de sistemas comuns tende a modelar um saldo como coluna mutável e tratar contabilidade como exportação para o time financeiro. Em banco isso não funciona — o registro contábil é a fonte da verdade, é auditado pelo Bacen e precisa ser reconstituível anos depois.

### Conta contábil

Uma **conta contábil** é uma categoria onde valores se acumulam. Cuidado: **ela não é a conta do cliente.**

> **Analogia:** conta contábil é o *tipo*; conta do cliente é a *instância*. João e Maria têm cada um sua conta corrente — dois registros operacionais distintos —, mas os dois saldos somam na mesma conta contábil, "depósitos à vista". É a diferença entre a classe e os objetos dela.

Toda conta tem uma **natureza**:

| Natureza | Grupos | Aumenta com | Diminui com |
|---|---|---|---|
| **Devedora** | Ativo, Despesa | Débito | Crédito |
| **Credora** | Passivo, Patrimônio Líquido, Receita | Crédito | Débito |

### Débito e crédito não significam entrada e saída

Esse é **o** ponto de confusão. No senso comum, "crédito" é dinheiro entrando. Na contabilidade, débito e crédito são apenas **os dois lados de um lançamento** — o efeito depende da natureza da conta:

- Debitar uma conta de **ativo** → aumenta (mais caixa)
- Debitar uma conta de **passivo** → diminui (menos dívida)

Quando o app diz "seu saldo foi creditado", isso está correto do ponto de vista do banco: o depósito do cliente é **passivo** do banco, e passivo aumenta a crédito. A intuição do usuário e a contabilidade coincidem por acidente — não confie nessa coincidência ao modelar.

### Lançamento e partidas dobradas

Um **lançamento** (*entry* / *posting*) é o registro de um fato contábil. Pelo método das **partidas dobradas**, todo lançamento tem no mínimo uma partida a débito e uma a crédito, e obedece a um invariante absoluto:

```
Σ débitos = Σ créditos     (sempre, sem exceção)
```

**Por que isso é sempre verdade?** Não é convenção arbitrária — é consequência lógica de um fato: **valor não aparece nem desaparece, ele se desloca.** Todo evento econômico responde simultaneamente a duas perguntas:

- *De onde o valor veio?* → o **crédito** (a origem)
- *Para onde o valor foi?* → o **débito** (o destino)

Se o valor que saiu de algum lugar não for igual ao que chegou em outro, você não registrou um fato econômico — registrou um erro. A soma bater não é a *meta* do lançamento; é a *prova* de que ele descreve algo real.

É a mesma lógica de uma equação de conservação em física, ou de um `diff` que precisa fechar. Se você já debugou um sistema distribuído procurando mensagens perdidas, o instinto é o mesmo: **a diferença aponta para onde está o bug.**

### Visualizando: a conta em "T"

Contadores desenham cada conta como um "T" — débitos à esquerda, créditos à direita. É o modelo mental que eles usam em reunião:

```
      Caixa (Ativo)                 Depósito do cliente (Passivo)
   D  │  C                              D  │  C
──────┼──────                      ──────┼──────
 1.000│                                  │ 1.000
```

O cliente depositou R$ 1.000. O caixa do banco aumentou (débito em ativo). A dívida do banco com o cliente aumentou (crédito em passivo). Duas contas, dois lados, mesmo valor.

**Mnemônico que funciona:** débito é sempre **esquerda**, crédito é sempre **direita**. Só isso.

> **Analogia:** é como sinal de vetor. `+1` no eixo X move para a direita; `+1` no eixo Y move para cima. O sinal é o mesmo — o efeito depende do eixo. Débito é o "sinal"; a natureza da conta é o "eixo". Debitar aumenta o ativo e diminui o passivo, e não há contradição nisso, do mesmo jeito que não há contradição em `+1` significar coisas diferentes em eixos diferentes.

Pare de traduzir para "entrada/saída" e pense só em "lado esquerdo/lado direito". A confusão desaparece.

### O que um lançamento quebrado parece

Vale ver o erro para reconhecê-lo. Suponha uma transferência de R$ 500 em que o desenvolvedor esqueceu de registrar a tarifa de R$ 5 cobrada:

| Conta | Débito | Crédito |
|---|---|---|
| Depósito — cliente pagador | 505,00 | |
| Depósito — cliente recebedor | | 500,00 |
| **Total** | **505,00** | **500,00** ❌ |

Diferença de R$ 5,00. O sistema debitou o cliente em 505 mas só creditou 500 em algum lugar — os outros 5 sumiram. O invariante detectou o bug **na hora da escrita**, antes de virar divergência no balancete do mês seguinte.

A versão correta inclui a partida que faltava:

| Conta | Débito | Crédito |
|---|---|---|
| Depósito — cliente pagador | 505,00 | |
| Depósito — cliente recebedor | | 500,00 |
| Receita de tarifas | | 5,00 |
| **Total** | **505,00** | **505,00** ✓ |

**Os erros mais comuns que o invariante pega:**

- Partida esquecida (tarifa, imposto, juros não registrados)
- Erro de arredondamento em rateio (o capítulo 6 trata disso)
- Gravação parcial por falha no meio da transação
- Duplicação de uma das partidas em retry sem idempotência

### Implementando: o modelo mínimo de um ledger

Traduzindo tudo isso para esquema. O essencial cabe em duas tabelas:

```sql
-- O lançamento: o "cabeçalho" do fato contábil
CREATE TABLE lancamento (
    id                BIGSERIAL PRIMARY KEY,
    evento_negocio_id VARCHAR(64) NOT NULL,   -- rastreabilidade até a origem
    chave_idempotencia VARCHAR(64) NOT NULL,  -- reprocessar não duplica
    data_evento       TIMESTAMPTZ NOT NULL,   -- quando o fato ocorreu
    data_contabil     DATE        NOT NULL,   -- em que período contabiliza
    historico         TEXT        NOT NULL,
    criado_em         TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_idempotencia UNIQUE (chave_idempotencia)
);

-- As partidas: as linhas débito/crédito
CREATE TABLE partida (
    id             BIGSERIAL PRIMARY KEY,
    lancamento_id  BIGINT      NOT NULL REFERENCES lancamento(id),
    conta_contabil VARCHAR(20) NOT NULL,   -- rubrica (mapeável ao COSIF)
    sentido        CHAR(1)     NOT NULL CHECK (sentido IN ('D','C')),
    valor          NUMERIC(18,2) NOT NULL CHECK (valor > 0),
    moeda          CHAR(3)     NOT NULL DEFAULT 'BRL'
);
```

Repare em três decisões deliberadas:

1. **`valor` é sempre positivo**, e o sinal vive em `sentido`. Não misture os dois — valor negativo com sentido explícito gera ambiguidade e bugs de agregação.
2. **`NUMERIC`, nunca `FLOAT`**.
3. **Sem coluna de saldo.** Saldo é projeção (veja adiante).

A validação do invariante, em pseudocódigo:

```csharp
void Registrar(Lancamento lancamento)
{
    var totalDebito  = lancamento.Partidas
        .Where(p => p.Sentido == 'D').Sum(p => p.Valor);
    var totalCredito = lancamento.Partidas
        .Where(p => p.Sentido == 'C').Sum(p => p.Valor);

    if (totalDebito != totalCredito)
        throw new LancamentoDesbalanceadoException(totalDebito, totalCredito);

    if (lancamento.Partidas.Count < 2)
        throw new LancamentoInvalidoException("mínimo de duas partidas");

    // cabeçalho + partidas gravam na MESMA transação de banco de dados
    _repo.SalvarAtomicamente(lancamento);
}
```

**Onde validar?** Em três camadas, porque cada uma pega um tipo de falha diferente:

| Camada | Como | Pega |
|---|---|---|
| **Aplicação** | Validação antes de persistir | Erro de lógica de negócio |
| **Banco de dados** | *Trigger* ou constraint diferida por `lancamento_id` | Escrita por fora da aplicação |
| **Rotina de verificação** | Job periódico somando tudo | Corrupção, migração malfeita, bug histórico |

E o saldo, que **não** é armazenado como verdade, mas derivado:

```sql
SELECT conta_contabil,
       SUM(CASE WHEN sentido = 'D' THEN valor ELSE -valor END) AS saldo
FROM   partida p
JOIN   lancamento l ON l.id = p.lancamento_id
WHERE  l.data_contabil <= :data_referencia
GROUP  BY conta_contabil;
```

Essa query é a razão de ser do desenho append-only: **você consegue reconstruir o saldo de qualquer conta em qualquer data passada.** Se o volume exigir, materialize saldos em snapshot — mas mantenha sempre a capacidade de recalcular do zero e comparar. A divergência entre o snapshot e o recálculo é o seu alarme de integridade.

> **Pergunta que todo dev faz:** "não seria mais simples guardar valores com sinal (+/-) e somar?" Funciona matematicamente, mas você perde a semântica contábil — o time financeiro não consegue ler seu ledger, o mapeamento para o COSIF fica implícito, e a distinção entre "aumentou o ativo" e "diminuiu o passivo" desaparece. Em sistema que será auditado, mantenha `sentido` explícito.

### Transação × lançamento × partida

Três níveis distintos, e confundi-los é erro clássico de modelagem:

| Nível | O que é | Cardinalidade |
|---|---|---|
| **Transação (evento de negócio)** | O fato do mundo real: "João pagou um boleto" | 1 |
| **Lançamento contábil** | O registro do fato, atômico e balanceado | 1..N por transação |
| **Partida** | Cada linha débito/crédito do lançamento | 2..N por lançamento |

**Um evento de negócio quase nunca gera um único par débito/crédito.** Veja o desembolso de um empréstimo de R$ 10.000 com IOF de R$ 150 e tarifa de R$ 50:

| Conta | Débito | Crédito |
|---|---|---|
| Operações de crédito (ativo) | 10.000,00 | |
| Depósitos à vista — cliente (passivo) | | 9.800,00 |
| IOF a recolher (passivo) | | 150,00 |
| Receita de tarifas (receita) | | 50,00 |
| **Total** | **10.000,00** | **10.000,00** |

Uma transação, quatro partidas, quatro contas contábeis diferentes — e fecha.

```mermaid
graph TB
    EV["Evento de negócio<br/>'Desembolso de empréstimo'"] --> LC["Lançamento contábil<br/>(atômico, balanceado)"]
    LC --> P1["Partida: D — Operações de crédito"]
    LC --> P2["Partida: C — Depósito do cliente"]
    LC --> P3["Partida: C — IOF a recolher"]
    LC --> P4["Partida: C — Receita de tarifas"]

    P1 --> RZ["Razão<br/>(saldo por conta contábil)"]
    P2 --> RZ
    P3 --> RZ
    P4 --> RZ
    RZ --> BAL["Balancete<br/>(Σ D = Σ C)"]

    style LC fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

### Diário, Razão e Balancete

Vocabulário que você vai ouvir em toda reunião com o time contábil:

- **Diário (*journal*)** — todos os lançamentos em ordem cronológica. É o *log de eventos*, append-only por natureza.
- **Razão (*ledger*)** — os mesmos lançamentos agrupados por conta contábil, com saldo acumulado. É a *projeção* por agregado.
- **Balancete de verificação (*trial balance*)** — a lista de saldos de todas as contas, provando que débitos e créditos totais se igualam.

Se isso soa familiar, é porque **contabilidade de partidas dobradas é event sourcing com 500 anos de vantagem**. O Diário é o event store, o Razão é a read model, e o Balancete é o teste de integridade. Quem inventou o padrão foi Luca Pacioli, em 1494.

### COSIF: o plano de contas é imposto pelo regulador

Diferente de uma empresa comum, uma instituição financeira **não escolhe livremente seu plano de contas**. O **COSIF (Plano Contábil das Instituições do Sistema Financeiro Nacional)**, criado pelo Bacen pela Circular nº 1.273/1987, padroniza a contabilização de bancos, cooperativas de crédito, corretoras, distribuidoras e demais instituições — garantindo demonstrações consistentes e comparáveis entre instituições e permitindo a supervisão pelo regulador.

O COSIF é organizado em quatro capítulos: normas básicas, elenco e funções de contas, documentos e anexos.

**Estrutura do código da conta** — hierárquica, no formato `G.S.D.TT.SS-V`:

```
1.6.8.10.00-2
│ │ │ │  │  └── dígito verificador
│ │ │ │  └───── subtítulo
│ │ │ └──────── título contábil
│ │ └────────── desdobramento de subgrupo
│ └──────────── subgrupo
└────────────── grupo
```

A instituição **pode criar desdobramentos internos** para controle gerencial, desde que sejam conversíveis ao nível analítico do elenco oficial. Ou seja: seu plano de contas interno pode ser mais granular que o COSIF, mas precisa **agregar** corretamente nele — o que na prática significa manter uma camada de *mapeamento* entre suas contas internas e as rubricas oficiais.

**Atributos** — cada conta tem atributos que definem **qual tipo de instituição pode usá-la**. Por exemplo, `U` para bancos múltiplos, `J` para **SCD** (Sociedade de Crédito Direto — fintech que empresta apenas capital próprio), **SEP** (Sociedade de Empréstimo entre Pessoas — plataforma de *peer-to-peer lending*) e **SCM** (Sociedade de Crédito ao Microempreendedor), e `Y` para instituições de pagamento. Uma SEP só pode lançar em contas que tenham o atributo `J`. Isso significa que o mesmo evento de negócio pode ter contabilização diferente conforme o tipo de licença da instituição — algo que precisa ser configurável no seu sistema, não hardcoded.

### Contas de compensação

Existem obrigações e riscos que **não entram no balanço** mas precisam ser registrados: limites de crédito aprovados e não usados, garantias recebidas, avais concedidos. São as **contas de compensação** (*off-balance sheet*).

> **Analogia:** é quota reservada e ainda não consumida. Um limite de cheque especial de R$ 5.000 que o cliente não usou não é dívida do banco — mas, se todos sacarem o limite amanhã, vira. Assim como capacidade provisionada não é carga atual, mas você precisa dimensionar por ela.

### Contas transitórias

Quando um valor chega e você ainda não sabe onde classificá-lo (um crédito não identificado, uma remessa em processamento), ele vai para uma **conta transitória** (*suspense account*).

> **Analogia:** é a *dead letter queue* da contabilidade. Recebeu, não soube rotear, guardou num lugar seguro para tratar depois. E vale a mesma disciplina de operação: uma DLQ que só cresce não é um buffer, é um incidente.

Duas regras práticas:

1. Conta transitória precisa **zerar** — saldo residual crescente é sintoma de bug ou de processo quebrado.
2. Monitore idade e volume desses saldos. É um dos melhores indicadores de saúde de um sistema financeiro.

### Competência × Caixa

- **Regime de caixa** — registra quando o dinheiro se move.
- **Regime de competência** — registra quando o fato econômico acontece, mesmo que o dinheiro só entre depois.

> **Analogia:** é a diferença entre medir throughput no momento em que a requisição chega e medir no momento em que a fatura do mês fecha. A competência acompanha o evento; o caixa acompanha o pagamento.

Contabilidade bancária usa **competência**. Exemplo: juros de um empréstimo são **apropriados diariamente** (receita reconhecida todo dia), mesmo que o cliente só pague daqui a 30 dias. Por isso existem rotinas batch noturnas de apropriação — não é capricho, é regime de competência.

### Fechamento e períodos

Ao final do dia/mês, o período contábil é **fechado**. Depois disso:

- **Não se altera lançamento de período fechado.** Correção se faz com lançamento de ajuste no período aberto.
- Isso reforça o desenho append-only: se seu sistema faz `UPDATE` em lançamento, ele é incompatível com o fechamento contábil.

### Consequências diretas para a arquitetura

Consolidando os princípios que decorrem de tudo acima:

| Princípio | Implementação |
|---|---|
| **Imutabilidade** | Lançamentos são append-only. Correção = lançamento reverso, nunca `UPDATE`/`DELETE` |
| **Atomicidade** | Todas as partidas de um lançamento gravam juntas ou nenhuma grava |
| **Invariante de balanço** | Valide `Σ débitos = Σ créditos` na escrita, não só no relatório |
| **Saldo é projeção** | Derive de lançamentos. Se cachear, tenha como recalcular do zero |
| **Idempotência** | Chave única por evento de negócio, para reprocessar sem duplicar |
| **Data tripla** | Data do evento, data contábil e data de liquidação são campos distintos |
| **Rastreabilidade** | Todo lançamento aponta para o evento de negócio que o originou |
| **Mapeamento configurável** | A regra "evento X → contas Y e Z" muda por norma; não deve estar no código |

> **Regra de ouro:** se você não consegue reconstruir o saldo de qualquer conta em qualquer data passada apenas relendo os lançamentos, seu sistema não é auditável — e auditabilidade não é opcional aqui.

### Checkpoint

Antes de seguir, confira se você consegue responder sem voltar ao texto:

1. Por que a soma dos débitos é sempre igual à dos créditos? (Se sua resposta for "porque é a regra", releia — a resposta é sobre origem e destino do valor.)
2. Um cliente deposita R$ 200. Qual conta é debitada e qual é creditada, do ponto de vista do banco?
3. Qual a diferença entre uma transação de negócio e um lançamento contábil?
4. Por que `valor` deve ser positivo e o sinal ficar em `sentido`?
5. Por que o saldo não deve ser a fonte da verdade?
6. Seu sistema precisa corrigir um lançamento de um mês já fechado. O que se faz?

*Respostas curtas: (1) todo evento econômico é um deslocamento de valor — crédito é a origem, débito é o destino; (2) débito em caixa/reservas, crédito em depósitos à vista do cliente; (3) uma transação é o fato de negócio e pode gerar N lançamentos, cada um com 2..N partidas; (4) para preservar a semântica contábil e evitar ambiguidade de sinal na agregação; (5) porque ele é projeção derivada — mantê-lo como verdade destrói a auditabilidade; (6) lançamento de ajuste no período aberto, nunca `UPDATE` no período fechado.*

---

## 6. Dinheiro no código

Este capítulo é a ponte direta entre domínio e implementação. São os erros que mais aparecem em code review de sistema financeiro.

### Nunca use ponto flutuante para dinheiro

`0.1 + 0.2 != 0.3` em IEEE 754. Num sistema que soma milhões de lançamentos, esse erro acumula e **quebra o fechamento contábil**.

Use:
- **`decimal` / `BigDecimal`** — precisão decimal exata (`decimal` em C#, `Decimal` em Python, `BigDecimal` em Java)
- **Inteiro em centavos** — armazenar `10050` para R$ 100,50, fazendo a divisão só na apresentação

Guarde também a **moeda** junto do valor. Um campo `valor: decimal` solto é uma bomba-relógio em qualquer sistema que um dia opere câmbio.

### Arredondamento é regra de negócio, não detalhe técnico

Ao dividir R$ 100,00 em 3 parcelas, cada uma dá R$ 33,3333… Você **precisa** decidir onde vai o centavo residual. A convenção mais comum no mercado brasileiro é jogar a diferença na **primeira ou na última parcela**:

```
100,00 / 3 → 33,33 + 33,33 + 33,34
```

O invariante que seu teste deve verificar: **a soma das parcelas é sempre igual ao valor original**. Se não for, você está criando ou destruindo dinheiro.

Cuidado também com o modo de arredondamento: `HALF_UP` (comercial, o que a intuição espera) versus `HALF_EVEN` ("arredondamento bancário", que reduz viés estatístico em grandes volumes). São resultados diferentes — a norma ou o contrato define qual usar.

### Juros: simples × compostos

- **Juros simples** — incidem sempre sobre o principal. `M = C × (1 + i × n)`
- **Juros compostos** — incidem sobre o montante acumulado. `M = C × (1 + i)ⁿ`

Praticamente todo crédito no Brasil usa **juros compostos**. E atenção à conversão de taxas: **não se converte taxa mensal em anual multiplicando por 12**. A conversão correta é exponencial:

```
i_anual = (1 + i_mensal)^12 - 1
```

2% ao mês não é 24% ao ano — é 26,82% ao ano.

### Sistemas de amortização: Price × SAC

| | **Price (Tabela Price)** | **SAC** |
|---|---|---|
| Parcela | Fixa do início ao fim | Decrescente |
| Amortização | Crescente | Constante |
| Juros | Decrescentes | Decrescentes |
| Uso típico | Crédito pessoal, consignado, veículo | Financiamento imobiliário |
| Total de juros pagos | Maior | Menor |

Toda parcela, em ambos, decompõe-se em **amortização + juros**. Seu sistema precisa registrar essa decomposição separadamente — o cliente tem direito a saber e a contabilidade exige.

### CET, IOF e o custo real

- **CET (Custo Efetivo Total)** — a taxa que engloba **tudo**: juros, tarifas, tributos, seguros. É **obrigatório** informar ao cliente antes da contratação. Não é a taxa de juros nominal.
- **IOF** — tributo sobre operações de crédito, com componente fixo e componente diário proporcional ao prazo. É calculado e recolhido pela instituição.

Se seu sistema exibe uma taxa ao cliente sem calcular CET corretamente, isso não é bug de UI — é descumprimento regulatório.

### Encargos de atraso

Limites clássicos aplicados a boletos e crédito ao consumidor:

- **Multa** — geralmente limitada a **2%** sobre o valor
- **Juros de mora** — tipicamente **1% ao mês**, calculados *pro rata die* ("proporcionalmente ao dia": divide-se a taxa mensal pelo número de dias e multiplica-se pelos dias de atraso)
- **Correção monetária** — quando prevista em contrato

O cálculo `pro rata die` exige, de novo, **calendário de dias úteis/corridos correto**. Contar dias com aritmética ingênua de `datetime` é uma das causas mais comuns de divergência de centavos em cobrança.

### Datas: o inimigo silencioso

- **Fuso horário** — armazene em UTC, exiba em `America/Sao_Paulo`. O horário de corte bancário é local.
- **Dia útil × dia corrido** — regras diferentes por produto e por praça.
- **Feriados** — nacionais, estaduais e municipais afetam compensação. Precisa vir de fonte confiável e atualizável, nunca hardcoded.
- **Data de operação × data de liquidação × data contábil** — são três datas distintas e seu modelo precisa das três.

### Checkpoint

1. Por que `float`/`double` não serve para dinheiro?
2. R$ 100,00 em 3 parcelas: qual invariante seu teste precisa garantir?
3. 2% ao mês equivale a 24% ao ano?
4. Qual a diferença entre a taxa de juros anunciada e o CET?
5. Em Price e SAC, o que muda para o cliente?

*Respostas: (1) IEEE 754 não representa decimais exatamente e o erro acumula, quebrando o fechamento contábil; (2) a soma das parcelas tem de ser igual ao valor original — o centavo residual vai para uma parcela específica; (3) não: (1,02)¹² − 1 = 26,82% ao ano; (4) o CET inclui tarifas, tributos e seguros, não só os juros; (5) na Price a parcela é fixa, na SAC ela começa maior e vai caindo, com menos juros totais.*

---

## 7. Estrutura do SFN

O Sistema Financeiro Nacional (SFN) tem uma separação clássica entre quem **normatiza**, quem **supervisiona/executa** e quem **opera** no mercado.

- **CMN (Conselho Monetário Nacional)** — órgão normativo máximo. Não executa nada, só define regras (resoluções). Composto por três membros: o Ministro da Fazenda (que o preside), o Ministro do Planejamento e Orçamento e o presidente do Bacen. O Bacen exerce a secretaria-executiva do conselho.
- **Bacen (BCB)** — autarquia federal, órgão executivo/supervisor. Regulamenta detalhes operacionais (circulares, instruções normativas, resoluções BCB), autoriza o funcionamento de instituições financeiras e de pagamento, fiscaliza, controla a base monetária e opera os sistemas centrais de liquidação.
- **CVM (Comissão de Valores Mobiliários)** — supervisiona o mercado de capitais (ações, debêntures, fundos).
- **Susep** — seguros e capitalização. **Previc** — previdência complementar fechada.

Do ponto de vista de um dev de banco, o que importa é: **CMN decide a regra, Bacen constrói e fiscaliza a infraestrutura técnica que sua aplicação vai consumir** (APIs do PIX, STR, SCR, Open Finance, etc.).

```mermaid
graph TD
    subgraph Normativo["Órgão normativo"]
        CMN["CMN — Conselho Monetário Nacional<br/>(define políticas e regras)"]
    end

    subgraph Supervisores["Supervisores e executores"]
        BACEN["Bacen / BCB<br/>(moeda, crédito, câmbio, sistema de pagamentos)"]
        CVM["CVM<br/>(mercado de capitais)"]
        SUSEP["Susep<br/>(seguros)"]
        PREVIC["Previc<br/>(previdência fechada)"]
    end

    subgraph Operadores["Operadores (quem você provavelmente trabalha)"]
        BANCOS["Bancos múltiplos / comerciais"]
        COOP["Cooperativas de crédito"]
        IP["Instituições de Pagamento (IPs) /<br/>fintechs — NÃO são bancos"]
        CORRETORAS["Corretoras / distribuidoras"]
        SEGURADORAS["Seguradoras"]
    end

    CMN --> BACEN
    CMN --> CVM
    CMN --> SUSEP
    CMN --> PREVIC

    BACEN --> BANCOS
    BACEN --> COOP
    BACEN --> IP
    CVM --> CORRETORAS
    SUSEP --> SEGURADORAS
```

### Ponto de atenção técnico: Banco x Instituição de Pagamento (IP)

Uma diferença que aparece direto em specs de integração:

| | Banco (múltiplo/comercial) | Instituição de Pagamento (IP) |
|---|---|---|
| Capta depósito à vista? | Sim | Não — mantém conta de pagamento, com recursos de terceiros |
| Os recursos do cliente entram no balanço? | Sim (viram passivo e podem lastrear crédito) | Não — devem ficar **segregados** |
| Concede crédito com recursos captados? | Sim, é o modelo de negócio | Não com recursos de conta de pagamento |
| Participa do STR diretamente? | Normalmente sim | Frequentemente indireto, via **banco liquidante** (instituição com conta no Bacen que liquida em nome de terceiros) |
| Regulador | Bacen, sob regras do CMN | Bacen, regime específico de IP |

Isso importa porque, ao integrar um parceiro, você precisa saber se ele é **participante direto** do SPB (tem conta de Reservas Bancárias no Bacen) ou **indireto** (liquida através de um banco liquidante) — isso muda o desenho de conciliação e o tempo de liquidação.

---

## 8. O Bacen como orquestrador técnico

Antes de falar de PIX ou boleto, dois conceitos de infraestrutura aparecem em praticamente toda integração bancária:

### RSFN — Rede do Sistema Financeiro Nacional

É a rede privada que interliga o Bacen às instituições financeiras. Os serviços centrais (STR, SPI, DICT, SCR) **só respondem a máquinas conectadas a ela**, com certificados previamente registrados.

> **Analogia:** é uma VPN corporativa, não a internet pública. Você não "chama o endpoint do Bacen" de qualquer lugar com uma chave de API — o acesso pressupõe estar dentro da rede, e a identidade do participante é o certificado, não um token.

A segurança tem duas camadas que vale distinguir: **mTLS** (*mutual TLS*: além do servidor provar sua identidade ao cliente, como no HTTPS comum, o cliente também precisa apresentar certificado válido — as duas pontas se autenticam) e **HSM** (*Hardware Security Module*: um dispositivo físico dedicado que guarda chaves privadas e assina operações sem nunca expor a chave ao sistema operacional).

### ISPB — Identificador do Sistema de Pagamentos Brasileiro

Todo participante do SPB recebe um código numérico único de 8 dígitos, o **ISPB**. Ele **não substituiu** o antigo código COMPE de 3 dígitos (ex.: 001 = Banco do Brasil) — os dois **coexistem**: o COMPE segue em uso em arquivos e produtos legados como identificação do banco, e o ISPB é o identificador do participante no SPB. É o que aparece:

- no cabeçalho das mensagens de pagamento do PIX (o formato `pacs.008`, explicado no capítulo 10);
- nas chaves do DICT (associadas à conta e ao ISPB do PSP);
- nos arquivos CNAB, como código do banco.

Na prática, você vai precisar dos dois e de uma tabela de correspondência entre eles — o Bacen publica a relação oficial de participantes com ISPB e código COMPE. Nem toda instituição com ISPB tem código COMPE (é o caso de muitas IPs), o que costuma quebrar integrações que assumem que todo participante tem os dois.

---

## 9. Sistema de Pagamentos Brasileiro (SPB)

O SPB é o guarda-chuva que engloba **todas** as infraestruturas de mercado financeiro (IMFs) responsáveis por compensar e liquidar valores no Brasil — PIX, TED, boleto, cartões, títulos públicos, ações. Pense nele como "o barramento de liquidação nacional", com o Bacen operando a peça mais crítica: o **STR**.

- **STR (Sistema de Transferência de Reservas)** — operado pelo próprio Bacen. É onde ocorre a **Liquidação Bruta em Tempo Real (LBTR)**: cada transação é liquidada uma a uma, em moeda de banco central, sem esperar lote. É o "coração" do SPB — todo saldo final entre bancos passa por aqui, nas contas de Reservas Bancárias que cada banco mantém no Bacen.
- **CIP / Núclea** — câmara privada (associação sem fins lucrativos, hoje rebatizada "Núclea") responsável por **compensar** grande parte do varejo: boletos, cartões e TED. O SPI (motor do PIX) é operado pelo próprio Bacen — a Núclea atua no PIX prestando serviços de conectividade e tecnologia às instituições participantes, não operando o motor de liquidação. Compensação ≠ liquidação: a Núclea apura o líquido devido entre bancos; quem efetivamente move o dinheiro entre as contas de reserva é o STR.
- **SELIC** — sistema do Bacen para custódia e liquidação de **títulos públicos federais**.
- **B3** — liquida ações, derivativos e renda fixa privada.

```mermaid
graph LR
    subgraph Varejo["Camada de varejo (o que o cliente final vê)"]
        PIX_APP["App do banco: PIX"]
        BOLETO_APP["Emissão/pagamento de boleto"]
        TED_APP["TED"]
        CARTAO_APP["Cartão de crédito/débito"]
        INVEST_APP["Compra de ações<br/>e investimentos"]
    end

    subgraph Compensacao["Compensação (apuração do saldo líquido)"]
        SPI["SPI — Sistema de Pagamentos Instantâneos<br/>(Bacen, motor do PIX)"]
        NUCLEA["Núclea (ex-CIP)<br/>boletos, cartões, TED historicamente"]
        B3C["B3<br/>ações, derivativos, renda fixa privada"]
    end

    subgraph Liquidacao["Liquidação final (Bacen)"]
        STR["STR — Sistema de Transferência de Reservas<br/>LBTR: liquidação bruta em tempo real"]
        SELIC["SELIC — títulos públicos federais"]
    end

    RESERVAS["Contas de Reservas Bancárias<br/>(cada banco tem uma no Bacen)"]

    PIX_APP --> SPI
    BOLETO_APP --> NUCLEA
    TED_APP --> NUCLEA
    CARTAO_APP --> NUCLEA
    INVEST_APP --> B3C

    SPI --> STR
    NUCLEA --> STR
    B3C --> STR
    STR --> RESERVAS
    SELIC --> RESERVAS
```

**Para o dev:** se seu sistema faz uma TED ou processa boleto, ele está, em algum nível, gerando eventos que passam pela Núclea antes de virarem liquidação final no STR. Já o PIX tem um trilho próprio e mais direto — o **SPI** — que veremos a seguir.

---

## 10. PIX: arquitetura técnica em detalhe

Antes de tudo, um termo regulatório que aparece o tempo todo: **arranjo de pagamento** é o conjunto de regras que faz um meio de pagamento funcionar entre várias instituições — quem pode participar, como as mensagens trafegam, prazos, responsabilidades e resolução de disputas. Quem define essas regras é o **instituidor do arranjo**. No caso das bandeiras de cartão, o instituidor é a própria empresa privada (Visa, Mastercard); no caso do PIX, o instituidor é o **Bacen**. É essa diferença que explica por que o PIX tem regras uniformes e obrigatórias para todos.

O PIX, portanto, não é "mais um método de pagamento" no sentido de gateway comercial — é um **arranjo de pagamentos instantâneos criado e operado pelo Bacen**, com API pública especificada (repositórios `bacen/pix-api` e `bacen/pix-dict-api` no GitHub) e peças bem definidas:

- **SPI (Sistema de Pagamentos Instantâneos)** — motor de liquidação do Bacen. Roda 24/7/365, liquida em segundos, um a um (também é LBTR, como o STR, mas dedicado ao PIX). Regulado pela **Resolução BCB nº 195/2022**.
- **Conta PI (Conta Pagamentos Instantâneos)** — e aqui está a peça que quase todo material omite: o PIX **não liquida em Reservas Bancárias**. Cada participante direto do SPI mantém no Bacen uma **Conta PI** dedicada, e é entre Contas PI que os fundos se movem. Ela **nunca pode ficar negativa**, ou seja, é pré-financiada: o participante transfere recursos da conta de Reservas para a Conta PI, via STR, antes de precisar deles. O saldo é remunerado pela Selic.

  > **Analogia:** é crédito pré-pago de API. Você carrega saldo antes e vai consumindo; se zerar às 3h da manhã de domingo, as chamadas falham — não existe "fatura depois". Por isso a instituição precisa provisionar liquidez para a madrugada e o fim de semana, quando o STR está fechado.
- **DICT (Diretório de Identificadores de Contas Transacionais)** — o "diretório de chaves" do PIX.

  > **Analogia:** o DICT é o DNS do dinheiro. Você digita um nome amigável (a chave) e ele devolve o endereço real (instituição, agência e conta), do mesmo jeito que o DNS traduz um domínio em IP. E, como no DNS, existe cache com prazo de validade e a resolução acontece antes de a conexão ser aberta.

  Na prática, mapeia chave PIX (CPF/CNPJ, e-mail, telefone ou **EVP** — *Endereço Virtual de Pagamento*, a chave aleatória gerada pelo sistema, que não expõe nenhum dado pessoal do titular) → ISPB + agência + conta do PSP recebedor. É consultado pelo PSP do pagador antes de iniciar a transação.
- **PSP (Prestador de Serviço de Pagamento)** — qualquer instituição participante do arranjo PIX (banco, IP, cooperativa).
- **Mensageria ISO 20022** — as mensagens entre PSPs seguem o **ISO 20022**, padrão internacional que define um dicionário comum de mensagens financeiras em XML, adotado por sistemas de pagamento no mundo todo. Cada tipo de mensagem tem um código: `pacs` é a família de mensagens entre instituições (*payments clearing and settlement*), e `pacs.008` especificamente é a **ordem de transferência de crédito** — na prática, "pague este valor desta conta para aquela". Vale conhecer a nomenclatura porque ela aparece crua em log e em documentação de integração.
- **Autenticação mTLS** — toda comunicação com DICT/SPI exige certificado ISPB e assinatura digital das requisições (recomendação de HSM em produção).

### Fluxo de um PIX (chave → confirmação)

```mermaid
sequenceDiagram
    autonumber
    participant Pagador as App do Pagador
    participant PSP_A as PSP do Pagador (banco A)
    participant DICT as DICT (Bacen)
    participant SPI as SPI (Bacen)
    participant PSP_B as PSP do Recebedor (banco B)
    participant Recebedor as Conta do Recebedor

    Pagador->>PSP_A: Informa chave PIX + valor
    PSP_A->>DICT: Consulta chave (GET /entries/{chave})
    DICT-->>PSP_A: Retorna ISPB + conta do PSP_B
    PSP_A->>Pagador: Exibe nome do recebedor p/ confirmação
    Pagador->>PSP_A: Confirma pagamento
    PSP_A->>SPI: Envia ordem de pagamento (pacs.008)
    SPI->>SPI: Debita Conta PI do PSP_A / credita Conta PI do PSP_B
    SPI->>PSP_B: Notifica crédito recebido
    PSP_B->>Recebedor: Credita na conta em tempo real
    SPI-->>PSP_A: Confirmação de liquidação
    PSP_A-->>Pagador: "Pix enviado" (segundos)
```

**Por que isso importa para o dev:**

- **Gestão de caixa 24/7.** Como o PIX roda fora do horário do STR e a Conta PI não pode zerar, o participante precisa provisionar liquidez para noites, fins de semana e feriados. Existe até um **redesconto no SPI** (desde nov/2020) para necessidades de liquidez fora do horário regular do STR — nesse caso, com custo.
- **Falha por saldo insuficiente é um cenário real**, não teórico. Seu tratamento de erro precisa distinguir "recusado pelo recebedor" de "sem liquidez no participante".
- **Participante direto × indireto.** Só o direto tem Conta PI; o indireto liquida através de um direto. Isso muda o desenho de conciliação — você concilia contra quem liquida, não contra o Bacen.

> **Curiosidade que vale citar em code review:** a idempotência no PIX não é boa prática opcional — a regulamentação do Bacen **exige** que os participantes preparem seus sistemas para observar o princípio da idempotência.

### Cobranças (QR Code / `cob`, `cobv`)

Do lado do recebedor, a API PIX expõe endpoints padronizados como `/cob` (cobrança imediata) e `/cobv` (cobrança com vencimento), além de webhooks para notificação assíncrona de pagamento — é o que você provavelmente já implementou se gerou QR Codes PIX dinâmicos. A API segue **versionamento semântico** (major/minor/patch) e é a mesma especificação para qualquer PSP, o que padroniza a integração de gateways/ERPs com múltiplos bancos.

### Pix Automático (2025/2026)

Novidade regulatória recente: o **Pix Automático** é o equivalente ao débito automático, mas multibancos e padronizado pelo Bacen — o cliente autoriza uma vez, via Open Finance, e cobranças recorrentes fluem sem convênio bilateral entre empresa e banco.

---

## 11. TED, boleto e CNAB

### TED (Transferência Eletrônica Disponível)

Nasceu na reforma do SPB de 2002 para permitir liquidação no mesmo dia, em contraste com o DOC (que levava um dia útil). Tecnicamente, a ordem de TED trafega das instituições até a liquidação final no STR, dentro do horário de funcionamento dele.

**O DOC e a TEC** (Transferência Especial de Crédito, usada por empresas para pagamento de benefícios) **foram descontinuados como produtos de varejo em 2024** por decisão da Febraban: 15 de janeiro foi o último dia de emissão e agendamento, e 29 de fevereiro o encerramento definitivo. Restaram apenas usos residuais entre instituições financeiras. Na prática, se você encontrar suporte a DOC num fluxo de cliente em sistema legado, é código morto.

**A TED não foi extinta** e não há previsão nesse sentido — segue ativa e é o trilho preferencial para grandes valores, ainda que amplamente ofuscada pelo PIX no varejo.

### Boleto bancário + CNAB: o fluxo que você provavelmente já mexeu

O **CNAB** (padrão Febraban, "Centro Nacional de Automação Bancária") é o formato de arquivo de texto posicional usado para troca em lote entre empresa e banco: **remessa** (empresa → banco, ex.: registrar título) e **retorno** (banco → empresa, ex.: baixa de liquidação, rejeição).

Duas variantes convivem, e o número no nome é simplesmente **o tamanho de cada registro (linha) em caracteres**:

| | **CNAB 240** | **CNAB 400** |
|---|---|---|
| Tamanho do registro | 240 posições | 400 posições |
| Estrutura | Hierárquica: header de arquivo → header de lote → detalhes (**segmentos** P, Q, R… — cada segmento é um tipo de registro que carrega um conjunto específico de campos: P traz dados do título, Q traz dados do sacado, e assim por diante) → trailer de lote → trailer de arquivo | Plana: header → detalhes → trailer |
| Multiproduto | Sim, vários lotes/produtos no mesmo arquivo | Não, um produto por arquivo |
| Status | Padrão mais moderno | Legado, ainda muito usado em cobrança |

**Cuidado com a expectativa de portabilidade:** apesar de "padrão Febraban", cada banco publica seu próprio manual, com particularidades de campos e códigos de ocorrência.

> **Analogia:** é o "SQL padrão". Existe uma norma, todo mundo diz seguir, e mesmo assim seu código quebra ao trocar de banco de dados. CNAB é igual: uma família de dialetos, não um formato único. Parser sem configuração por instituição não sobrevive ao segundo banco.

Dois termos do dia a dia de boleto: a **linha digitável** é a sequência numérica impressa que o pagador pode digitar manualmente, e o **código de barras** é a mesma informação em forma legível por leitor óptico. Ambos codificam banco, moeda, valor, vencimento e o identificador do título — ou seja, o boleto carrega os próprios dados, e o banco confere se batem com o registro.

Desde as regras mais recentes do SFN, **todo boleto precisa ser registrado numa base centralizada operada pela Núclea** antes de ser apresentado ao pagador — isso elimina fraudes de boleto "não registrado" e permite pagamento em qualquer banco, não só no emissor.

```mermaid
sequenceDiagram
    autonumber
    participant ERP as Sistema da Empresa (ERP)
    participant BancoEmissor as Banco Emissor (registrador)
    participant Nuclea as Núclea (base centralizada de boletos)
    participant BancoPagador as Banco do Pagador
    participant Pagador as Cliente Pagador
    participant STR as STR (Bacen)

    ERP->>BancoEmissor: Arquivo de remessa CNAB (ou API) — registra título
    BancoEmissor->>Nuclea: Registra boleto na base centralizada
    Nuclea-->>BancoEmissor: Confirma registro
    BancoEmissor-->>ERP: Boleto disponível (linha digitável + PDF)
    ERP->>Pagador: Envia boleto ao cliente

    Pagador->>BancoPagador: Paga o boleto (qualquer banco/canal)
    BancoPagador->>Nuclea: Envia confirmação de pagamento
    Nuclea->>BancoEmissor: Repassa informação de compensação
    Nuclea->>STR: Solicita liquidação interbancária
    STR->>STR: Move saldo entre reservas dos dois bancos
    BancoEmissor->>ERP: Arquivo de retorno CNAB — baixa do título
    ERP->>ERP: Concilia e marca título como pago
```

**Nota prática:** compensação e liquidação são etapas **distintas** — a compensação confirma que o pagamento ocorreu e dispara a comunicação entre bancos; a liquidação é o momento em que o valor de fato muda de mãos entre as instituições no STR. Prazos recentes de mercado vêm migrando de D+1 para modelos D+0 em parte do fluxo, o que impacta diretamente sua régua de conciliação e baixa automática.

### Carteiras de cobrança: simples, caucionada e descontada

Aqui está um vocabulário que aparece como **campo obrigatório** em todo layout CNAB e que raramente é explicado: a **carteira de cobrança**.

Carteira é o contrato entre a empresa (o **cedente**, quem tem a receber) e o banco, definindo **qual papel o banco exerce sobre aqueles títulos**. E esse papel muda tudo — quem é o dono do crédito, quem assume o risco de calote e como o evento é contabilizado.

#### Cobrança simples

O banco é apenas **prestador de serviço**: registra os títulos, cobra, recebe e credita o valor na conta do cedente. Juridicamente atua como *mandatário* — um procurador do credor.

- O título continua pertencendo ao cedente do começo ao fim.
- Não há adiantamento de dinheiro nem operação de crédito.
- O risco de o sacado não pagar é **integralmente do cedente**.

> **Analogia:** o banco é um serviço de cobrança terceirizado, como um gateway que recebe por você. Ele processa e repassa, mas o recebível nunca deixa de ser seu — não há transferência de propriedade, só delegação de execução.

#### Cobrança vinculada

Aqui os títulos deixam de ser apenas objeto de cobrança e passam a **sustentar uma operação de crédito**: o cedente recebe dinheiro antes do vencimento. É um termo guarda-chuva com duas modalidades bem diferentes.

**Caucionada** — os títulos são dados em **garantia** (caução) de uma linha de crédito. O banco não vira dono deles; ele os mantém em custódia como lastro. Conforme os sacados pagam, o valor amortiza a dívida do cedente, ou a garantia é substituída por novos títulos, conforme o contrato.

**Descontada** — o cedente **endossa** os títulos ao banco, que antecipa o valor de face com **deságio** e passa a ser o credor. É a operação de desconto clássica.

> **Ponto que causa muito mal-entendido:** mesmo na descontada, o cedente normalmente **continua responsável** se o sacado não pagar — é a chamada coobrigação, ou direito de regresso. O banco cobra de volta. Ou seja, o risco não some com a antecipação; ele apenas fica adormecido até o vencimento.

| | **Simples** | **Caucionada** | **Descontada** |
|---|---|---|---|
| Cedente recebe antes do vencimento? | Não | Sim (como crédito) | Sim (valor de face com deságio) |
| Quem é o titular do crédito? | Cedente | Cedente (título em garantia) | Banco (por endosso) |
| Há operação de crédito? | Não | Sim | Sim |
| Quem assume o calote do sacado? | Cedente | Cedente | Cedente, por coobrigação |
| Papel do banco | Mandatário | Credor com garantia | Credor do título |

> **Analogia das três:** na simples, o banco é o **entregador** — leva e traz, a mercadoria é sua. Na caucionada, é o **penhor**: você deixa algo de valor como garantia enquanto pega dinheiro emprestado, e recupera quando quita. Na descontada, é uma **venda com direito de devolução**: você vende o recebível, recebe na hora, mas se o produto vier com defeito (o sacado não pagar) o comprador devolve para você.

#### Por que isso importa no seu código

A modalidade não é um rótulo cadastral — ela muda a modelagem:

- **A contabilização é diferente.** Na simples, o título permanece no ativo do cedente e o banco só transita valores. Na descontada, há transferência de titularidade e surge um passivo de coobrigação. Se o seu sistema gera lançamentos (capítulo 5), a carteira é insumo direto do mapeamento de contas.
- **"Baixa" significa coisas diferentes.** Na simples, o pagamento liquida o recebível e credita o cedente. Na caucionada, o pagamento **amortiza uma dívida**. Tratar os dois com o mesmo evento de baixa produz conciliação errada.
- **O risco precisa ser rastreado depois da antecipação.** Antecipou não é o mesmo que recebeu. Enquanto houver coobrigação, existe exposição em aberto.
- **O código da carteira varia por banco.** Não existe numeração nacional: a mesma "cobrança simples" pode ser carteira `109` num banco, `17` em outro e `06` num terceiro. É mais um caso do "dialeto CNAB" — mantenha esse mapeamento em configuração por instituição, nunca em constante no código.

#### Registrada × não registrada

Antigamente existia a **cobrança sem registro**, em que o banco só tomava conhecimento do título no momento em que alguém o pagava. Isso acabou: a Febraban extinguiu a modalidade e hoje **toda cobrança é registrada**, com o título previamente informado à base centralizada. Se você encontrar suporte a boleto sem registro num sistema legado, é resíduo.

```mermaid
graph TD
    T["Título a receber<br/>(duplicata, prestação de serviço)"] --> Q{"Qual carteira?"}

    Q -->|Simples| S["Banco cobra e repassa<br/>Cedente continua dono<br/>Sem adiantamento"]
    Q -->|Caucionada| C["Título vira garantia<br/>Cedente pega crédito<br/>Pagamento amortiza a dívida"]
    Q -->|Descontada| D["Título é endossado ao banco<br/>Cedente recebe com deságio<br/>Banco vira credor"]

    S --> P1["Sacado paga →<br/>crédito na conta do cedente"]
    C --> P2["Sacado paga →<br/>abate o saldo devedor"]
    D --> P3["Sacado paga →<br/>quita o crédito do banco"]

    P2 -.->|"Se o sacado não pagar"| R["Cedente responde<br/>(coobrigação / regresso)"]
    P3 -.->|"Se o sacado não pagar"| R

    style Q fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

#### O vocabulário operacional: instruções e ocorrências

Fechando o ciclo do CNAB, três termos que aparecem em todo manual de banco:

- **Espécie do título** — o que originou a cobrança: DM (duplicata mercantil), DS (duplicata de serviço), NP (nota promissória), entre outras. Vai em campo próprio do registro e tem efeito jurídico, sobretudo na hora de protestar.
- **Instrução** — o comando que o cedente envia na **remessa** para o banco agir sobre um título já registrado: pedir baixa, mandar protestar, sustar protesto, conceder abatimento, alterar vencimento, prorrogar prazo.
- **Código de ocorrência** — o que o banco responde no **retorno**: entrada confirmada, entrada rejeitada, liquidação, baixa, alteração aceita, título encaminhado a cartório. Cada código carrega ainda um **motivo**, que é o campo que de fato explica por que algo foi recusado.

> **Analogia:** remessa e retorno formam um protocolo assíncrono de comando e resposta, correlacionado por identificador de título — parecido com uma fila de comandos e um tópico de eventos de resultado. A diferença é que o *round-trip* leva horas, não milissegundos, e a ordem de chegada não é garantida. Por isso o processamento do retorno precisa ser idempotente e tolerante a eventos fora de ordem.

O ponto de atenção prático: **a rejeição costuma ser silenciosa para o negócio**. O título "foi enviado", o arquivo "foi aceito", mas um registro individual pode ter sido recusado. Sem tratamento explícito dos códigos de rejeição, a empresa acredita estar cobrando um título que o banco nunca registrou.

#### DDA

O **DDA (Débito Direto Autorizado)** é o serviço que entrega o boleto eletronicamente ao pagador, dentro do app do banco dele, dispensando o envio do documento impresso ou em PDF. Para quem emite, muda pouco no fluxo de registro — mas muda a expectativa do cliente, que passa a ver a cobrança aparecer sozinha, e reduz o espaço para fraude de boleto adulterado enviado por e-mail.

### CNAB x APIs modernas (Web Services bancários)

O mercado está migrando de **arquivo em lote (CNAB)** para **APIs em tempo real**.

O motivo principal é regulatório: regras como o ***split payment*** da Reforma Tributária — em que o tributo é separado e recolhido automaticamente no instante do pagamento, em vez de ser apurado pelo vendedor depois — exigem confirmação imediata. Um ciclo de remessa e retorno com corte diário simplesmente não entrega isso.

> **Analogia:** é a migração de *batch* noturno para evento em streaming. O arquivo continua existindo por inércia e por volume, mas tudo que exige resposta síncrona vai vazando para a API.

O CNAB não vai desaparecer no curto prazo. Mas isso explica por que os bancos vêm expondo APIs REST paralelas ao mesmo fluxo que antes só existia via arquivo.

---

## 11-A. CNAB 240 de pagamento: segmentos, estados e ocorrências

A seção anterior apresentou o CNAB pelo lado da **cobrança** — segmentos P, Q, R, título registrado, baixa por liquidação. O mesmo formato carrega um segundo produto, com regras próprias e um perfil de risco bem diferente: o **pagamento**. É o assunto desta seção.

> **Sobre as posições citadas:** os intervalos deste texto seguem o leiaute de referência da Febraban. Vale o de sempre — cada banco publica seu próprio manual e o manual do banco prevalece. Use as posições daqui para entender a *forma* do registro, nunca como fonte para implementar contra uma instituição específica.

---

### 11-A.1 A virada de sentido

Em cobrança, você **emite um título e espera receber**. A remessa é um pedido de registro, e o pior caso de um erro costuma ser retrabalho: o título não registra, você corrige e reenvia.

Em pagamento, você **manda o dinheiro sair**. A remessa é uma ordem de débito na conta do próprio cliente. O pior caso de um erro é dinheiro fora da casa, creditado na conta de um terceiro que não tem obrigação nenhuma de devolver.

| | **Cobrança** | **Pagamento** |
|---|---|---|
| Direção do dinheiro | Entra | Sai |
| A remessa é | Um pedido de registro | Uma ordem de débito |
| Contraparte | Sacado (deve ao cliente) | Favorecido (vai receber) |
| Erro típico | Título não registra | Crédito indevido a terceiro |
| Reversão | Baixa, cancelamento, novo título | Depende da boa vontade de quem recebeu |
| Segmentos do detalhe | P, Q, R | A, B, C, J, N, O, Z |

Essa assimetria explica praticamente todas as regras "chatas" que aparecem no produto: alçada e pré-aprovação antes do envio, bloqueio de saldo, idempotência levada a sério, e um ciclo de estados bem mais longo que o de cobrança.

> **Analogia:** cobrança é um `INSERT` que você pode repetir sem grande estrago, porque a chave natural te protege. Pagamento é um `DELETE` sem transação aberta: rodou, saiu, e o `ROLLBACK` depende de um sistema que não é seu.

---

### 11-A.2 Nível 1 — a anatomia do arquivo

O 240 é hierárquico. Cinco tipos de registro montam a estrutura, e o tipo fica na **posição 8** de toda linha:

```
Registro 0 ─ Header de Arquivo         (1 por arquivo)
   Registro 1 ─ Header de Lote         (1 por lote)
      Registro 3 ─ Detalhe             (N por lote — aqui moram os segmentos)
      Registro 3 ─ Detalhe
   Registro 5 ─ Trailer de Lote        (1 por lote)
   Registro 1 ─ Header de Lote         (outro lote, outro produto)
      ...
   Registro 5 ─ Trailer de Lote
Registro 9 ─ Trailer de Arquivo        (1 por arquivo)
```

Existem ainda os registros **2** e **4** (inicial e final de lote), opcionais e pouco usados.

Toda linha tem 240 caracteres, sem exceção, com preenchimento à direita com espaços em campos alfanuméricos e à esquerda com zeros em campos numéricos.

**A regra que organiza tudo:**

> Um lote agrupa pagamentos que compartilham **o mesmo tipo de serviço e a mesma forma de lançamento**.

Quase toda dúvida de "por que isso está em lotes separados?" se resolve com essa frase. Uma empresa que paga folha por crédito em conta, fornecedores por TED e ainda quita alguns boletos vai gerar **três lotes** no mesmo arquivo. Não é escolha do desenvolvedor, é consequência do leiaute.

---

### 11-A.3 Tipo de serviço e forma de lançamento

Ambos ficam no **header de lote** (registro 1) e são os dois campos que mais determinam o comportamento do arquivo.

**Tipo de serviço** (posições 10-11) — *o que* está sendo pago:

| Código | Serviço |
|---|---|
| `20` | Pagamento a fornecedor |
| `22` | Pagamento de contas e tributos |
| `30` | Pagamento de salários (folha) |
| `98` | Pagamentos diversos |

**Forma de lançamento** (posições 12-13) — *por qual trilho* o dinheiro anda:

| Código | Forma | Segmentos |
|---|---|---|
| `01` | Crédito em conta corrente | A + B |
| `05` | Crédito em conta poupança | A + B |
| `03` | DOC/TED (legado, exige câmara) | A + B |
| `41` | TED — outra titularidade | A + B |
| `43` | TED — mesma titularidade | A + B |
| `45` | **PIX Transferência** | A + B |
| `47` | **PIX QR Code** | A + B |
| `30` | Liquidação de título do próprio banco | J |
| `31` | Pagamento de título de outros bancos | J |
| `11` | Contas e tributos com código de barras | O |
| `16` / `17` / `18` | DARF Normal / GPS / DARF Simples | N |

**Nota prática:** repare que a forma de lançamento **determina o segmento**. Se você está escrevendo um gerador ou um parser, essa é a chave de despacho natural: leia o header de lote, resolva a forma, e só então saiba que tipo de detalhe esperar. Parser que tenta adivinhar o segmento lendo a posição 14 sem contexto do lote funciona, mas perde a validação cruzada que pega metade dos arquivos malformados.

> **No modelo do ASA:** o `Arquivo` guarda `LayoutBanco` e `LayoutTipoArquivo`, e a tabela `PagamentoParametroLayout` amarra `TipoPagamento` (FK para `TipoTransacao`) a um `LayoutIntegrado` por cliente. Ou seja, a forma de lançamento não está no código — está parametrizada por conta e por produto. É o desenho certo, e é também onde vão morar os bugs mais difíceis, porque um parâmetro errado gera um arquivo sintaticamente válido e semanticamente absurdo.

---

### 11-A.4 Nível 2 — os segmentos

#### O par A + B: dinheiro indo para uma conta

O **segmento A** é o registro principal de crédito em conta, DOC, TED e PIX. É onde estão favorecido, valor e data.

| Posições | Campo | Por que importa |
|---|---|---|
| 14 | Código do segmento (`A`) | Despacho do parser |
| 15 | Tipo de movimento | `0` inclusão, `5` alteração, `9` exclusão |
| 16-17 | Código de instrução | `00` inclusão, `99` exclusão |
| 18-20 | Câmara centralizadora | Obrigatória para forma `03`; alguns bancos exigem para TED |
| 21-23 | Banco do favorecido | |
| 24-29 | Agência + DV | |
| 30-42 | Conta + DVs | |
| 43-72 | Nome do favorecido | 30 posições, trunca sem avisar |
| **73-92** | **Nº do documento atribuído pela empresa** | **A chave de correlação. É o "seu número" do pagamento** |
| 93-100 | Data do pagamento | `DDMMAAAA` |
| 119-133 | Valor do pagamento | 15 posições, **2 decimais implícitas** |
| 134-148 | Nº do documento atribuído pelo banco | "Nosso número", vem preenchido no retorno |
| **149-156** | **Data real da efetivação** | Só no retorno |
| **157-171** | **Valor real da efetivação** | Só no retorno |
| **231-240** | **Códigos de ocorrência** | Ver 11-A.6 |

Três coisas aqui merecem atenção especial.

**A chave de correlação são as posições 73-92.** É o campo que você preenche na remessa e que o banco devolve intacto no retorno. Correlacionar por conta, valor e data funciona até o dia em que o cliente paga duas vezes o mesmo valor para o mesmo favorecido, o que acontece mais do que parece em folha e em aluguel. Use o documento da empresa e trate-o como identificador de verdade: único por cliente, imutável, gerado por você.

**Valor solicitado e valor efetivado são campos diferentes.** As posições 119-133 dizem quanto o cliente pediu; as 157-171 dizem quanto o banco efetivamente pagou. Eles divergem legitimamente em pagamento de título com desconto, multa ou juros calculados pelo banco na data. Sistema que sobrescreve o solicitado com o efetivado perde a informação de que houve divergência, e a conciliação do cliente vai perguntar exatamente por isso.

**As duas decimais são implícitas.** `000000000012345` são R$ 123,45. Isso vale para todo campo de valor do 240.

#### O segmento B: o complemento que virou o coração do PIX

Historicamente, o **B** era o registro chato de endereço do favorecido. Com a chegada das formas `45` e `47`, ele passou a carregar a informação mais importante da transação.

O B traz o **tipo/número de inscrição do favorecido** (CPF ou CNPJ), o endereço completo, os valores nominais (vencimento, desconto, abatimento, mora, multa) e, no caso de PIX, dois campos novos:

- **Forma de iniciação** — o domínio típico é `01` telefone, `02` e-mail, `03` CPF/CNPJ, `04` chave aleatória, `05` dados bancários.
- **Chave de pagamento** — a chave PIX propriamente dita, a URL do QR Code dinâmico, ou a chave de endereçamento do QR estático.

**Armadilha de tamanho:** o TXID de um QR dinâmico é limitado a **30 posições** dentro do CNAB, embora o padrão PIX admita mais. Se o seu sistema gera TXID longo, ele não cabe.

**Armadilha de posição:** os campos de PIX no segmento B são a parte **menos padronizada** do 240 inteiro. Foram acrescentados depois, cada banco escolheu um intervalo, e alguns publicaram versões diferentes em anos diferentes. Este é o ponto onde "parser sem configuração por instituição não sobrevive ao segundo banco" deixa de ser piada.

> **Nota de modelagem:** o par A+B é uma unidade atômica. Nunca separe um B do seu A entre lotes ou arquivos diferentes, e nunca gere um A de PIX sem o B correspondente. No `PixInfo` do ASA isso está resolvido de forma elegante: `ChaveTipo`, `ChavePixUrl` e `QrCodePix` moram na mesma linha que os dados do favorecido, então a atomicidade é garantida pelo modelo e não por disciplina do programador.

#### O segmento C

Complemento opcional do A, usado principalmente em folha. Carrega valores acessórios do lançamento — INSS, IR, FGTS, descontos, abatimentos — e uma segunda conta para casos específicos. Se você não faz folha, provavelmente nunca vai vê-lo.

#### O par J + J-52: pagamento de título

O **segmento J** é o registro de liquidação de boleto. O campo central são as **posições 18-61: o código de barras de 44 posições**. Diferente do A, aqui você não descreve o favorecido — o código de barras já contém banco, moeda, valor, vencimento e identificador do título.

| Posições | Campo |
|---|---|
| 14 | Código do segmento (`J`) |
| 18-61 | Código de barras (44) |
| 62-91 | Nome do beneficiário |
| 92-99 | Data de vencimento |
| 100-114 | Valor nominal do título |
| 115-129 | Desconto + abatimento |
| 130-144 | Mora + multa |
| 145-152 | Data do pagamento |
| 153-167 | Valor do pagamento |
| 183-202 | Nº do documento atribuído pela empresa |
| 231-240 | Códigos de ocorrência |

O **segmento J-52** é um J estendido, identificado por `52` nas posições 18-19, que informa **sacado, cedente e sacador avalista**. Ele existe porque a legislação de tributação e de comprovante exige saber quem pagou e quem recebeu, informação que o código de barras não carrega. Vários bancos o tornaram obrigatório.

> **No modelo do ASA:** o desenho mapeia bem. `Pix`, `Ted` e `Tef` são o par A+B; `Boleto` e `Tricon` são o par J + J-52. Note que `BoletoInfo` já tem `CodigoBarra`, `LinhaDigitavel`, `Sacado*` e `Sacador*` — exatamente o conteúdo do J mais o do J-52. E `TriconInfo` tem `NossoNumero` e `SacadorBancoIspb`, sinal de que ali entra título de terceiro com dados de liquidação bancária.

#### Os segmentos O, N e Z

- **O** — contas de concessionária e tributos **com** código de barras (forma `11`).
- **N** — tributos **sem** código de barras, com variantes por guia: N1 GPS, N2 DARF Normal, N3 DARF Simples, além de GARE, IPVA e licenciamento. Cada variante tem seu próprio conjunto de campos.
- **Z** — registro opcional de autenticação do pagamento, devolvido no retorno com o código de autenticação bancária. É o comprovante.

> **No modelo do ASA:** `CodigoAutenticacao VARCHAR(50)` no cabeçalho de todas as transações é justamente o destino do segmento Z. Vale conferir se o parser realmente o consome, porque é um segmento opcional e é comum ele ser ignorado até o dia em que um cliente pede comprovante.

---

### 11-A.5 Nível 3 — o ciclo de estados

Aqui está a diferença conceitual mais importante em relação à cobrança: **existem dois ciclos de vida rodando ao mesmo tempo**, e confundi-los é a origem de boa parte dos bugs de produto de pagamento.

- O **arquivo** tem estados: recebido, validado, rejeitado, processado.
- Cada **pagamento** dentro dele tem estados próprios: incluído, agendado, efetivado, rejeitado, devolvido.

Um arquivo aceito pode conter pagamentos rejeitados. Um arquivo rejeitado derruba todos os pagamentos de uma vez. E um lote pode ser recusado inteiro sem que o arquivo seja.

```mermaid
stateDiagram-v2
    direction LR
    [*] --> Recebido: arquivo chega

    state "Validação estrutural" as VAL
    Recebido --> VAL
    VAL --> ArquivoRejeitado: erro de leiaute/header
    VAL --> Aceito: estrutura ok

    state Aceito {
        [*] --> Incluido
        Incluido --> Rejeitado: crítica de campo\nou saldo insuficiente
        Incluido --> Agendado: data futura
        Incluido --> Processando: data hoje
        Agendado --> Processando: chega a data
        Agendado --> Cancelado: exclusão pelo cliente
        Processando --> Efetivado: crédito confirmado
        Processando --> Rejeitado: recusa do destino
        Efetivado --> Devolvido: devolução\n(MED, conta encerrada)
    }

    ArquivoRejeitado --> [*]
```

Três observações que valem mais que o diagrama:

**Agendado é um estado de primeira classe, não um detalhe.** Em cobrança quase não existe; em pagamento, ele domina o volume de folha e de fornecedor. Um pagamento agendado já consumiu validação, já pode ter reservado saldo, e ainda pode ser cancelado. Sistema que trata agendado como "ainda não aconteceu nada" erra o cálculo de saldo disponível do cliente.

**Rejeitado tem dois momentos muito diferentes.** Rejeição na entrada (campo inválido, conta inexistente) chega em minutos e é barata. Rejeição na efetivação (destino recusou, conta encerrada, chave PIX inválida) chega horas depois, quando o cliente já viu o pagamento como "aceito". Se o seu modelo tem um estado só, o cliente não entende o que aconteceu.

**Efetivado não é terminal.** Vale aqui a lição da seção 15: uma devolução pode chegar dias depois, por decisão de terceiro. Modele o caminho de volta desde o começo.

> **No modelo do ASA:** o enum de `dbo.Status` tem hoje `1 Incluído`, `2 Processando`, `3 Rejeitado`, `4 Cancelado`, `5 Erro`, `6 Finalizado`, `7 Pendente Pix Url`, `8 Processando Pix Url`. Comparando com o diagrama, faltam dois estados que o produto vai precisar mais cedo ou mais tarde: **Agendado** e **Devolvido**. Hoje um pagamento agendado provavelmente fica em `Processando` por dias, o que torna impossível distinguir "está na fila do banco" de "vai sair na semana que vem". E `Erro` versus `Rejeitado` merece um critério escrito: a leitura natural é que `Rejeitado` é recusa de negócio (com código de ocorrência) e `Erro` é falha técnica do próprio worker (sem código). Se essa distinção não estiver documentada, os dois viram sinônimos na prática.

---

### 11-A.6 Nível 4 — ocorrências

#### O campo

Nas posições **231-240** dos segmentos A e J ficam os códigos de ocorrência do retorno. São 10 posições que comportam **até cinco ocorrências de dois caracteres cada**.

Esse é o detalhe que derruba a maior parte dos parsers de primeira viagem:

```
231-240:  "AEAN      "
           ↑ ↑
           │ └── AN — tipo de conta do favorecido inválido
           └──── AE — tipo/nº de inscrição inválido
```

Duas rejeições, não uma. Um parser que lê `SUBSTRING(linha, 231, 2)` captura `AE` e joga `AN` fora, e aí o suporte passa a tarde explicando ao cliente uma rejeição pela metade.

#### Os códigos

Reprodução parcial da tabela de referência. **A tabela do banco prevalece** — vários bancos redefinem códigos e acrescentam os seus.

| Código | Significado |
|---|---|
| `00` | Crédito ou débito efetivado |
| `BD` | Inclusão efetuada com sucesso |
| `BE` | Alteração efetuada com sucesso |
| `BF` | Exclusão efetuada com sucesso |
| `AJ` | Rejeição CNAB — estrutura, obrigatoriedade ou domínio |
| `AE` | Tipo/número de inscrição inválido |
| `AL` | Banco favorecido inválido |
| `AN` | Conta do favorecido inválida |
| `AO` | Nome do favorecido não informado |
| `AR` | Valor do lançamento inválido |
| `BB` | Seu número inválido |
| `BG` | Agência/conta impedida legalmente |
| `HF` | Conta da empresa com saldo insuficiente |
| `HA` | Lote não aceito |
| `HI` | Arquivo não aceito |

E o bloco específico de PIX, que é recente e vale conhecer:

| Código | Significado |
|---|---|
| `PA` | PIX não efetivado (recusa negocial) |
| `PC` | Conta do recebedor inválida, inexistente, encerrada ou bloqueada |
| `PE` | Tipo de transação não autorizado na conta do recebedor |
| `PG` | CPF/CNPJ do recebedor incorreto |

Repare no prefixo como pista de escopo: `A*` e `B*` são críticas de registro, `H*` são problemas de lote ou arquivo, `P*` são específicos de PIX. Não é regra formal, mas ajuda a classificar código desconhecido.

#### Ocorrência não é status

Esta é a distinção que mais economiza dor de cabeça depois.

**Ocorrência é o que o banco disse.** É dado bruto, do dialeto daquela instituição, e deve ser persistido exatamente como veio — as dez posições inteiras, não a primeira.

**Status é o que o seu domínio concluiu.** É o resultado do seu mapeamento, é o que a interface mostra e o que a regra de negócio consulta.

Se você guardar só o status, perdeu a informação. No dia em que um cliente perguntar por que o pagamento dele foi recusado, ou no dia em que descobrir que estava mapeando `PC` para o status errado, o dado bruto é a única forma de reprocessar sem pedir o arquivo de novo ao banco.

> **No modelo do ASA:** `CodigoOcorrencia VARCHAR(10)` está dimensionado exatamente para o campo inteiro, o que sugere que quem desenhou sabia disso. Vale confirmar no worker se ele realmente grava as dez posições ou se grava só as duas primeiras. E como `DescricaoOcorrencia VARCHAR(100)` é uma única string, ela naturalmente comporta só uma descrição — para múltiplas ocorrências, a descrição precisa ser concatenada ou resolvida em tempo de leitura contra uma tabela de domínio por banco.

---

### 11-A.7 Como o cliente consome o retorno

Antes de decidir *como* gerar o arquivo, vale entender o que acontece com ele do outro lado. Boa parte das regras que parecem arbitrárias existe porque o consumidor é mais rígido do que o padrão.

#### Quem é o "cliente"

É a empresa pagadora, e do lado dela quase sempre existe um **ERP** ou um sistema de contas a pagar. Você não está integrando com uma pessoa, está integrando com um software que alguém configurou uma vez e ninguém mais quer mexer.

Quatro modos de consumo convivem no mesmo produto:

| Modo | Como funciona | O que isso significa para você |
|---|---|---|
| **Importação automática por ERP** | Rotina agendada varre um diretório, importa e baixa os títulos | O mais comum e o mais rígido. Parser fechado, sem tolerância, mensagem de erro genérica |
| **Rotina própria** | O cliente escreveu o próprio importador | Mais tolerante, mas cada cliente tem um bug diferente e você vira o suporte dele |
| **Conferência manual** | O financeiro abre o arquivo ou um relatório derivado | Muito mais comum do que a engenharia imagina, principalmente em cliente pequeno |
| **API/webhook em paralelo** | O cliente reage ao evento e usa o CNAB só para contabilidade e auditoria | O CNAB deixa de ser o canal e vira o registro. Mas continua tendo que fechar |

#### O ciclo do lado de lá

Independente do modo, o roteiro é sempre o mesmo:

1. **Recebe** o arquivo (SFTP, portal, API, e em casos legados ainda e-mail).
2. **Valida o cabeçalho** — banco, convênio, conta, NSA. Se não bate com o que está configurado, para aqui.
3. **Casa cada detalhe com o título interno**, pelo nº do documento atribuído pela empresa.
4. **Decide a ação por ocorrência** — baixa, mantém aberto, reabre para correção.
5. **Gera o lançamento contábil** — baixa de contas a pagar contra crédito em banco.
6. **Arquiva o arquivo bruto** para auditoria.

O passo 4 é o que importa, e ele é uma tabela de decisão:

| Ocorrência | O que o ERP faz | Consequência de errar |
|---|---|---|
| `00` | Baixa definitiva + lançamento contábil | Baixa indevida, título quitado que não foi pago |
| `BD` | Confirma agendamento, **mantém o título aberto** | Se o ERP tratar `BD` como `00`, o cliente acha que pagou e não pagou |
| `AJ`, `A*`, `H*` | Reabre o título e gera pendência para o financeiro | Título fica em limbo, ninguém paga o fornecedor |
| Devolução | Estorno — novo lançamento, não apagar o anterior | Contabilidade não fecha |

**A confusão entre `00` e `BD` é a mais cara da lista.** Um é "o dinheiro saiu", o outro é "está marcado para sair". Se o seu retorno emite `BD` num momento em que o cliente espera `00`, ou vice-versa, o efeito não é um erro de tela: é um fornecedor que não recebeu ou um título baixado sem pagamento.

#### As expectativas implícitas

Nada disso está no leiaute, e tudo isso derruba integração:

- **NSA único e crescente por cliente.** Repetição costuma ser rejeitada; furo na sequência costuma gerar alerta. Alguns ERPs simplesmente ignoram o arquivo em silêncio, que é o pior desfecho possível.
- **Nome de arquivo previsível.** Muito ERP varre diretório por máscara. Se o padrão do nome muda, a rotina não acha nada e ninguém percebe até o fechamento.
- **Arquivo atômico.** Se você escreve direto no destino, o ERP pode ler pela metade. Escreva com extensão temporária e renomeie no final — em SFTP o rename é atômico, o upload não.
- **Um arquivo por conta/convênio.** Misturar contas diferentes no mesmo arquivo quebra a validação de header.
- **Encoding e quebra de linha.** ASCII sem acento e `CRLF` continuam sendo exigência de muito importador.
- **Reprocessamento não é garantido.** ERP idempotente é minoria. Se você reenviar o mesmo arquivo, assuma que o cliente vai dar baixa duas vezes.

#### O que o cliente realmente pergunta

Ele nunca pergunta qual segmento ou qual posição. Ele pergunta **"por que o meu título não baixou?"**.

Para responder isso em minutos e não em horas, você precisa de um caminho reverso completo: do nº do documento que ele informou até o evento, a ocorrência bruta e o arquivo em que aquilo foi reportado. É por isso que guardar `CodigoOcorrencia` inteiro e manter `ControlePagamentoReportado` valem mais do que parecem — juntos, eles respondem "o que aconteceu" e "quando eu te contei".

**Nota prática:** a divergência de data é a origem número um de ticket. O cliente compara o retorno com o extrato bancário e vê datas diferentes, porque o extrato mostra a data real da efetivação e ele importou a data do pagamento solicitada. Levar as posições 149-156 e 157-171 a sério resolve a maior parte disso antes de virar chamado.

---

### 11-A.8 Nível 5 — retorno parcial e retorno consolidado

Primeiro, o mais importante: **isso não existe no leiaute Febraban**. Não há campo, flag ou tipo de registro que diga "este é um retorno parcial". É uma **convenção comercial** entre a instituição e o cliente. Se a regra parece confusa, muito provavelmente é porque ela é confusa mesmo, e não porque falta um manual.

#### Por que o parcial existe

Porque as formas de lançamento liquidam em ritmos diferentes. Uma remessa com três lotes — folha por crédito em conta, fornecedores por TED, boletos por J — vira naturalmente "lote 1 fechado hoje de manhã, lotes 2 e 3 pendentes". O parcial é o mecanismo de devolver feedback antes de tudo fechar.

Isso torna o **lote**, e não o arquivo, o recorte natural do parcial.

#### As três regras mecânicas

**1. Numeração é por arquivo emitido, sempre.** O número do lote começa em 1 e o sequencial de registro começa em 1 dentro de cada lote, no arquivo que você está gerando agora. Se você preservar a numeração da remessa original, vai emitir um arquivo que começa no lote 2 ou que tem buraco na sequência, e muitos ERPs rejeitam. A correlação com a remessa original é feita pelo **nº do documento atribuído pela empresa** (A: 73-92, J: 183-202), não pela numeração.

**2. Trailers refletem o arquivo emitido, nunca a remessa original.** Quantidade de lotes, quantidade de registros e somatória de valores contam o que está neste arquivo. É o bug clássico de retorno parcial e some em qualquer teste que compare trailer com conteúdo.

**3. Lote partido é normal.** Se um lote de 100 pagamentos teve 60 efetivados, emita o lote com 60 detalhes agora e os 40 restantes depois. O mesmo número de lote aparecendo em dois arquivos não é problema, porque a numeração é por arquivo. Só mantenha o par A+B (ou J + J-52) atômico.

#### O modelo de dados que sustenta os dois

O desenho que evita divergência é tratar parcial e consolidado como **dois recortes da mesma consulta**, saindo do mesmo emissor:

- **Parcial** = pagamentos cuja ocorrência mudou desde a última emissão para aquele cliente.
- **Consolidado** = todos os pagamentos da remessa, com a ocorrência atual.

Se cada tipo tiver seu próprio caminho de geração, eles vão divergir. Não é hipótese, é questão de tempo.

Para o parcial funcionar, você precisa de duas coisas: um **cursor por cliente** (até onde já reportei) e um **registro do que já foi reportado** (para não repetir).

> **No modelo do ASA:** as duas tabelas já existem, e é bom sinal.
>
> - `ControleJanelaRetorno (ClienteDocumento, UltimoInstanteReportado)` é exatamente o cursor. Cuidado com um detalhe: cursor por instante sofre com transações que gravam com timestamp anterior ao avanço do cursor e ficam invisíveis para sempre. Se o volume for alto, considere avançar o cursor para o menor instante ainda em processamento, e não para "agora".
> - `ControlePagamentoReportado (PagamentoID, CodigoStatus)` com PK composta é o registro do que já saiu. A PK sendo `(PagamentoID, CodigoStatus)` significa que o mesmo pagamento pode ser reportado várias vezes, uma por status — o que é exatamente o comportamento correto para um pagamento que vai de agendado a efetivado.
> - `SequencialArquivo (Documento, SequencialAtual)` é o NSA por cliente. O NSA no header de arquivo precisa ser único e crescente por cedente; muitos ERPs rejeitam repetição e alguns acusam furo na sequência. Incremente dentro da mesma transação que grava o arquivo, nunca antes.

#### A pergunta que precisa ser respondida por quem pediu

**O consolidado repete o que já saiu nos parciais?**

Se repete, o ERP do cliente recebe o mesmo movimento duas vezes e pode dar baixa em duplicidade. Nesse caso o consolidado tem que ser explicitamente um **arquivo de conferência**, com o cliente deduplicando pelo nº do documento, e isso precisa estar acordado por escrito.

Se não repete, o consolidado é **complementar**: só o que ainda não foi reportado, e a diferença entre ele e um parcial vira apenas o momento em que é gerado.

Não dá para inferir isso do leiaute. É decisão de produto, e enquanto ela não estiver escrita, qualquer implementação é chute.

**Nota final:** se o recorte vier vazio, **não gere o arquivo**. Um 240 com zero lotes é sintaticamente construível e quebra uma quantidade surpreendente de ERPs.

---

### 11-A.9 O fluxo ponta a ponta

Juntando tudo: da chegada da remessa até o consolidado do fechamento.

```mermaid
sequenceDiagram
    autonumber
    participant C as Cliente (ERP)
    participant G as Gateway de arquivo
    participant P as Processador
    participant T as Trilho (PIX/STR/interno)
    participant R as Emissor de retorno

    C->>G: Remessa CNAB 240 (SFTP/API)
    G->>G: Hash + persiste linhas brutas
    G->>P: Arquivo aceito
    P->>P: Valida leiaute e header
    P->>P: Explode em pagamentos (Incluído)
    P->>P: Crítica de negócio + autorização
    P->>T: Envia efetivação (por janela)
    T-->>P: Desfecho + autenticação
    P->>R: Eventos com ocorrência
    R->>C: Retorno parcial (ao longo do dia)
    T-->>P: Desfechos restantes
    R->>C: Retorno consolidado (fechamento)
```

#### Fase 1 — Ingestão

**O que entra:** o arquivo bruto, como veio.

Grave **antes de interpretar**. A linha do header e do trailer vão para `ArquivoCnabLinha`, os headers e trailers de cada lote para `LoteCnabLinha`, e o registro em `Arquivo` recebe `ClienteDocumento`, `AppID`, `CanalOrigem` e o hash de idempotência.

Isso parece burocracia até o primeiro arquivo que quebra o parser. Com as linhas brutas guardadas, você reprocessa; sem elas, precisa pedir o arquivo de volta ao cliente, que raramente ainda tem.

**Idempotência entra aqui, não depois.** Hash do conteúdo, não do nome. Cliente reenviando o mesmo arquivo é rotina, e o custo de processar duas vezes é pagamento em duplicidade.

**Estado:** o arquivo nasce recebido. Nenhum pagamento existe ainda.

#### Fase 2 — Validação estrutural

**O que se olha:** tamanho de linha, tipos de registro na ordem certa, banco, convênio, conta, NSA, coerência dos trailers.

Falha aqui derruba o arquivo inteiro. Registre em `ArquivoErro` com o máximo de contexto — `LayoutNumeroLinha`, `LayoutCampo`, `LayoutPosicao`, `LayoutConteudoEnviado`, `LayoutConteudoEsperadoId`. Esses campos existem no modelo justamente para que o suporte responda "linha 47, posição 119, esperado numérico, veio branco" em vez de "arquivo inválido".

**Colete todos os erros, não pare no primeiro.** Um cliente que corrige um erro por vez faz cinco rodadas de dois dias cada.

**Estado:** arquivo rejeitado, ou aceito.

#### Fase 3 — Explosão em pagamentos

Cada segmento vira uma linha de movimentação com seu `Info` correspondente:

| Forma de lançamento | Segmentos | Tabelas |
|---|---|---|
| `01`, `05` | A + B | `Tef` / `TefInfo` |
| `41`, `43`, `03` | A + B | `Ted` / `TedInfo` |
| `45`, `47` | A + B | `Pix` / `PixInfo` |
| `30`, `31` | J + J-52 | `Boleto` ou `Tricon` + `Info` |

Aqui você preenche `NumeroLote` (que lote da remessa originou aquele pagamento) e `IdentificadorExterno` (o nº do documento do cliente). Guarde também as linhas originais no campo `Linhas`, em JSON — é o que permite reemitir o retorno sem reconstruir o registro do zero.

**Estado:** `Incluído`.

#### Fase 4 — Crítica de negócio e autorização

Agora entram saldo, alçada, limites, validade do favorecido e chave PIX.

O que for rejeitado aqui **já ganha código de ocorrência**, e essa é a primeira leva que alimenta um retorno parcial. Rejeição de crítica é rápida e barata, e devolvê-la em minutos em vez de no fechamento é a diferença mais visível de qualidade percebida pelo cliente.

`PreAprovado` decide se o pagamento segue direto ou espera aprovação no canal.

**Estados:** `Rejeitado` (com ocorrência) ou segue.

#### Fase 5 — Efetivação

O pagamento vai para o trilho conforme a forma de lançamento, respeitando a janela: PIX 24/7, TED com horário, boleto com corte, agendado esperando a data.

É aqui que a distinção entre agendado e processando ganha valor prático. Um pagamento com data futura fica parado, consumindo saldo comprometido, e ainda pode ser cancelado.

**Estados:** `Agendado` → `Processando` → `Efetivado` ou `Rejeitado`.

#### Fase 6 — Captura do desfecho

Do trilho voltam quatro informações, e todas as quatro importam:

- **Código de ocorrência** (bruto, as dez posições)
- **Data real da efetivação**
- **Valor real da efetivação**
- **Código de autenticação**

Grave em `CodigoOcorrencia`, `CodigoAutenticacao` e nos campos de data e valor real. É esse conjunto que preenche o retorno.

#### Fase 7 — Emissão do retorno

**De onde vem cada campo:**

| Campo do retorno | Origem no modelo |
|---|---|
| Header — NSA | `SequencialArquivo.SequencialAtual + 1` |
| Header — conta/documento | `Arquivo.ClienteContaHeader`, `ClienteDocumento` |
| Header de lote — serviço/forma | `PagamentoParametroLayout` |
| A 73-92 / J 183-202 — seu número | `<Tipo>Info.IdentificadorExterno` |
| A 134-148 — nosso número | ID interno ou identificador do trilho |
| A 93-100 — data do pagamento | `<Tipo>Info.DataTransacao` |
| A 119-133 — valor | `<Tipo>Info.ValorPagamento` |
| A 149-156 — data real | data do desfecho |
| A 157-171 — valor real | valor efetivado |
| A/J 231-240 — ocorrências | `<Tipo>.CodigoOcorrencia` |
| Segmento Z — autenticação | `<Tipo>.CodigoAutenticacao` |
| Trailers | Contagem e soma **do arquivo emitido** |

**O recorte:**

- **Parcial** — pagamentos do cliente cujo par `(PagamentoID, CodigoStatus)` ainda não existe em `ControlePagamentoReportado`, com `DataAtualizacao` posterior ao cursor.
- **Consolidado** — todos os pagamentos da remessa, com a ocorrência atual.

Mesma consulta, filtro diferente. Mesmo emissor.

#### Fase 8 — Publicação

Disponibiliza no SFTP ou expõe pela API, com o nome de arquivo no padrão que o cliente configurou.

**A ordem das operações importa muito aqui.** Duas falhas possíveis, e você escolhe qual:

- Publicar antes de commitar: se a transação falhar, o cliente tem um arquivo que o seu sistema não sabe que existe. Reconciliação manual, NSA duplicado depois.
- Commitar antes de publicar: se o upload falhar, você acha que enviou e não enviou. Detectável, reexecutável.

**Escolha a segunda.** A sequência correta é: gera o conteúdo → transação (incrementa NSA, grava `Arquivo` e linhas, insere em `ControlePagamentoReportado`, avança `ControleJanelaRetorno`) → commit → publica → marca como publicado.

Consumir o NSA fora da transação é um erro comum e caro: se a geração falhar depois, o sequencial já foi queimado e o próximo arquivo sai com furo.

#### Fase 9 — Depois

Devolução, cancelamento tardio e reprocessamento. Cada um gera um novo evento, com nova ocorrência, que entra no próximo parcial. Nunca sobrescreva o evento anterior.

---

#### Velocidade: onde o tempo realmente vai

Uma intuição errada comum é achar que o gargalo é montar as linhas de 240 caracteres. Não é — isso é concatenação de string, roda em microssegundos. O tempo vai em três lugares:

**1. A consulta que decide o recorte.** É o gargalo real, e piora com o volume. No modelo do ASA são cinco tabelas de movimentação, então o recorte é um `UNION ALL` de cinco consultas, cada uma com anti-join contra `ControlePagamentoReportado`.

Os índices atuais (`IX_Pix_ArquivoID` e similares) atendem a busca por arquivo, que é a do consolidado. Falta o índice do parcial, que busca por cliente e status:

```sql
CREATE INDEX IX_Pix_Cliente_Status ON dbo.Pix
    (ClienteDocumento, CodigoStatus, DataAtualizacao)
    INCLUDE (PixID, ArquivoID, NumeroLote, CodigoOcorrencia);
```

O `INCLUDE` faz o índice cobrir a consulta e evita o lookup na tabela base. Repita para as cinco. Se o parcial roda de hora em hora e o volume é alto, esse índice é a diferença entre segundos e minutos.

**2. O anti-join.** `NOT EXISTS` contra a PK `(PagamentoID, CodigoStatus)` é a forma certa — a PK já é o índice de que você precisa. `LEFT JOIN ... WHERE IS NULL` produz plano pior em volume alto no SQL Server.

**3. A gravação do que foi reportado.** Inserir linha a linha em `ControlePagamentoReportado` é o clássico N+1 que transforma dez segundos em dez minutos. Use inserção em massa: table-valued parameter, `MERGE` com TVP, ou `SqlBulkCopy` para volume grande.

**Outras alavancas, em ordem de retorno:**

- **Gere em stream, não em memória.** Escreva linha a linha para o `Stream` de saída. Um arquivo de 100 mil pagamentos são 24 MB de texto; materializar tudo antes de escrever é desperdício sem ganho.
- **Paralelize por cliente, nunca dentro do cliente.** O NSA é sequencial por cliente, então dois geradores para o mesmo cliente disputam o mesmo contador. Entre clientes não há contenção nenhuma.
- **Escrita atômica sempre.** `arquivo.tmp` e rename ao final. Barato e elimina uma classe inteira de bug.
- **Cadência combinada, não máxima.** Se o ERP do cliente importa de hora em hora, gerar parcial a cada cinco minutos só multiplica arquivo e chance de erro sem entregar nada. A frequência certa é a que o consumidor consegue absorver.
- **Consolidado fora do pico.** Ele varre a remessa inteira e é naturalmente o mais pesado. Fechamento noturno resolve.
- **Geração retomável.** Se o processo cair no meio, a próxima execução tem que produzir o mesmo resultado. Isso vem de graça com a ordem de operações da fase 8: nada foi marcado como reportado, então o recorte volta idêntico.

---

### 11-A.10 Armadilhas de implementação

**Dinheiro em inteiro, sempre.** Vale a seção 6 inteira, com um agravante: o 240 já entrega o valor como inteiro de centavos com decimais implícitas. Converter para decimal na leitura e voltar para inteiro na escrita é introduzir dois pontos de erro onde havia zero.

**A linha tem exatamente 240 caracteres.** Não 239, não 241. Truncamento silencioso em nome de favorecido (30 posições) e observação são a causa mais comum de "o arquivo passou no meu teste e o banco rejeitou".

**Encoding e quebra de linha são parte do contrato.** Muitos bancos ainda exigem ASCII sem acento e `CRLF`. Nome com cedilha ou til vira caractere inválido e o arquivo inteiro cai.

**Idempotência precisa de hash de conteúdo, não só de identificador.** Reprocessar o mesmo arquivo de retorno não pode duplicar lançamento. As tabelas `*Idempotencia (HashID)` e `*RetornoIdempotencia (RetornoID, Data)` do ASA cobrem os dois lados — envio e retorno — e a PK composta com `Data` no retorno é o que permite o mesmo pagamento ter eventos em dias diferentes sem colidir.

**Retorno chega fora de ordem.** É a mesma observação da seção 11 sobre protocolo assíncrono, e em pagamento ela é mais grave: uma efetivação que chega antes do agendamento não pode fazer o pagamento voltar para agendado. Guarde a ordem por data do evento, não por ordem de chegada do arquivo, e trate transição inválida como alerta, não como exceção fatal.

**Saldo é problema de arquitetura, não de validação.** Entre a inclusão e a efetivação existe uma janela em que o dinheiro está comprometido mas não saiu. Se o saldo disponível não descontar os agendados, o cliente gasta o mesmo dinheiro duas vezes. É o mesmo erro de `disponível = aprovado − utilizado` da seção 13.2, e a correção é a mesma: falta subtrair o reservado.

---

### Checkpoint

1. Por que uma empresa que paga folha, fornecedores por TED e boletos gera três lotes, e não um?
2. Qual campo do segmento A você deve usar para correlacionar remessa e retorno, e por que não conta, valor e data?
3. Por que o segmento A tem dois campos de valor?
4. O que muda no segmento B quando a forma de lançamento é `45` ou `47`?
5. Um arquivo foi aceito. Isso significa que os pagamentos dele foram aceitos?
6. Por que "agendado" precisa ser um estado próprio no modelo?
7. Quantas ocorrências cabem nas posições 231-240, e o que acontece com quem lê só as duas primeiras?
8. Por que persistir `CodigoOcorrencia` bruto se você já derivou o status?
9. Num retorno parcial, o trailer de arquivo conta os registros de quê?
10. Por que a numeração de lote não deve ser herdada da remessa original?
11. Qual pergunta precisa ser respondida antes de implementar o consolidado?
12. Por que confundir `00` com `BD` é o erro mais caro do lado do cliente?
13. Por que escrever o arquivo direto no destino do SFTP é arriscado?
14. Qual é a causa mais comum de ticket sobre divergência entre retorno e extrato?
15. Por que gravar as linhas brutas na ingestão, antes de interpretar?
16. Qual a ordem correta entre commitar a transação e publicar o arquivo, e por quê?
17. Por que o NSA deve ser incrementado dentro da transação de geração?
18. Onde está o gargalo real da geração de um retorno parcial em volume alto?
19. Por que paralelizar a geração dentro de um mesmo cliente é má ideia?

*Respostas: (1) porque o lote agrupa por tipo de serviço e forma de lançamento, e as três são combinações diferentes; (2) o nº do documento atribuído pela empresa, posições 73-92, porque conta, valor e data colidem quando o mesmo valor é pago duas vezes ao mesmo favorecido, o que é rotina em folha e aluguel; (3) porque o valor solicitado e o valor efetivamente pago divergem legitimamente quando o banco calcula desconto, mora ou multa na data; (4) ele passa a carregar a forma de iniciação e a chave PIX ou URL do QR Code, e vira a parte menos padronizada do leiaute entre bancos; (5) não — a validação estrutural do arquivo é independente da crítica de cada registro, e um arquivo aceito pode conter pagamentos rejeitados; (6) porque um agendado já consumiu validação e pode ter comprometido saldo, mas ainda é cancelável, então tratá-lo como "nada aconteceu" faz o saldo disponível ficar errado; (7) até cinco, de dois caracteres cada, e quem lê só as duas primeiras descarta rejeições e explica ao cliente uma recusa pela metade; (8) porque o mapeamento pode estar errado e o dado bruto é a única forma de reprocessar sem pedir o arquivo de novo ao banco; (9) do próprio arquivo emitido, nunca da remessa original; (10) porque a numeração é por arquivo e herdar gera arquivo começando fora do 1 ou com furo na sequência, o que muitos ERPs rejeitam; (11) se o consolidado repete o que já saiu nos parciais, porque disso depende o cliente dar ou não baixa em duplicidade; (12) porque `00` significa que o dinheiro saiu e `BD` que está apenas marcado para sair, e trocar um pelo outro resulta em fornecedor não pago ou título baixado sem pagamento; (13) porque o ERP pode ler o arquivo pela metade — escreva com extensão temporária e renomeie, já que o rename é atômico e o upload não; (14) datas diferentes, porque o extrato mostra a data real da efetivação e o cliente importou a data solicitada; (15) porque sem elas um arquivo que quebra o parser só pode ser reprocessado se o cliente ainda tiver o original, o que raramente acontece; (16) commitar primeiro e publicar depois, porque a falha resultante (achar que enviou sem ter enviado) é detectável e reexecutável, enquanto a inversa deixa o cliente com um arquivo que o seu sistema desconhece; (17) porque consumir o sequencial fora dela queima o número se a geração falhar, e o próximo arquivo sai com furo na sequência; (18) na consulta que decide o recorte, não na montagem das linhas — daí a necessidade de índice por cliente e status, com colunas cobertas; (19) porque o NSA é sequencial por cliente e dois geradores simultâneos disputam o mesmo contador, enquanto entre clientes distintos não há contenção alguma.*

---

## 12. Cartões: o arranjo de quatro partes

Cartão aparecia nos diagramas anteriores sem explicação — e é o trilho com a estrutura de participantes mais complexa do SFN. Vale entender porque quase toda fintech de pagamentos toca nele em algum ponto.

### Os cinco papéis

| Papel | Quem é | Função |
|---|---|---|
| **Portador** | Cliente final | Usa o cartão |
| **Emissor (issuer)** | Banco/IP que emitiu o cartão | Concede o limite, assume o risco de crédito, cobra a fatura |
| **Estabelecimento (EC)** | Loja/prestador | Vende e recebe |
| **Credenciadora (acquirer)** | Cielo, Rede, Stone, PagSeguro | Afilia o EC, captura a transação, paga o lojista |
| **Bandeira (scheme)** | Visa, Mastercard, Elo | Define regras do arranjo e roteia mensagens entre emissor e credenciadora |

O nome "quatro partes" vem do modelo clássico (portador, emissor, credenciadora, EC), com a bandeira operando o trilho entre emissor e credenciadora. Existe também o modelo de **três partes**, em que a mesma empresa é emissora e credenciadora (caso histórico do American Express).

### Autorização ≠ captura ≠ liquidação

Essa é a distinção que mais gera bug em integração de checkout.

> **Analogia:** é *two-phase commit*. A autorização é o *prepare* — reserva o recurso e garante que ele está disponível, sem efetivar nada. A captura é o *commit* — confirma que a venda aconteceu. E a liquidação é a replicação chegando ao destino, dias depois. Cada fase pode falhar de um jeito diferente, e tratar as três como um evento único é a origem de boa parte dos bugs de conciliação.

- **Autorização** — verificação em tempo real (segundos) de que existe limite e o cartão é válido. **Reserva** o valor, não move dinheiro.
- **Captura (confirmação)** — o EC confirma que a venda se concretizou. Pode ser imediata (varejo) ou posterior (hotelaria, e-commerce que só captura no envio).
- **Liquidação** — o dinheiro efetivamente chega ao lojista, tipicamente em **D+30** para crédito à vista (ou D+1/D+2 para débito), e é aí que entra a antecipação de recebíveis.

```mermaid
sequenceDiagram
    autonumber
    participant Portador
    participant EC as Estabelecimento
    participant Cred as Credenciadora
    participant Band as Bandeira
    participant Emissor as Banco Emissor

    Portador->>EC: Paga com cartão
    EC->>Cred: Envia transação
    Cred->>Band: Roteia autorização
    Band->>Emissor: Solicita autorização
    Emissor->>Emissor: Valida limite, antifraude, senha/token
    Emissor-->>Band: Aprova ou nega
    Band-->>Cred: Retorna resposta
    Cred-->>EC: Aprovado (segundos)
    EC->>Portador: Entrega produto

    Note over Cred,Emissor: Depois — ciclo de compensação
    Cred->>Band: Envia lote de capturas
    Band->>Emissor: Compensa valores
    Emissor->>Cred: Paga (menos taxas)
    Cred->>EC: Paga lojista em D+30 (crédito à vista)
    Emissor->>Portador: Cobra na fatura
```

### MDR, intercâmbio e antecipação

- **MDR (Merchant Discount Rate)** — taxa total que o lojista paga sobre a venda.
- **Tarifa de intercâmbio** — a maior fatia do MDR, repassada da credenciadora ao **emissor**. É o que remunera o banco emissor e financia os programas de pontos.
- **Antecipação de recebíveis de cartão** — o lojista vende o direito de receber em D+30 para receber hoje, com deságio. Esses recebíveis são registrados em **registradoras** (as mesmas do capítulo 13), o que permite usá-los como garantia sem risco de duplicidade.

### Chargeback

O **chargeback** é a contestação de uma compra pelo portador junto ao **emissor**. Se procedente, o valor é revertido — e, em regra, quem absorve a perda é o **lojista** (via credenciadora), não o banco. Prazos e regras são definidos pelas bandeiras, não pelo Bacen.

> Para o dev: chargeback significa que uma transação **aprovada e liquidada** ainda pode ser revertida meses depois. Se sua modelagem trata "pago" como estado terminal, o chargeback quebra seu modelo. O mesmo raciocínio vale para o MED do PIX (capítulo 15).

---

## 13. Área de crédito

Aqui muda o eixo: pagamento é sobre **mover dinheiro**; crédito é sobre **registrar obrigação e risco**. A peça central do lado do Bacen é o **SCR**.

### SCR — Sistema de Informações de Crédito

- Banco de dados do Bacen que centraliza **todas as operações de crédito** (empréstimos, financiamentos, avais, fianças, limites concedidos) de pessoas físicas e jurídicas, reportadas mensalmente pelas instituições financeiras.
- Regulado pela **Resolução CMN nº 4.571/2017**, administrado pelo BCB.
- Threshold histórico: instituições reportam operações com responsabilidade igual ou superior a **R$ 200,00**.
- Serve dois papéis: (1) **supervisão bancária** — Bacen monitora concentração de risco e inadimplência sistêmica; (2) **consulta entre instituições** — mediante autorização do cliente, um banco pode consultar o histórico de crédito de um tomador antes de aprovar uma nova operação.
- Cliente pessoa física/jurídica acessa seu próprio extrato via **Registrato** (conta gov.br nível prata/ouro), com histórico dos últimos 5 anos.
- Diferença importante de negócio: o SCR **não é um "SPC/Serasa"** — ele não julga bom/mau pagador, apenas registra o fato da operação e seu status; mas, na prática, funciona como insumo de motor de decisão de crédito em qualquer instituição.

```mermaid
graph TD
    IF1["Instituição financeira A<br/>(concede empréstimo)"] -->|"Envia mensalmente<br/>saldo contábil das operações"| SCR["SCR — Bacen<br/>(base centralizada de crédito)"]
    IF2["Instituição financeira B"] -->|Reporte mensal| SCR
    IF3["Cooperativa de crédito"] -->|Reporte mensal| SCR

    SCR -->|"Supervisão de risco sistêmico"| BACEN_SUP["Supervisão Bancária do Bacen"]
    SCR -->|"Consulta (com autorização do cliente)"| IF4["Outra instituição avaliando<br/>novo crédito para o mesmo cliente"]
    SCR -->|"Extrato pessoal (Registrato)"| CLIENTE["Cliente PF/PJ"]
```

### Correspondentes bancários e registradoras

- **Correspondentes bancários** — empresas contratadas por instituições financeiras para originar operações (ex.: lojas, fintechs de crédito) em nome do banco, sob regulação do Bacen (Resolução CMN específica). O correspondente não é o credor; ele atua como canal.
- **Registradoras de recebíveis** (Núclea, B3, CERC, entre outras) — plataformas onde duplicatas, recebíveis de cartão e outros direitos creditórios são registrados centralmente, permitindo rastreabilidade e uso como garantia (antecipação de recebíveis, garantia de operações de crédito). A **liquidação centralizada de recebíveis** (Resolução BCB nº 150 e correlatas) reduziu risco de duplicidade de garantia — um problema real que existia quando o mesmo recebível podia ser oferecido como garantia em mais de um banco.
- **CVM/B3** entra quando o crédito vira **instrumento negociável** — ou seja, deixa de ser um contrato entre duas partes e passa a ser um papel que investidores compram e vendem. Aí o crédito sai do universo Bacen puro e entra em mercado de capitais. Os principais formatos:
  - **Debênture** — título de dívida emitido por uma empresa para captar diretamente com investidores, sem passar por banco.
  - **CRI / CRA** (Certificado de Recebíveis Imobiliários / do Agronegócio) — papéis lastreados em recebíveis daqueles setores.
  - **FIDC** (Fundo de Investimento em Direitos Creditórios) — fundo que compra carteiras de recebíveis (duplicatas, parcelas de empréstimo) e as transforma em cotas para investidores. É a estrutura por trás de boa parte da originação de crédito das fintechs.

### 13.1 Ciclo de vida de uma operação de crédito

Se você vai trabalhar em crédito, este é **o** modelo mental central. Toda operação percorre as mesmas etapas, e cada uma vira um conjunto de serviços, estados e eventos no seu sistema.

```mermaid
graph TD
    A["1. Originação<br/>proposta entra"] --> B["2. Análise<br/>score, políticas, SCR"]
    B --> C{"3. Decisão"}
    C -->|Aprovado| D["4. Formalização<br/>contrato, assinatura"]
    C -->|Negado| X["Recusa<br/>(motivo registrado)"]
    D --> E["5. Desembolso<br/>dinheiro sai"]
    E --> F["6. Gestão da carteira<br/>parcelas, cobrança"]
    F -->|Pagou tudo| G["7. Quitação"]
    F -->|Atrasou| H["8. Inadimplência<br/>régua de cobrança"]
    H --> I["9. Provisão<br/>reconhece perda esperada"]
    I --> J["10. Write-off<br/>baixa contábil"]
    J --> K["11. Recuperação<br/>ou venda da carteira"]

    style C fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

**O que cada etapa significa tecnicamente:**

1. **Originação** — de onde vem a proposta: canal próprio, correspondente bancário, marketplace, API de parceiro.
2. **Análise** — é onde vive a maior parte da complexidade de regra de negócio. O **motor de crédito** combina quatro insumos:
   - **Score** — nota estatística (0 a 1000, nas escalas mais comuns) que estima a chance de o tomador pagar em dia. Pode ser calculada internamente ou comprada de **bureaus de crédito**: empresas como Serasa, Boa Vista e Quod, que agregam o histórico de pagamento do mercado inteiro.
   - **SCR** — o histórico de dívidas do tomador em outras instituições.
   - **Política de crédito** — as regras próprias da instituição sobre a quem emprestar, quanto e a que taxa.
   - **Compliance e verificação de renda** — as checagens do capítulo 14.
3. **Decisão** — aprovar, negar ou aprovar com condições (valor menor, taxa maior, exigência de garantia). Toda decisão precisa ser **auditável e explicável** — o cliente tem direito ao motivo da recusa.
4. **Formalização** — contrato (CCB — Cédula de Crédito Bancário, na maioria dos casos), assinatura eletrônica, registro de garantias.
5. **Desembolso** — o dinheiro efetivamente sai, hoje quase sempre via PIX ou TED.
6. **Gestão** — geração do carnê/parcelas, cobrança (boleto, débito automático, consignação), tratamento de pagamento antecipado (o cliente tem **direito legal a desconto proporcional dos juros** ao antecipar).
7. **Inadimplência** — a **régua de cobrança**: sequência escalonada de ações por faixa de atraso (lembrete → notificação → negativação → protesto → cobrança judicial). Dois desses termos merecem definição:
   - **Negativação** — inclusão do nome do devedor nos cadastros de inadimplentes dos bureaus de crédito, o que restringe seu acesso a crédito no mercado inteiro.
   - **Protesto** — registro formal da dívida em cartório, que dá publicidade legal à inadimplência e reforça o título como prova para execução judicial.
8. **Provisão, write-off e recuperação** — detalhados em 12.3.

> **Armadilha de modelagem:** o estado da *operação de crédito* e o estado da *cobrança* são coisas diferentes e evoluem em ritmos diferentes. Colapsar os dois num único enum é uma das refatorações mais dolorosas que se faz nesse domínio.

### 13.2 Limites

**Limite** é o teto que a instituição estabelece para algo — quanto um cliente pode dever, quanto pode transferir, quanto a própria instituição pode arriscar. Parecem regras arbitrárias quando chegam no seu backlog, mas cada família de limite existe por um motivo diferente.

| Família | Protege contra | Quem define |
|---|---|---|
| **Limite de crédito** | Inadimplência do cliente | A instituição, por política própria |
| **Limite transacional** | Fraude e golpe | Regulação + instituição + o próprio cliente |
| **Limite prudencial** | Concentração de risco e quebra sistêmica | O regulador (CMN/Bacen) |

#### Limite de crédito

É o valor máximo que o cliente pode dever à instituição. Duas variações que mudam completamente a modelagem:

- **Limite rotativo** — o teto se **recompõe** conforme o cliente paga. É o caso do cartão de crédito e do cheque especial: usou R$ 3.000 de R$ 5.000, pagou R$ 1.000, volta a ter R$ 3.000 disponíveis.
- **Limite não rotativo (pontual)** — aprovado e consumido uma vez só, como num empréstimo pessoal. Pagar não devolve disponibilidade.

Existe ainda o **limite global** (ou compartilhado): um teto único que vários produtos consomem — cartão, cheque especial e crédito pessoal dividindo a mesma exposição aprovada. É a fonte clássica de bug de "o cliente tem limite em dois lugares e usou os dois".

#### A conta que todo sistema de limite precisa acertar

```
disponível = aprovado − utilizado − reservado
```

O termo **reservado** é o que separa uma implementação correta de uma que perde dinheiro. Entre a autorização de uma compra e a sua liquidação existe um intervalo em que o valor **não foi debitado, mas também não está mais disponível**. Se você calcular disponibilidade apenas com o que já foi efetivado, o cliente gasta duas vezes o mesmo limite.

> **Analogia:** é exatamente o problema de reserva de estoque num e-commerce. Entre colocar no carrinho e concluir o pagamento, a unidade precisa sair da disponibilidade sem ainda ter saído do inventário. Quem só decrementa o estoque na confirmação vende o mesmo produto duas vezes — e quem só debita limite na liquidação empresta duas vezes o mesmo dinheiro.

```mermaid
stateDiagram-v2
    [*] --> Disponivel: limite aprovado
    Disponivel --> Reservado: autorização<br/>(valor bloqueado)
    Reservado --> Utilizado: captura/liquidação
    Reservado --> Disponivel: expiração ou cancelamento
    Utilizado --> Disponivel: pagamento (só se rotativo)
    Utilizado --> [*]: quitação (se pontual)
```

Consequências práticas de engenharia:

- **Concorrência é o risco principal.** Duas compras simultâneas que cabem sozinhas mas não cabem juntas precisam ser serializadas. Leitura seguida de escrita sem controle de concorrência (bloqueio pessimista, versionamento otimista ou operação atômica no banco) leva a estouro de limite.
- **Reserva precisa de expiração.** Autorização que nunca é capturada e nunca expira congela limite do cliente para sempre. Trate como TTL.
- **Idempotência, de novo.** Um retry de autorização não pode reservar o valor duas vezes.

#### Limite transacional

Não tem relação com crédito: existe para conter fraude. O caso mais visível é o PIX, onde o Bacen definiu um padrão nacional:

- **Período noturno** (20h às 6h, com opção de o cliente deslocar o início para 22h): limite padrão de **R$ 1.000** para pessoa física.
- **Período diurno**: não há teto nacional — cada instituição define conforme o perfil do cliente.
- **Pix Saque e Pix Troco**: R$ 3.000 no diurno e R$ 1.000 no noturno.

E aqui está o detalhe de design que vale internalizar: **aumentar limite leva no mínimo 24 horas; reduzir é instantâneo.**

> **Analogia:** é escalonamento de privilégio. Conceder permissão exige aprovação e tempo; revogar é imediato. O motivo é o mesmo nos dois mundos: se alguém está sob coação, a janela de espera dá chance de intervir antes que o dano aconteça. Um sistema que aplicasse aumento de limite na hora seria uma ferramenta de sequestro-relâmpago.

Se você implementa limites, essa assimetria não é opcional — é regra do arranjo. As regras operacionais do PIX são detalhadas em instrução normativa do Bacen e mudam com frequência (a IN BCB nº 512/2024 foi alterada pela IN BCB nº 746/2026, com efeitos a partir de outubro de 2026), então trate valores e prazos como **configuração**, nunca como constante no código.

#### Limite prudencial

Este limita a **própria instituição**, não o cliente. O principal é o **limite de exposição por cliente**: uma instituição não pode ter, com um mesmo cliente, exposição superior a **25% do Nível I do seu Patrimônio de Referência** (o capital próprio de melhor qualidade). Cooperativas de crédito não filiadas a centrais observam limite mais restrito, de 15%. Há também um teto para o conjunto das **exposições concentradas** — as individualmente relevantes — de 600% do Nível I.

Um ponto sutil e importante: **"cliente" aqui não é CPF/CNPJ**. Empresas do mesmo grupo econômico, ou que tenham dependência econômica entre si, contam como um único cliente. Se seu sistema calcula exposição por documento, ele subestima o risco real.

> **Analogia:** é limite de blast radius. Não importa quão bom pareça o cliente — se ele sozinho pode derrubar o banco ao quebrar, a exposição precisa ser capada. A regra existe porque a história bancária é uma sequência de instituições que faliram por confiar demais num único devedor.

---

### 13.3 Garantias

Garantia é o que o credor pode acionar se o devedor não pagar. Duas famílias:

| Tipo | Definição | Exemplos |
|---|---|---|
| **Garantia real** | Vinculada a um **bem** específico | Alienação fiduciária, hipoteca, penhor, cessão fiduciária de recebíveis |
| **Garantia fidejussória** | Vinculada a uma **pessoa** que se responsabiliza | Aval, fiança |

Traduzindo cada uma:

- **Hipoteca** — o imóvel continua em nome do devedor, mas fica vinculado à dívida; se não pagar, o credor executa o bem judicialmente.
- **Penhor** — mesma lógica para bens móveis (equipamentos, safra, joias), com o bem frequentemente ficando na posse do credor.
- **Cessão fiduciária de recebíveis** — o devedor entrega em garantia valores que ele tem a receber de terceiros.
- **Aval** — alguém assina o título junto com o devedor e responde pela dívida em pé de igualdade, podendo ser cobrado diretamente.
- **Fiança** — alguém garante a dívida por contrato, mas em regra só é cobrado depois de esgotada a cobrança do devedor principal.

Conceitos que aparecem direto em integração de sistemas:

- **Alienação fiduciária** — o bem fica em nome do credor até a quitação. É o mecanismo padrão de financiamento de veículos e imóveis.
- **Gravame** — o registro do ônus sobre o bem. No caso de veículos, é registrado junto ao Detran e movimentado por integração eletrônica; um financiamento de veículo só é seguro depois que o gravame está efetivado.
- **Cessão fiduciária de recebíveis** — recebíveis (duplicatas, recebíveis de cartão) dados em garantia, registrados em registradora.

Garantia reduz o risco de crédito e, portanto, a taxa — e, sob a Resolução CMN 4.966, também impacta diretamente o cálculo de provisão da instituição.

#### Quanto a garantia realmente vale

Nenhuma instituição empresta 100% do valor do bem dado em garantia. Dois conceitos governam isso:

- **LTV (*Loan-to-Value*)** — a razão entre o valor emprestado e o valor do bem. Um financiamento imobiliário com LTV de 80% significa que o banco financiou R$ 400 mil de um imóvel de R$ 500 mil; os 20% restantes são a entrada do cliente.
- **Deságio de avaliação (*haircut*)** — a margem de segurança aplicada sobre o valor do bem. Um veículo avaliado em R$ 50 mil pode ser considerado como R$ 35 mil para fins de garantia.

> **Analogia:** é o mesmo raciocínio de não dimensionar capacidade pelo pico teórico. O bem pode desvalorizar, o mercado pode secar, a execução pode demorar meses e custar honorários. O *haircut* é a margem entre o número no papel e o que se recupera de fato sob estresse.

A folga entre o valor do bem e a dívida é o que protege o credor. Quando o bem desvaloriza mais rápido do que a dívida amortiza, o LTV sobe e a operação fica **descoberta** — o cliente deve mais do que o bem vale, e o incentivo a simplesmente parar de pagar aumenta.

#### Garantia não registrada é garantia inexistente

Este é o ponto que mais gera trabalho de integração. Constituir a garantia no contrato **não basta**: ela precisa ser registrada no órgão competente para valer contra terceiros.

| Tipo de bem | Onde se registra |
|---|---|
| Veículo | Gravame no Detran, via integração eletrônica |
| Imóvel | Cartório de Registro de Imóveis |
| Recebíveis | Registradora autorizada pelo Bacen |

Sem registro, dois credores podem achar que têm a mesma garantia — e quem registrou primeiro leva. Por isso o desembolso costuma ser condicionado à confirmação do registro, o que torna essas integrações um caminho crítico do fluxo de crédito, não um detalhe administrativo.

> **Analogia:** o contrato é o `commit` local; o registro é o `push`. Enquanto não subiu para o repositório central que todos consultam, sua alteração não existe para os outros — e alguém pode ter empurrado a dele primeiro.

#### Execução: o que acontece quando o cliente não paga

Realizar a garantia não é automático nem rápido, e o caminho varia por tipo:

- **Alienação fiduciária** — como o bem já está em nome do credor, a retomada é mais ágil (busca e apreensão, no caso de veículos).
- **Hipoteca** — exige execução judicial, historicamente lenta.
- **Cessão fiduciária de recebíveis** — o credor passa a receber diretamente os valores cedidos, sem precisar executar nada.

Para o sistema, isso significa que a garantia tem **estados próprios** — constituída, registrada, em execução, liberada — que evoluem de forma independente do estado da operação de crédito. Modelar garantia como um campo do contrato, em vez de entidade com ciclo de vida próprio, é uma limitação que aparece cedo.

#### Um "garantia" que significa outra coisa: o FGC

Cuidado com a ambiguidade do termo. O **FGC (Fundo Garantidor de Créditos)** não protege o banco contra o cliente — protege o **cliente contra o banco**. É um fundo mantido pelas próprias instituições que devolve o dinheiro do depositante caso a instituição quebre.

A cobertura é de **R$ 250 mil por CPF ou CNPJ, por instituição ou conglomerado**, com teto global de **R$ 1 milhão a cada quatro anos**. Cobre depósitos à vista, poupança e aplicações como CDB, LCI e LCA. Não cobre fundos de investimento, ações ou títulos públicos — que não são dívida do banco.

O tema esteve em movimento recentemente: após liquidações relevantes no mercado, a Resolução CMN nº 5.295/2026 introduziu o **Ativo de Referência**, indicador de qualidade, liquidez e diversificação dos ativos da instituição, para desestimular bancos a captar agressivamente apoiados na garantia do fundo.

### 13.4 Inadimplência, provisão e write-off

Um crédito que não é pago não some do balanço — ele percorre um caminho contábil regulado:

- **Provisão (PDD — Provisão para Devedores Duvidosos, hoje também chamada PPE — Provisão para Perdas Esperadas)** — reconhecimento contábil antecipado de que parte da carteira não será recebida. Reduz o lucro **antes** de a perda acontecer.

  > **Analogia:** é *error budget*. Você não sabe qual requisição vai falhar, mas sabe estatisticamente que algumas vão — então reserva a margem antes, em vez de fingir surpresa quando o erro chega.
- **Write-off (baixa para prejuízo)** — a operação sai do ativo, mas a dívida **continua existindo juridicamente**. Contabilidade e cobrança seguem caminhos separados a partir daqui.
- **Recuperação** — valor eventualmente recebido depois do write-off, ou venda da carteira inadimplente para empresas especializadas.

**Mudança regulatória importante e recente:** a **Resolução CMN nº 4.966/2021**, em vigor desde **janeiro de 2025**, substituiu a antiga Resolução CMN 2.682/1999 e alinhou o Brasil ao **IFRS 9** — a norma contábil internacional que trata de instrumentos financeiros e determina que perdas de crédito sejam reconhecidas *antes* de acontecerem, com base em expectativa estatística. O que muda:

| | **Modelo antigo (2.682/99)** | **Modelo atual (4.966/21)** |
|---|---|---|
| Lógica | Perda **incorrida** | Perda **esperada** (ECL — *Expected Credit Loss*) |
| Classificação | Níveis AA até H, com % mínimo por faixa de atraso | **Três estágios** de risco |
| Gatilho | O atraso já ocorreu | Aumento significativo de risco, mesmo sem atraso |

Nos três estágios: **Estágio 1** (sem aumento significativo de risco) provisiona a perda esperada dos próximos 12 meses; **Estágios 2 e 3** ampliam o horizonte conforme a deterioração do risco, com o estágio 3 abrangendo os ativos problemáticos.

> **Por que um dev precisa saber disso:** o modelo de perda esperada é **prospectivo** e depende de modelos estatísticos alimentados por dados históricos granulares. Isso empurrou os bancos a reformular pipelines de dados de crédito — você precisa capturar e reter atributos de risco por operação ao longo do tempo, não só o estado atual. É um requisito de arquitetura de dados, não só de contabilidade.

### 13.5 Duplicatas

A duplicata é o **título de crédito** que dá lastro a boa parte das operações de antecipação de recebíveis no Brasil — é a peça que conecta "empresa vendeu a prazo" com "banco pode financiar isso". Vale detalhar porque ela é o ativo que efetivamente circula pelas registradoras citadas acima.

**O que é, na prática:** toda vez que uma empresa vende mercadoria ou presta serviço a prazo (mínimo 30 dias, entre partes domiciliadas no Brasil), a lei obriga a emissão de uma **fatura**. A duplicata é "sacada" a partir dessa fatura contra o comprador (o **sacado**) e vira um título executivo — ou seja, cobrável judicialmente sem necessidade de novo processo de conhecimento, e transferível (endossável) para terceiros, o que é exatamente o mecanismo por trás da antecipação de recebíveis: o banco "compra" a duplicata com deságio e assume o direito de receber do sacado no vencimento.

Base legal: **Lei nº 5.474/68** (Lei das Duplicatas Mercantis) e, mais recentemente, **Lei nº 13.775/2018** (duplicata escritural).

**Tipos de duplicata:**

| Critério | Tipo | Descrição |
|---|---|---|
| Origem da operação | **Duplicata mercantil** | Decorre de compra e venda de mercadorias entre empresas, prazo ≥ 30 dias da entrega/despacho |
| Origem da operação | **Duplicata de prestação de serviços** | Mesma lógica, mas para serviços faturados; segue regime equivalente de aceite/protesto |
| Forma de emissão | **Cartular** | Modelo tradicional, título físico em papel |
| Forma de emissão | **Escritural** | Registro 100% eletrônico numa entidade escrituradora autorizada pelo Bacen (Lei 13.775/2018), vinculado à chave da NF-e/CT-e/NFS-e/MDF-e/NFC-e |

> **Importante:** cartular e escritural **não são títulos diferentes** — a Lei 13.775/18 não cria um novo instrumento, apenas regulamenta uma nova forma de emitir e circular a mesma duplicata da Lei 5.474/68.

**Aceite:** o sacado tem até 15 dias para dar aceite (formal, ou "aceite presumido" quando há comprovante de entrega/prestação e ausência de recusa justificada). Sem aceite e sem pagamento, o título pode ser **protestado** — na duplicata escritural, o protesto usa o extrato eletrônico da entidade registradora no lugar do documento físico apresentado em cartório.

**Por que isso importa para quem constrói sistemas de crédito/cobrança:**

- A **Resolução CMN nº 4.815/2020** (alterada pela **5.094/2023**) está tornando o modelo escritural **obrigatório**, com adoção escalonada e expectativa de regime pleno a partir de 2027 — sistemas que hoje tratam duplicata como documento (PDF/imagem) vão precisar migrar para integração via API/registradora.
- A lei **veta cláusulas contratuais que proíbam ou limitem a negociação da duplicata** pelo credor — historicamente, o sacado (comprador) conseguia "travar" a duplicata no contrato comercial, impedindo o fornecedor de antecipá-la; isso deixou de valer, o que amplia o mercado de antecipação de recebíveis como produto de crédito.
- Cada duplicata escritural recebe um **identificador único vinculado ao documento fiscal** (NF-e/CT-e/etc.), o que dá rastreabilidade ponta a ponta — venda → fatura → duplicata → cessão/antecipação → liquidação — e reduz o problema histórico de duplicata "fria" (emitida sem lastro comercial real) ou cedida em duplicidade para bancos diferentes.

> **Analogia:** é o problema do gasto duplo. Sem um registro central, nada impedia o mesmo recebível de ser dado como garantia em três bancos ao mesmo tempo — cada um achando que o crédito era exclusivamente seu. A registradora cumpre o papel do livro-razão único que resolve a disputa: o recebível só pode estar em um lugar por vez.

```mermaid
sequenceDiagram
    autonumber
    participant Vendedor as Empresa vendedora (cedente)
    participant Sacado as Comprador (sacado)
    participant NFe as Nota Fiscal Eletrônica (NF-e/CT-e)
    participant Registradora as Entidade escrituradora<br/>(registradora de duplicatas)
    participant Banco as Banco / fintech de crédito

    Vendedor->>Sacado: Vende mercadoria/serviço a prazo (≥30 dias)
    Vendedor->>NFe: Emite nota fiscal
    Vendedor->>Registradora: Registra duplicata escritural<br/>vinculada à chave da NF-e
    Registradora-->>Sacado: Notifica duplicata para aceite
    Sacado->>Registradora: Dá aceite (ou "aceite presumido")

    alt Empresa precisa de capital de giro
        Vendedor->>Banco: Oferece duplicata para antecipação
        Banco->>Registradora: Consulta/confirma titularidade do recebível
        Banco->>Vendedor: Paga valor com deságio (antecipação)
        Registradora->>Registradora: Registra cessão de titularidade ao Banco
        Sacado->>Banco: Paga no vencimento (agora credor é o Banco)
    else Empresa aguarda o vencimento
        Sacado->>Vendedor: Paga a duplicata no vencimento
    end
```

### 13.6 Registradoras de recebíveis

Quando um banco antecipa recebíveis, o registro numa **registradora** autorizada pelo Bacen — CERC, Núclea, TAG, B3 — não é burocracia opcional: é o que dá **oponibilidade a terceiros**, ou seja, o que faz aquele direito ser reconhecidamente seu perante o resto do mercado. Sem registro, nada impede que o mesmo recebível seja prometido a outro financiador.

#### O vocabulário exato (e por que ele importa)

Esta é a parte que mais gera confusão em integração, porque três coisas diferentes convivem:

- **UR (Unidade de Recebível)** — a unidade atômica do registro. Não é "uma venda": é a combinação de recebedor + arranjo (bandeira) + credenciadora + **data de liquidação**. Todas as vendas de um lojista no Visa crédito que caem no dia 15 formam uma UR.
- **Agenda de recebíveis** — o conjunto de URs futuras daquele recebedor. É a "fila" do que ele tem a receber.
- **Contrato** — a operação que incide sobre as URs. E ela vem em dois sabores juridicamente distintos:
  - **Cessão** — a titularidade do recebível é transferida ao financiador.
  - **Gravame / ônus** — a titularidade permanece com o recebedor, mas o recebível fica **onerado**, servindo de garantia. É o mecanismo que impede que ele seja usado de novo em outra operação enquanto estiver ativo.

#### A precisão que evita bug: baixa de quê?

Aqui está o ponto da sua pergunta. É correto dizer que existe uma etapa de baixa depois da liquidação — mas o objeto da baixa geralmente **não é o recebível**, e sim o **ônus** que pesa sobre ele.

A distinção na prática:

- **A UR liquida sozinha.** Ela tem data de liquidação; chegado o dia, ela é liquidada por natureza. Ninguém precisa "dar baixa" na UR para que isso aconteça.
- **O gravame não some sozinho.** Ele precisa de um comando explícito de **desconstituição**, enviado à registradora. Enquanto ele existir, aquele recebível continua bloqueado para novas operações — mesmo já tendo sido pago.
- **A liquidação é direcionada.** No modelo de liquidação centralizada, a registradora informa à credenciadora **para quem** pagar. Ou seja, o dinheiro já sai direto para o financiador; a baixa do ônus é o acerto do registro, não o caminho do dinheiro.

E a desconstituição não acontece só por liquidação. Ela também ocorre por **cancelamento da operação**, **resilição do contrato** pelo recebedor (com prazo regulatório de até dois dias úteis para a credenciadora solicitar a baixa) e **substituição de garantia**. Um sistema que só desconstitui no caminho feliz deixa gravame preso nos demais.

```mermaid
stateDiagram-v2
    [*] --> Registrada: credenciadora registra<br/>a agenda de recebíveis
    Registrada --> Onerada: constituição de<br/>gravame ou cessão
    Onerada --> Liquidada: chega a data<br/>de liquidação da UR
    Liquidada --> Livre: desconstituição<br/>do gravame
    Onerada --> Livre: cancelamento, resilição<br/>ou substituição
    Livre --> [*]

    note right of Onerada
        Enquanto onerada, a UR não pode
        lastrear outra operação
    end note
```

#### Interoperabilidade: o registro é do mercado, não do seu banco

As registradoras são **interoperáveis** por convenção sob supervisão do Bacen. Você registra numa delas, mas a informação circula entre todas — é isso que impede que o mesmo recebível seja onerado na CERC e cedido na Núclea ao mesmo tempo.

> **Analogia:** é um registro distribuído com garantia de unicidade. Cada registradora é um nó, a convenção é o protocolo de consenso, e o efeito prático é o mesmo de um sistema anti-*double-spend*: o recebível só pode estar comprometido em um lugar por vez. Antes disso existir, o mercado operava sem essa trava — e o resultado era exatamente o que se esperaria.

#### Implicações para o seu sistema

- **Conciliação com a registradora é obrigatória**, não opcional. A norma prevê conciliação entre credenciadoras, instituições financeiras e sistemas de registro, inclusive entre registradoras. Na prática: você precisa de uma rotina que compare sua base com a posição da registradora e trate divergências — o mesmo motor de conciliação do capítulo 15.
- **Consulta antes de contratar.** A credenciadora precisa verificar, antes de fechar um contrato, se já existem contratos incidindo sobre aquela agenda. Traduzindo para o código: consulta obrigatória de gravames pré-existentes antes de originar a operação, e ela não pode ser "melhor esforço".
- **Modele o gravame como entidade com estado**, não como flag no título. Ele tem ciclo próprio, prazos regulatórios e motivos de baixa diferentes.
- **Eventos assíncronos e fora de ordem.** Confirmações de registro, liquidação e desconstituição chegam por caminhos distintos e em tempos distintos. Processamento idempotente, com chave por evento, é requisito.
- **A norma muda.** A Resolução BCB nº 264/2022 é a base do registro de recebíveis de arranjo de pagamento, e já foi alterada — a Resolução BCB nº 514/2025 trouxe mudanças com efeitos a partir de maio de 2026. Prazos e comandos devem estar em configuração.

> **Resumo da sua dúvida, em uma frase:** cadastra-se o recebível e constitui-se o ônus; a UR liquida por conta própria na data; e o que exige comando explícito de baixa é a **desconstituição do gravame** — que também precisa acontecer nos casos de cancelamento e resilição, não só na liquidação.

### Checkpoint

1. Quais são as etapas do ciclo de vida de uma operação de crédito, da proposta à recuperação?
2. Por que `disponível = aprovado − utilizado` está errado?
3. Por que aumentar limite de PIX demora 24h, mas reduzir é instantâneo?
4. No limite prudencial de exposição, por que "cliente" não é o mesmo que CPF/CNPJ?
5. Qual a diferença entre garantia real e fidejussória?
6. O que é LTV e por que o banco aplica *haircut* sobre o valor do bem?
7. A garantia está no contrato assinado. Ela já vale contra terceiros?
8. O FGC protege quem?
9. Depois do write-off, a dívida deixa de existir?
10. O que mudou com a Resolução CMN 4.966/2021 e por que isso é um problema de arquitetura de dados?
11. Numa antecipação de recebíveis, o que exige comando explícito de baixa na registradora?

*Respostas: (1) originação, análise, decisão, formalização, desembolso, gestão, quitação ou inadimplência, provisão, write-off e recuperação; (2) falta subtrair o reservado — valores autorizados e ainda não liquidados, sem os quais o cliente gasta o mesmo limite duas vezes; (3) porque a espera protege quem está sob coação, enquanto reduzir exposição nunca precisa ser barrado; (4) porque empresas do mesmo grupo econômico ou com dependência econômica entre si contam como um único cliente; (5) real é vinculada a um bem, fidejussória a uma pessoa que se responsabiliza; (6) LTV é a razão entre o emprestado e o valor do bem, e o haircut é a margem de segurança para desvalorização, custo e demora da execução; (7) não — sem registro no órgão competente ela não vale contra terceiros, e quem registrar primeiro tem preferência; (8) o depositante, não o banco: devolve até R$ 250 mil por CPF/CNPJ por instituição se ela quebrar; (9) não — sai do ativo contábil mas continua existindo juridicamente; (10) a provisão passou de perda incorrida para perda esperada, exigindo histórico granular de atributos de risco por operação ao longo do tempo; (11) a desconstituição do gravame — a UR liquida sozinha na data, mas o ônus só sai por comando, e também precisa sair em cancelamento e resilição.*

---

## 14. Compliance

Este é provavelmente o capítulo mais subestimado por devs vindos de outros domínios — e o que mais impacta o dia a dia. Em banco, **compliance não é um departamento que te atrapalha: é requisito funcional do seu sistema.**

### KYC e KYB: conheça seu cliente

**KYC (Know Your Customer)** para pessoa física e **KYB (Know Your Business)** para pessoa jurídica são as obrigações de identificar e qualificar quem entra na instituição. Na prática, o onboarding precisa:

- Identificar e validar documentos (com prova de vida/biometria contra fraude de identidade);
- Verificar consistência dos dados (CPF/CNPJ na Receita, endereço, renda declarada);
- Checar listas restritivas: **sanções** internacionais — listas de pessoas e entidades com quem é proibido negociar, publicadas pela ONU e pelo **OFAC** (*Office of Foreign Assets Control*, órgão do Tesouro dos EUA cuja lista é seguida globalmente por causa do alcance do dólar), **PEP** (Pessoas Expostas Politicamente, que exigem diligência reforçada), listas internas;
- No caso PJ, identificar o **beneficiário final** — a pessoa física que de fato controla a empresa, mesmo através de camadas societárias.

**Diligência é contínua, não pontual.** O cliente é re-avaliado ao longo do relacionamento; mudança de comportamento transacional dispara reanálise.

### PLD/FT: prevenção à lavagem de dinheiro e ao financiamento do terrorismo

A instituição é obrigada a **monitorar transações** e **comunicar** operações suspeitas ao **COAF** (Conselho de Controle de Atividades Financeiras). Tecnicamente, isso significa que existe — ou você vai construir — um sistema de:

- **Regras e tipologias** — padrões que disparam alerta (fracionamento de valores para escapar de limiares, movimentação incompatível com a renda declarada, transações circulares entre contas relacionadas, uso de "contas laranja"). É detecção de anomalia, com a mesma tensão entre falso positivo e falso negativo que você conhece de antifraude: regra frouxa deixa passar crime, regra apertada afoga o time de análise;
- **Fila de análise** — alertas revisados por analistas humanos;
- **Comunicação ao COAF** — dentro de prazo regulatório, e **sem informar o cliente** (a comunicação é sigilosa por lei).

```mermaid
graph LR
    T["Transação"] --> M["Motor de monitoramento<br/>(regras + modelos)"]
    M -->|Normal| OK["Segue o fluxo"]
    M -->|Suspeita| A["Alerta gerado"]
    A --> AN["Análise humana<br/>(time de PLD)"]
    AN -->|Descartado| OK
    AN -->|Confirmado| COAF["Comunicação ao COAF<br/>(sigilosa)"]

    style COAF fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

### Sigilo bancário

Regido pela **Lei Complementar nº 105/2001**: dados de operações e serviços financeiros são protegidos por sigilo. Consequências diretas no seu código:

- **Log é vazamento em potencial.** Logar payload completo de transação, saldo ou dados de conta é violação — não "má prática".
- **Acesso precisa ser justificado e rastreável.** Consultar dados de cliente sem motivo de negócio é infração, mesmo para um dev com acesso ao banco de dados de produção.
- **Ambientes de teste não podem usar dados reais** sem mascaramento/anonimização.
- **Quebra de sigilo** só ocorre em hipóteses legais específicas (ordem judicial, CPI, requisição fiscal regulada) — nunca por solicitação informal.

### LGPD e a interseção com o sigilo

A **LGPD (Lei nº 13.709/2018)** se soma ao sigilo bancário — não o substitui. Pontos que afetam arquitetura:

- **Base legal** — em banco, boa parte do tratamento se apoia em *obrigação legal/regulatória* e *execução de contrato*, não em consentimento. Isso é importante: o cliente **não pode** pedir exclusão de dados que a instituição é obrigada a reter.
- **Retenção** — normas do SFN exigem guarda de registros por prazos longos (tipicamente 5 a 10 anos, conforme o tipo). "Direito ao esquecimento" convive com essa obrigação.
- **Minimização** — colete só o necessário para a finalidade declarada.
- **Trilha de auditoria** — quem acessou o quê, quando e por quê, de forma imutável.

### Regulação prudencial (por que existem limites que parecem arbitrários)

Sob o acordo internacional de **Basileia**, cada instituição precisa manter **capital próprio proporcional ao risco** que assume. Se a carteira de crédito cresce, a exigência de capital cresce junto. É por isso que existem limites de exposição por cliente, por setor e por tipo de operação — restrições que chegam ao seu backlog como "regra de negócio" e cuja origem real é regulatória.

### Ouvidoria e reclamações no Bacen

Instituições têm **ouvidoria obrigatória** como última instância interna, e o Bacen publica ranking de reclamações. Uma falha sistêmica no seu código — cobrança duplicada, boleto emitido errado, PIX não creditado — vira reclamação registrada e pode virar processo administrativo. É a tradução concreta do **risco operacional** do capítulo 3.

---

## 15. Conciliação, estorno, fraude e disputas

### Conciliação: o ritual diário do backend financeiro

**Conciliação** é conferir se o que o seu sistema registrou bate com o que a contraparte registrou. É rotina diária e inegociável em qualquer instituição.

O padrão geral:

1. Receber o **extrato/arquivo da contraparte** (banco, adquirente, câmara);
2. Comparar com os registros internos, casando por chave (identificador da transação, valor, data);
3. Classificar as divergências: existe só na contraparte, existe só internamente, valores diferentes, duplicidade;
4. Tratar cada divergência — automaticamente quando a regra permite, manualmente quando não.

```mermaid
graph TB
    INT["Registros internos<br/>(seu ledger)"] --> CONC["Motor de conciliação"]
    EXT["Arquivo da contraparte<br/>(retorno CNAB, extrato, EDI adquirente)"] --> CONC
    CONC --> OK["Conciliado ✓"]
    CONC --> D1["Só no interno<br/>(não liquidou?)"]
    CONC --> D2["Só no externo<br/>(evento não capturado)"]
    CONC --> D3["Divergência de valor<br/>(tarifa? arredondamento?)"]
    D1 --> TRAT["Fila de tratamento<br/>+ alerta"]
    D2 --> TRAT
    D3 --> TRAT
```

Requisitos que decorrem disso e que valem como princípio de projeto:

- **Idempotência** — reprocessar o mesmo arquivo não pode duplicar lançamento. Chave natural + hash do conteúdo.
- **Ledger append-only** — nunca sobrescreva um lançamento; registre um novo que o corrige.
- **Rastreabilidade ponta a ponta** — um identificador único que atravessa todos os sistemas envolvidos.

### Estorno, cancelamento e devolução

Três coisas diferentes, frequentemente confundidas:

| Termo | O que é |
|---|---|
| **Cancelamento** | Anula antes da liquidação — a operação não chega a acontecer |
| **Estorno** | Lançamento contrário que anula o efeito de uma operação já liquidada |
| **Devolução** | Nova transação em sentido oposto, com identidade própria |

O princípio contábil: **você não apaga o lançamento original** — você registra o movimento compensatório. O histórico precisa mostrar que algo aconteceu e depois foi revertido.

### MED: o mecanismo de devolução do PIX

O **MED (Mecanismo Especial de Devolução)**, regulamentado pela **Resolução BCB nº 103/2021**, permite contestar um PIX quando há indício de **fraude, golpe ou falha operacional da instituição**.

O **MED 2.0** tornou-se obrigatório para todas as instituições participantes do PIX **a partir de fevereiro de 2026**. A principal evolução é o **bloqueio em cadeia**: antes, só a primeira conta que recebia o valor era analisada; agora o rastreamento segue o dinheiro por múltiplas contas, acompanhando a tática de fraudadores que pulverizam valores. Prazos: o usuário pode contestar em até 80 dias, e a análise mais a devolução ocorrem em até 11 dias a partir da contestação.

**Limites importantes** (e que geram muito ticket de suporte mal direcionado):

- **Não cobre erro de digitação** do próprio usuário — mandar PIX para a chave errada não é caso de MED.
- **Não cobre arrependimento de compra** nem **desacordo comercial** (produto não entregue, defeito). Isso é relação de consumo, resolvida por outros canais.
- **Não garante devolução** — depende de comprovação de fraude e de ainda existir saldo na conta do recebedor. Se o valor foi sacado em espécie, sai do sistema e não há como recuperar.

### O padrão que se repete

Repare que **chargeback** (cartão), **MED** (PIX) e **estorno de boleto** são instâncias do mesmo problema arquitetural:

> Uma transação liquidada e considerada "final" pode ser revertida depois, por decisão de um terceiro, fora do controle do seu sistema.

Se você tira uma única lição de arquitetura desta apostila, que seja esta: **em sistema financeiro, "pago" nunca é um estado terminal imutável.**

> **Analogia:** é o padrão *saga*. Em sistemas distribuídos, você não consegue segurar uma transação aberta entre serviços que não controla, então aceita o *commit* local e prepara uma **transação compensatória** para quando algo der errado lá na frente. Chargeback e MED são exatamente isso: a compensação chegando semanas depois, disparada por alguém que não é você. Se o seu modelo não previu o caminho de volta, ele vai precisar ser reescrito.

Modele estados reversíveis, guarde histórico completo e nunca destrua informação.

---

## 16. Open Finance

O Open Finance Brasil é hoje um dos maiores ecossistemas do mundo em volume: **mais de 800 instituições participantes**, dezenas de milhões de consentimentos ativos e bilhões de chamadas de API por semana entre instituições (números de 2026). Para o dev, ele resolve um problema concreto: antes, cada banco tinha API própria, onboarding próprio e credenciais próprias. O Open Finance padroniza **compartilhamento de dados e iniciação de pagamento** sob regras comuns do Bacen.

> **Analogia:** é OAuth aplicado a dinheiro. O cliente autoriza um app terceiro a acessar dados que estão em outro provedor, com escopo definido ("só extrato", "só iniciar pagamento") e prazo de validade. Ninguém entrega senha do banco a ninguém, e o consentimento pode ser revogado a qualquer momento — exatamente o modelo de token que você já conhece.

Peças relevantes:

- **Consentimento** — cliente autoriza explicitamente o compartilhamento, válido por até 12 meses, renovável.
- **Iniciação de pagamento (payment initiation)** — uma instituição terceira pode iniciar um PIX em nome do cliente, com autorização, sem que ele saia do app do iniciador.
- **JSR — Jornada Sem Redirecionamento** — mudança regulatória (Resolução BCB nº 406/2024) que elimina o antigo fluxo de "sair do app A, autenticar no app B, voltar para o app A"; a confirmação passa a ocorrer dentro do próprio app, via biometria ou push do banco. Tornou-se obrigatória para todos os participantes do arranjo PIX a partir de janeiro de 2026.
- **Pix Automático via Open Finance** — portabilidade de recorrência entre bancos sem convênio bilateral.

```mermaid
graph LR
    CLIENTE["Cliente"] -->|Autoriza consentimento| ITP["Iniciador de Pagamento / Agregador<br/>(app terceiro, ex.: ERP, ITP)"]
    ITP -->|"API padronizada Bacen<br/>(escopo de dados/serviços)"| BANCO_DETENTOR["Instituição detentora da conta<br/>(banco onde o dinheiro está)"]
    BANCO_DETENTOR -->|"Confirma (JSR: sem redirecionamento)"| CLIENTE
    ITP -->|"Inicia PIX em nome do cliente"| SPI["SPI (Bacen)"]
    SPI --> BANCO_DETENTOR
```

---

## 17. Visão consolidada

Juntando tudo: da tela do cliente até a conta de Reservas Bancárias no Bacen.

```mermaid
graph TB
    subgraph L1["Camada de aplicação (o que você constrói)"]
        APP["App / core bancário / sistema de cobrança"]
    end

    subgraph L2["Camada regulatória de dados"]
        DICT2["DICT — chaves PIX"]
        SCR2["SCR — histórico de crédito"]
        OF["Open Finance — consentimento e dados"]
    end

    subgraph L3["Camada de compensação"]
        SPI2["SPI (PIX)"]
        NUCLEA2["Núclea (boleto, cartão)"]
        B32["B3 (mercado de capitais)"]
    end

    subgraph L4["Camada de liquidação final — Bacen"]
        STR2["STR — LBTR"]
        SELIC2["SELIC — títulos públicos"]
        RESERVA["Conta de Reservas Bancárias"]
    end

    APP --> DICT2
    APP --> SCR2
    APP --> OF
    APP --> SPI2
    APP --> NUCLEA2
    APP --> B32

    SPI2 --> STR2
    NUCLEA2 --> STR2
    B32 --> STR2
    SELIC2 --> RESERVA
    STR2 --> RESERVA

    style RESERVA fill:#1a1a1a,color:#fff,stroke:#f5a623,stroke-width:2px
```

**Takeaway de arquitetura:** não importa o trilho (PIX, TED, boleto, ação, título público) — todos convergem para o mesmo ponto final: uma movimentação na **conta de Reservas Bancárias** do participante no Bacen. Isso é o que garante baixo risco sistêmico no Brasil (o Bacen frequentemente é citado como um dos sistemas de liquidação mais seguros do mundo) e é também por isso que **liquidação é sempre a etapa mais lenta e mais auditável** do seu fluxo — vale desenhar reconciliação em cima dela, não em cima da "confirmação" otimista da camada de compensação.

---

## 18. Glossário técnico

| Termo | Significado | Onde você vê na prática |
|---|---|---|
| **Conta PI** | Conta no Bacen usada exclusivamente para liquidação do PIX | Pré-financiada, nunca negativa, remunerada pela Selic |
| **Redesconto intradia** | Operação compromissada sem custo para liquidez no dia | Rotina do STR, não medida de emergência |
| **ISPB × COMPE** | Identificador de 8 dígitos no SPB × código legado de 3 dígitos | Coexistem; nem toda IP tem COMPE |
| **Conta contábil** | Categoria classificatória da instituição (≠ conta do cliente) | Plano de contas, natureza devedora/credora |
| **Lançamento** | Registro atômico e balanceado de um fato contábil | Append-only, Σ D = Σ C |
| **Partida** | Cada linha débito/crédito de um lançamento | 2..N por lançamento |
| **Diário / Razão / Balancete** | Journal / ledger / trial balance | Event store, read model e teste de integridade |
| **COSIF** | Plano contábil obrigatório do SFN | Circular BCB 1.273/1987; código G.S.D.TT.SS-V e atributos |
| **Conta de compensação** | Registro fora do balanço | Limites não usados, garantias, avais |
| **Conta transitória** | Classificação temporária de valor não identificado | Precisa zerar; saldo residual é sintoma de bug |
| **Competência × Caixa** | Reconhecer pelo fato econômico × pelo movimento do dinheiro | Apropriação diária de juros em batch |
| **Emissor / Credenciadora / Bandeira** | Papéis do arranjo de cartões | Autorização, captura, liquidação |
| **MDR / Intercâmbio** | Taxa paga pelo lojista / fatia repassada ao emissor | Precificação de adquirência |
| **Chargeback** | Contestação de compra no cartão | Transação liquidada pode ser revertida |
| **MED** | Mecanismo Especial de Devolução do PIX | Res. BCB 103/2021; MED 2.0 obrigatório desde fev/2026 |
| **KYC / KYB** | Identificação de cliente PF / PJ | Onboarding, listas de sanções, PEP |
| **PLD/FT** | Prevenção à lavagem de dinheiro e financiamento ao terrorismo | Monitoramento, alertas, comunicação ao COAF |
| **COAF** | Órgão que recebe comunicações de operações suspeitas | Comunicação sigilosa, sem avisar o cliente |
| **PEP** | Pessoa Exposta Politicamente | Exige diligência reforçada no onboarding |
| **Sigilo bancário** | LC 105/2001 | Log de payload é vazamento; acesso precisa ser rastreável |
| **PDD / PPE** | Provisão para perdas de crédito | Res. CMN 4.966/21 — perda esperada (IFRS 9) |
| **Write-off** | Baixa contábil do crédito para prejuízo | A dívida continua existindo juridicamente |
| **Alienação fiduciária / Gravame** | Garantia real sobre bem / registro do ônus | Financiamento de veículo e imóvel |
| **CCB** | Cédula de Crédito Bancário | Instrumento de formalização do crédito |
| **CET** | Custo Efetivo Total | Obrigatório informar antes da contratação |
| **Price / SAC** | Sistemas de amortização | Parcela fixa vs. amortização constante |
| **Basileia** | Acordo de capital mínimo por risco | Origem de limites de exposição |
| **Conciliação** | Casamento entre registros internos e da contraparte | Rotina diária; exige idempotência |
| **Liquidez** | Facilidade de converter um ativo em caixa sem perda de valor | Horários de corte, limites, produtos de resgate |
| **Compensação (clearing)** | Apuração de quem deve quanto a quem | Núclea, câmaras — ainda não é dinheiro movido |
| **Liquidação (settlement)** | Transferência definitiva e irrevogável de recursos | STR, SPI — estado final do seu fluxo |
| **LBTR / LDL** | Liquidação bruta em tempo real / diferida líquida | STR e SPI (LBTR); câmaras de varejo (LDL) |
| **Netting** | Compensação multilateral de saldos | Reduz liquidez necessária em modelos LDL |
| **D+0 / D+1 / D+2** | Prazo de liquidação em dias úteis | Régua de conciliação, calendário de feriados |
| **Float** | Dinheiro "no ar" entre saída e chegada | Reduzido drasticamente pelo PIX |
| **Deságio** | Desconto ao antecipar um recebível | Antecipação de duplicatas |
| **Spread bancário** | Margem entre captação e empréstimo | Precificação de crédito |
| **Moeda escritural** | Saldo do cliente = passivo do banco | O `saldo` no seu banco de dados |
| **Moeda de banco central** | Reservas do banco no Bacen | Única que quita obrigação interbancária |
| **Partidas dobradas** | Todo lançamento tem débito e crédito equivalentes | Ledger append-only, invariante de auditoria |
| **Bacen / BCB** | Banco Central do Brasil | Regulador e operador de STR, SPI, SCR |
| **CMN** | Conselho Monetário Nacional | Origem das Resoluções que você cita em specs |
| **ISPB** | Identificador único do participante do SPB (8 dígitos) | Header de mensagens PIX, cadastro DICT |
| **RSFN** | Rede do Sistema Financeiro Nacional | Rede privada exigida p/ acessar STR/SPI/DICT |
| **STR** | Sistema de Transferência de Reservas | Liquidação final, LBTR, contas de reserva |
| **SPI** | Sistema de Pagamentos Instantâneos | Motor de liquidação do PIX |
| **DICT** | Diretório de Identificadores de Contas Transacionais | Resolve chave PIX → conta/ISPB |
| **CIP / Núclea** | Câmara de compensação privada | Boleto, cartão, parte do PIX |
| **SELIC** | Sistema de custódia/liquidação de títulos públicos | Não confundir com a taxa Selic |
| **SCR** | Sistema de Informações de Crédito | Histórico de crédito, base de decisão |
| **Carteira de cobrança** | Contrato que define o papel do banco sobre os títulos | Campo obrigatório no CNAB; código varia por banco |
| **Cobrança simples** | Banco só cobra e repassa, como mandatário | Título continua do cedente; sem crédito envolvido |
| **Cobrança caucionada** | Títulos dados em garantia de uma linha de crédito | Pagamento amortiza a dívida do cedente |
| **Cobrança descontada** | Títulos endossados ao banco, que antecipa com deságio | Banco vira credor, mas há coobrigação |
| **Coobrigação / regresso** | Cedente responde se o sacado não pagar | Risco não some com a antecipação |
| **Cedente** | Quem tem o valor a receber e emite a cobrança | O outro lado do sacado |
| **Instrução / ocorrência** | Comando na remessa / resposta no retorno | Rejeição silenciosa é armadilha clássica |
| **DDA** | Débito Direto Autorizado | Boleto entregue eletronicamente no app do pagador |
| **CNAB 240/400** | Layout de arquivo posicional padrão Febraban | Remessa/retorno de cobrança e pagamento; dialeto por banco |
| **Forma de lançamento** | Trilho pelo qual o pagamento anda (`01` conta, `41` TED, `45` PIX, `31` título) | Fica no header de lote e determina o segmento do detalhe |
| **Tipo de serviço** | O que está sendo pago (`20` fornecedor, `30` folha, `22` tributos) | Junto com a forma de lançamento, define o recorte do lote |
| **Segmento A / B** | Detalhe de crédito em conta, TED ou PIX, e seu complemento | O B virou o registro da chave PIX e do QR Code |
| **Segmento J / J-52** | Detalhe de liquidação de título, e o complemento com sacado e sacador | O J carrega o código de barras de 44 posições |
| **Seu número (pagamento)** | Nº do documento atribuído pela empresa (A 73-92, J 183-202) | A chave de correlação entre remessa e retorno |
| **NSA** | Nº sequencial do arquivo, no header | Único e crescente por cliente; furo ou repetição derruba ERP |
| **Retorno parcial / consolidado** | Convenção comercial de emitir o retorno em recortes ao longo do dia ou completo no fechamento | Não existe no padrão Febraban; precisa ser acordado por escrito |
| **PSP** | Prestador de Serviço de Pagamento | Qualquer participante do arranjo PIX |
| **Open Finance** | Compartilhamento regulado de dados/serviços financeiros | Consentimento, iniciação de pagamento |
| **JSR** | Jornada Sem Redirecionamento | UX de confirmação sem sair do app |
| **LBTR** | Liquidação Bruta em Tempo Real | Modelo usado por STR e SPI |
| **Correspondente bancário** | Canal terceirizado de originação | Fintechs de crédito operando "para" um banco |
| **Limite rotativo** | Teto que se recompõe conforme o cliente paga | Cartão de crédito, cheque especial |
| **Limite reservado** | Valor autorizado e ainda não liquidado | `disponível = aprovado − utilizado − reservado` |
| **LTV** | Razão entre valor emprestado e valor do bem | Financiamento imobiliário e de veículos |
| **Haircut** | Deságio de segurança sobre o valor da garantia | Protege contra desvalorização e custo de execução |
| **Gravame** | Registro do ônus sobre um bem | Detran, para veículos |
| **Exposição por cliente** | Teto de 25% do Nível I do PR | "Cliente" inclui o grupo econômico |
| **FGC** | Fundo Garantidor de Créditos | Protege o depositante, não o banco: R$ 250 mil |
| **UR (Unidade de Recebível)** | Recebedor + arranjo + credenciadora + data de liquidação | Unidade atômica do registro |
| **Agenda de recebíveis** | Conjunto de URs futuras de um recebedor | O que a antecipação consome |
| **Gravame / ônus** | Recebível onerado como garantia, sem troca de titularidade | Bloqueia novo uso até a desconstituição |
| **Cessão** | Transferência de titularidade do recebível | Diferente de gravame |
| **Desconstituição** | Baixa do gravame na registradora | Exige comando; não acontece sozinha |
| **Duplicata** | Título de crédito sacado sobre uma venda/serviço a prazo | Lastro de operações de antecipação de recebíveis |
| **Sacado** | O comprador/devedor da duplicata | Quem paga o título no vencimento |
| **Duplicata escritural** | Duplicata registrada 100% eletronicamente | Lei 13.775/18, entidades escrituradoras autorizadas pelo Bacen |

---

## 19. Referências

- Banco Central do Brasil — Sistema de Pagamentos Brasileiro: bcb.gov.br
- Banco Central do Brasil — repositórios oficiais da API PIX: `github.com/bacen/pix-api`, `github.com/bacen/pix-dict-api`, `github.com/bacen/pix-dict-quickstart`
- Circular BCB nº 1.273/1987 e Manual do COSIF (bcb.gov.br/aplica/cosif)
- Resolução BCB nº 195/2022 (Regulamento do SPI e da Conta PI) e Resolução BCB nº 175/2021 (redesconto no SPI)
- Comunicado Febraban sobre a descontinuação de DOC e TEC (fev/2024)
- Resolução CMN nº 4.966/2021 e Resolução BCB nº 352/2023 (perda esperada / IFRS 9, vigência jan/2025)
- Resolução BCB nº 103/2021 (MED — Mecanismo Especial de Devolução do PIX) e regras do MED 2.0 (fev/2026)
- Lei Complementar nº 105/2001 (sigilo bancário)
- Lei nº 13.709/2018 (LGPD)
- Resolução CMN nº 4.571/2017 (SCR)
- Resolução BCB nº 264/2022 (registro de recebíveis de arranjo de pagamento), alterada pela Resolução BCB nº 514/2025, e Convenção entre Entidades Registradoras
- Resolução CMN nº 4.734/2019 (recebíveis de arranjo de pagamento)
- Resolução CMN nº 4.677/2018 (limites de exposição por cliente) e Resolução CMN nº 5.076/2023 (IPs tipo 3)
- Instrução Normativa BCB nº 512/2024, alterada pela IN BCB nº 746/2026 (limites de valor no PIX)
- Resolução CMN nº 5.295/2026 (FGC e Ativo de Referência)
- Lei nº 5.474/68 (Lei das Duplicatas Mercantis) e Lei nº 13.775/2018 (duplicata escritural)
- Resolução CMN nº 4.815/2020, alterada pela Resolução CMN nº 5.094/2023 (obrigatoriedade do modelo escritural)
- Resolução BCB nº 406/2024 (Jornada Sem Redirecionamento)
- Open Finance Brasil — Atos Normativos: openfinancebrasil.org.br
- Wikipédia — Sistema de Pagamentos Brasileiro
- Febraban — padrão CNAB 240/400

---

*Apostila gerada com pesquisa web em agosto de 2026 — as regras do Bacen mudam com frequência (novas resoluções, novos manuais de Open Finance); vale sempre checar `bcb.gov.br` para a versão vigente antes de implementar em produção.*
