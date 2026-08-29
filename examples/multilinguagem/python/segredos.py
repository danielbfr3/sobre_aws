"""
Secrets Manager com cache e TTL - Python / boto3.  (guia 04)

Demonstra, contra o Floci, as duas coisas que o guia 04 §3 diz que quase toda
primeira implementacao erra:

  1. Buscar o segredo A CADA USO. E chamada paga, tem limite de taxa, e sob
     carga o worker cai por nao conseguir ler uma senha.

  2. Cachear PARA SEMPRE. Funciona por semanas e quebra numa madrugada de
     rotacao, voltando so com restart. E o modo de falha mais irritante do
     tema, porque o erro parece problema do banco.

O meio-termo e um provider com TTL + invalidacao sob falha de autenticacao.

    ./run.sh segredos python

Equivalentes: ../go/segredos.go e ../dotnet/Segredos.cs.
"""

import json
import sys
import time
from dataclasses import dataclass
from typing import Any

from botocore.exceptions import ClientError

import aws

SEGREDO = "asa/dev/cash-cobranca/postgres"
TTL_SEGUNDOS = 5  # no lab, 5s. Em producao, minutos - e MENOR que a rotacao.


@dataclass
class _Entrada:
    valor: dict[str, Any]
    expira_em: float


class SegredoProvider:
    """
    Cache com TTL sobre o Secrets Manager.

    O equivalente do ISegredoProvider do guia 04 §7. A interface importa mais
    que a implementacao: em teste local voce troca por uma versao que le de um
    arquivo, e o codigo de negocio nao muda.
    """

    def __init__(self, cliente, ttl: float = TTL_SEGUNDOS):
        self._cliente = cliente
        self._ttl = ttl
        self._cache: dict[str, _Entrada] = {}
        self.chamadas_a_aws = 0  # so para o demo poder contar

    def obter(self, secret_id: str) -> dict[str, Any]:
        entrada = self._cache.get(secret_id)
        if entrada and time.monotonic() < entrada.expira_em:
            return entrada.valor

        self.chamadas_a_aws += 1
        resposta = self._cliente.get_secret_value(SecretId=secret_id)
        valor = json.loads(resposta["SecretString"])
        self._cache[secret_id] = _Entrada(valor, time.monotonic() + self._ttl)
        return valor

    def invalidar(self, secret_id: str) -> None:
        """
        Chame isto quando o BANCO recusar a senha - e so uma vez.

        Cobre a janela entre a rotacao acontecer e o TTL expirar. Repetir
        indefinidamente com senha errada bloqueia a conta no banco, entao a
        regra e: invalida, tenta MAIS UMA vez, e se falhar propaga o erro.
        """
        self._cache.pop(secret_id, None)


def preparar(sm) -> None:
    """Cria o segredo no emulador (na AWS isso seria Terraform)."""
    valor = json.dumps(
        {"host": "postgres", "port": 5432, "username": "app", "password": "senha-v1"}
    )
    try:
        sm.create_secret(Name=SEGREDO, SecretString=valor)
        print(f"  segredo {SEGREDO} criado")
    except ClientError as e:
        if e.response["Error"]["Code"] != "ResourceExistsException":
            raise
        sm.put_secret_value(SecretId=SEGREDO, SecretString=valor)
        print(f"  segredo {SEGREDO} ja existia, valor restaurado")


def main() -> int:
    sm = aws.cliente("secretsmanager")

    print("=" * 67)
    print(" Secrets Manager: cache com TTL - Python / boto3")
    print("=" * 67)
    print()
    preparar(sm)

    provider = SegredoProvider(sm, ttl=TTL_SEGUNDOS)

    print(f"\n--- 1. Cinco leituras seguidas, TTL de {TTL_SEGUNDOS}s")
    for i in range(1, 6):
        senha = provider.obter(SEGREDO)["password"]
        print(f"  leitura {i}: password={senha}   "
              f"(idas a AWS ate agora: {provider.chamadas_a_aws})")
        time.sleep(0.4)

    print("\n  -> 5 leituras, 1 ida a AWS. Sem cache seriam 5 GetSecretValue:")
    print("     5x o custo, 5x a latencia, e 5 eventos no CloudTrail.")

    print(f"\n--- 2. A rotacao acontece enquanto o processo esta no ar")
    sm.put_secret_value(
        SecretId=SEGREDO,
        SecretString=json.dumps(
            {"host": "postgres", "port": 5432, "username": "app",
             "password": "senha-v2-ROTACIONADA"}
        ),
    )
    print("  senha rotacionada no Secrets Manager (novo AWSCURRENT)")

    senha = provider.obter(SEGREDO)["password"]
    print(f"  leitura logo em seguida: password={senha}")
    print("  -> o cache quente ainda devolve a senha VELHA. Ate aqui, tudo bem:")
    print("     e a janela do TTL, e ela e limitada de proposito.")

    print("\n--- 3. O banco recusa a senha -> invalidar e tentar UMA vez")
    provider.invalidar(SEGREDO)
    senha = provider.obter(SEGREDO)["password"]
    print(f"  apos invalidar: password={senha}   "
          f"(idas a AWS: {provider.chamadas_a_aws})")

    print(f"\n--- 4. Ou simplesmente esperar o TTL ({TTL_SEGUNDOS}s)")
    print("  aguardando...")
    time.sleep(TTL_SEGUNDOS + 0.5)
    senha = provider.obter(SEGREDO)["password"]
    print(f"  apos o TTL: password={senha}   (idas a AWS: {provider.chamadas_a_aws})")

    print()
    print("  Um cache ETERNO (uma static populada no Startup) travaria na")
    print("  senha-v1 para sempre. O worker so voltaria com restart - e o")
    print("  incidente aconteceria de madrugada, no dia da rotacao.")
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
