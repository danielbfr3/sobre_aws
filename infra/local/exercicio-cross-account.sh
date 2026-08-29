#!/usr/bin/env bash
#
# EXERCICIO: conta de ORIGEM e conta de DESTINO.
#
# Fixa o vocabulario do guia 02, secao 2 ("Origem e destino: dois papeis, nao
# dois rotulos") fazendo a travessia de conta acontecer na sua frente.
#
# A ideia central que o exercicio quer gravar:
#
#     ORIGEM  = a conta onde o WORKLOAD roda   (cash-prd,  111122223333)
#     DESTINO = a conta onde o RECURSO esta    (dados-prd, 444455556666)
#
# O momento que vale o exercicio inteiro e a ETAPA 3: o numero da conta em
# "get-caller-identity" MUDA de 111122223333 para 444455556666 sem que voce
# troque de terminal, de perfil ou de maquina. E a fronteira de conta sendo
# atravessada, visivel em uma linha.
#
# ATENCAO, e o exercicio repete isso no fim:
#
#     O Floci NAO e um ponto de decisao de autorizacao. As policies que este
#     script cria servem para voce praticar a FORMA (qual documento mora em
#     qual conta). Uma policy mal escrita passa aqui e falha na AWS. Para
#     validar least privilege, IAM Policy Simulator e Access Analyzer.
#
# O que o exercicio prova DE VERDADE esta marcado com [REAL].
# O que e so pratica de forma esta marcado com [FORMA].
#
# Uso:  ./run.sh cross-account
#       ./run.sh cross-account --limpar    apaga o que o exercicio criou
#
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

VERDE='\033[0;32m'; VERMELHO='\033[0;31m'; AMARELO='\033[0;33m'
AZUL='\033[0;36m'; CINZA='\033[0;90m'; NEUTRO='\033[0m'
PASSOU=0; FALHOU=0

ENDPOINT="${AWS_ENDPOINT_URL:-http://localhost:4566}"
REGIAO="${AWS_DEFAULT_REGION:-us-east-1}"

# As mesmas contas e os mesmos nomes de role do guia 02. E de proposito:
# o exercicio e o texto contam a mesma historia.
ORIGEM="111122223333"    # cash-prd  - onde o cluster EKS e os pods rodam
DESTINO="444455556666"   # dados-prd - onde o bucket, a chave e a fila estao

ROLE_ORIGEM="asa-prd-cash-cobranca-consumer-registro"
ROLE_DESTINO="asa-prd-dados-gravador-comprovantes"
ARN_ROLE_ORIGEM="arn:aws:iam::${ORIGEM}:role/${ROLE_ORIGEM}"
ARN_ROLE_DESTINO="arn:aws:iam::${DESTINO}:role/${ROLE_DESTINO}"
FILA="auditoria-comprovantes"

passa() { echo -e "  ${VERDE}PASSOU${NEUTRO}  $1"; PASSOU=$((PASSOU+1)); }
falha() {
  echo -e "  ${VERMELHO}FALHOU${NEUTRO}  $1"
  [[ -n "${2:-}" ]] && echo -e "          ${AMARELO}$2${NEUTRO}"
  FALHOU=$((FALHOU+1))
}
nota()  { echo -e "  ${CINZA}$*${NEUTRO}"; }
etapa() { echo; echo -e "${AMARELO}=== $* ${NEUTRO}"; echo; }

# Roda um comando aws COMO uma conta especifica e MOSTRA o comando antes.
#
# O truque do Floci: um AWS_ACCESS_KEY_ID com exatamente 12 digitos vira o ID
# da conta, e recursos criados por uma conta ficam invisiveis para outra. E o
# que torna este exercicio possivel sem uma AWS de verdade.
#
# O comando aparece na tela de proposito - metade do valor do exercicio e
# voce poder copiar a linha e rodar na mao depois.
#
# O eco vai para STDERR, nao para stdout: quase toda chamada aqui e capturada
# com $(...), e um echo em stdout entraria DENTRO da variavel - o jq receberia
# a linha do comando grudada no JSON e quebraria.
como_conta() {
  local conta="$1"; shift
  echo -e "  ${AZUL}\$ AWS_ACCESS_KEY_ID=${conta} aws $*${NEUTRO}" >&2
  AWS_ACCESS_KEY_ID="$conta" AWS_SECRET_ACCESS_KEY=test \
    aws --endpoint-url "$ENDPOINT" --region "$REGIAO" "$@" 2>&1
}

