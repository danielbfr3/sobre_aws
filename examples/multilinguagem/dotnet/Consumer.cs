// Consumer - .NET / AWSSDK.  (no docker-compose: consumer-rejeicao)
//
// Consome a propria fila, grava um comprovante .txt no volume compartilhado e
// so entao apaga a mensagem.
//
// Cinco decisoes deste arquivo valem mais que o resto do codigo:
//
//  1. NAO EXISTE FILTRO AQUI. Quem decidiu que so eventos
//     "cobranca.rejeitada" chegam nesta fila foi a FilterPolicy da
//     subscription - roteamento e do broker, nao da aplicacao.
//
//  2. O DeleteMessage vem DEPOIS de gravar o comprovante. Se o processo
//     morrer entre as duas coisas, a mensagem volta pela expiracao do
//     visibility timeout. A ordem inversa criaria a janela oposta: mensagem
//     sumindo sem comprovante nenhum.
//
//  3. Erro NAO deleta a mensagem. E assim que a DLQ funciona (etapa 9 do
//     ROTEIRO).
//
//  4. Idempotencia mora no NOME DO ARQUIVO, nao em memoria. Um HashSet de
//     messageIds nao sobreviveria a restart do pod nem valeria entre
//     replicas.
//
//  5. Um client, criado uma vez. Guia 03 §4.
//
// Equivalentes: ../python/consumer.py e ../go/cmd/consumer/main.go.

using Amazon.SQS;
using Amazon.SQS.Model;

namespace Lab;

public static class Consumer
{
    // Dentro de um pod, MachineName e o nome do pod.
    private static readonly string Host = Environment.MachineName;
    private static readonly string Worker =
        Environment.GetEnvironmentVariable("WORKER") ?? "rejeicao";

    private static readonly Log LOG = new(servico: "consumer", worker: Worker);

    /// <summary>
    /// Devolve (traceId, origem) da mensagem.
    ///
    /// O caminho normal: o publisher pos o traceId num MessageAttribute e ele
    /// atravessou o SNS ate aqui. Para isso funcionar, o ReceiveMessage
    /// precisa PEDIR os atributos (MessageAttributeNames) - sem pedir, eles
    /// nao vem.
    ///
    /// O caminho anormal, e o que mais ensina: alguem publicou direto na fila
    /// com `aws sqs send-message`, sem atributo nenhum - e exatamente o que a
    /// etapa 9 do ROTEIRO faz para exercitar a DLQ. Ai o trace e DERIVADO do
    /// MessageId, nunca sorteado: o MessageId e estavel entre reentregas,
    /// entao as tres tentativas da mesma mensagem caem no mesmo trace e a
    /// cadeia ate a DLQ fica visivel.
    /// </summary>
    private static (string Trace, string Origem) TraceDaMensagem(Message m)
    {
        if (m.MessageAttributes is not null &&
            m.MessageAttributes.TryGetValue("traceId", out var attr) &&
            !string.IsNullOrEmpty(attr.StringValue))
            return (attr.StringValue, "propagado");

        return (Log.TraceDoMessageId(m.MessageId ?? ""), "derivado-do-message-id");
    }

    /// <summary>
    /// Grava e apaga um arquivo-sonda em DATA_PATH antes de processar
    /// qualquer coisa.
    ///
    /// Parece paranoia e nao e. No EFS o diretorio pode existir e ainda assim
    /// negar escrita, quando o fsGroup do pod nao bate com o gid do Access
    /// Point. Sem esta checagem o worker sobe saudavel, processa mensagens,
    /// grava tudo no filesystem EFEMERO do container - e voce so descobre
    /// quando o pod morre.
    ///
    /// Repare no tipo da excecao: UnauthorizedAccessException. Nao e
    /// AccessDenied, nao menciona role nenhuma, porque nao e IAM - e POSIX.
    /// Procurar a causa na policy e o caminho errado e custa tempo
    /// (guia 06, tabela de sintomas).
    /// </summary>
    private static void VerificarVolume(string dataPath)
    {
        var sonda = Path.Combine(dataPath, $".sonda-{Host}");
        try
        {
            Directory.CreateDirectory(dataPath);
            File.WriteAllText(sonda, "ok");
            File.Delete(sonda);
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine(
                $"sem permissao de escrita em {dataPath}: {e.Message}\n" +
                "  isto e POSIX (uid/gid), nao IAM. No EKS, confira o " +
                "securityContext.fsGroup contra o gid do Access Point do EFS.");
            Environment.Exit(1);
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"volume {dataPath} indisponivel: {e.Message}");
            Environment.Exit(1);
        }

