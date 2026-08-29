# Publisher e consumer em Python. A mesma imagem para os dois - o que muda e
# o `command:` no docker-compose.yml, exatamente como os tres consumers do lab
# original rodam a mesma imagem e diferem so no ServiceAccount e no QUEUE_URL.
FROM python:3.12-slim

# Sem isto o stdout fica em buffer e o `docker compose logs` mostra o log em
# blocos - pessimo justamente quando voce esta acompanhando uma mensagem
# atravessar o pipeline.
ENV PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

# ---------------------------------------------------------------------------
# Usuario nao-root, uid 1000 - E O MESMO NAS TRES LINGUAGENS.
#
# Nao e detalhe de estilo. O volume nomeado do compose e criado com o dono e a
# permissao que o diretorio tem NA IMAGEM do primeiro container que o montar.
# Se o worker Python fosse uid 1000 e o Go fosse uid 1001, um dos dois tomaria
# "permission denied" ao escrever - dependendo de quem subiu primeiro, o que
# da um bug intermitente delicioso de caçar.
#
# E exatamente o mesmo problema que o securityContext.fsGroup resolve no
# Kubernetes contra o gid do Access Point do EFS. Ali o sintoma tambem e
# PermissionError / UnauthorizedAccessException, e nao AccessDenied: e POSIX,
# nao IAM (guia 06, tabela de sintomas). O arquivo-sonda que o consumer grava
# na subida existe para transformar esse erro tardio numa falha imediata.
# ---------------------------------------------------------------------------
#
# O getent antes de cada criacao existe porque as imagens base nao concordam:
# a do .NET 10 ja traz um usuario com uid 1000, a do Python nao.
RUN (getent group 1000  >/dev/null || groupadd --gid 1000 worker) \
 && (getent passwd 1000 >/dev/null || useradd --uid 1000 --gid 1000 --no-create-home worker) \
 && mkdir -p /data \
 && chown 1000:1000 /data
USER 1000:1000

CMD ["python", "-u", "consumer.py"]
