# Build multi-estagio: o SDK do .NET fica no primeiro estagio e NAO vai para
# a imagem final. Voce nao precisa de dotnet instalado na sua maquina.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src
COPY Lab.csproj .
# restore separado do build: o Docker guarda esta camada em cache e so refaz
# o download dos pacotes quando o .csproj muda. Sem isso, cada alteracao de
# uma linha de C# baixaria o AWSSDK de novo.
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /out --no-restore

# ---------------------------------------------------------------------------
# Runtime. Imagem "runtime" (nao "aspnet", nao "sdk"): este e um worker de
# console, nao tem servidor HTTP dentro.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0

# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT casa com o InvariantGlobalization do
# .csproj: sem dados de ICU na imagem, qualquer CultureInfo("pt-BR") estouraria
# em tempo de execucao. Por isso o Comprovante.cs formata dinheiro na mao.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_EnableDiagnostics=0

# uid 1000 - O MESMO NAS TRES LINGUAGENS. Ver o comentario longo no
# ../python/Dockerfile: o volume nomeado herda o dono do diretorio da imagem
# que o montar primeiro, entao workers com uid diferente brigariam pelo /data.
#
# O getent antes de cada criacao existe porque as imagens base nao concordam:
# a do .NET 10 JA TRAZ um usuario com uid 1000, a do Python e a do Debian nao.
# Sem o teste, o build da imagem .NET quebra com "GID '1000' already exists".
RUN (getent group 1000  >/dev/null || groupadd --gid 1000 worker) \
 && (getent passwd 1000 >/dev/null || useradd --uid 1000 --gid 1000 --no-create-home worker) \
 && mkdir -p /data \
 && chown 1000:1000 /data

WORKDIR /app
COPY --from=build /out/ ./
USER 1000:1000

ENTRYPOINT []
CMD ["dotnet", "Lab.dll", "consumer"]
