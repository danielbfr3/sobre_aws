// Modulo do lab. As dependencias nao estao fixadas aqui de proposito: o
// Dockerfile roda `go mod tidy` no build e resolve a versao mais recente do
// aws-sdk-go-v2. Num repositorio de produção voce faria o contrário - go.mod
// e go.sum versionados e commitados.
module lab

go 1.24
