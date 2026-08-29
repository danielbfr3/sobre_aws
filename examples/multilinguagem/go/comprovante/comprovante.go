// Package comprovante e a porta em Go da implementacao de referencia que
// vive em ../python/comprovante.py.
//
// O contrato entre as tres linguagens: o MESMO payload gera o MESMO nome de
// arquivo e o MESMO conteudo, byte a byte. Se voce mexer em qualquer regra
// daqui - largura da linha, formato do valor, algoritmo do hash - tem que
// mexer nas tres ao mesmo tempo, senao dois workers do mesmo lab passam a
// gravar comprovantes com cara diferente.
package comprovante

import (
	"crypto/md5"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	Largura         = 64
	larguraRotulo   = 18
	formatoData     = "02/01/2006 15:04:05 UTC"
	formatoISO      = "2006-01-02T15:04:05Z"
	formatoParticao = "2006-01-02"
)

var titulos = map[string]string{
	"registro": "COMPROVANTE DE REGISTRO DE COBRANÇA",
	"baixa":    "COMPROVANTE DE BAIXA DE COBRANÇA",
	"rejeicao": "COMPROVANTE DE REJEIÇÃO DE COBRANÇA",
}

var nomeSeguro = regexp.MustCompile(`[^A-Za-z0-9_-]`)

// Evento e o payload que trafega no SNS e na SQS.
type Evento struct {
	NossoNumero   string
	TipoEvento    string
	ValorCentavos int64
	OcorridoEm    time.Time
}

// Ponteiros de proposito: em Go o zero-value nao distingue "veio 0" de "nao
// veio". Com ponteiro, nil == campo ausente, e da para recusar o payload.
type eventoJSON struct {
	NossoNumero   *string `json:"nossoNumero"`
	TipoEvento    *string `json:"tipoEvento"`
	ValorCentavos *int64  `json:"valorCentavos"`
	OcorridoEm    *string `json:"ocorridoEm"`
}

// DoJSON faz o parse do Body da mensagem SQS.
//
// Devolve erro em qualquer coisa que nao seja o payload esperado - e e disso
// que a etapa 9 do ROTEIRO depende. Mandar 'isto-nao-e-json' para a fila tem
// que falhar aqui, a mensagem NAO ser deletada, e depois de maxReceiveCount
// tentativas cair na DLQ.
func DoJSON(corpo string) (Evento, error) {
	var raw eventoJSON
	if err := json.Unmarshal([]byte(corpo), &raw); err != nil {
		return Evento{}, fmt.Errorf("corpo nao e JSON valido: %w", err)
	}

	var faltando []string
	if raw.NossoNumero == nil {
		faltando = append(faltando, "nossoNumero")
	}
	if raw.TipoEvento == nil {
		faltando = append(faltando, "tipoEvento")
	}
	if raw.ValorCentavos == nil {
		faltando = append(faltando, "valorCentavos")
	}
	if raw.OcorridoEm == nil {
		faltando = append(faltando, "ocorridoEm")
	}
	if len(faltando) > 0 {
		return Evento{}, fmt.Errorf("campos ausentes no payload: %s", strings.Join(faltando, ", "))
	}

	ocorrido, err := time.Parse(time.RFC3339, *raw.OcorridoEm)
	if err != nil {
		return Evento{}, fmt.Errorf("ocorridoEm invalido: %w", err)
	}

	return Evento{
		NossoNumero:   *raw.NossoNumero,
		TipoEvento:    *raw.TipoEvento,
		ValorCentavos: *raw.ValorCentavos,
		OcorridoEm:    ocorrido.UTC(),
	}, nil
}

// ParaJSON serializa de forma COMPACTA e com as chaves nesta ordem.
//
// Nao e estilo: o hash de idempotencia sai destes bytes. Um espaco a mais
// muda o nome do arquivo la na frente, e duas publicacoes do "mesmo" evento
// deixariam de ser duplicatas. (encoding/json ja emite sem espacos e na ordem
// de declaracao da struct - por isso os campos abaixo estao na mesma ordem do
// dict do Python.)
func (e Evento) ParaJSON() (string, error) {
	b, err := json.Marshal(struct {
		NossoNumero   string `json:"nossoNumero"`
		TipoEvento    string `json:"tipoEvento"`
		ValorCentavos int64  `json:"valorCentavos"`
		OcorridoEm    string `json:"ocorridoEm"`
	}{e.NossoNumero, e.TipoEvento, e.ValorCentavos, e.OcorridoEm.UTC().Format(formatoISO)})
	return string(b), err
}

// HashPayload devolve o MD5 do corpo bruto em hexadecimal MAIUSCULO.
//
// Isto NAO e seguranca, e deduplicacao: o hash so responde "ja vi exatamente
// estes bytes?". Se o seu caso for anti-fraude, troque por SHA-256 - nas tres
// linguagens ao mesmo tempo.
func HashPayload(corpo string) string {
	soma := md5.Sum([]byte(corpo))
	return strings.ToUpper(hex.EncodeToString(soma[:]))
}

// NomeArquivo e a chave de idempotencia: {nossoNumero}-{8 do hash}.txt
func NomeArquivo(nossoNumero, hash32 string) string {
	return fmt.Sprintf("%s-%s.txt", nomeSeguro.ReplaceAllString(nossoNumero, "_"), hash32[:8])
}

