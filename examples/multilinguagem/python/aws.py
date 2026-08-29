"""
Como este lab constroi clients boto3.

Regra que atravessa as tres linguagens: NENHUMA credencial no construtor.
O que muda entre local, EKS, ECS e Lambda e o AMBIENTE, nunca o codigo.
"""

import os

import boto3
from botocore.config import Config

# Retry adaptativo: o modo "standard"/"adaptive" do botocore ja trata
# throttling e erros transitorios. Sem isto, um pico no STS ou na SQS vira
# excecao no seu loop em vez de uma nova tentativa silenciosa.
_CONFIG = Config(retries={"max_attempts": 5, "mode": "standard"})


def endpoint() -> str | None:
    """
    O endpoint do emulador, quando existe.

    boto3 recente ja le AWS_ENDPOINT_URL sozinho, mas a versao em que isso
    passou a valer varia entre SDKs (o .NET so ganhou equivalente bem depois).
    Ler a variavel na mao e passar explicitamente deixa as tres linguagens
    deste lab com o MESMO comportamento, independente da versao instalada.

    Em producao a variavel simplesmente nao existe e isto devolve None -
    o SDK resolve o endpoint real da regiao.
    """
    return os.environ.get("AWS_ENDPOINT_URL", "").strip() or None


def cliente(servico: str):
    """
    Um client boto3 para o servico pedido.

    Repare no que NAO tem aqui: aws_access_key_id, aws_secret_access_key,
    profile_name, nenhum if de ambiente. O botocore percorre a propria cadeia
    de provedores (guia 03, secao 2) e para no primeiro que responder.

    E repare que quem chama guarda o resultado: client boto3 e caro de criar
    e o cache de credencial mora dentro dele. Criar um por mensagem forca um
    AssumeRoleWithWebIdentity novo a cada vez - latencia, throttling do STS e
    um CloudTrail impossivel de ler.
    """
    return boto3.client(servico, endpoint_url=endpoint(), config=_CONFIG)
