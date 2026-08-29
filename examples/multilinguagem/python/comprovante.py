"""
O nucleo compartilhado: modelo do evento, formatacao do comprovante,
hash de idempotencia e escrita atomica.

Este arquivo e a IMPLEMENTACAO DE REFERENCIA. As versoes em Go
(../go/comprovante.go) e em .NET (../dotnet/Comprovante.cs) reproduzem
exatamente o que esta aqui: o mesmo payload gera o mesmo nome de arquivo e
o mesmo conteudo, byte a byte, nas tres linguagens.

Isso e proposital e da para conferir:

    docker compose exec -T consumer-registro sh -c \\
      'md5sum $(find /data/comprovantes -name "*.txt" | head -3)'

Se as tres linguagens divergissem no formato, o ./run.sh verify nao pegaria
(ele so procura o tipo do evento no texto) - mas voce perceberia na hora ao
abrir dois comprovantes de workers diferentes lado a lado.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

LARGURA = 64
LARGURA_ROTULO = 18

# O titulo muda por worker; o resto do documento e identico.
TITULOS = {
    "registro": "COMPROVANTE DE REGISTRO DE COBRANÇA",
    "baixa": "COMPROVANTE DE BAIXA DE COBRANÇA",
    "rejeicao": "COMPROVANTE DE REJEIÇÃO DE COBRANÇA",
}

_NOME_SEGURO = re.compile(r"[^A-Za-z0-9_-]")


@dataclass(frozen=True)
class Evento:
    """O payload que trafega no SNS e na SQS. Quatro campos, todos obrigatorios."""

    nosso_numero: str
    tipo_evento: str
    valor_centavos: int
    ocorrido_em: datetime

    @staticmethod
    def do_json(corpo: str) -> "Evento":
        """
        Faz o parse do Body da mensagem SQS.

        Levanta ValueError em qualquer coisa que nao seja o payload esperado -
        e e disso que a etapa 9 do ROTEIRO depende. Mandar 'isto-nao-e-json'
        para a fila tem que estourar aqui, a mensagem NAO ser deletada, e
        depois de maxReceiveCount tentativas ela cair na DLQ.
        """
        try:
            d = json.loads(corpo)
        except json.JSONDecodeError as e:
            raise ValueError(f"corpo nao e JSON valido: {e}") from e

        if not isinstance(d, dict):
            raise ValueError("corpo nao e um objeto JSON")

        faltando = [c for c in ("nossoNumero", "tipoEvento", "valorCentavos", "ocorridoEm")
                    if c not in d]
        if faltando:
            raise ValueError(f"campos ausentes no payload: {', '.join(faltando)}")

        return Evento(
            nosso_numero=str(d["nossoNumero"]),
            tipo_evento=str(d["tipoEvento"]),
            valor_centavos=int(d["valorCentavos"]),
            ocorrido_em=_parse_iso(str(d["ocorridoEm"])),
        )

    def para_json(self) -> str:
        """Serializacao COMPACTA e de chaves ordenadas.

        Nao e estilo: o hash de idempotencia e tirado destes bytes. Um espaco
        a mais aqui muda o nome do arquivo la na frente, e duas publicacoes do
        "mesmo" evento deixariam de ser duplicatas.
        """
        return json.dumps(
            {
                "nossoNumero": self.nosso_numero,
                "tipoEvento": self.tipo_evento,
                "valorCentavos": self.valor_centavos,
                "ocorridoEm": self.ocorrido_em.astimezone(timezone.utc)
                .strftime("%Y-%m-%dT%H:%M:%SZ"),
            },
            separators=(",", ":"),
            ensure_ascii=False,
        )


def _parse_iso(s: str) -> datetime:
    dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def hash_payload(corpo: str) -> str:
    """
    MD5 do corpo bruto, em hexadecimal MAIUSCULO.

    Por que MD5 e nao SHA-256: isto NAO e seguranca, e deduplicacao. O hash
    so responde "ja vi exatamente estes bytes?". MD5 cabe nos 32 caracteres
    que o comprovante mostra e ninguem esta tentando forjar colisao com um
    boleto de laboratorio. Se o seu caso for anti-fraude, use SHA-256 - e
    lembre de trocar nas tres linguagens ao mesmo tempo.
    """
    return hashlib.md5(corpo.encode("utf-8")).hexdigest().upper()


def nome_arquivo(nosso_numero: str, hash32: str) -> str:
    """{nossoNumero}-{8 primeiros do hash}.txt - a chave de idempotencia."""
    return f"{_NOME_SEGURO.sub('_', nosso_numero)}-{hash32[:8]}.txt"


def caminho(data_path: str, worker: str, evento: Evento, hash32: str) -> Path:
    """
    /data/comprovantes/<worker>/<AAAA-MM-DD>/<nossoNumero>-<hash8>.txt

    A data da particao vem do OCORRIDO_EM do evento, nao do relogio da
    maquina. Se viesse do relogio, uma mensagem reentregue depois da
    meia-noite cairia noutro diretorio, o teste de existencia nao acharia
    o arquivo anterior, e a idempotencia falharia uma vez por dia.
    Idempotencia nao pode depender de "que horas sao agora".
    """
    dia = evento.ocorrido_em.strftime("%Y-%m-%d")
    return Path(data_path) / "comprovantes" / worker / dia / nome_arquivo(
        evento.nosso_numero, hash32
    )


def formatar_valor(centavos: int) -> str:
    """1234567 -> 'R$ 12.345,67'. Na mao mesmo: locale pt_BR nao existe nas
    imagens slim, e depender de locale para formatar dinheiro e uma fonte
    conhecida de bug que so aparece em producao."""
    sinal = "-" if centavos < 0 else ""
    inteiro, cent = divmod(abs(centavos), 100)
    milhar = f"{inteiro:,}".replace(",", ".")
    return f"R$ {sinal}{milhar},{cent:02d}"


def formatar_data(dt: datetime) -> str:
    return dt.astimezone(timezone.utc).strftime("%d/%m/%Y %H:%M:%S UTC")


def _campo(rotulo: str, valor: str) -> str:
    # ljust conta CARACTERES, nao bytes. Importa porque "Nosso número" tem
    # acento - em Go isso vira contagem de runes, nao de len([]byte).
    return f"{rotulo.ljust(LARGURA_ROTULO, '.')}: {valor}"


def renderizar(
    evento: Evento,
    worker: str,
    host: str,
    message_id: str,
    tentativa: str,
    trace: str,
    hash32: str,
    registrado_em: datetime,
) -> str:
    titulo = TITULOS.get(worker, f"COMPROVANTE DE {worker.upper()}")
    linhas = [
        "=" * LARGURA,
        f"  {titulo}",
        "=" * LARGURA,
        _campo("Nosso número", evento.nosso_numero),
        _campo("Evento", evento.tipo_evento),
        _campo("Valor", formatar_valor(evento.valor_centavos)),
        _campo("Ocorrido em", formatar_data(evento.ocorrido_em)),
        "-" * LARGURA,
        _campo("Processado por", worker),
        _campo("Pod / host", host),
        _campo("Message ID", message_id),
        # O trace no proprio documento e o que fecha o circuito: de um
        # comprovante no volume voce volta para a cadeia de log inteira
        # (./run.sh trace <id>). Sem ele, o artefato e um beco sem saida.
        _campo("Trace ID", trace),
        _campo("Tentativa", tentativa),
        _campo("Registrado em", formatar_data(registrado_em)),
        _campo("Hash do payload", hash32),
        "=" * LARGURA,
        "Documento gerado automaticamente para fins de laboratório.",
        "Não possui valor fiscal ou probatório.",
    ]
    return "\n".join(linhas) + "\n"


def gravar_atomico(destino: Path, conteudo: str) -> bool:
    """
    Grava o comprovante. Devolve True se gravou, False se ja existia.

    Duas armadilhas resolvidas aqui, e a segunda e especifica de Python:

    1. Escrita atomica. Escrever direto no destino final deixa um .txt
       truncado se o processo morrer no meio - e como o nome ja existe, a
       logica de idempotencia nunca mais tenta de novo. Por isso: grava
       num temporario e so depois publica o nome definitivo.

    2. os.rename() SOBRESCREVE em POSIX, em silencio. E a traducao ingenua
       de File.Move(..., overwrite: false) do .NET, e ela QUEBRA a corrida
       entre duas replicas: as duas "ganham", a ultima sobrescreve a
       primeira. os.link() e o oposto - falha com FileExistsError se o
       destino ja existe, que e exatamente a semantica que queremos.
       Em Go o equivalente e os.Link.
    """
    destino.parent.mkdir(parents=True, exist_ok=True)

    # Atalho barato: na esmagadora maioria das duplicatas o arquivo ja esta
    # la e nem vale a pena escrever o temporario.
    if destino.exists():
        return False

    tmp = destino.with_name(f".{destino.name}.{os.getpid()}.tmp")
    tmp.write_text(conteudo, encoding="utf-8")
    try:
        os.link(tmp, destino)
        return True
    except FileExistsError:
        # Perdemos a corrida para outra replica. Isso NAO e erro: o
        # comprovante existe, que era o objetivo.
        return False
    finally:
        try:
            tmp.unlink()
        except FileNotFoundError:
            pass