# Igual, mas silencioso: para os passos de preparo que nao ensinam nada.
quieto() {
  local conta="$1"; shift
  AWS_ACCESS_KEY_ID="$conta" AWS_SECRET_ACCESS_KEY=test \
    aws --endpoint-url "$ENDPOINT" --region "$REGIAO" "$@" >/dev/null 2>&1
}

limpar() {
  # IAM nao deixa apagar uma role que ainda tem inline policy pendurada.
  quieto "$ORIGEM"  iam delete-role-policy --role-name "$ROLE_ORIGEM"  --policy-name permite-assumir-dados
  quieto "$ORIGEM"  iam delete-role        --role-name "$ROLE_ORIGEM"
  quieto "$DESTINO" iam delete-role-policy --role-name "$ROLE_DESTINO" --policy-name gravar-auditoria
  quieto "$DESTINO" iam delete-role        --role-name "$ROLE_DESTINO"
  local url
  url=$(AWS_ACCESS_KEY_ID="$DESTINO" AWS_SECRET_ACCESS_KEY=test \
        aws --endpoint-url "$ENDPOINT" --region "$REGIAO" \
        sqs get-queue-url --queue-name "$FILA" --query QueueUrl --output text 2>/dev/null)
  [[ -n "$url" && "$url" != "None" ]] && quieto "$DESTINO" sqs delete-queue --queue-url "$url"
  return 0
}

if [[ "${1:-}" == "--limpar" ]]; then
  limpar
  echo -e "  ${VERDE}OK${NEUTRO}   roles e fila do exercicio removidas"
  exit 0
fi

echo "==================================================================="
echo " Exercicio: conta de ORIGEM e conta de DESTINO"
echo " Guia 02, secao 2"
echo "==================================================================="
echo
nota "origem  ${ORIGEM}  cash-prd   - onde o workload roda (cluster EKS, pods)"
nota "destino ${DESTINO}  dados-prd  - onde o recurso esta (fila de auditoria)"

# ===========================================================================
etapa "PRE-REQUISITO"
# ===========================================================================

if curl -sf "$ENDPOINT/_localstack/health" >/dev/null 2>&1; then
  passa "Floci respondendo em $ENDPOINT"
else
  falha "Floci nao responde em $ENDPOINT" "rode ./run.sh up"
  echo
  echo " Sem o emulador no ar o exercicio nao tem onde acontecer."
  exit 1
fi

command -v jq >/dev/null || { falha "jq nao encontrado" "brew install jq"; exit 1; }

# Re-rodar um exercicio e normal. Sem isso, o create-role da segunda vez
# falharia com EntityAlreadyExists e a licao viraria depuracao de script.
limpar
nota "estado anterior do exercicio limpo"

# ===========================================================================
etapa "ETAPA 1 - a fronteira de conta e real  [REAL]"
# ===========================================================================

echo "  A conta de DESTINO cria a fila de auditoria dela:"
echo
FILA_URL=$(como_conta "$DESTINO" sqs create-queue --queue-name "$FILA" \
             --query QueueUrl --output text)
echo "  -> $FILA_URL"
echo

if [[ -n "$FILA_URL" && "$FILA_URL" != "None" && "$FILA_URL" != *error* ]]; then
  passa "fila $FILA criada na conta de destino ($DESTINO)"
else
  falha "nao consegui criar a fila na conta $DESTINO" \
        "o emulador suporta SQS? veja ./run.sh logs floci"
fi

echo "  Agora a conta de ORIGEM pergunta quais filas existem."
echo "  Previsao antes de olhar: ela enxerga a fila da outra conta?"
echo
VISTO_DA_ORIGEM=$(como_conta "$ORIGEM" sqs list-queues --output json)
echo "$VISTO_DA_ORIGEM" | head -5 | sed 's/^/    /'
echo

if grep -q "$FILA" <<< "$VISTO_DA_ORIGEM"; then
  falha "a conta de origem enxergou a fila da conta de destino" \
        "o Floci pode estar sem isolamento por conta; confira o guia 02 secao 5"
else
  passa "a conta de origem NAO enxerga a fila da conta de destino"
  nota  "nenhuma conta se auto-concede acesso a outra - guia 02, 'a regra que"
  nota  "explica 90% das falhas'. E por isso que a fronteira de conta e a"
  nota  "fronteira de seguranca mais forte da AWS."
fi

# ===========================================================================
etapa "ETAPA 2 - um documento em cada lado  [FORMA]"
# ===========================================================================

echo "  Este e o mapa que o guia 02 desenha. Repare em QUEM cria o que:"
echo "  a origem cria UM documento, o destino cria DOIS."
echo