        LOG.Evento("volume.ok", $"volume {dataPath} montado e gravavel",
            campos: new Dictionary<string, object?> { ["dataPath"] = dataPath });
    }

    private static void Processar(Message mensagem, string dataPath, string trace)
    {
        var corpo = mensagem.Body;

        // Lanca em payload invalido -> a mensagem NAO e deletada -> volta
        // pelo visibility timeout -> DLQ depois de maxReceiveCount.
        var evento = Evento.DoJson(corpo);

        var hash32 = Comprovante.HashPayload(corpo);
        var destino = Comprovante.Caminho(dataPath, Worker, evento, hash32);

        // ApproximateReceiveCount so vem se a gente PEDIR no receive.
        // Se aparecer 2 ou 3, aquela mensagem ja falhou antes.
        var tentativa = mensagem.Attributes is not null &&
                        mensagem.Attributes.TryGetValue("ApproximateReceiveCount", out var t)
            ? t
            : "1";

        var texto = Comprovante.Renderizar(
            evento, Worker, Host, mensagem.MessageId, tentativa, trace, hash32, DateTimeOffset.UtcNow);

        if (Comprovante.GravarAtomico(destino, texto))
        {
            LOG.Evento("comprovante.gravado", $"comprovante gravado: {destino}", trace,
                new Dictionary<string, object?>
                {
                    ["caminho"] = destino, ["hash"] = hash32,
                    ["nossoNumero"] = evento.NossoNumero, ["tipoEvento"] = evento.TipoEvento,
                    ["tentativa"] = tentativa,
                });
        }
        else
        {
            // A duplicata tem trace PROPRIO, mas aponta para um comprovante que
            // outro trace gravou. E o comportamento correto: o arquivo nao e
            // reescrito, entao ele guarda para sempre a cadeia da PRIMEIRA
            // gravacao.
            LOG.Evento("comprovante.duplicado",
                $"comprovante ja existe, duplicata ignorada: {Path.GetFileName(destino)}", trace,
                new Dictionary<string, object?>
                {
                    ["caminho"] = destino, ["hash"] = hash32,
                    ["nossoNumero"] = evento.NossoNumero, ["tentativa"] = tentativa,
                });
        }
    }

    public static async Task<int> RodarAsync(CancellationToken ct)
    {
        var queueUrl = Environment.GetEnvironmentVariable("QUEUE_URL");
        if (string.IsNullOrWhiteSpace(queueUrl))
        {
            Console.Error.WriteLine("QUEUE_URL nao definida");
            return 1;
        }
        var dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data";

        VerificarVolume(dataPath);

        using var sqs = Aws.Sqs();
        LOG.Evento("worker.iniciado", $"lendo {queueUrl}",
            campos: new Dictionary<string, object?> { ["queueUrl"] = queueUrl });

        while (!ct.IsCancellationRequested)
        {
            ReceiveMessageResponse resposta;
            try
            {
                resposta = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    // Long polling. Com 0 aqui voce faria uma chamada por
                    // segundo devolvendo vazio - custa dinheiro na AWS de
                    // verdade e nao entrega a mensagem mais rapido.
                    WaitTimeSeconds = 20,
                    // Sem pedir, o campo "Tentativa" do comprovante seria
                    // sempre 1.
                    //
                    // AttributeNames esta marcado como obsoleto em favor de
                    // MessageSystemAttributeNames. Usamos o antigo de
                    // proposito: ele gera o parametro legado AttributeName.N
                    // no wire, que TODO emulador entende - inclusive os que
                    // ainda nao implementaram o parametro novo. Contra a AWS
                    // de verdade, prefira o novo.
#pragma warning disable CS0618
                    AttributeNames = ["ApproximateReceiveCount"],
#pragma warning restore CS0618
                    // Sem pedir, o traceId que o publisher mandou nao chega
                    // aqui - e a cadeia se parte no meio, no ponto mais
                    // importante dela.
                    MessageAttributeNames = ["All"],
                }, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Esperado nos primeiros segundos: a fila pode nao existir ainda.
                LOG.Erro("receive.erro",
                    $"receive falhou ({e.GetType().Name}); tentando de novo em 5s",
                    campos: new Dictionary<string, object?> { ["erro"] = e.GetType().Name });
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            foreach (var mensagem in resposta.Messages ?? [])
            {
                var (trace, origemTrace) = TraceDaMensagem(mensagem);
                var tentativa = mensagem.Attributes is not null &&
                                mensagem.Attributes.TryGetValue("ApproximateReceiveCount", out var n)
                    ? n : "1";
                LOG.Evento("mensagem.recebida", $"mensagem recebida (tentativa {tentativa})", trace,
                    new Dictionary<string, object?>
                    {
                        ["messageId"] = mensagem.MessageId, ["tentativa"] = tentativa,
                        ["origemTrace"] = origemTrace,
                    });

                try
                {
                    Processar(mensagem, dataPath, trace);
                }
                catch (Exception e)
                {
                    LOG.Erro("mensagem.falhou",
                        $"FALHOU ao processar {mensagem.MessageId}: {e.Message}", trace,
                        new Dictionary<string, object?>
                        {
                            ["messageId"] = mensagem.MessageId, ["tentativa"] = tentativa,
                            ["erro"] = e.Message,
                        });
                    continue; // <- o pulo do gato: sem delete, a mensagem volta
                }

                try
                {
                    await sqs.DeleteMessageAsync(queueUrl, mensagem.ReceiptHandle, ct);
                    LOG.Evento("mensagem.deletada", "mensagem removida da fila", trace,
                        new Dictionary<string, object?> { ["messageId"] = mensagem.MessageId });
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // O comprovante ja esta gravado. A mensagem vai voltar e a
                    // idempotencia (nome do arquivo) resolve na proxima.
                    LOG.Erro("delete.erro",
                        $"delete falhou ({e.GetType().Name}); a mensagem voltara", trace,
                        new Dictionary<string, object?> { ["erro"] = e.GetType().Name });
                }
            }
        }
        return 0;
    }
}
