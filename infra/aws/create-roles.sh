#!/usr/bin/env bash
#
# Cria UMA ROLE POR WORKER, a partir dos JSONs de infra/iam/.
#
# O ponto didatico deste script cabe em duas linhas, e e o que a etapa 11 do
# ROTEIRO manda voce conferir:
#
#     aws iam create-role     --assume-role-policy-document  → QUEM pode assumir
#     aws iam put-role-policy --policy-document              → O QUE pode fazer
#
# Sao dois documentos, dois comandos, dois conceitos. Confundi-los e o erro
# numero 1 de quem comeca (guia 01, secao 4).
#
# Os JSONs de infra/iam/ usam valores de EXEMPLO - conta 111122223333, regiao
# us-east-1, provedor OIDC EXAMPLED539... Este script troca por valores reais
# antes de enviar, sem editar os arquivos.
#
# Uso:
#   # praticando contra o Floci, sem custo e sem acesso a conta da empresa:
#   AWS_ENDPOINT_URL=http://localhost:4566 ./infra/aws/create-roles.sh
#
#   # contra a AWS de verdade:
#   ACCOUNT_ID=123456789012 \
#   OIDC_ID=$(aws eks describe-cluster --name meu-cluster \
#              --query 'cluster.identity.oidc.issuer' --output text | rev | cut -d/ -f1 | rev) \
#   ENVIRONMENT=dev \
#   ./infra/aws/create-roles.sh
#
#   ./infra/aws/create-roles.sh --limpar     apaga as roles que ele criou
#
# ATENCAO, e vale repetir o que o guia 01 secao 11 diz: o Floci aceita policy
# mal escrita. Ele nao e ponto de decisao de autorizacao. Rodar isto aqui
# pratica os COMANDOS e a ESTRUTURA; se a policy concede o certo, quem
# responde e o IAM Policy Simulator.
#
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

VERDE='\033[0;32m'; VERMELHO='\033[0;31m'; AMARELO='\033[0;33m'
CINZA='\033[0;90m'; NEUTRO='\033[0m'
ok()    { echo -e "  ${VERDE}OK${NEUTRO}   $*"; }
erro()  { echo -e "  ${VERMELHO}ERRO${NEUTRO} $*"; }
nota()  { echo -e "  ${CINZA}$*${NEUTRO}"; }
etapa() { echo; echo -e "${AMARELO}==> $*${NEUTRO}"; }

# --- valores de exemplo que moram nos JSONs --------------------------------
CONTA_EXEMPLO="111122223333"
OIDC_EXEMPLO="EXAMPLED539D4633E53DE1B71EXAMPLE"
REGIAO_EXEMPLO="us-east-1"
AMBIENTE_EXEMPLO="dev"

# --- valores reais, vindos do ambiente -------------------------------------
CONTA="${ACCOUNT_ID:-$CONTA_EXEMPLO}"
OIDC="${OIDC_ID:-$OIDC_EXEMPLO}"
REGIAO="${AWS_DEFAULT_REGION:-${AWS_REGION:-$REGIAO_EXEMPLO}}"
AMBIENTE="${ENVIRONMENT:-$AMBIENTE_EXEMPLO}"
NAMESPACE="${K8S_NAMESPACE:-cash}"

ENDPOINT="${AWS_ENDPOINT_URL:-}"
LOCAL=0
if [[ -n "$ENDPOINT" ]]; then
  LOCAL=1
  export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-test}"
  export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-test}"
fi
export AWS_DEFAULT_REGION="$REGIAO"

aws_iam() {
  if [[ $LOCAL -eq 1 ]]; then aws --endpoint-url "$ENDPOINT" iam "$@"
  else aws iam "$@"; fi
}

# worker  ->  nome do ServiceAccount
WORKERS=("consumer-registro" "consumer-baixa" "consumer-rejeicao" "publisher" "arquivos")
SAS=("sa-consumer-registro" "sa-consumer-baixa" "sa-consumer-rejeicao" "sa-publisher" "sa-arquivos")

role_de() { echo "asa-${AMBIENTE}-cash-cobranca-$1"; }

# Substitui os valores de exemplo pelos reais. Fica em stdout - nenhum arquivo
# de infra/iam/ e modificado.
renderizar() {
  sed -e "s/${CONTA_EXEMPLO}/${CONTA}/g" \
      -e "s/${OIDC_EXEMPLO}/${OIDC}/g" \
      -e "s/${REGIAO_EXEMPLO}/${REGIAO}/g" \
      -e "s/system:serviceaccount:cash:/system:serviceaccount:${NAMESPACE}:/g" \
      "$1"
}

