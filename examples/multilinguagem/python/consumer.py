"""
Consumer - Python / boto3.  (no docker-compose: consumer-registro)

Consome a propria fila, grava um comprovante .txt no volume compartilhado e
so entao apaga a mensagem.

Cinco decisoes deste arquivo valem mais que o resto do codigo:

  1. NAO EXISTE FILTRO AQUI. O worker le a fila dele e pronto. Quem decidiu
     que so eventos "cobranca.registrada" chegam nesta fila foi a FilterPolicy
     da subscription - roteamento e do broker, nao da aplicacao.

  2. O DeleteMessage vem DEPOIS de gravar o comprovante. Se o processo morrer
     entre as duas coisas, a mensagem volta pela expiracao do visibility
     timeout e o comprovante sai na proxima tentativa. A ordem inversa criaria
     a janela oposta: mensagem sumindo sem comprovante nenhum.

  3. Erro NAO deleta a mensagem. E assim que a DLQ funciona (etapa 9 do
     ROTEIRO): a mensagem e tentada maxReceiveCount vezes e so entao migra.
     Um "except: pass" com delete no fim transformaria a DLQ em enfeite.

  4. Idempotencia mora no NOME DO ARQUIVO, nao em memoria. Um set() de
     messageIds nao sobreviveria a restart do pod nem valeria entre replicas.
     O volume e compartilhado; o nome derivado do hash do payload, tambem.

  5. Um client, criado uma vez. Ver guia 03 §4.

Equivalentes: ../go/consumer.go e ../dotnet/Consumer.cs.
"""

import os
import socket
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from botocore.exceptions import BotoCoreError, ClientError

import aws
import comprovante
import log as logmod
from comprovante import Evento

HOST = socket.gethostname()  # dentro de um pod, isto e o nome do pod
WORKER = os.environ.get("WORKER", "registro")

LOG = logmod.Log(servico="consumer", worker=WORKER, linguagem="python")


def trace_da_mensagem(mensagem: dict) -> tuple[str, str]:
    """
    Devolve (traceId, origem) da mensagem.

    O caminho normal: o publisher pos o traceId num MessageAttribute e ele
    atravessou o SNS ate aqui. Para isso funcionar, o ReceiveMessage precisa
    PEDIR os atributos (MessageAttributeNames) - sem pedir, eles nao vem.

    O caminho anormal, e o que mais ensina: alguem publicou direto na fila com
    `aws sqs send-message`, sem atributo nenhum - e exatamente o que a etapa 9
    do ROTEIRO faz para exercitar a DLQ. Ai o trace e DERIVADO do MessageId,
    nunca sorteado: o MessageId e estavel entre reentregas, entao as tres
    tentativas da mesma mensagem caem no mesmo trace e a cadeia ate a DLQ fica
    visivel. Um id sorteado a cada receive quebraria justamente o caso em que
    rastrear vale mais.
    """
    atributos = mensagem.get("MessageAttributes") or {}
    vindo = (atributos.get("traceId") or {}).get("StringValue")
    if vindo:
        return vindo, "propagado"
    return logmod.trace_do_message_id(mensagem.get("MessageId", "")), "derivado-do-message-id"


def verificar_volume(data_path: str) -> None:
    """
    Grava e apaga um arquivo-sonda em DATA_PATH antes de processar qualquer
    coisa.

    Parece paranoia e nao e. No EFS o diretorio pode existir e ainda assim
    negar escrita, quando o fsGroup do pod nao bate com o gid do Access Point.
    Sem esta checagem o worker sobe saudavel, processa mensagens, grava tudo
    no filesystem EFEMERO do container - e voce so descobre quando o pod morre.

    Repare tambem no tipo do erro: PermissionError (UnauthorizedAccessException
    no .NET, os.ErrPermission no Go). Nao e AccessDenied, nao menciona role
    nenhuma, porque nao e IAM - e POSIX. Procurar a causa na policy e o
    caminho errado e custa tempo (guia 06, tabela de sintomas).
    """
    sonda = Path(data_path) / f".sonda-{HOST}"
    try:
        Path(data_path).mkdir(parents=True, exist_ok=True)
        sonda.write_text("ok", encoding="utf-8")
        sonda.unlink()
    except PermissionError as e:
        raise SystemExit(
            f"sem permissao de escrita em {data_path}: {e}\n"
            f"  isto e POSIX (uid/gid), nao IAM. No EKS, confira o "
            f"securityContext.fsGroup contra o gid do Access Point do EFS."
        ) from e
    except OSError as e:
        raise SystemExit(f"volume {data_path} indisponivel: {e}") from e

    LOG.evento("volume.ok", f"volume {data_path} montado e gravavel", dataPath=data_path)


