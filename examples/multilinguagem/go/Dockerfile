# Build multi-estagio: o toolchain do Go fica no primeiro estagio e NAO vai
# para a imagem final. Voce nao precisa de Go instalado na sua maquina.
FROM golang:1.25-bookworm AS build

WORKDIR /src
COPY . .

# `go mod tidy` no build resolve as dependencias do aws-sdk-go-v2 e gera o
# go.sum. Num repositorio de produção voce commitaria go.mod e go.sum
# resolvidos e usaria `go mod download` - build reprodutivel e sem surpresa
# de versao nova quebrando o CI.
RUN go mod tidy \
 && CGO_ENABLED=0 go build -trimpath -o /out/publisher    ./cmd/publisher \
 && CGO_ENABLED=0 go build -trimpath -o /out/consumer     ./cmd/consumer \
 && CGO_ENABLED=0 go build -trimpath -o /out/diagnostico  ./cmd/diagnostico \
 && CGO_ENABLED=0 go build -trimpath -o /out/segredos     ./cmd/segredos \
 && CGO_ENABLED=0 go build -trimpath -o /out/amostra      ./cmd/amostra

# ---------------------------------------------------------------------------
# Runtime.
#
# Um binario Go estatico rodaria em `scratch` ou `distroless` - menor e mais
# seguro, e e o que voce faria em producao. Aqui usamos debian-slim de
# proposito: o ./run.sh verify e os experimentos do ROTEIRO entram no
# container com `docker compose exec ... sh -c 'find ... | grep ...'`, e numa
# imagem scratch nao existe sh, nem find, nem grep.
#
# Vale registrar o trade-off: imagem minima e otima ate a primeira vez que
# voce precisa depurar de dentro dela.
# ---------------------------------------------------------------------------
FROM debian:bookworm-slim

# Certificados raiz: sem eles, qualquer chamada HTTPS para a AWS de verdade
# falha com "x509: certificate signed by unknown authority". Contra o Floci
# (HTTP) o erro nao aparece - e por isso e uma armadilha classica de "funciona
# local, quebra em producao".
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# uid 1000 - O MESMO NAS TRES LINGUAGENS. Ver o comentario longo no
# ../python/Dockerfile: o volume nomeado herda o dono do diretorio da imagem
# que o montar primeiro, entao workers com uid diferente brigariam pelo /data.
#
# O getent antes de cada criacao existe porque as imagens base nao concordam:
# a do .NET 10 ja traz um usuario com uid 1000, a do Debian nao.
RUN (getent group 1000  >/dev/null || groupadd --gid 1000 worker) \
 && (getent passwd 1000 >/dev/null || useradd --uid 1000 --gid 1000 --no-create-home worker) \
 && mkdir -p /data \
 && chown 1000:1000 /data

COPY --from=build /out/ /app/
USER 1000:1000
WORKDIR /app

CMD ["/app/consumer"]
