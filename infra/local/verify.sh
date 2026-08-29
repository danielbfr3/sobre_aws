#!/usr/bin/env bash
#
# Verifica se o lab produziu OS RESULTADOS ESPERADOS.
#
# Nao e um teste de fumaca generico: cada checagem corresponde a um conceito
# ensinado nos guias. Quando uma falha, a mensagem diz qual capitulo revisar.
#
# Uso:  ./run.sh verify
#
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

VERDE='\033[0;32m'; VERMELHO='\033[0;31m'; AMARELO='\033[0;33m'; NEUTRO='\033[0m'
PASSOU=0; FALHOU=0

ENDPOINT="${AWS_ENDPOINT_URL:-http://localhost:4566}"
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"
export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-test}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-test}"
CONTA="000000000000"

aws_local() { aws --endpoint-url "$ENDPOINT" "$@" 2>/dev/null; }
compose() {
  if docker compose version >/dev/null 2>&1; then docker compose "$@"
  else docker-compose "$@"; fi
}

passa() { echo -e "  ${VERDE}PASSOU${NEUTRO}  $1"; PASSOU=$((PASSOU+1)); }
falha() {
  echo -e "  ${VERMELHO}FALHOU${NEUTRO}  $1"
  [[ -n "${2:-}" ]] && echo -e "          ${AMARELO}$2${NEUTRO}"
  FALHOU=$((FALHOU+1))
}
secao() { echo; echo "--- $1"; }

QUEUES=("cobranca-registro" "cobranca-baixa" "cobranca-rejeicao")
EVENTOS=("cobranca.registrada" "cobranca.baixada" "cobranca.rejeitada")
WORKERS=("registro" "baixa" "rejeicao")

echo "==================================================================="
echo " Verificando os resultados esperados do lab"
echo "==================================================================="

# ===========================================================================
secao "1. Infraestrutura (guia 01, README secao 1)"
# ===========================================================================

if curl -sf "$ENDPOINT/_localstack/health" >/dev/null 2>&1; then
  passa "Floci respondendo"
else
  falha "Floci nao responde em $ENDPOINT" "rode ./run.sh up"
  echo; echo "Sem o emulador no ar nao da para verificar mais nada."; exit 1
fi

TOPICOS=$(aws_local sns list-topics --query 'Topics[].TopicArn' --output text)
if [[ -n "$TOPICOS" ]]; then
  passa "topico(s) SNS criado(s)"
else
  falha "nenhum topico SNS" "rode ./run.sh bootstrap"
fi

# ===========================================================================
secao "2. Filas e DLQs (README secao 1)"
# ===========================================================================

for q in "${QUEUES[@]}"; do
  if aws_local sqs get-queue-url --queue-name "$q" >/dev/null; then
    passa "fila $q existe"
  else
    falha "fila $q nao existe" "rode ./run.sh bootstrap"
    continue
  fi

  if aws_local sqs get-queue-url --queue-name "${q}-dlq" >/dev/null; then
    passa "DLQ ${q}-dlq existe"
  else
    falha "DLQ ${q}-dlq nao existe"
  fi

  URL=$(aws_local sqs get-queue-url --queue-name "$q" --query QueueUrl --output text)

  # RedrivePolicy: sem isso, mensagem problematica fica em loop eterno
  REDRIVE=$(aws_local sqs get-queue-attributes --queue-url "$URL" \
       --attribute-names RedrivePolicy --query 'Attributes.RedrivePolicy' \
       --output text || true)
  if grep -q deadLetterTargetArn <<< "$REDRIVE"; then
    passa "$q tem RedrivePolicy apontando para a DLQ"
  else
    falha "$q sem RedrivePolicy" "mensagem com erro ficaria em loop eterno"
  fi

  # Resource policy: o segundo tipo de policy, do guia 01 secao 3
  QPOLICY=$(aws_local sqs get-queue-attributes --queue-url "$URL" \
       --attribute-names Policy --query 'Attributes.Policy' \
       --output text || true)
  if grep -q "sns.amazonaws.com" <<< "$QPOLICY"; then
    passa "$q tem resource policy permitindo o SNS"
  else
    falha "$q sem resource policy para o SNS" \
          "em AWS real a mensagem sumiria em silencio - guia 01 secao 3"
  fi
done

# ===========================================================================
secao "3. Subscriptions e roteamento (README secao 1)"
# ===========================================================================

TOPIC_ARN=$(echo "$TOPICOS" | tr '\t' '\n' | grep 'eventos-cobranca$' | head -1)
MODO="multi"
if [[ -n "$TOPIC_ARN" ]]; then
  MODO="single"
  N_SUBS=$(aws_local sns list-subscriptions-by-topic --topic-arn "$TOPIC_ARN" \
             --query 'length(Subscriptions)' --output text)
  if [[ "$N_SUBS" == "3" ]]; then
    passa "3 subscriptions no topico"
  else
    falha "esperava 3 subscriptions, achei ${N_SUBS:-0}"
  fi

  COM_FILTRO=0
  for arn in $(aws_local sns list-subscriptions-by-topic --topic-arn "$TOPIC_ARN" \
                 --query 'Subscriptions[].SubscriptionArn' --output text); do
    ATTRS=$(aws_local sns get-subscription-attributes --subscription-arn "$arn" \
              --query 'Attributes' --output json)
    echo "$ATTRS" | grep -q '"FilterPolicy"' && COM_FILTRO=$((COM_FILTRO+1))
    echo "$ATTRS" | grep -q '"RawMessageDelivery": *"true"' \
      || falha "subscription sem RawMessageDelivery" "o corpo viria embrulhado no envelope do SNS"
  done

  if [[ "$COM_FILTRO" == "3" ]]; then
    passa "3 subscriptions com FilterPolicy"
  else
    falha "so $COM_FILTRO de 3 subscriptions com FilterPolicy" \
          "o emulador pode nao suportar - use TOPIC_MODE=multi"
  fi