# ---- lado ORIGEM: a identity policy do worker -----------------------------
# "Eu, role do consumer, posso pedir para assumir aquela role de la."
quieto "$ORIGEM" iam create-role --role-name "$ROLE_ORIGEM" \
  --assume-role-policy-document '{
    "Version":"2012-10-17",
    "Statement":[{
      "Effect":"Allow",
      "Principal":{"Service":"eks.amazonaws.com"},
      "Action":"sts:AssumeRole"
    }]
  }'

IDENTITY_POLICY='{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "AssumirRoleDaContaDeDados",
    "Effect": "Allow",
    "Action": "sts:AssumeRole",
    "Resource": "'"$ARN_ROLE_DESTINO"'"
  }]
}'

echo -e "  ${AZUL}identity policy${NEUTRO} - mora na ORIGEM ($ORIGEM)"
nota "responde: esta role pode PEDIR para assumir aquela outra?"
echo "$IDENTITY_POLICY" | sed 's/^/    /'
echo

quieto "$ORIGEM" iam put-role-policy --role-name "$ROLE_ORIGEM" \
  --policy-name permite-assumir-dados --policy-document "$IDENTITY_POLICY"

if quieto "$ORIGEM" iam get-role --role-name "$ROLE_ORIGEM"; then
  passa "role de origem criada com a identity policy"
else
  falha "nao consegui criar a role na conta de origem"
fi

# ---- lado DESTINO: trust policy + permission policy -----------------------
# "Eu, role da conta de dados, aceito ser assumida por aquela role de la."
TRUST_POLICY='{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "ConfiaApenasNaRoleDoWorkerDeRegistro",
    "Effect": "Allow",
    "Principal": { "AWS": "'"$ARN_ROLE_ORIGEM"'" },
    "Action": "sts:AssumeRole"
  }]
}'

echo -e "  ${AZUL}trust policy${NEUTRO} - mora no DESTINO ($DESTINO)"
nota "responde: quem, la de fora, eu aceito que me assuma?"
echo "$TRUST_POLICY" | sed 's/^/    /'
echo

PERMISSION_POLICY='{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "GravarNaFilaDeAuditoria",
    "Effect": "Allow",
    "Action": "sqs:SendMessage",
    "Resource": "arn:aws:sqs:'"$REGIAO"':'"$DESTINO"':'"$FILA"'"
  }]
}'

echo -e "  ${AZUL}permission policy${NEUTRO} - mora no DESTINO ($DESTINO)"
nota "responde: depois de assumida, o que ela faz?"
echo "$PERMISSION_POLICY" | sed 's/^/    /'
echo

quieto "$DESTINO" iam create-role --role-name "$ROLE_DESTINO" \
  --assume-role-policy-document "$TRUST_POLICY"
quieto "$DESTINO" iam put-role-policy --role-name "$ROLE_DESTINO" \
  --policy-name gravar-auditoria --policy-document "$PERMISSION_POLICY"

if quieto "$DESTINO" iam get-role --role-name "$ROLE_DESTINO"; then
  passa "role de destino criada com trust policy + permission policy"
else
  falha "nao consegui criar a role na conta de destino"
fi

echo
nota "[FORMA] o Floci aceitou os tres JSONs sem avaliar nenhum deles."
nota "Ele nao e ponto de decisao de autorizacao. O que voce praticou aqui foi"
nota "QUAL DOCUMENTO MORA EM QUAL CONTA - e isso vale, porque e o que a"
nota "maioria erra. Se a policy CONCEDE o certo, so o Policy Simulator diz."

# ===========================================================================
etapa "ETAPA 3 - a travessia: a identidade muda de conta  [REAL]"
# ===========================================================================

echo "  Quem voce e ANTES de assumir:"
echo
ANTES=$(como_conta "$ORIGEM" sts get-caller-identity --output json)
echo "$ANTES" | sed 's/^/    /'
echo

CONTA_ANTES=$(echo "$ANTES" | jq -r '.Account // empty')
if [[ "$CONTA_ANTES" == "$ORIGEM" ]]; then
  passa "identidade inicial esta na conta de ORIGEM ($ORIGEM)"
else
  falha "esperava a conta $ORIGEM, veio '${CONTA_ANTES:-vazio}'" \
        "no Floci a conta vem do AWS_ACCESS_KEY_ID de 12 digitos"
fi

echo "  Agora a travessia. Repare no --role-session-name: ele nao e"
echo "  decorativo, e o que responde 'quem foi' no CloudTrail do destino."
echo
CRED=$(como_conta "$ORIGEM" sts assume-role \
        --role-arn "$ARN_ROLE_DESTINO" \
        --role-session-name exercicio-origem-destino \
        --duration-seconds 3600 \
        --output json)
