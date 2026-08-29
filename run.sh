#!/usr/bin/env bash
#
# Orquestra o lab na ORDEM CORRETA.
#
# A ordem importa e nao e obvia:
#   1. Floci de pe e respondendo
#   2. topico, filas, DLQs e subscriptions criados
#   3. so entao os workers
#
# Subir os workers antes do passo 2 nao quebra nada (eles tem retry), mas
# gera uma enxurrada de erro no log que confunde quem esta aprendendo.
#
# Uso:
#   ./run.sh up          sobe tudo na ordem (o caminho normal)
#   ./run.sh verify      checa se os resultados esperados aconteceram
#   ./run.sh comprovantes  lista os .txt gerados
#   ./run.sh logs [nome] acompanha os logs
#   ./run.sh reset       derruba tudo e APAGA o volume
#
#   ./run.sh build       compila os projetos .NET e as 3 imagens do cap. 07
#   ./run.sh diagnostico [python|go|dotnet]  qual provedor venceu (guia 03)
#   ./run.sh segredos    [python|go|dotnet]  cache com TTL (guia 04)
#   ./run.sh comparar    prova que as 3 linguagens geram o mesmo comprovante
#   ./run.sh trace [id]  remonta a cadeia de uma mensagem pelo trace_id
#   ./run.sh exportar-logs  junta os NDJSON para abrir no visualizador
#   ./run.sh cross-account  exercicio: conta de origem x conta de destino (guia 02)
#
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

VERDE='\033[0;32m'; VERMELHO='\033[0;31m'; AMARELO='\033[0;33m'; NEUTRO='\033[0m'
ok()    { echo -e "  ${VERDE}OK${NEUTRO}   $*"; }
erro()  { echo -e "  ${VERMELHO}ERRO${NEUTRO} $*"; }
aviso() { echo -e "  ${AMARELO}!${NEUTRO}    $*"; }
etapa() { echo; echo -e "${AMARELO}==> $*${NEUTRO}"; }

compose() {
  if docker compose version >/dev/null 2>&1; then docker compose "$@"
  else docker-compose "$@"; fi
}