else
  passa "modo multi: 3 topicos independentes (sem FilterPolicy)"
fi

# ===========================================================================
secao "4. Mensagens fluindo (README secao 1)"
# ===========================================================================

TOTAL_DLQ=0
for q in "${QUEUES[@]}"; do
  URL=$(aws_local sqs get-queue-url --queue-name "${q}-dlq" --query QueueUrl --output text)
  N=$(aws_local sqs get-queue-attributes --queue-url "$URL" \
        --attribute-names ApproximateNumberOfMessages \
        --query 'Attributes.ApproximateNumberOfMessages' --output text)
  TOTAL_DLQ=$((TOTAL_DLQ + ${N:-0}))
done

if [[ "$TOTAL_DLQ" == "0" ]]; then
  passa "nenhuma mensagem nas DLQs"
else
  falha "$TOTAL_DLQ mensagem(ns) nas DLQs" \
        "algo falhou 3 vezes; veja ./run.sh logs"
fi

# ===========================================================================
secao "5. Comprovantes no volume (README secao 3)"
# ===========================================================================

# Capturado numa variavel de proposito: com "set -o pipefail", um
# "compose ps | grep -q" pode falhar sem motivo - o grep encontra o que
# procurava e sai, o compose morre de SIGPIPE, e o status do PIPELINE vira
# erro. Bug intermitente classico de shell script com pipefail ligado.
PS_RODANDO=$(compose ps --status running 2>/dev/null || true)
if ! grep -q consumer-registro <<< "$PS_RODANDO"; then
  falha "consumer-registro nao esta rodando" "rode ./run.sh workers"
else
  TOTAL=$(compose exec -T consumer-registro sh -c \
            'find /data/comprovantes -name "*.txt" 2>/dev/null | wc -l' 2>/dev/null | tr -d '\r ')

  if [[ "${TOTAL:-0}" -gt 0 ]]; then
    passa "$TOTAL comprovante(s) .txt gravado(s) no volume"
  else
    falha "nenhum comprovante gerado" \
          "aguarde ~30s apos ./run.sh up, ou veja ./run.sh logs consumer-registro"
  fi

  # ---- O TESTE MAIS IMPORTANTE ------------------------------------------
  # Cada worker so pode ter recebido o SEU tipo de evento. Se o roteamento
  # do broker funcionou, o comprovante do worker "baixa" nunca contem
  # "cobranca.registrada". Isso valida a FilterPolicy de ponta a ponta.
  # -----------------------------------------------------------------------
  for i in "${!WORKERS[@]}"; do
    W="${WORKERS[$i]}"; E="${EVENTOS[$i]}"

    N_W=$(compose exec -T consumer-registro sh -c \
            "find /data/comprovantes/$W -name '*.txt' 2>/dev/null | wc -l" 2>/dev/null | tr -d '\r ')

    if [[ "${N_W:-0}" -eq 0 ]]; then
      falha "worker '$W' sem comprovantes" "veja ./run.sh logs consumer-$W"
      continue
    fi

    # o volume e compartilhado: um container enxerga o que o outro escreveu
    ERRADOS=$(compose exec -T consumer-registro sh -c \
      "grep -L '$E' /data/comprovantes/$W/*/*.txt 2>/dev/null | wc -l" 2>/dev/null | tr -d '\r ')

    if [[ "${ERRADOS:-1}" -eq 0 ]]; then
      passa "roteamento OK: os $N_W comprovantes de '$W' sao todos de $E"
    else
      falha "worker '$W' recebeu $ERRADOS evento(s) que nao sao dele" \
            "a FilterPolicy nao esta filtrando - use TOPIC_MODE=multi"
    fi
  done

  # ---- volume compartilhado ---------------------------------------------
  VISIVEIS=$(compose exec -T consumer-registro sh -c \
    'ls /data/comprovantes 2>/dev/null | wc -l' 2>/dev/null | tr -d '\r ')
  if [[ "${VISIVEIS:-0}" -ge 2 ]]; then
    passa "volume compartilhado: consumer-registro enxerga $VISIVEIS pastas de workers"
  else
    falha "consumer-registro so enxerga ${VISIVEIS:-0} pasta(s)" \
          "esperado: o volume e ReadWriteMany, cada um ve o do outro"
  fi

  # ---- idempotencia -----------------------------------------------------
  # O nome do arquivo deriva do hash do payload. Nomes repetidos entre
  # diretorios de data seriam sinal de reprocessamento nao detectado.
  DUPLICADOS=$(compose exec -T consumer-registro sh -c \
    'find /data/comprovantes -name "*.txt" 2>/dev/null | xargs -r -n1 basename | sort | uniq -d | wc -l' \
    2>/dev/null | tr -d '\r ')
  if [[ "${DUPLICADOS:-1}" -eq 0 ]]; then
    passa "idempotencia: nenhum comprovante duplicado"
  else
    falha "$DUPLICADOS nome(s) de comprovante repetido(s)"
  fi
fi

# ===========================================================================
echo
echo "==================================================================="
echo -e " ${VERDE}${PASSOU} passaram${NEUTRO}   ${VERMELHO}${FALHOU} falharam${NEUTRO}   (modo: ${MODO})"
echo "==================================================================="

if [[ $FALHOU -eq 0 ]]; then
  echo
  echo " O lab esta produzindo os resultados esperados."
  echo " Proximo passo: os experimentos da secao 6 do README."
  exit 0
fi
exit 1