echo "$CRED" | sed 's/^/    /' | head -20
echo

KEY=$(echo "$CRED" | jq -r '.Credentials.AccessKeyId // empty')
SECRET=$(echo "$CRED" | jq -r '.Credentials.SecretAccessKey // empty')
TOKEN=$(echo "$CRED" | jq -r '.Credentials.SessionToken // empty')

if [[ -z "$KEY" ]]; then
  falha "o AssumeRole nao devolveu credenciais" \
        "veja a resposta acima; o emulador suporta sts:AssumeRole?"
  echo
  echo " Sem a credencial assumida as proximas etapas nao tem o que mostrar."
  exit 1
fi

# A dica de diagnostico do guia 02, secao 1: o prefixo do AccessKeyId diz
# tudo. AKIA = permanente, de usuario IAM. ASIA = temporaria, do STS.
if [[ "$KEY" == ASIA* ]]; then
  passa "AccessKeyId comeca com ASIA - credencial TEMPORARIA, do STS"
else
  falha "AccessKeyId comeca com ${KEY:0:4}, esperava ASIA" \
        "AKIA seria credencial permanente de usuario IAM - errado num pod"
fi

if [[ -n "$TOKEN" ]]; then
  passa "veio SessionToken - credencial permanente nao tem um"
else
  falha "sem SessionToken na resposta"
fi

echo
echo "  E agora o momento do exercicio. MESMO TERMINAL, credencial nova:"
echo
DEPOIS=$(AWS_ACCESS_KEY_ID="$KEY" AWS_SECRET_ACCESS_KEY="$SECRET" \
         AWS_SESSION_TOKEN="$TOKEN" \
         aws --endpoint-url "$ENDPOINT" --region "$REGIAO" \
         sts get-caller-identity --output json 2>&1)
echo "$DEPOIS" | sed 's/^/    /'
echo

CONTA_DEPOIS=$(echo "$DEPOIS" | jq -r '.Account // empty' 2>/dev/null)
ARN_DEPOIS=$(echo "$DEPOIS" | jq -r '.Arn // empty' 2>/dev/null)

if [[ "$CONTA_DEPOIS" == "$DESTINO" ]]; then
  passa "a conta virou $DESTINO - voce atravessou a fronteira"
  nota  "de $ORIGEM (origem) para $DESTINO (destino), sem trocar de terminal"
else
  falha "esperava a conta $DESTINO, veio '${CONTA_DEPOIS:-vazio}'" \
        "credencial de AssumeRole deveria resolver para a conta da role assumida"
fi

# ---- a pegadinha do ARN da sessao ----------------------------------------
if [[ "$ARN_DEPOIS" == *":assumed-role/"* ]]; then
  passa "a identidade e uma SESSAO da role, nao a role"
else
  falha "esperava um ARN de sessao (assumed-role/), veio '${ARN_DEPOIS:-vazio}'"
fi

# Limite de fidelidade observado no Floci: ele IGNORA o --role-session-name e
# carimba um nome proprio (floci-session). Avisar e melhor que deixar o aluno
# comparar o ARN com o que pediu e achar que entendeu errado - ainda mais
# depois do script ter dito que o session name nao e decorativo.
if [[ -n "$ARN_DEPOIS" && "$ARN_DEPOIS" != */exercicio-origem-destino ]]; then
  echo
  nota "[emulador] pedi --role-session-name exercicio-origem-destino e o ARN"
  nota "veio com '$(echo "$ARN_DEPOIS" | sed 's|.*/||')'. O Floci ignora o nome"
  nota "de sessao. Na AWS ele aparece no ARN e no CloudTrail do destino - e e"
  nota "exatamente o que responde 'quem foi'. Limite do emulador, nao seu erro."
fi

echo
echo -e "  ${AMARELO}A pegadinha que consome tardes inteiras:${NEUTRO}"
echo
echo "    o que voce ACABOU DE VER (a sessao):"
echo -e "      ${VERMELHO}${ARN_DEPOIS:-arn:aws:sts::...:assumed-role/Role/Sessao}${NEUTRO}"
echo
echo "    o que uma POLICY precisa (a role):"
echo -e "      ${VERDE}${ARN_ROLE_DESTINO}${NEUTRO}"
echo
nota "sts, nao iam. assumed-role/Nome/Sessao, nao role/Nome. Copiar o ARN do"
nota "get-caller-identity para dentro de uma bucket policy nao funciona - e a"
nota "mensagem de erro mostra justamente o ARN que nao serve."
nota "A excecao e dentro de Condition, onde aws:PrincipalArn opera na sessao."