def processar(mensagem: dict, worker: str, data_path: str, trace: str) -> None:
    corpo = mensagem["Body"]

    # Estoura ValueError em payload invalido -> a mensagem NAO e deletada
    # -> volta pelo visibility timeout -> DLQ depois de maxReceiveCount.
    evento: Evento = Evento.do_json(corpo)

    hash32 = comprovante.hash_payload(corpo)
    destino = comprovante.caminho(data_path, worker, evento, hash32)

    # ApproximateReceiveCount so vem se a gente PEDIR no receive_message.
    # Se aparecer 2 ou 3, aquela mensagem ja falhou antes.
    tentativa = mensagem.get("Attributes", {}).get("ApproximateReceiveCount", "1")

    texto = comprovante.renderizar(
        evento=evento,
        worker=worker,
        host=HOST,
        message_id=mensagem.get("MessageId", "-"),
        tentativa=tentativa,
        trace=trace,
        hash32=hash32,
        registrado_em=datetime.now(timezone.utc),
    )

    if comprovante.gravar_atomico(destino, texto):
        LOG.evento("comprovante.gravado", f"comprovante gravado: {destino}", trace=trace,
                   caminho=str(destino), hash=hash32, nossoNumero=evento.nosso_numero,
                   tipoEvento=evento.tipo_evento, tentativa=tentativa)
    else:
        # A duplicata tem trace PROPRIO, mas aponta para um comprovante que
        # outro trace gravou. E o comportamento correto: o arquivo nao e
        # reescrito, entao ele guarda para sempre a cadeia da PRIMEIRA
        # gravacao. Ao rastrear uma republicacao voce vai encontrar a cadeia
        # terminando aqui, e o Trace ID do arquivo apontando para outra.
        LOG.evento("comprovante.duplicado",
                   f"comprovante ja existe, duplicata ignorada: {destino.name}",
                   trace=trace, caminho=str(destino), hash=hash32,
                   nossoNumero=evento.nosso_numero, tentativa=tentativa)


def main() -> None:
    queue_url = os.environ.get("QUEUE_URL", "").strip()
    if not queue_url:
        raise SystemExit("QUEUE_URL nao definida")
    data_path = os.environ.get("DATA_PATH", "/data")

    verificar_volume(data_path)

    sqs = aws.cliente("sqs")
    LOG.evento("worker.iniciado", f"lendo {queue_url}", queueUrl=queue_url)

    while True:
        try:
            resposta = sqs.receive_message(
                QueueUrl=queue_url,
                MaxNumberOfMessages=10,
                # Long polling. Com 0 aqui voce faria uma chamada por segundo
                # devolvendo vazio - custa dinheiro na AWS de verdade e nao
                # entrega a mensagem mais rapido.
                WaitTimeSeconds=20,
                # Sem pedir, o campo "Tentativa" do comprovante seria sempre 1.
                AttributeNames=["ApproximateReceiveCount"],
                # Sem pedir, o traceId que o publisher mandou nao chega aqui -
                # e a cadeia se parte no meio, no ponto mais importante dela.
                MessageAttributeNames=["All"],
            )
        except (ClientError, BotoCoreError) as e:
            # Esperado nos primeiros segundos: a fila pode nao existir ainda.
            LOG.erro("receive.erro",
                     f"receive falhou ({type(e).__name__}); tentando de novo em 5s",
                     erro=type(e).__name__)
            time.sleep(5)
            continue

        for mensagem in resposta.get("Messages", []):
            trace, origem_trace = trace_da_mensagem(mensagem)
            tentativa = mensagem.get("Attributes", {}).get("ApproximateReceiveCount", "1")
            LOG.evento("mensagem.recebida", f"mensagem recebida (tentativa {tentativa})",
                       trace=trace, messageId=mensagem.get("MessageId"),
                       tentativa=tentativa, origemTrace=origem_trace)

            try:
                processar(mensagem, WORKER, data_path, trace)
            except Exception as e:  # noqa: BLE001 - qualquer falha e "nao deleta"
                LOG.erro("mensagem.falhou",
                         f"FALHOU ao processar {mensagem.get('MessageId')}: {e}",
                         trace=trace, messageId=mensagem.get("MessageId"),
                         tentativa=tentativa, erro=str(e))
                continue  # <- o pulo do gato: sem delete, a mensagem volta

            try:
                sqs.delete_message(
                    QueueUrl=queue_url, ReceiptHandle=mensagem["ReceiptHandle"]
                )
                LOG.evento("mensagem.deletada", "mensagem removida da fila", trace=trace,
                           messageId=mensagem.get("MessageId"))
            except (ClientError, BotoCoreError) as e:
                # O comprovante ja esta gravado. A mensagem vai voltar e a
                # idempotencia (nome do arquivo) resolve na proxima.
                LOG.erro("delete.erro",
                         f"delete falhou ({type(e).__name__}); a mensagem voltara",
                         trace=trace, erro=type(e).__name__)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
