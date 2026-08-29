// Amostra deterministica - Go.
//
// Renderiza UM comprovante a partir de um payload fixo, com host, message id
// e horario tambem fixos. Nada aqui varia entre execucoes.
//
// E o que torna a afirmacao "as tres implementacoes sao equivalentes"
// VERIFICAVEL em vez de so escrita no capitulo:
//
//	./run.sh comparar
//
// roda esta amostra nas tres linguagens e faz `diff`.
//
// Equivalentes: ../../../python/amostra.py e ../../../dotnet/Amostra.cs.
package main

import (
	"fmt"
	"log"
	"time"

	"lab/comprovante"
)

// O mesmo evento de exemplo que aparece no README secao 3.
var (
	evento = comprovante.Evento{
		NossoNumero:   "4827193",
		TipoEvento:    "cobranca.baixada",
		ValorCentavos: 123456,
		OcorridoEm:    time.Date(2026, 8, 8, 17, 42, 3, 0, time.UTC),
	}
	worker    = "baixa"
	host      = "consumer-baixa-7d9f8b4c2-x2k4l"
	messageID = "9f3a1c22-5e88-4b1d-a0f7-1c9e2b6d4a51"
	tentativa = "1"
	// Trace FIXO: a amostra tem que ser identica a cada execucao para o
	// ./run.sh comparar poder fazer diff. Em producao o trace nunca se repete.
	trace        = "a1b2c3d4e5f6071829304a5b6c7d8e9f"
	registradoEm = time.Date(2026, 8, 8, 17, 42, 4, 0, time.UTC)
)

func main() {
	corpo, err := evento.ParaJSON()
	if err != nil {
		log.Fatal(err)
	}
	hash32 := comprovante.HashPayload(corpo)
	caminho := comprovante.Caminho("/data", worker, evento, hash32)

	fmt.Printf("payload : %s\n", corpo)
	fmt.Printf("hash    : %s\n", hash32)
	fmt.Printf("caminho : %s\n", caminho)
	fmt.Println("---")
	fmt.Print(comprovante.Renderizar(evento, worker, host, messageID, tentativa, trace, hash32, registradoEm))
}