# ---------------------------------------------------------------------------
limpar() {
  for i in "${!WORKERS[@]}"; do
    W="${WORKERS[$i]}"; ROLE="$(role_de "$W")"
    # IAM nao deixa apagar uma role que ainda tem inline policy pendurada.
    aws_iam delete-role-policy --role-name "$ROLE" \
      --policy-name "permissoes-${W}" >/dev/null 2>&1 || true
    aws_iam delete-role --role-name "$ROLE" >/dev/null 2>&1 || true
  done
  ok "roles removidas"
}

if [[ "${1:-}" == "--limpar" ]]; then limpar; exit 0; fi

# ---------------------------------------------------------------------------
echo "==================================================================="
echo " Criando uma role por worker"
echo "==================================================================="
nota "conta ....: $CONTA"
nota "regiao ...: $REGIAO"
nota "ambiente .: $AMBIENTE"
nota "namespace : $NAMESPACE"
nota "OIDC .....: $OIDC"
if [[ $LOCAL -eq 1 ]]; then
  nota "destino ..: $ENDPOINT  (emulador)"
else
  nota "destino ..: AWS de verdade"
fi

CRIADAS=0; FALHAS=0

for i in "${!WORKERS[@]}"; do
  W="${WORKERS[$i]}"
  SA="${SAS[$i]}"
  ROLE="$(role_de "$W")"

  # publisher e arquivos tem os JSONs sem o prefixo "consumer-"
  BASE="$W"
  TRUST="infra/iam/trust-policy-${BASE}.json"
  PERM="infra/iam/policy-${BASE}.json"

  if [[ ! -f "$TRUST" || ! -f "$PERM" ]]; then
    erro "faltam os JSONs de $W ($TRUST / $PERM)"
    FALHAS=$((FALHAS+1)); continue
  fi

  etapa "$ROLE   (ServiceAccount: ${NAMESPACE}/${SA})"

  # --- documento 1: QUEM pode assumir --------------------------------------
  if aws_iam create-role \
       --role-name "$ROLE" \
       --assume-role-policy-document "$(renderizar "$TRUST")" \
       --description "Worker ${W} do dominio cash-cobranca (${AMBIENTE})" \
       >/dev/null 2>&1; then
    ok "trust policy  (create-role)        <- $TRUST"
  elif aws_iam update-assume-role-policy \
         --role-name "$ROLE" \
         --policy-document "$(renderizar "$TRUST")" >/dev/null 2>&1; then
    ok "trust policy  (ja existia, atualizada)"
  else
    erro "nao consegui criar/atualizar a role $ROLE"
    FALHAS=$((FALHAS+1)); continue
  fi

  # --- documento 2: O QUE pode fazer ---------------------------------------
  # Inline, e nao managed: esta policy so faz sentido para esta role, entao ela
  # nasce e morre com ela (guia 01, secao 8).
  if aws_iam put-role-policy \
       --role-name "$ROLE" \
       --policy-name "permissoes-${W}" \
       --policy-document "$(renderizar "$PERM")" >/dev/null 2>&1; then
    ok "permission policy (put-role-policy) <- $PERM"
    CRIADAS=$((CRIADAS+1))
  else
    erro "nao consegui anexar a permission policy de $ROLE"
    FALHAS=$((FALHAS+1))
  fi
done

# ---------------------------------------------------------------------------
echo
echo "==================================================================="
echo -e " ${VERDE}${CRIADAS} role(s) prontas${NEUTRO}   ${VERMELHO}${FALHAS} falha(s)${NEUTRO}"
echo "==================================================================="
echo
echo " Confira os DOIS documentos, separadamente:"
echo
echo "     aws iam get-role --role-name $(role_de consumer-registro) \\"
echo "       --query 'Role.AssumeRolePolicyDocument'"
echo
echo "     aws iam get-role-policy --role-name $(role_de consumer-registro) \\"
echo "       --policy-name permissoes-consumer-registro --query 'PolicyDocument'"
echo
echo " E veja o que muda entre dois workers - deve ser so o nome da fila:"
echo
echo "     diff infra/iam/policy-consumer-registro.json \\"
echo "          infra/iam/policy-consumer-baixa.json"

if [[ $LOCAL -eq 1 ]]; then
  echo
  echo -e " ${AMARELO}Lembrete:${NEUTRO} o Floci aceitou estes JSONs sem avaliar nenhum deles."
  echo " Voce praticou a FORMA. Se a policy concede o certo, so o IAM Policy"
  echo " Simulator e o Access Analyzer respondem (guia 01, secao 11)."
fi

[[ $FALHAS -eq 0 ]] || exit 1
