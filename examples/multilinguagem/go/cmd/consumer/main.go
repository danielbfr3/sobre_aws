// Consumer - Go / aws-sdk-go-v2.  (no docker-compose: consumer-baixa)
//
// Consome a propria fila, grava um comprovante .txt no volume compartilhado e
// so entao apaga a mensagem.
//
// Cinco decisoes deste arquivo valem mais que o resto do codigo:
//
//  1. NAO EXISTE FILTRO AQUI. Quem decidiu que so eventos "cobranca.baixada"
//     chegam nesta fila foi a FilterPolicy da subscription - roteamento e do
//     broker, nao da aplicacao.
//
//  2. O DeleteMessage vem DEPOIS de gravar o comprovante. Se o processo
//     morrer entre as duas coisas, a mensagem volta pela expiracao do
//     visibility timeout. A ordem inversa criaria a janela oposta: mensagem
//     sumindo sem comprovante nenhum.
//
//  3. Erro NAO deleta a mensagem. E assim que a DLQ funciona (etapa 9 do
//     ROTEIRO).
//
//  4. Idempotencia mora no NOME DO ARQUIVO, nao em memoria. Um map de
//     messageIDs nao sobreviveria a restart do pod nem valeria entre
//     replicas.
//
//  5. Um client, criado uma vez. Ver guia 03 §4.
//
// Equivalentes: ../../python/consumer.py e ../../dotnet/Consumer.cs.
package main

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"log"
	"os"
	"path/filepath"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/sqs"
	sqstypes "github.com/aws/aws-sdk-go-v2/service/sqs/types"

	"lab/awsx"
	"lab/comprovante"
	"lab/logx"
)

var (
	host   = hostname() // dentro de um pod, isto e o nome do pod
	worker = env("WORKER", "baixa")
)

func hostname() string {
	h, err := os.Hostname()
	if err != nil {
		return "desconhecido"
	}
	return h
}

func env(nome, padrao string) string {
	if v := os.Getenv(nome); v != "" {
		return v
	}
	return padrao
}

var LOG = logx.Novo("consumer", worker, "go", "")

// traceDaMensagem devolve (traceId, origem).
//
// O caminho normal: o publisher pos o traceId num MessageAttribute e ele
// atravessou o SNS ate aqui. Para isso funcionar, o ReceiveMessage precisa
// PEDIR os atributos (MessageAttributeNames) - sem pedir, eles nao vem.
//
// O caminho anormal, e o que mais ensina: alguem publicou direto na fila com
// `aws sqs send-message`, sem atributo nenhum - e exatamente o que a etapa 9
// do ROTEIRO faz para exercitar a DLQ. Ai o trace e DERIVADO do MessageId,
// nunca sorteado: o MessageId e estavel entre reentregas, entao as tres
// tentativas da mesma mensagem caem no mesmo trace e a cadeia ate a DLQ fica
// visivel.
func traceDaMensagem(m sqstypes.Message) (string, string) {
	if v, ok := m.MessageAttributes["traceId"]; ok && aws.ToString(v.StringValue) != "" {
		return aws.ToString(v.StringValue), "propagado"
	}
	return logx.TraceDoMessageID(aws.ToString(m.MessageId)), "derivado-do-message-id"
}

// verificarVolume grava e apaga um arquivo-sonda em DATA_PATH antes de
// processar qualquer coisa.
//
// Parece paranoia e nao e. No EFS o diretorio pode existir e ainda assim
// negar escrita, quando o fsGroup do pod nao bate com o gid do Access Point.
// Sem esta checagem o worker sobe saudavel, processa mensagens, grava tudo no
// filesystem EFEMERO do container - e voce so descobre quando o pod morre.
//
// Repare no tipo do erro: fs.ErrPermission (PermissionError no Python,
// UnauthorizedAccessException no .NET). Nao e AccessDenied, nao menciona role
// nenhuma, porque nao e IAM - e POSIX. Procurar a causa na policy e o caminho
// errado e custa tempo (guia 06, tabela de sintomas).
func verificarVolume(dataPath string) {
	if err := os.MkdirAll(dataPath, 0o755); err != nil {
		log.Fatalf("volume %s indisponivel: %v", dataPath, err)
	}
	sonda := filepath.Join(dataPath, ".sonda-"+host)
	if err := os.WriteFile(sonda, []byte("ok"), 0o644); err != nil {
		if errors.Is(err, fs.ErrPermission) {
			log.Fatalf("sem permissao de escrita em %s: %v\n"+
				"  isto e POSIX (uid/gid), nao IAM. No EKS, confira o "+
				"securityContext.fsGroup contra o gid do Access Point do EFS.",
				dataPath, err)
		}
		log.Fatalf("volume %s nao gravavel: %v", dataPath, err)
	}
	_ = os.Remove(sonda)
	LOG.Evento("volume.ok", fmt.Sprintf("volume %s montado e gravavel", dataPath), "",
		logx.Campos{"dataPath": dataPath})
}

