#!/usr/bin/env bash
#
# Prova que as tres implementacoes sao equivalentes - sem acreditar em mim.
#
# Roda o subcomando "amostra" nas tres linguagens. Ele renderiza um
# comprovante a partir de um payload FIXO, com host, message id e horario
# fixos: nada varia entre execucoes nem entre linguagens.
#
# Depois faz `diff`. Se qualquer coisa divergir - uma casa decimal no valor,
# um ponto a mais no alinhamento do rotulo, um BOM no comeco do arquivo, uma
# ordem de chave diferente no JSON - aparece aqui.
#
# Uso:  ./run.sh comparar
#
# Nao mexe no volume, nao publica nada, nao suja o ./run.sh verify.
#
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

VERDE='\033[0;32m'; VERMELHO='\033[0;31m'; AMARELO='\033[0;33m'; NEUTRO='\033[0m'

compose() {
  if docker compose version >/dev/null 2>&1; then docker compose "$@"
  else docker-compose "$@"; fi
}

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "==================================================================="
echo " Comparando as tres implementacoes do comprovante"
echo "==================================================================="
echo

# --no-deps: nao sobe o Floci por causa disto. A amostra e offline - nao
# chama AWS nenhuma, so renderiza.
echo "  gerando a amostra em Python..."
compose run --rm --no-deps -T consumer-registro python -u amostra.py > "$TMP/python.txt" 2>"$TMP/python.err"
echo "  gerando a amostra em Go..."
compose run --rm --no-deps -T consumer-baixa /app/amostra          > "$TMP/go.txt"     2>"$TMP/go.err"
echo "  gerando a amostra em .NET..."
compose run --rm --no-deps -T consumer-rejeicao dotnet Lab.dll amostra > "$TMP/dotnet.txt" 2>"$TMP/dotnet.err"

for l in python go dotnet; do
  if [[ ! -s "$TMP/$l.txt" ]]; then
    echo -e "  ${VERMELHO}ERRO${NEUTRO} a amostra em $l nao produziu saida:"
    sed 's/^/        /' "$TMP/$l.err"
    exit 1
  fi
done

echo
echo "--- A amostra (identica nas tres):"
echo
sed 's/^/  /' "$TMP/python.txt"
echo

DIVERGIU=0
for outra in go dotnet; do
  if diff -u "$TMP/python.txt" "$TMP/$outra.txt" > "$TMP/diff-$outra.txt"; then
    echo -e "  ${VERDE}IGUAL${NEUTRO}   python == $outra   ($(wc -c < "$TMP/python.txt" | tr -d ' ') bytes)"
  else
    echo -e "  ${VERMELHO}DIFERE${NEUTRO}  python != $outra"
    sed 's/^/          /' "$TMP/diff-$outra.txt"
    DIVERGIU=1
  fi
done

echo
if [[ $DIVERGIU -eq 0 ]]; then
  echo " As tres linguagens produzem o MESMO comprovante, byte a byte."
  echo
  echo " O que isso quer dizer na pratica: o nome do arquivo (a chave de"
  echo " idempotencia) tambem e o mesmo. Se voce trocar o consumer de baixa"
  echo " de Go para Python amanha, ele reconhece como duplicata os"
  echo " comprovantes que a versao Go ja tinha gravado no volume."
  exit 0
fi

echo -e " ${AMARELO}Divergencia entre implementacoes.${NEUTRO}"
echo " As regras do comprovante vivem em tres arquivos e precisam mudar"
echo " juntas: python/comprovante.py, go/comprovante/comprovante.go e"
echo " dotnet/Comprovante.cs. Capitulo 07, secao 3."
exit 1
