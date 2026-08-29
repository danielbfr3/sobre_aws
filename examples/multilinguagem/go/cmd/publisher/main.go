// Publisher - Go / aws-sdk-go-v2.
//
// Publica um evento de cobranca a cada INTERVALO_SEGUNDOS no SNS.
//
// O que este arquivo prova, e que e o motivo de o SNS existir no desenho:
// o publisher NAO SABE QUEM CONSOME. Nao ha nome de fila aqui, nao ha lista
// de assinantes, nao ha if de roteamento.
//
// Equivalentes: ../../python/publisher.py e ../../dotnet/Publisher.cs.
package main

import (
	"context"
	"fmt"
	"log"
	"math/rand"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/sns"
	snstypes "github.com/aws/aws-sdk-go-v2/service/sns/types"

	"lab/awsx"
	"lab/comprovante"
	"lab/logx"
)

var tipos = []string{"cobranca.registrada", "cobranca.baixada", "cobranca.rejeitada"}

var LOG = logx.Novo("publisher", "publisher", "go", "")

// topicoDe devolve o ARN do topico para este tipo de evento.
//
// O bootstrap.sh grava um ARN por tipo no .env. Em TOPIC_MODE=single os tres
// apontam para o mesmo topico (o roteamento e da FilterPolicy); em
// TOPIC_MODE=multi cada um e um topico proprio. O publisher nunca pergunta em
// que modo esta - modo de topico e decisao de INFRA.
func topicoDe(tipoEvento string) (string, error) {
	nome := "TOPIC_ARN_" + strings.ToUpper(strings.ReplaceAll(tipoEvento, ".", "_"))
	arn := strings.TrimSpace(os.Getenv(nome))
	if arn == "" {
		return "", fmt.Errorf("variavel %s nao definida - rode ./run.sh bootstrap", nome)
	}
	return arn, nil
}

func sortear() comprovante.Evento {
	return comprovante.Evento{
		NossoNumero:   strconv.Itoa(1_000_000 + rand.Intn(9_000_000)),
		TipoEvento:    tipos[rand.Intn(len(tipos))],
		ValorCentavos: int64(1_000 + rand.Intn(499_000)),
		OcorridoEm:    time.Now().UTC().Truncate(time.Second),
	}
}

func main() {
	ctx := context.Background()

	intervalo := 3 * time.Second
	if v := os.Getenv("INTERVALO_SEGUNDOS"); v != "" {
		if s, err := strconv.ParseFloat(v, 64); err == nil {
			intervalo = time.Duration(s * float64(time.Second))
		}
	}

	cfg, err := awsx.Config(ctx)
	if err != nil {
		log.Fatalf("nao consegui montar a configuracao AWS: %v", err)
	}
	// UM client, criado uma vez, reusado pelo processo inteiro. Ver guia 03 §4.
	cliente := awsx.SNS(cfg)

	destino := awsx.Endpoint()
	if destino == "" {
		destino = "AWS real"
	}
	LOG.Evento("publisher.iniciado",
		fmt.Sprintf("publicando a cada %s (endpoint: %s)", intervalo, destino), "",
		logx.Campos{"intervaloSegundos": intervalo.Seconds()})

	publicados := 0
	for {
		evento := sortear()
		corpo, err := evento.ParaJSON()
		if err != nil {
			log.Fatalf("erro serializando o evento: %v", err)
		}

		arn, err := topicoDe(evento.TipoEvento)
		if err != nil {
			log.Fatal(err)
		}

		// O TRACE NASCE AQUI. Um por evento publicado, e ele acompanha a
		// mensagem ate o comprovante no volume.
		trace := logx.NovoTrace()
		LOG.Evento("publish.iniciado", "publicando "+evento.TipoEvento, trace,
			logx.Campos{"tipoEvento": evento.TipoEvento, "nossoNumero": evento.NossoNumero,
				"valorCentavos": evento.ValorCentavos})
		t0 := time.Now()

		resposta, err := cliente.Publish(ctx, &sns.PublishInput{
			TopicArn: aws.String(arn),
			Message:  aws.String(corpo),
			// O ATRIBUTO E O ROTEAMENTO. A FilterPolicy de cada subscription
			// casa com este valor. Publicar sem ele faz a mensagem nao casar
			// com filtro nenhum e sumir sem erro nenhum.
			MessageAttributes: map[string]snstypes.MessageAttributeValue{
				"eventType": {DataType: aws.String("String"), StringValue: aws.String(evento.TipoEvento)},
				// O TRACE VIAJA NO ATRIBUTO, NUNCA NO CORPO.
				//
				// Isto nao e preferencia de estilo. O nome do comprovante
				// deriva do hash do CORPO da mensagem; se o traceId entrasse
				// ali, cada republicacao do "mesmo" evento geraria um hash
				// diferente, um nome de arquivo diferente, e a idempotencia
				// deixaria de funcionar - silenciosamente.
				//
				// Atributo e metadado de transporte; corpo e o fato de
				// negocio. O hash so pode cobrir o segundo.
				"traceId": {DataType: aws.String("String"), StringValue: aws.String(trace)},
			},
		})
		if err != nil {
			// Normal nos primeiros segundos: o topico pode nao existir ainda.
			LOG.Erro("publish.erro",
				fmt.Sprintf("publish falhou (%v); tentando de novo em 5s", err), trace,
				logx.Campos{"erro": err.Error()})
			time.Sleep(5 * time.Second)
			continue
		}

		publicados++
		LOG.Evento("publish.ok",
			fmt.Sprintf("#%d %s nossoNumero=%s messageId=%s",
				publicados, evento.TipoEvento, evento.NossoNumero, aws.ToString(resposta.MessageId)),
			trace,
			logx.Campos{"tipoEvento": evento.TipoEvento, "nossoNumero": evento.NossoNumero,
				"messageId": aws.ToString(resposta.MessageId),
				"duracaoMs": float64(time.Since(t0).Microseconds()) / 1000})
		time.Sleep(intervalo)
	}
}