# ===========================================================================
etapa "ETAPA 4 - com a credencial do destino, a fila aparece  [REAL]"
# ===========================================================================

echo "  Na ETAPA 1 a conta de origem nao enxergava esta fila."
echo "  A credencial assumida resolve para a conta de DESTINO - entao:"
echo
echo -e "  ${AZUL}\$ AWS_ACCESS_KEY_ID=ASIA... AWS_SESSION_TOKEN=... aws sqs list-queues${NEUTRO}"
AGORA=$(AWS_ACCESS_KEY_ID="$KEY" AWS_SECRET_ACCESS_KEY="$SECRET" \
        AWS_SESSION_TOKEN="$TOKEN" \
        aws --endpoint-url "$ENDPOINT" --region "$REGIAO" \
        sqs list-queues --output json 2>&1)
echo "$AGORA" | head -5 | sed 's/^/    /'
echo

if grep -q "$FILA" <<< "$AGORA"; then
  passa "com a credencial assumida, a fila do destino esta visivel"
  nota  "mesma conta, mesmos comandos, mesma fila. O que mudou foi QUEM pergunta."
else
  falha "a fila ainda nao aparece com a credencial assumida" \
        "a credencial do AssumeRole deveria resolver para a conta $DESTINO"
fi

echo
echo "  E a gravacao de verdade, agindo como principal da conta de destino:"
echo
ENVIO=$(AWS_ACCESS_KEY_ID="$KEY" AWS_SECRET_ACCESS_KEY="$SECRET" \
        AWS_SESSION_TOKEN="$TOKEN" \
        aws --endpoint-url "$ENDPOINT" --region "$REGIAO" \
        sqs send-message --queue-url "$FILA_URL" \
        --message-body '{"evento":"comprovante.gravado","origem":"consumer-registro"}' \
        --output json 2>&1)
echo "$ENVIO" | sed 's/^/    /' | head -6
echo

if grep -q MessageId <<< "$ENVIO"; then
  passa "mensagem gravada na fila da conta de destino"
else
  falha "o send-message nao devolveu MessageId"
fi

# ===========================================================================
etapa "PARA FIXAR"
# ===========================================================================

cat <<'PERGUNTAS'
  Responda sem voltar ao guia. Se travar em alguma, o guia 02 secao 2 responde.

  1. Na ETAPA 2 voce criou tres documentos. Quantos ficaram na conta de
     ORIGEM, e por que a assimetria explica onde os AccessDenied cross-account
     costumam morar?

  2. O ARN que apareceu no get-caller-identity depois do assume-role nao serve
     numa bucket policy. Qual voce colocaria no lugar, e qual e a diferenca de
     uma letra no comeco do ARN?

  3. Se uma Lambda da conta 444455556666 passasse a LER a fila
     cobranca-registro da conta 111122223333, qual das duas seria a conta de
     origem daquela chamada?

  4. A fila deste exercicio nao usa KMS. Se usasse, qual QUARTO documento
     entraria, em qual das duas contas, e por que ter kms:Decrypt na role
     assumida nao bastaria?

  5. No padrao 3 (IRSA cross-account) o provedor OIDC e registrado na conta de
     DESTINO, apontando para o cluster que roda na ORIGEM. Isso muda quem e a
     origem? Por que?

  6. Este exercicio criou as policies e o Floci aceitou todas. Se a identity
     policy da ETAPA 2 tivesse "Resource" apontando para a role ERRADA, em que
     ponto exato deste script voce teria percebido? (Pense antes de responder:
     a resposta honesta e desconfortavel.)
PERGUNTAS

# ===========================================================================
echo
echo "==================================================================="
echo -e " ${VERDE}${PASSOU} passaram${NEUTRO}   ${VERMELHO}${FALHOU} falharam${NEUTRO}"
echo "==================================================================="
echo
echo -e " ${AMARELO}O que este exercicio provou e o que nao provou:${NEUTRO}"
echo
echo "   PROVOU   o isolamento entre contas e real"
echo "   PROVOU   AssumeRole devolve credencial temporaria (ASIA + SessionToken)"
echo "   PROVOU   a identidade resultante troca de conta e vira uma SESSAO"
echo "   PROVOU   a mesma pergunta tem respostas diferentes por conta"
echo
echo "   NAO PROVOU  que as policies concedem o acesso certo. O Floci nao"
echo "               avalia autorizacao: uma policy mal escrita passa aqui e"
echo "               falha na AWS. Para isso, IAM Policy Simulator."
echo
echo " Limpar o que foi criado:  ./run.sh cross-account --limpar"

[[ $FALHOU -eq 0 ]] || exit 1
exit 0
