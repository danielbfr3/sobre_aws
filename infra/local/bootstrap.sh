#!/usr/bin/env bash
#
# Cria a infraestrutura do lab no Floci: topico(s) SNS, filas SQS, DLQs,
# resource policies e subscriptions.
#
# Roda com o emulador JA DE PE - por isso o run.sh chama este script no passo
# 2, depois de esperar o /_localstack/health responder.
#
# No fim grava o .env na raiz do repositorio. O docker compose le esse arquivo
# sozinho, e e assim que o publisher descobre o ARN de um topico que so passou
# a existir agora.
#
# Modos:
#   TOPIC_MODE=single  (padrao)  1 topico + 3 subscriptions com FilterPolicy
#   TOPIC_MODE=multi             3 topicos independentes, sem filtro
#
# O modo multi existe como escape: se o emulador ignorar a FilterPolicy, o
# ./run.sh verify acusa "worker recebeu evento que nao e dele" e a saida e
# rodar  ./run.sh reset && TOPIC_MODE=multi ./run.sh up
#
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

VERDE='\033[0;32m'; AMARELO='\033[0;33m'; NEUTRO='\033[0m'
ok() { echo -e "  ${VERDE}OK${NEUTRO}   $*"; }

ENDPOINT="${AWS_ENDPOINT_URL:-http://localhost:4566}"
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"
export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-test}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-test}"
REGIAO="$AWS_DEFAULT_REGION"
CONTA="000000000000"
MODO="${TOPIC_MODE:-single}"

# O endpoint que os CONTAINERS usam. O seu terminal fala com localhost:4566;
# os workers, na rede do compose, falam com floci:4566. As URLs de fila
# gravadas no .env precisam ser as da rede interna.
ENDPOINT_INTERNO="${ENDPOINT_INTERNO:-http://floci:4566}"