checar_dependencias() {
  local faltando=()
  command -v docker >/dev/null || faltando+=("docker")
  command -v aws    >/dev/null || faltando+=("aws-cli")
  if [[ ${#faltando[@]} -gt 0 ]]; then
    erro "faltando: ${faltando[*]}"
    exit 1
  fi
}

esperar_floci() {
  echo -n "  aguardando o Floci responder"
  for _ in $(seq 1 60); do
    if curl -sf http://localhost:4566/_localstack/health >/dev/null 2>&1; then
      echo; ok "Floci respondendo em http://localhost:4566"
      return 0
    fi
    echo -n "."
    sleep 2
  done
  echo
  erro "Floci nao respondeu em 120s. Veja: ./run.sh logs floci"
  return 1
}

comando="${1:-up}"

case "$comando" in

  up)
    checar_dependencias

    etapa "ETAPA 1/3 - subindo o emulador"
    compose up -d floci
    esperar_floci

    etapa "ETAPA 2/3 - criando topico, filas, DLQs e subscriptions"
    ./infra/local/bootstrap.sh

    etapa "ETAPA 3/3 - subindo publisher e os 3 consumers"
    # --build na primeira vez; nas seguintes o cache do Docker resolve
    compose up -d --build publisher consumer-registro consumer-baixa consumer-rejeicao

    etapa "pronto"
    echo "  O publisher publica um evento a cada 3s. Aguarde ~30s e rode:"
    echo
    echo "      ./run.sh verify"
    echo "      ./run.sh comprovantes"
    echo "      ./run.sh logs consumer-registro"
    echo
    echo "  Os tres consumers estao em linguagens diferentes (capitulo 07):"
    echo "      consumer-registro  Python   consumer-baixa  Go   consumer-rejeicao  .NET"
    ;;

  bootstrap)
    ./infra/local/bootstrap.sh
    ;;

  workers)
    compose up -d --build publisher consumer-registro consumer-baixa consumer-rejeicao
    ok "workers no ar"
    ;;

  verify)
    ./infra/local/verify.sh
    ;;

  build)
    # Compila TUDO. Os projetos .NET soltos precisam do dotnet na maquina;
    # as tres imagens do capitulo 07 compilam DENTRO do Docker (build
    # multi-estagio), entao nao exigem Python, Go nem dotnet instalados.
    if command -v dotnet >/dev/null; then
      for proj in src/Publisher src/Consumer \
                  examples/dotnet-credenciais examples/secrets \
                  examples/lambda; do
        [[ -d "$proj" ]] || continue
        echo
        etapa "build $proj"
        if dotnet build "$proj" -v quiet --nologo; then ok "$proj"; else erro "$proj"; fi
      done
    else
      aviso "dotnet nao encontrado - pulando os projetos .NET soltos"
    fi
    echo
    etapa "build das imagens do capitulo 07 (python, go, dotnet)"
    compose build publisher consumer-registro consumer-baixa consumer-rejeicao
    ok "imagens prontas"
    ;;

  diagnostico)
    # Mostra qual provedor da cadeia de credenciais venceu (guia 03).
    #
    #   ./run.sh diagnostico            projeto .NET solto, se existir
    #   ./run.sh diagnostico python     |
    #   ./run.sh diagnostico go         |  no container, sem toolchain local
    #   ./run.sh diagnostico dotnet     |
    shift || true
    LINGUAGEM="${1:-}"
    if [[ -z "$LINGUAGEM" && -d examples/dotnet-credenciais ]] && command -v dotnet >/dev/null; then
      dotnet run --project examples/dotnet-credenciais
      exit $?
    fi
    case "${LINGUAGEM:-python}" in
      python) compose run --rm -T consumer-registro python -u diagnostico.py ;;
      go)     compose run --rm -T consumer-baixa    /app/diagnostico ;;
      dotnet) compose run --rm -T consumer-rejeicao dotnet Lab.dll diagnostico ;;
      *)      erro "linguagem desconhecida: $LINGUAGEM (use python, go ou dotnet)"; exit 1 ;;
    esac
    ;;

  segredos)
    # Demo do cache com TTL contra o Floci (guia 04). Mesma logica do
    # diagnostico: sem argumento usa o projeto .NET solto, se ele existir.
    shift || true
    LINGUAGEM="${1:-}"
    if [[ -z "$LINGUAGEM" && -d examples/secrets ]] && command -v dotnet >/dev/null; then
      AWS_ENDPOINT_URL=http://localhost:4566 \
      AWS_ACCESS_KEY_ID=test AWS_SECRET_ACCESS_KEY=test AWS_REGION=us-east-1 \
        dotnet run --project examples/secrets
      exit $?
    fi
    case "${LINGUAGEM:-python}" in
      python) compose run --rm -T consumer-registro python -u segredos.py ;;
      go)     compose run --rm -T consumer-baixa    /app/segredos ;;
      dotnet) compose run --rm -T consumer-rejeicao dotnet Lab.dll segredos ;;
      *)      erro "linguagem desconhecida: $LINGUAGEM (use python, go ou dotnet)"; exit 1 ;;
    esac
    ;;

  comparar)
    # Prova que as tres implementacoes geram o MESMO comprovante (cap. 07).
    ./examples/multilinguagem/comparar.sh
    ;;

  trace)
    # Remonta a cadeia de uma mensagem, do publish ao comprovante (cap. 07).
    #   ./run.sh trace          lista os traces recentes
    #   ./run.sh trace <id>     a cadeia daquele trace
    #   ./run.sh trace --dlq    so os que falharam
    shift || true
    ./infra/local/trace.sh "$@"
    ;;

  cross-account)
    # Exercicio do guia 02: faz a travessia de conta acontecer na tela.
    #   ./run.sh cross-account            roda o exercicio
    #   ./run.sh cross-account --limpar   apaga as roles e a fila que ele criou
    #
    # So depende do Floci de pe - cria os proprios recursos nas contas
    # ficticias, entao nao precisa do bootstrap nem dos workers.
    shift || true
    ./infra/local/exercicio-cross-account.sh "$@"
    ;;

  exportar-logs)
    # Junta os NDJSON dos 4 processos num arquivo unico, para carregar na aba
    # "Logs reais" do visualizador.
    DESTINO="${2:-logs-do-lab.ndjson}"
    compose exec -T consumer-registro sh -c \
      'cat /data/logs/*.ndjson 2>/dev/null | sort' > "$DESTINO"
    LINHAS=$(wc -l < "$DESTINO" | tr -d ' ')
    if [[ "$LINHAS" -eq 0 ]]; then
      erro "nenhum log exportado - o lab esta no ar?"
      rm -f "$DESTINO"
      exit 1
    fi
    ok "$LINHAS evento(s) em $DESTINO"
    echo
    echo "  Abra o visualizador, va na aba 'Logs reais' e arraste o arquivo:"
    echo "      open docs/assets/visualizador.html"
    ;;

  comprovantes)
    etapa "comprovantes por worker"
    compose exec -T consumer-registro sh -c \
      'find /data/comprovantes -name "*.txt" 2>/dev/null | cut -d/ -f4 | sort | uniq -c' \
      || aviso "nenhum comprovante ainda - aguarde alguns segundos"
    echo
    etapa "exemplo de comprovante"
    compose exec -T consumer-registro sh -c \
      'f=$(find /data/comprovantes -name "*.txt" 2>/dev/null | head -1); [ -n "$f" ] && cat "$f"' \
      || aviso "nenhum comprovante ainda"
    ;;

  logs)
    shift || true
    if [[ $# -gt 0 ]]; then compose logs -f --tail=50 "$@"
    else compose logs -f --tail=30; fi
    ;;

  reset)
    aviso "isto apaga o volume e TODOS os comprovantes gerados"
    read -r -p "  confirma? [s/N] " resposta
    if [[ "$resposta" =~ ^[sS]$ ]]; then
      compose down -v
      rm -f .env
      ok "ambiente limpo"
    else
      echo "  cancelado"
    fi
    ;;

  *)
    echo "uso: ./run.sh [up|verify|comprovantes|logs|build|bootstrap|workers|reset]"
    echo "     ./run.sh diagnostico [python|go|dotnet]"
    echo "     ./run.sh segredos    [python|go|dotnet]"
    echo "     ./run.sh comparar"
    echo "     ./run.sh trace [id|--dlq]"
    echo "     ./run.sh exportar-logs [arquivo.ndjson]"
    echo "     ./run.sh cross-account [--limpar]"
    exit 1
    ;;
esac
