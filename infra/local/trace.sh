#!/usr/bin/env bash
#
# Remonta a cadeia de uma mensagem, do publish ao comprovante.
#
# Os quatro processos (publisher + 3 consumers, em 3 linguagens) escrevem
# NDJSON em /data/logs/<servico>-<host>.ndjson. Este script junta tudo,
# ordena e filtra por traceId.
#
# Uso:
#   ./run.sh trace              lista os traces mais recentes
#   ./run.sh trace <id>         mostra a cadeia daquele trace
#   ./run.sh trace --dlq        so os traces que falharam (rumo a DLQ)
#
# Repare no que NAO tem aqui: nenhum jq, nenhum parser. O `sort` de texto puro
# ordena os eventos corretamente porque "ts" e a PRIMEIRA chave do JSON e tem
# largura fixa. Foi essa decisao, la no log.py, que deixou este script curto.
#
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

VERDE='\033[0;32m'; VERMELHO='\033[0;31m'; AMARELO='\033[0;33m'
AZUL='\033[0;34m'; CINZA='\033[0;90m'; NEUTRO='\033[0m'

compose() {
  if docker compose version >/dev/null 2>&1; then docker compose "$@"
  else docker-compose "$@"; fi
}

# Junta os arquivos de todos os processos e ordena por tempo.
# Roda dentro do consumer-registro porque e ele que tem o volume montado -
# qualquer um dos quatro serviria: o volume e ReadWriteMany.
todos_os_logs() {
  compose exec -T consumer-registro sh -c \
    'cat /data/logs/*.ndjson 2>/dev/null | sort' 2>/dev/null
}

campo() {  # campo <linha> <chave>   - extrai "chave":"valor" sem jq
  sed -n "s/.*\"$2\":\"\([^\"]*\)\".*/\1/p" <<< "$1"
}

LOGS="$(todos_os_logs)"
if [[ -z "$LOGS" ]]; then
  echo "nenhum log encontrado em /data/logs/"
  echo "o lab esta no ar? rode ./run.sh up e aguarde alguns segundos"
  exit 1
fi

ALVO="${1:-}"

# ---------------------------------------------------------------------------
# Sem argumento: lista os traces mais recentes
# ---------------------------------------------------------------------------
if [[ -z "$ALVO" || "$ALVO" == "--dlq" ]]; then
  SO_FALHA=0
  [[ "$ALVO" == "--dlq" ]] && SO_FALHA=1

  echo "==================================================================="
  if [[ $SO_FALHA -eq 1 ]]; then
    echo " Traces que falharam (a caminho da DLQ)"
  else
    echo " Traces recentes"
  fi
  echo "==================================================================="
  echo
  printf "  %-34s %-10s %-9s %-22s %s\n" "TRACE" "WORKER" "LING" "EVENTO FINAL" "TENTATIVAS"
  echo "  ---------------------------------------------------------------------------------------"

  # Um trace por linha, com o ultimo evento de cada um.
  while read -r trace; do
    [[ -z "$trace" ]] && continue
    DO_TRACE=$(grep -F "\"traceId\":\"$trace\"" <<< "$LOGS")
    ULTIMA=$(tail -1 <<< "$DO_TRACE")

    # O DESFECHO nao e simplesmente o ultimo evento.
    #
    # Uma duplicata termina em "mensagem.deletada" igual a uma gravacao bem
    # sucedida - ela foi processada e saiu da fila, afinal. O que distingue as
    # duas e um evento no MEIO da cadeia. Por isso o desfecho e procurado, e
    # nao lido do fim. (O visualizador classifica pela mesma regra.)
    if grep -qF '"evento":"mensagem.falhou"' <<< "$DO_TRACE"; then
      EVT="mensagem.falhou"
    elif grep -qF '"evento":"comprovante.duplicado"' <<< "$DO_TRACE"; then
      EVT="comprovante.duplicado"
    else
      EVT=$(campo "$ULTIMA" evento)
    fi
    [[ $SO_FALHA -eq 1 && "$EVT" != "mensagem.falhou" ]] && continue

    # o worker/linguagem que CONSUMIU (o publisher aparece primeiro)
    LINHA_CONS=$(grep -F '"servico":"consumer"' <<< "$DO_TRACE" | tail -1)
    [[ -z "$LINHA_CONS" ]] && LINHA_CONS="$ULTIMA"
    W=$(campo "$LINHA_CONS" worker)
    L=$(campo "$LINHA_CONS" linguagem)
    N=$(grep -cF '"evento":"mensagem.recebida"' <<< "$DO_TRACE")

    case "$EVT" in
      comprovante.gravado|mensagem.deletada) COR="$VERDE" ;;
      comprovante.duplicado)                 COR="$AMARELO" ;;
      mensagem.falhou)                       COR="$VERMELHO" ;;
      *)                                     COR="$CINZA" ;;
    esac
    printf "  %-34s %-10s %-9s ${COR}%-22s${NEUTRO} %s\n" "$trace" "$W" "$L" "$EVT" "$N"
  done < <(grep -o '"traceId":"[0-9a-f]\{32\}"' <<< "$LOGS" \
             | sed 's/.*:"//;s/"//' | awk '!visto[$0]++' | tail -25)

  echo
  echo "  Para ver a cadeia inteira de um deles:"
  echo "      ./run.sh trace <TRACE>"
  echo
  echo "  Para abrir no visualizador:"
  echo "      ./run.sh exportar-logs   e depois carregue o .ndjson na aba 'Logs reais'"
  exit 0
