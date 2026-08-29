// Package logx e o log estruturado com trace_id em Go.
//
// Duas saidas para o mesmo evento, de proposito:
//
//	stdout  linha legivel, para o `docker compose logs`
//	arquivo NDJSON em /data/logs/<servico>-<host>.ndjson
//
// Em producao voce escreveria SO no stdout, em JSON, e um coletor
// (fluent-bit, CloudWatch agent, Datadog) leria dali. O arquivo no volume e o
// substituto de laboratorio para esse coletor.
//
// UM ARQUIVO POR ESCRITOR - o nome carrega o host. Mesmo raciocinio da escrita
// dos comprovantes: em vez de disputar append no mesmo arquivo (que sobre NFS
// tem semantica frouxa), cada processo escreve no seu e a juncao acontece na
// leitura.
//
// Equivalentes: ../../python/log.py e ../../dotnet/Log.cs.
package logx

import (
	"crypto/md5"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

const SemTrace = "--------"

// NovoTrace devolve 32 caracteres hexadecimais minusculos - o mesmo formato do
// trace-id do W3C Trace Context. Em producao isso viria do OpenTelemetry, pelo
// cabecalho `traceparent`; aqui esta na mao para deixar o mecanismo a vista.
func NovoTrace() string {
	b := make([]byte, 16) // 16 bytes = 32 caracteres hex
	if _, err := rand.Read(b); err != nil {
		// Fallback: melhor um id feio que um processo morto por causa do log.
		soma := md5.Sum([]byte(fmt.Sprintf("%d-%d", time.Now().UnixNano(), os.Getpid())))
		return hex.EncodeToString(soma[:])
	}
	return hex.EncodeToString(b)
}

// TraceDoMessageID e o trace de uma mensagem que chegou SEM o atributo traceId.
//
// Acontece quando alguem publica direto na fila (`aws sqs send-message`), que e
// exatamente o que a etapa 9 do ROTEIRO faz para exercitar a DLQ.
//
// Derivado do MessageId, NUNCA sorteado. O MessageId e estavel entre
// reentregas, entao as 3 tentativas da mesma mensagem caem no mesmo trace e
// voce enxerga a cadeia inteira ate a DLQ. Um id sorteado a cada receive
// quebraria justamente o caso em que rastrear vale mais.
func TraceDoMessageID(messageID string) string {
	soma := md5.Sum([]byte(messageID))
	return hex.EncodeToString(soma[:])
}

// Log e o emissor de eventos. Um por processo.
//
// Cada evento tem um NOME (`comprovante.gravado`), e nao so um texto. E o nome
// que o `./run.sh trace` e o visualizador usam para remontar a cadeia - texto
// livre serve para humano, nome de evento serve para maquina.
type Log struct {
	servico   string
	worker    string
	linguagem string
	host      string
	arquivo   string
	mu        sync.Mutex
}

func Novo(servico, worker, linguagem, dataPath string) *Log {
	host, err := os.Hostname()
	if err != nil {
		host = "desconhecido"
	}
	l := &Log{servico: servico, worker: worker, linguagem: linguagem, host: host}

	if dataPath == "" {
		dataPath = os.Getenv("DATA_PATH")
	}
	if dataPath == "" {
		dataPath = "/data"
	}
	pasta := filepath.Join(dataPath, "logs")
	// Sem volume montado o log vai so para o stdout. Nao e motivo para o
	// worker deixar de subir.
	if err := os.MkdirAll(pasta, 0o755); err == nil {
		l.arquivo = filepath.Join(pasta, fmt.Sprintf("%s-%s.ndjson", servico, host))
	}
	return l
}

// Campos e o par nome/valor extra de um evento. Ordem preservada.
type Campos map[string]any

func (l *Log) Evento(evento, msg, trace string, campos Campos) {
	l.escrever("info", evento, msg, trace, campos)
}

func (l *Log) Erro(evento, msg, trace string, campos Campos) {
	l.escrever("erro", evento, msg, trace, campos)
}

func (l *Log) escrever(nivel, evento, msg, trace string, campos Campos) {
	agora := time.Now().UTC()

	// A ORDEM DAS CHAVES IMPORTA e "ts" vem primeiro.
	//
	// Os arquivos das tres linguagens sao concatenados e ordenados com um
	// `sort` de texto puro - sem jq, sem parser. Isso so funciona porque o
	// timestamp e a primeira chave e tem LARGURA FIXA: 6 casas decimais
	// sempre, sempre UTC, sempre com o Z no fim.
	//
	// Microssegundos e nao milissegundos de proposito. Com 3 casas, dois
	// eventos do mesmo processo caem no mesmo instante com frequencia, e ai o
	// `sort` desempata pelo RESTO da linha - em ordem alfabetica do nome do
	// evento. O resultado e uma cadeia mostrando "mensagem.falhou" antes de
	// "mensagem.recebida".
	//
	// encoding/json ordena as chaves de um map alfabeticamente, o que
	// estragaria isso - por isso a parte fixa e montada a mao, campo a campo,
	// e so os extras passam pelo Marshal.
	var b []byte
	b = append(b, '{')
	b = par(b, "ts", agora.Format("2006-01-02T15:04:05.000000Z"), true)
	b = par(b, "nivel", nivel, false)
	b = par(b, "servico", l.servico, false)
	b = par(b, "worker", l.worker, false)
	b = par(b, "linguagem", l.linguagem, false)
	b = par(b, "host", l.host, false)
	b = par(b, "traceId", trace, false)
	b = par(b, "evento", evento, false)
	b = par(b, "msg", msg, false)
	for k, v := range campos {
		chave, _ := json.Marshal(k)
		valor, err := json.Marshal(v)
		if err != nil {
			continue
		}
		b = append(b, ',')
		b = append(b, chave...)
		b = append(b, ':')
		b = append(b, valor...)
	}
	b = append(b, '}', '\n')

	curto := SemTrace
	if len(trace) >= 8 {
		curto = trace[:8]
	}

	l.mu.Lock()
	defer l.mu.Unlock()

	// stdout legivel: e o que o ROTEIRO manda voce ler.
	fmt.Printf("[%s] %s %s  %s\n", agora.Format("15:04:05"), l.worker, curto, msg)

	if l.arquivo != "" {
		f, err := os.OpenFile(l.arquivo, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
		if err != nil {
			return // log que derruba o worker e pior que log perdido
		}
		_, _ = f.Write(b)
		_ = f.Close()
	}
}

func par(b []byte, chave, valor string, primeiro bool) []byte {
	if !primeiro {
		b = append(b, ',')
	}
	k, _ := json.Marshal(chave)
	v, _ := json.Marshal(valor)
	b = append(b, k...)
	b = append(b, ':')
	b = append(b, v...)
	return b
}
