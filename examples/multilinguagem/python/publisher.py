"""
Publisher - Python / boto3.

Publica um evento de cobranca a cada INTERVALO_SEGUNDOS no SNS.

O que este arquivo prova, e que e o motivo de o SNS existir no desenho:
o publisher NAO SABE QUEM CONSOME. Nao ha nome de fila aqui, nao ha lista de
assinantes, nao ha if de roteamento. Ele publica um evento com um atributo
"eventType" e acabou. Amanha, quando surgir um quarto consumidor (antifraude,
BI, auditoria), ninguem toca neste arquivo - cria-se mais uma subscription.

Equivalentes: ../go/publisher.go e ../dotnet/Publisher.cs.
"""

import os
import random
import sys
import time
from datetime import datetime, timezone

from botocore.exceptions import BotoCoreError, ClientError

import aws
import log as logmod
from comprovante import Evento

TIPOS = ["cobranca.registrada", "cobranca.baixada", "cobranca.rejeitada"]

LOG = logmod.Log(servico="publisher", worker="publisher", linguagem="python")


def topico_de(tipo_evento: str) -> str:
    """
    O ARN do topico para este tipo de evento.

    O bootstrap.sh grava um ARN por tipo no .env. Em TOPIC_MODE=single os tres
    apontam para o mesmo topico (o roteamento e da FilterPolicy); em
    TOPIC_MODE=multi cada um e um topico proprio.

    O publisher nunca pergunta em que modo esta - ele so procura o ARN do
    evento que vai publicar. Modo de topico e decisao de INFRA, e o codigo
    fica de fora dela.
    """
    var = "TOPIC_ARN_" + tipo_evento.replace(".", "_").upper()
    arn = os.environ.get(var, "").strip()
    if not arn:
        raise SystemExit(f"variavel {var} nao definida - rode ./run.sh bootstrap")
    return arn


def sortear() -> Evento:
    return Evento(
        nosso_numero=str(random.randint(1_000_000, 9_999_999)),
        tipo_evento=random.choice(TIPOS),
        valor_centavos=random.randint(1_000, 500_000),
        ocorrido_em=datetime.now(timezone.utc).replace(microsecond=0),
    )


def main() -> None:
    intervalo = float(os.environ.get("INTERVALO_SEGUNDOS", "3"))

    # UM client, criado uma vez, reusado pelo processo inteiro. Ver guia 03 §4.
    sns = aws.cliente("sns")

    LOG.evento("publisher.iniciado",
               f"publicando a cada {intervalo:g}s (endpoint: {aws.endpoint() or 'AWS real'})",
               intervaloSegundos=intervalo)

    publicados = 0
    while True:
        evento = sortear()
        corpo = evento.para_json()

        # O TRACE NASCE AQUI. Um por evento publicado, e ele acompanha a
        # mensagem ate o comprovante no volume.
        trace = logmod.novo_trace()

        LOG.evento("publish.iniciado", f"publicando {evento.tipo_evento}", trace=trace,
                   tipoEvento=evento.tipo_evento, nossoNumero=evento.nosso_numero,
                   valorCentavos=evento.valor_centavos)
        t0 = time.monotonic()
        try:
            resposta = sns.publish(
                TopicArn=topico_de(evento.tipo_evento),
                Message=corpo,
                # O ATRIBUTO E O ROTEAMENTO. A FilterPolicy de cada subscription
                # casa com este valor. Publicar sem ele faz a mensagem nao casar
                # com filtro nenhum e sumir sem erro - o sintoma mais cruel do
                # fan-out, e o que o visualizador.html deixa voce provocar.
                MessageAttributes={
                    "eventType": {"DataType": "String", "StringValue": evento.tipo_evento},
                    # O TRACE VIAJA NO ATRIBUTO, NUNCA NO CORPO.
                    #
                    # Isto nao e preferencia de estilo. O nome do comprovante
                    # deriva do hash do CORPO da mensagem; se o traceId
                    # entrasse ali, cada republicacao do "mesmo" evento geraria
                    # um hash diferente, um nome de arquivo diferente, e a
                    # idempotencia deixaria de funcionar - silenciosamente.
                    #
                    # Atributo e metadado de transporte; corpo e o fato de
                    # negocio. O hash so pode cobrir o segundo.
                    "traceId": {"DataType": "String", "StringValue": trace},
                },
            )
        except (ClientError, BotoCoreError) as e:
            # Normal nos primeiros segundos: o topico pode nao existir ainda.
            LOG.erro("publish.erro",
                     f"publish falhou ({type(e).__name__}); tentando de novo em 5s",
                     trace=trace, erro=type(e).__name__)
            time.sleep(5)
            continue

        publicados += 1
        LOG.evento("publish.ok",
                   f"#{publicados} {evento.tipo_evento} "
                   f"nossoNumero={evento.nosso_numero} "
                   f"messageId={resposta['MessageId']}",
                   trace=trace,
                   tipoEvento=evento.tipo_evento, nossoNumero=evento.nosso_numero,
                   messageId=resposta["MessageId"],
                   duracaoMs=round((time.monotonic() - t0) * 1000, 1))
        time.sleep(intervalo)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