fi

# ---------------------------------------------------------------------------
# Com argumento: a cadeia daquele trace
# ---------------------------------------------------------------------------
# Aceita o trace curto (8 primeiros), que e o que aparece no stdout dos workers.
DO_TRACE=$(grep -F "\"traceId\":\"$ALVO" <<< "$LOGS")
if [[ -z "$DO_TRACE" ]]; then
  echo "trace '$ALVO' nao encontrado."
  echo "rode ./run.sh trace sem argumento para listar os disponiveis."
  exit 1
fi

TRACE_CHEIO=$(campo "$(head -1 <<< "$DO_TRACE")" traceId)
echo "==================================================================="
echo " Cadeia do trace $TRACE_CHEIO"
echo "==================================================================="
echo

PRIMEIRO_TS=""
while IFS= read -r linha; do
  [[ -z "$linha" ]] && continue
  TS=$(campo "$linha" ts)
  EVT=$(campo "$linha" evento)
  SVC=$(campo "$linha" servico)
  W=$(campo "$linha" worker)
  L=$(campo "$linha" linguagem)
  MSG=$(campo "$linha" msg)
  NIVEL=$(campo "$linha" nivel)

  [[ -z "$PRIMEIRO_TS" ]] && PRIMEIRO_TS="$TS"

  COR="$NEUTRO"
  [[ "$NIVEL" == "erro" ]] && COR="$VERMELHO"
  [[ "$EVT" == "comprovante.gravado" ]] && COR="$VERDE"
  [[ "$EVT" == "comprovante.duplicado" ]] && COR="$AMARELO"
  [[ "$SVC" == "publisher" ]] && [[ "$NIVEL" != "erro" ]] && COR="$AZUL"

  printf "  ${CINZA}%s${NEUTRO}  ${COR}%-22s${NEUTRO} ${CINZA}%s/%s${NEUTRO}\n" \
    "${TS:11:15}" "$EVT" "$W" "$L"
  printf "                  %s\n" "$MSG"
done <<< "$DO_TRACE"

echo
# Se a cadeia terminou em falha repetida, e o caminho da DLQ.
N_FALHAS=$(grep -cF '"evento":"mensagem.falhou"' <<< "$DO_TRACE")
if [[ "$N_FALHAS" -ge 3 ]]; then
  echo -e "  ${VERMELHO}$N_FALHAS tentativas falharam.${NEUTRO} Com maxReceiveCount=3 na RedrivePolicy,"
  echo "  esta mensagem ja foi para a DLQ. Repare que as tres tentativas tem o"
  echo "  MESMO trace: ele foi derivado do MessageId, nao sorteado (guia 07 §6)."
elif [[ "$N_FALHAS" -gt 0 ]]; then
  echo -e "  ${AMARELO}$N_FALHAS tentativa(s) falharam${NEUTRO} - a mensagem volta pelo visibility timeout."
fi

CAMINHO=$(campo "$(grep -F '"evento":"comprovante.gravado"' <<< "$DO_TRACE" | head -1)" caminho)
if [[ -n "$CAMINHO" ]]; then
  echo "  O comprovante gravado por esta cadeia:"
  echo "      docker compose exec consumer-registro cat $CAMINHO"
  echo "  (o campo 'Trace ID' dentro dele fecha o circuito de volta para cá)"
fi
