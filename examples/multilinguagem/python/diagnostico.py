"""
Diagnostico da cadeia de credenciais - Python / botocore.

O equivalente do examples/dotnet-credenciais do guia 03, em Python.
Responde duas perguntas, nesta ordem:

    1. QUAL PROVEDOR DA CADEIA VENCEU?
    2. QUEM EU SOU, na visao da AWS?

Vale copiar para dentro de um container quando o IRSA "nao funciona".

    ./run.sh diagnostico python

Equivalentes: ../go/diagnostico.go e ../dotnet/Diagnostico.cs.
"""

import os
import sys

import boto3
from botocore.exceptions import BotoCoreError, ClientError

import aws

# botocore identifica o provedor vencedor pelo campo `method` do objeto de
# credenciais. Estes sao os valores que interessam num worker.
PROVEDORES = {
    "env": "variaveis de ambiente (AWS_ACCESS_KEY_ID...)",
    "shared-credentials-file": "~/.aws/credentials",
    "config-file": "~/.aws/config",
    "custom-process": "credential_process",
    "assume-role": "perfil com role_arn + source_profile",
    "assume-role-with-web-identity": "IRSA / web identity (EKS)",
    "sso": "AWS SSO / IAM Identity Center",
    "container-role": "endpoint de container (ECS ou EKS Pod Identity)",
    "iam-role": "IMDS - a role do NO EC2, nao a do seu workload",
}

# As variaveis que denunciam cada elo da cadeia.
INTERESSANTES = [
    "AWS_ACCESS_KEY_ID",
    "AWS_SECRET_ACCESS_KEY",
    "AWS_SESSION_TOKEN",
    "AWS_PROFILE",
    "AWS_ROLE_ARN",
    "AWS_WEB_IDENTITY_TOKEN_FILE",
    "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
    "AWS_CONTAINER_CREDENTIALS_FULL_URI",
    "AWS_REGION",
    "AWS_DEFAULT_REGION",
    "AWS_ENDPOINT_URL",
]


def secao(titulo: str) -> None:
    print(f"\n--- {titulo}")


def main() -> int:
    print("=" * 67)
    print(" Cadeia de credenciais - Python / botocore")
    print("=" * 67)

    # -----------------------------------------------------------------------
    secao("1. O ambiente")
    # -----------------------------------------------------------------------
    presentes = {v: os.environ[v] for v in INTERESSANTES if os.environ.get(v)}
    if not presentes:
        print("  nenhuma variavel AWS_* definida")
    for nome, valor in presentes.items():
        # Nunca imprima segredo inteiro, nem em ferramenta de diagnostico.
        mostrado = valor if "SECRET" not in nome and "TOKEN" not in nome else "***"
        print(f"  {nome}={mostrado}")

    # O bug do guia 03 §2, detectado antes de qualquer chamada.
    if presentes.get("AWS_ACCESS_KEY_ID") and presentes.get("AWS_ROLE_ARN"):
        print()
        print("  (!) AWS_ACCESS_KEY_ID e AWS_ROLE_ARN presentes ao mesmo tempo.")
        print("      Variavel de ambiente vence IRSA na cadeia. "
              "O IRSA esta sendo IGNORADO.")

    # -----------------------------------------------------------------------
    secao("2. Qual provedor venceu")
    # -----------------------------------------------------------------------
    sessao = boto3.Session()
    credenciais = sessao.get_credentials()

    if credenciais is None:
        print("  NENHUMA credencial encontrada na cadeia.")
        print("  Em AWS real isso e NoCredentialsError na primeira chamada.")
        return 1

    metodo = getattr(credenciais, "method", "desconhecido")
    print(f"  method .......: {metodo}")
    print(f"  classe .......: {type(credenciais).__name__}")
    print(f"  significa ....: {PROVEDORES.get(metodo, 'provedor nao mapeado')}")

    # RefreshableCredentials = o botocore vai renovar sozinho antes de expirar.
    # E o que voce QUER ver num worker de longa duracao. Credencial nao
    # renovavel num processo que vive semanas termina em ExpiredToken.
    renovavel = type(credenciais).__name__ == "RefreshableCredentials"
    print(f"  renovavel ....: {'sim' if renovavel else 'nao (credencial estatica)'}")

    if metodo == "iam-role":
        print()
        print("  (!) IMDS venceu. Num pod do EKS isso significa que voce esta")
        print("      usando a ROLE DO NO, nao a do seu ServiceAccount. Parte")
        print("      das coisas funciona - e por isso que passa despercebido.")

    # -----------------------------------------------------------------------
    secao("3. Quem eu sou (sts:GetCallerIdentity)")
    # -----------------------------------------------------------------------
    try:
        eu = aws.cliente("sts").get_caller_identity()
    except (ClientError, BotoCoreError) as e:
        print(f"  falhou: {e}")
        return 1

    print(f"  Account ......: {eu['Account']}")
    print(f"  Arn ..........: {eu['Arn']}")
    print(f"  UserId .......: {eu['UserId']}")
    print()
    if ":assumed-role/" in eu["Arn"]:
        print("  -> assumed-role: algum AssumeRole funcionou. Confira se o nome")
        print("     da role e o do SEU workload e nao o do nodegroup.")
    elif ":user/" in eu["Arn"]:
        print("  -> usuario IAM: credencial PERMANENTE. Num workload isso e o")
        print("     que o guia 01 §4 chama de falha grave.")

    # -----------------------------------------------------------------------
    secao("4. A regiao e uma segunda cadeia, independente")
    # -----------------------------------------------------------------------
    # Erro comum: achar que credencial e regiao vem juntas. Credencial resolve
    # e a aplicacao estoura com NoRegionError - que nao e problema de IAM.
    print(f"  regiao resolvida: {sessao.region_name or '(nenhuma!)'}")
    if not sessao.region_name:
        print("  (!) sem AWS_REGION a primeira chamada estoura com NoRegionError.")
        print("      No EKS o webhook do IRSA NAO injeta AWS_REGION - "
              "voce declara no Deployment.")

    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