func processar(m sqstypes.Message, dataPath, trace string) error {
	corpo := aws.ToString(m.Body)

	// Erro em payload invalido -> a mensagem NAO e deletada -> volta pelo
	// visibility timeout -> DLQ depois de maxReceiveCount.
	evento, err := comprovante.DoJSON(corpo)
	if err != nil {
		return err
	}

	hash32 := comprovante.HashPayload(corpo)
	destino := comprovante.Caminho(dataPath, worker, evento, hash32)

	// ApproximateReceiveCount so vem se a gente PEDIR no ReceiveMessage.
	// Se aparecer 2 ou 3, aquela mensagem ja falhou antes.
	tentativa := "1"
	if v, ok := m.Attributes["ApproximateReceiveCount"]; ok && v != "" {
		tentativa = v
	}

	texto := comprovante.Renderizar(evento, worker, host,
		aws.ToString(m.MessageId), tentativa, trace, hash32, time.Now().UTC())

	gravou, err := comprovante.GravarAtomico(destino, texto)
	if err != nil {
		return err
	}
	if gravou {
		LOG.Evento("comprovante.gravado", "comprovante gravado: "+destino, trace,
			logx.Campos{"caminho": destino, "hash": hash32, "nossoNumero": evento.NossoNumero,
				"tipoEvento": evento.TipoEvento, "tentativa": tentativa})
	} else {
		// A duplicata tem trace PROPRIO, mas aponta para um comprovante que
		// outro trace gravou. E o comportamento correto: o arquivo nao e
		// reescrito, entao ele guarda para sempre a cadeia da PRIMEIRA
		// gravacao.
		LOG.Evento("comprovante.duplicado",
			"comprovante ja existe, duplicata ignorada: "+filepath.Base(destino), trace,
			logx.Campos{"caminho": destino, "hash": hash32,
				"nossoNumero": evento.NossoNumero, "tentativa": tentativa})
	}
	return nil
}

func main() {
	ctx := context.Background()

	queueURL := os.Getenv("QUEUE_URL")
	if queueURL == "" {
		log.Fatal("QUEUE_URL nao definida")
	}
	dataPath := env("DATA_PATH", "/data")

	verificarVolume(dataPath)

	cfg, err := awsx.Config(ctx)
	if err != nil {
		log.Fatalf("nao consegui montar a configuracao AWS: %v", err)
	}
	cliente := awsx.SQS(cfg)
	LOG.Evento("worker.iniciado", "lendo "+queueURL, "", logx.Campos{"queueUrl": queueURL})

	for {
		saida, err := cliente.ReceiveMessage(ctx, &sqs.ReceiveMessageInput{
			QueueUrl:            aws.String(queueURL),
			MaxNumberOfMessages: 10,
			// Long polling. Com 0 aqui voce faria uma chamada por segundo
			// devolvendo vazio - custa dinheiro na AWS de verdade e nao
			// entrega a mensagem mais rapido.
			WaitTimeSeconds: 20,
			// Sem pedir, o campo "Tentativa" do comprovante seria sempre 1.
			// Este e o parametro legado (AttributeName.N no wire); o SDK
			// oferece MessageSystemAttributeNames, mais novo, mas o legado e
			// o que todo emulador entende.
			AttributeNames: []sqstypes.QueueAttributeName{
				sqstypes.QueueAttributeName("ApproximateReceiveCount"),
			},
			// Sem pedir, o traceId que o publisher mandou nao chega aqui - e a
			// cadeia se parte no meio, no ponto mais importante dela.
			MessageAttributeNames: []string{"All"},
		})
		if err != nil {
			// Esperado nos primeiros segundos: a fila pode nao existir ainda.
			LOG.Erro("receive.erro",
				fmt.Sprintf("receive falhou (%v); tentando de novo em 5s", err), "",
				logx.Campos{"erro": err.Error()})
			time.Sleep(5 * time.Second)
			continue
		}

		for _, m := range saida.Messages {
			trace, origemTrace := traceDaMensagem(m)
			tentativa := "1"
			if v, ok := m.Attributes["ApproximateReceiveCount"]; ok && v != "" {
				tentativa = v
			}
			LOG.Evento("mensagem.recebida", "mensagem recebida (tentativa "+tentativa+")", trace,
				logx.Campos{"messageId": aws.ToString(m.MessageId), "tentativa": tentativa,
					"origemTrace": origemTrace})

			if err := processar(m, dataPath, trace); err != nil {
				LOG.Erro("mensagem.falhou",
					fmt.Sprintf("FALHOU ao processar %s: %v", aws.ToString(m.MessageId), err), trace,
					logx.Campos{"messageId": aws.ToString(m.MessageId),
						"tentativa": tentativa, "erro": err.Error()})
				continue // <- o pulo do gato: sem delete, a mensagem volta
			}

			if _, err := cliente.DeleteMessage(ctx, &sqs.DeleteMessageInput{
				QueueUrl:      aws.String(queueURL),
				ReceiptHandle: m.ReceiptHandle,
			}); err != nil {
				// O comprovante ja esta gravado. A mensagem vai voltar e a
				// idempotencia (nome do arquivo) resolve na proxima.
				LOG.Erro("delete.erro",
					fmt.Sprintf("delete falhou (%v); a mensagem voltara", err), trace,
					logx.Campos{"erro": err.Error()})
			} else {
				LOG.Evento("mensagem.deletada", "mensagem removida da fila", trace,
					logx.Campos{"messageId": aws.ToString(m.MessageId)})
			}
		}
	}
}