// Caminho monta /data/comprovantes/<worker>/<AAAA-MM-DD>/<arquivo>.
//
// A data da particao vem do OcorridoEm do evento, nao do relogio da maquina.
// Se viesse do relogio, uma mensagem reentregue depois da meia-noite cairia
// noutro diretorio, o teste de existencia nao acharia o arquivo anterior, e a
// idempotencia falharia uma vez por dia.
func Caminho(dataPath, worker string, e Evento, hash32 string) string {
	return filepath.Join(
		dataPath, "comprovantes", worker,
		e.OcorridoEm.UTC().Format(formatoParticao),
		NomeArquivo(e.NossoNumero, hash32),
	)
}

// FormatarValor: 1234567 -> "R$ 12.345,67".
// Na mao mesmo - locale pt_BR nao existe nas imagens slim, e depender de
// locale para formatar dinheiro e bug que so aparece em producao.
func FormatarValor(centavos int64) string {
	sinal := ""
	if centavos < 0 {
		sinal = "-"
		centavos = -centavos
	}
	return fmt.Sprintf("R$ %s%s,%02d", sinal, comSeparadorDeMilhar(centavos/100), centavos%100)
}

func comSeparadorDeMilhar(n int64) string {
	s := strconv.FormatInt(n, 10)
	if len(s) <= 3 {
		return s
	}
	var b strings.Builder
	primeiro := len(s) % 3
	if primeiro > 0 {
		b.WriteString(s[:primeiro])
		b.WriteString(".")
	}
	for i := primeiro; i < len(s); i += 3 {
		b.WriteString(s[i : i+3])
		if i+3 < len(s) {
			b.WriteString(".")
		}
	}
	return b.String()
}

func FormatarData(t time.Time) string {
	return t.UTC().Format(formatoData)
}

// campo alinha o rotulo com pontos ate 18 CARACTERES.
// RuneCountInString, nao len(): "Nosso número" tem 12 runes e 13 bytes por
// causa do acento. Usar len() desalinharia essa linha - e so essa - em
// relacao ao Python e ao .NET.
func campo(rotulo, valor string) string {
	if faltam := larguraRotulo - utf8.RuneCountInString(rotulo); faltam > 0 {
		rotulo += strings.Repeat(".", faltam)
	}
	return rotulo + ": " + valor
}

// Renderizar monta o texto completo do comprovante.
func Renderizar(e Evento, worker, host, messageID, tentativa, trace, hash32 string, registradoEm time.Time) string {
	titulo, ok := titulos[worker]
	if !ok {
		titulo = "COMPROVANTE DE " + strings.ToUpper(worker)
	}
	regua := strings.Repeat("=", Largura)

	linhas := []string{
		regua,
		"  " + titulo,
		regua,
		campo("Nosso número", e.NossoNumero),
		campo("Evento", e.TipoEvento),
		campo("Valor", FormatarValor(e.ValorCentavos)),
		campo("Ocorrido em", FormatarData(e.OcorridoEm)),
		strings.Repeat("-", Largura),
		campo("Processado por", worker),
		campo("Pod / host", host),
		campo("Message ID", messageID),
		// O trace no proprio documento e o que fecha o circuito: de um
		// comprovante no volume voce volta para a cadeia de log inteira
		// (./run.sh trace <id>). Sem ele, o artefato e um beco sem saida.
		campo("Trace ID", trace),
		campo("Tentativa", tentativa),
		campo("Registrado em", FormatarData(registradoEm)),
		campo("Hash do payload", hash32),
		regua,
		"Documento gerado automaticamente para fins de laboratório.",
		"Não possui valor fiscal ou probatório.",
	}
	return strings.Join(linhas, "\n") + "\n"
}

// GravarAtomico grava o comprovante. Devolve true se gravou, false se ja
// existia.
//
// Duas armadilhas resolvidas aqui:
//
//  1. Escrita atomica. Escrever direto no destino final deixa um .txt
//     truncado se o processo morrer no meio - e como o nome ja existe, a
//     logica de idempotencia nunca mais tenta de novo.
//
//  2. os.Rename SOBRESCREVE em POSIX, em silencio. E a traducao ingenua de
//     File.Move(..., overwrite: false) do .NET, e quebra a corrida entre duas
//     replicas: as duas "ganham". os.Link e o oposto - falha com
//     fs.ErrExist se o destino ja existe, que e a semantica que queremos.
//     (Em Python o equivalente e os.link; os.rename tem o mesmo defeito.)
func GravarAtomico(destino, conteudo string) (bool, error) {
	if err := os.MkdirAll(filepath.Dir(destino), 0o755); err != nil {
		return false, err
	}

	// Atalho barato: na maioria das duplicatas o arquivo ja esta la.
	if _, err := os.Stat(destino); err == nil {
		return false, nil
	} else if !errors.Is(err, fs.ErrNotExist) {
		return false, err
	}

	tmp := filepath.Join(filepath.Dir(destino),
		fmt.Sprintf(".%s.%d.tmp", filepath.Base(destino), os.Getpid()))
	if err := os.WriteFile(tmp, []byte(conteudo), 0o644); err != nil {
		return false, err
	}
	defer os.Remove(tmp)

	if err := os.Link(tmp, destino); err != nil {
		if errors.Is(err, fs.ErrExist) {
			// Perdemos a corrida para outra replica. Nao e erro: o
			// comprovante existe, que era o objetivo.
			return false, nil
		}
		return false, err
	}
	return true, nil
}