aws_local() { aws --endpoint-url "$ENDPOINT" --output text "$@"; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# fila -> tipo de evento que ela recebe
WORKERS=("registro" "baixa" "rejeicao")
EVENTOS=("cobranca.registrada" "cobranca.baixada" "cobranca.rejeitada")

# ---------------------------------------------------------------------------
# 1. Topico(s)
# ---------------------------------------------------------------------------
declare -a TOPICOS_POR_EVENTO=()

if [[ "$MODO" == "multi" ]]; then
  for i in "${!WORKERS[@]}"; do
    NOME="eventos-cobranca-${WORKERS[$i]}"
    ARN=$(aws_local sns create-topic --name "$NOME" --query TopicArn)
    TOPICOS_POR_EVENTO+=("$ARN")
    echo "    topico: $ARN"
  done
  TOPIC_ARN_PRINCIPAL="${TOPICOS_POR_EVENTO[0]}"
else
  TOPIC_ARN_PRINCIPAL=$(aws_local sns create-topic --name eventos-cobranca --query TopicArn)
  echo "    topico: $TOPIC_ARN_PRINCIPAL"
  for _ in "${!WORKERS[@]}"; do TOPICOS_POR_EVENTO+=("$TOPIC_ARN_PRINCIPAL"); done
fi

# ---------------------------------------------------------------------------
# 2. Para cada worker: DLQ, fila, redrive, resource policy, subscription
# ---------------------------------------------------------------------------
for i in "${!WORKERS[@]}"; do
  W="${WORKERS[$i]}"
  EVENTO="${EVENTOS[$i]}"
  TOPICO="${TOPICOS_POR_EVENTO[$i]}"
  FILA="cobranca-${W}"
  DLQ="cobranca-${W}-dlq"

  # --- a DLQ vem PRIMEIRO: a fila principal precisa do ARN dela para a
  #     RedrivePolicy. Criar na ordem inversa obriga a um segundo passo.
  DLQ_URL=$(aws_local sqs create-queue --queue-name "$DLQ" --query QueueUrl)
  DLQ_ARN=$(aws_local sqs get-queue-attributes --queue-url "$DLQ_URL" \
              --attribute-names QueueArn --query 'Attributes.QueueArn')

  FILA_URL=$(aws_local sqs create-queue --queue-name "$FILA" --query QueueUrl)
  FILA_ARN=$(aws_local sqs get-queue-attributes --queue-url "$FILA_URL" \
               --attribute-names QueueArn --query 'Attributes.QueueArn')

  # --- RedrivePolicy ------------------------------------------------------
  # O valor do atributo e uma STRING que contem JSON. Dai o \" - e a fonte
  # classica de erro de escape neste script. Passar por arquivo (file://)
  # em vez de linha de comando evita mais uma camada de shell no meio.
  #
  # maxReceiveCount=3: a mensagem e tentada 3 vezes antes de ir para a DLQ.
  # E o numero que a etapa 9 do ROTEIRO espera ver no log.
  cat > "$TMP/redrive.json" <<JSON
{
  "RedrivePolicy": "{\"deadLetterTargetArn\":\"$DLQ_ARN\",\"maxReceiveCount\":\"3\"}",
  "VisibilityTimeout": "60",
  "MessageRetentionPeriod": "345600"
}
JSON
  aws --endpoint-url "$ENDPOINT" sqs set-queue-attributes \
      --queue-url "$FILA_URL" --attributes "file://$TMP/redrive.json" >/dev/null

  # --- Resource policy da fila -------------------------------------------
  # O segundo tipo de policy do guia 01 secao 3: nao esta colada em quem
  # chama, esta colada NO RECURSO. Sem ela, em AWS de verdade, o SNS nao
  # consegue entregar - e o sintoma e o pior possivel: publish retorna
  # sucesso, subscription aparece confirmada, e a mensagem nunca chega.
  #
  # A Condition com aws:SourceArn e o que impede que QUALQUER topico da
  # conta escreva nesta fila. Sem ela a policy seria frouxa demais.
  cat > "$TMP/policy.json" <<JSON
{
  "Policy": "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Sid\":\"PermiteSnsEntregarNestaFila\",\"Effect\":\"Allow\",\"Principal\":{\"Service\":\"sns.amazonaws.com\"},\"Action\":\"sqs:SendMessage\",\"Resource\":\"$FILA_ARN\",\"Condition\":{\"ArnEquals\":{\"aws:SourceArn\":\"$TOPICO\"}}}]}"
}
JSON
  aws --endpoint-url "$ENDPOINT" sqs set-queue-attributes \
      --queue-url "$FILA_URL" --attributes "file://$TMP/policy.json" >/dev/null

  # --- Subscription -------------------------------------------------------
  SUB_ARN=$(aws_local sns subscribe \
              --topic-arn "$TOPICO" \
              --protocol sqs \
              --notification-endpoint "$FILA_ARN" \
              --query SubscriptionArn)

  # RawMessageDelivery=true: sem isto o SNS embrulha o seu JSON dentro do
  # envelope dele e o consumer precisaria desembrulhar. Com raw, o Body da
  # mensagem SQS e exatamente o que foi publicado - e o hash do payload
  # (que vira o nome do comprovante) fica estavel.
  aws --endpoint-url "$ENDPOINT" sns set-subscription-attributes \
      --subscription-arn "$SUB_ARN" \
      --attribute-name RawMessageDelivery --attribute-value true >/dev/null

  if [[ "$MODO" != "multi" ]]; then
    # A FilterPolicy e o roteamento. Repare que ela mora na SUBSCRIPTION,
    # nao no publisher e nao no consumer: nenhum dos tres workers tem uma
    # linha de codigo de filtro.
    aws --endpoint-url "$ENDPOINT" sns set-subscription-attributes \
        --subscription-arn "$SUB_ARN" \
        --attribute-name FilterPolicy \
        --attribute-value "{\"eventType\":[\"$EVENTO\"]}" >/dev/null
  fi

  echo "    fila: $FILA  <-  $EVENTO"
done

# ---------------------------------------------------------------------------
# 3. O .env que o docker compose le sozinho
# ---------------------------------------------------------------------------
# O publisher nao recebe "o topico": ele recebe um ARN POR TIPO DE EVENTO.
# Em modo single os tres sao o mesmo valor. Isso deixa o codigo do publisher
# igual nos dois modos - ele nunca pergunta em que modo esta.
cat > .env <<ENV
# Gerado por infra/local/bootstrap.sh - nao edite na mao.
TOPIC_MODE=$MODO
TOPIC_ARN=$TOPIC_ARN_PRINCIPAL
TOPIC_ARN_COBRANCA_REGISTRADA=${TOPICOS_POR_EVENTO[0]}
TOPIC_ARN_COBRANCA_BAIXADA=${TOPICOS_POR_EVENTO[1]}
TOPIC_ARN_COBRANCA_REJEITADA=${TOPICOS_POR_EVENTO[2]}
QUEUE_URL_REGISTRO=$ENDPOINT_INTERNO/$CONTA/cobranca-registro
QUEUE_URL_BAIXA=$ENDPOINT_INTERNO/$CONTA/cobranca-baixa
QUEUE_URL_REJEICAO=$ENDPOINT_INTERNO/$CONTA/cobranca-rejeicao
ENV

echo "    .env gerado com TOPIC_ARN=$TOPIC_ARN_PRINCIPAL"
ok "infraestrutura pronta (modo: $MODO, regiao: $REGIAO)"
