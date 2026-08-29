// Publisher - .NET / AWSSDK.
//
// Publica um evento de cobranca a cada INTERVALO_SEGUNDOS no SNS.
//
// O que este arquivo prova, e que e o motivo de o SNS existir no desenho:
// o publisher NAO SABE QUEM CONSOME. Nao ha nome de fila aqui, nao ha lista
// de assinantes, nao ha if de roteamento.
//
// Equivalentes: ../python/publisher.py e ../go/cmd/publisher/main.go.

using System.Globalization;
using Amazon.SimpleNotificationService.Model;

namespace Lab;

public static class Publisher
{
    private static readonly string[] Tipos =
        ["cobranca.registrada", "cobranca.baixada", "cobranca.rejeitada"];

    private static readonly Log LOG = new(servico: "publisher", worker: "publisher");

    /// <summary>
    /// O ARN do topico para este tipo de evento.
    ///
    /// O bootstrap.sh grava um ARN por tipo no .env. Em TOPIC_MODE=single os
    /// tres apontam para o mesmo topico (o roteamento e da FilterPolicy); em
    /// TOPIC_MODE=multi cada um e um topico proprio. O publisher nunca
    /// pergunta em que modo esta - modo de topico e decisao de INFRA.
    /// </summary>
    private static string TopicoDe(string tipoEvento)
    {
        var nome = "TOPIC_ARN_" + tipoEvento.Replace('.', '_').ToUpperInvariant();
        var arn = Environment.GetEnvironmentVariable(nome);
        if (string.IsNullOrWhiteSpace(arn))
            throw new InvalidOperationException(
                $"variavel {nome} nao definida - rode ./run.sh bootstrap");
        return arn.Trim();
    }

    private static Evento Sortear()
    {
        var agora = DateTimeOffset.UtcNow;
        return new Evento(
            NossoNumero: Random.Shared.Next(1_000_000, 10_000_000).ToString(CultureInfo.InvariantCulture),
            TipoEvento: Tipos[Random.Shared.Next(Tipos.Length)],
            ValorCentavos: Random.Shared.Next(1_000, 500_000),
            // Truncado no segundo: o payload viaja com precisao de segundo, e
            // o hash tem que sair igual se o mesmo evento for republicado.
            OcorridoEm: new DateTimeOffset(agora.Year, agora.Month, agora.Day,
                                           agora.Hour, agora.Minute, agora.Second, TimeSpan.Zero));
    }

    public static async Task<int> RodarAsync(CancellationToken ct)
    {
        var intervalo = TimeSpan.FromSeconds(
            double.TryParse(Environment.GetEnvironmentVariable("INTERVALO_SEGUNDOS"),
                            CultureInfo.InvariantCulture, out var s) ? s : 3);

        // UM client, criado uma vez, reusado pelo processo inteiro. Guia 03 §4.
        using var sns = Aws.Sns();

        LOG.Evento("publisher.iniciado",
            $"publicando a cada {intervalo.TotalSeconds:0.#}s " +
            $"(endpoint: {Aws.Endpoint ?? "AWS real"})",
            campos: new Dictionary<string, object?> { ["intervaloSegundos"] = intervalo.TotalSeconds });

        var publicados = 0;
        while (!ct.IsCancellationRequested)
        {
            var evento = Sortear();

            // O TRACE NASCE AQUI. Um por evento publicado, e ele acompanha a
            // mensagem ate o comprovante no volume.
            var trace = Log.NovoTrace();
            LOG.Evento("publish.iniciado", $"publicando {evento.TipoEvento}", trace,
                new Dictionary<string, object?>
                {
                    ["tipoEvento"] = evento.TipoEvento,
                    ["nossoNumero"] = evento.NossoNumero,
                    ["valorCentavos"] = evento.ValorCentavos,
                });
            var t0 = System.Diagnostics.Stopwatch.StartNew();

            PublishResponse resposta;
            try
            {
                resposta = await sns.PublishAsync(new PublishRequest
                {
                    TopicArn = TopicoDe(evento.TipoEvento),
                    Message = evento.ParaJson(),
                    // O ATRIBUTO E O ROTEAMENTO. A FilterPolicy de cada
                    // subscription casa com este valor. Publicar sem ele faz a
                    // mensagem nao casar com filtro nenhum e sumir sem erro.
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new() { DataType = "String", StringValue = evento.TipoEvento },
                        // O TRACE VIAJA NO ATRIBUTO, NUNCA NO CORPO.
                        //
                        // Isto nao e preferencia de estilo. O nome do
                        // comprovante deriva do hash do CORPO da mensagem; se o
                        // traceId entrasse ali, cada republicacao do "mesmo"
                        // evento geraria um hash diferente, um nome de arquivo
                        // diferente, e a idempotencia deixaria de funcionar -
                        // silenciosamente.
                        //
                        // Atributo e metadado de transporte; corpo e o fato de
                        // negocio. O hash so pode cobrir o segundo.
                        ["traceId"] = new() { DataType = "String", StringValue = trace },
                    },
                }, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Normal nos primeiros segundos: o topico pode nao existir ainda.
                LOG.Erro("publish.erro",
                    $"publish falhou ({e.GetType().Name}); tentando de novo em 5s", trace,
                    new Dictionary<string, object?> { ["erro"] = e.GetType().Name });
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            publicados++;
            LOG.Evento("publish.ok",
                $"#{publicados} {evento.TipoEvento} " +
                $"nossoNumero={evento.NossoNumero} messageId={resposta.MessageId}", trace,
                new Dictionary<string, object?>
                {
                    ["tipoEvento"] = evento.TipoEvento,
                    ["nossoNumero"] = evento.NossoNumero,
                    ["messageId"] = resposta.MessageId,
                    ["duracaoMs"] = Math.Round(t0.Elapsed.TotalMilliseconds, 1),
                });
            await Task.Delay(intervalo, ct);
        }
        return 0;
    }
}
