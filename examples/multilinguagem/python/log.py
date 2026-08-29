"""
Log estruturado com trace_id - Python.

Duas saidas para o mesmo evento, de proposito:

  stdout  linha legivel, para o `docker compose logs`
  arquivo NDJSON em /data/logs/<servico>-<host>.ndjson

Em producao voce escreveria SO no stdout, em JSON, e um coletor
(fluent-bit, CloudWatch agent, Datadog) leria dali. O arquivo no volume e o
substituto de laboratorio para esse coletor: e o que permite `./run.sh trace`
e o visualizador juntarem, num lugar so, o que tres processos em tres
linguagens escreveram.

UM ARQUIVO POR ESCRITOR - o nome carrega o host. E o mesmo raciocinio da
escrita dos comprovantes: em vez de disputar append no mesmo arquivo (que
sobre NFS tem semantica frouxa), cada processo escreve no seu e a juncao
acontece na leitura.

Equivalentes: ../go/logx/logx.go e ../dotnet/Log.cs.
"""

from __future__ import annotations

import hashlib
import json
import os
import socket
import threading
import uuid
from datetime import datetime, timezone
from pathlib import Path

SEM_TRACE = "-" * 8


def novo_trace() -> str:
    """
    32 caracteres hexadecimais minusculos - o mesmo formato do trace-id do
    W3C Trace Context. Em producao isso viria do OpenTelemetry, pelo cabecalho
    `traceparent`; aqui esta na mao para deixar o mecanismo a vista.
    """
    return uuid.uuid4().hex


def trace_do_message_id(message_id: str) -> str:
    """
    O trace de uma mensagem que chegou SEM o atributo traceId.

    Acontece quando alguem publica direto na fila (`aws sqs send-message`),
    que e exatamente o que a etapa 9 do ROTEIRO faz para exercitar a DLQ.

    Derivado do MessageId, NUNCA sorteado. O MessageId e estavel entre
    reentregas, entao as 3 tentativas da mesma mensagem caem no mesmo trace e
    voce enxerga a cadeia inteira ate a DLQ. Um id sorteado a cada receive
    quebraria justamente o caso em que rastrear vale mais.
    """
    return hashlib.md5(message_id.encode("utf-8")).hexdigest()


class Log:
    """
    Emissor de eventos. Um por processo.

    Cada evento tem um NOME (`comprovante.gravado`), e nao so um texto. E o
    nome que o `./run.sh trace` e o visualizador usam para remontar a cadeia -
    texto livre serve para humano, nome de evento serve para maquina.
    """

    def __init__(self, servico: str, worker: str, linguagem: str = "python",
                 data_path: str | None = None):
        self.servico = servico
        self.worker = worker
        self.linguagem = linguagem
        self.host = socket.gethostname()
        self._trava = threading.Lock()

        self._arquivo: Path | None = None
        base = data_path or os.environ.get("DATA_PATH") or "/data"
        try:
            pasta = Path(base) / "logs"
            pasta.mkdir(parents=True, exist_ok=True)
            self._arquivo = pasta / f"{servico}-{self.host}.ndjson"
        except OSError:
            # Sem volume montado (ex.: rodando fora do compose) o log vai so
            # para o stdout. Nao e motivo para o worker deixar de subir.
            self._arquivo = None

    # -- API -----------------------------------------------------------------

    def evento(self, evento: str, msg: str, trace: str | None = None,
               nivel: str = "info", **campos) -> None:
        agora = datetime.now(timezone.utc)

        # A ORDEM DAS CHAVES IMPORTA e "ts" vem primeiro.
        # Os arquivos das tres linguagens sao concatenados e ordenados com um
        # `sort` de texto puro - sem jq, sem parser. Isso so funciona porque o
        # timestamp e a primeira chave e tem LARGURA FIXA: 6 casas decimais
        # sempre, sempre em UTC, sempre com o Z no fim.
        #
        # Microssegundos e nao milissegundos de proposito. Com 3 casas, dois
        # eventos do mesmo processo caem no mesmo instante com frequencia, e
        # ai o `sort` desempata pelo RESTO da linha - ou seja, em ordem
        # alfabetica do nome do evento. O resultado e uma cadeia que mostra
        # "mensagem.falhou" antes de "mensagem.recebida".
        registro = {
            "ts": agora.strftime("%Y-%m-%dT%H:%M:%S.") + f"{agora.microsecond:06d}Z",
            "nivel": nivel,
            "servico": self.servico,
            "worker": self.worker,
            "linguagem": self.linguagem,
            "host": self.host,
            "traceId": trace or "",
            "evento": evento,
            "msg": msg,
        }
        registro.update(campos)

        linha = json.dumps(registro, ensure_ascii=False, separators=(",", ":"))

        with self._trava:
            # stdout legivel: e o que o ROTEIRO manda voce ler.
            curto = (trace or "")[:8] or SEM_TRACE
            print(f"[{agora.strftime('%H:%M:%S')}] {self.worker} {curto}  {msg}", flush=True)

            if self._arquivo is not None:
                try:
                    with self._arquivo.open("a", encoding="utf-8") as f:
                        f.write(linha + "\n")
                except OSError:
                    pass  # log que derruba o worker e pior que log perdido

    def erro(self, evento: str, msg: str, trace: str | None = None, **campos) -> None:
        self.evento(evento, msg, trace=trace, nivel="erro", **campos)
