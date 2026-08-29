"""
Amostra deterministica - Python.

Renderiza UM comprovante a partir de um payload fixo, com host, message id e
horario tambem fixos. Nada aqui varia entre execucoes.

E o que torna a afirmacao "as tres implementacoes sao equivalentes"
VERIFICAVEL em vez de so escrita no capitulo:

    ./run.sh comparar

roda esta amostra nas tres linguagens e faz `diff`. Qualquer divergencia -
uma casa decimal, um ponto de alinhamento, um byte de BOM - aparece ali.

Equivalentes: ../go/cmd/amostra/main.go e ../dotnet/Amostra.cs.
"""

from datetime import datetime, timezone

import comprovante
from comprovante import Evento

# O mesmo evento de exemplo que aparece no README secao 3.
EVENTO = Evento(
    nosso_numero="4827193",
    tipo_evento="cobranca.baixada",
    valor_centavos=123_456,
    ocorrido_em=datetime(2026, 8, 8, 17, 42, 3, tzinfo=timezone.utc),
)
WORKER = "baixa"
HOST = "consumer-baixa-7d9f8b4c2-x2k4l"
MESSAGE_ID = "9f3a1c22-5e88-4b1d-a0f7-1c9e2b6d4a51"
TENTATIVA = "1"
# Trace FIXO: a amostra tem que ser identica a cada execucao para o
# ./run.sh comparar poder fazer diff. Em producao o trace nunca se repete.
TRACE = "a1b2c3d4e5f6071829304a5b6c7d8e9f"
REGISTRADO_EM = datetime(2026, 8, 8, 17, 42, 4, tzinfo=timezone.utc)


def main() -> None:
    corpo = EVENTO.para_json()
    hash32 = comprovante.hash_payload(corpo)
    caminho = comprovante.caminho("/data", WORKER, EVENTO, hash32)

    print(f"payload : {corpo}")
    print(f"hash    : {hash32}")
    print(f"caminho : {caminho}")
    print("---")
    print(comprovante.renderizar(
        evento=EVENTO, worker=WORKER, host=HOST, message_id=MESSAGE_ID,
        tentativa=TENTATIVA, trace=TRACE, hash32=hash32, registrado_em=REGISTRADO_EM,
    ), end="")


if __name__ == "__main__":
    main()
