// O mesmo worker, como Lambda.  (guia 06, secao 4)
//
// Este arquivo existe para ser lido LADO A LADO com
// ../multilinguagem/dotnet/Consumer.cs. O interessante nao e o que ele faz -
// e o que DESAPARECEU.
//
// SUMIU:
//   - o while (true)
//   - o ReceiveMessageAsync e o WaitTimeSeconds do long polling
//   - o DeleteMessageAsync
//   - o tratamento de erro do receive
//   - a sonda de volume (nao ha volume)
//
// Quem faz tudo isso agora e o SERVICO Lambda, atraves do event source
// mapping. Ele puxa da fila, invoca o handler com um lote pronto, e deleta as
// mensagens que voce NAO reportou como falhas.
//
// Consequencia que confunde: a execution role precisa de sqs:ReceiveMessage,
// sqs:DeleteMessage e sqs:GetQueueAttributes - mas este arquivo nao chama
// nenhuma das tres. Voce concede uma permissao que o seu codigo nao usa
// diretamente (policy-lambda-consumer-registro.json).
//
// APARECEU (e sao as duas coisas que quase todo mundo erra na primeira vez):
//   1. Falha parcial de lote - o SQSBatchResponse
//   2. Visibility timeout da fila >= 6x o timeout da funcao
//
// CONTINUOU IGUAL:
//   - nenhuma credencial no construtor; a cadeia do SDK resolve sozinha
//   - a idempotencia, porque event source mapping tambem e entrega
//     PELO MENOS UMA VEZ
//   - o formato do comprovante, reaproveitado de Comprovante.cs
//
// MUDOU DE DESTINO: o comprovante vai para o S3, nao para um volume. A Lambda
// ate monta EFS, mas isso exige por a funcao numa VPC - ENI, cold start maior,
// NAT ou VPC endpoints para falar com a AWS. Para gravar um objeto, o S3 ganha
// em quase tudo (guia 06, secao 3).

using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Lab;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace LabLambda;

public sealed class Function
{
    // UM client, no campo da classe - nao dentro do handler.
    //
    // O ambiente de execucao da Lambda e reaproveitado entre invocacoes
    // ("Lambda quente"). Um client criado no handler seria descartado e
    // recriado a cada invocacao, jogando fora o cache de credenciais - e
    // e a mesma licao do guia 03 secao 4, num lugar onde ela e ainda mais
    // barata de acertar.
    private static readonly IAmazonS3 S3 = new AmazonS3Client();

    private static readonly string Bucket =
        Environment.GetEnvironmentVariable("BUCKET_COMPROVANTES")
        ?? throw new InvalidOperationException("BUCKET_COMPROVANTES nao definida");

    private static readonly string Worker =
        Environment.GetEnvironmentVariable("WORKER") ?? "registro";

    /// <summary>
    /// Processa um lote da SQS.
    ///
    /// O tipo de retorno e o que habilita a falha parcial. Sem ele - e sem
    /// --function-response-types ReportBatchItemFailures no event source
    /// mapping - um erro numa mensagem devolve O LOTE INTEIRO para a fila, e
    /// as outras nove sao reprocessadas a toa.
    ///
    /// SAO DUAS METADES E AS DUAS SAO OBRIGATORIAS: o tipo aqui e a flag la.
    /// Com so uma, nao funciona, e o sintoma e silencioso.
    /// </summary>
    public async Task<SQSBatchResponse> HandlerAsync(SQSEvent evt, ILambdaContext ctx)
    {
        var falhas = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var registro in evt.Records)
        {
            try
            {
                await ProcessarAsync(registro, ctx);
            }
            catch (Exception e)
            {
                // Reporta SO esta mensagem. As outras do lote seguem deletadas
                // normalmente pelo servico.
                ctx.Logger.LogError($"FALHOU {registro.MessageId}: {e.Message}");
                falhas.Add(new SQSBatchResponse.BatchItemFailure
                {
                    ItemIdentifier = registro.MessageId,
                });
            }
        }

        // Lista vazia = o lote inteiro deu certo.
        return new SQSBatchResponse { BatchItemFailures = falhas };
    }

    private static async Task ProcessarAsync(SQSEvent.SQSMessage registro, ILambdaContext ctx)
    {
        var corpo = registro.Body;

        // Mesmo parse, mesma excecao em payload invalido. A diferenca e o que
        // acontece depois: aqui a mensagem volta porque foi REPORTADA como
        // falha, nao porque deixamos de deletar. O efeito e o mesmo - depois
        // de maxReceiveCount ela vai para a DLQ da FILA.
        var evento = Evento.DoJson(corpo);

        // O mesmo hash, o mesmo nome. A idempotencia nao muda de forma: event
        // source mapping tambem e entrega pelo menos uma vez.
        var hash32 = Comprovante.HashPayload(corpo);
        var dia = evento.OcorridoEm.ToUniversalTime().ToString("yyyy-MM-dd");
        var chave = $"{Worker}/{dia}/{Comprovante.NomeArquivo(evento.NossoNumero, hash32)}";

        var tentativa = registro.Attributes is not null &&
                        registro.Attributes.TryGetValue("ApproximateReceiveCount", out var t)
            ? t : "1";

        var trace = registro.MessageAttributes is not null &&
                    registro.MessageAttributes.TryGetValue("traceId", out var a) &&
                    !string.IsNullOrEmpty(a.StringValue)
            ? a.StringValue
            : Log.TraceDoMessageId(registro.MessageId);

        var texto = Comprovante.Renderizar(
            evento, Worker,
            // Nao existe "pod" aqui. O equivalente mais proximo e o id da
            // requisicao - e ele muda a cada invocacao, ao contrario do
            // MachineName de um pod, que dura enquanto o pod durar.
            host: ctx.AwsRequestId,
            messageId: registro.MessageId,
            tentativa: tentativa,
            trace: trace,
            hash32: hash32,
            registradoEm: DateTimeOffset.UtcNow);

        // IDEMPOTENCIA NO S3, e ela NAO e igual a do volume.
        //
        // Nao existe link(2) aqui, e o PutObject sobrescreve em silencio - o
        // mesmo defeito do rename que o guia 07 secao 5 descreve. Entao a
        // reserva do nome vira uma pergunta explicita antes de gravar.
        //
        // E uma janela de corrida, e vale ser honesto sobre isso: duas
        // invocacoes simultaneas da mesma mensagem podem passar as duas pelo
        // ExistsAsync. Como o conteudo e deterministico (mesmo payload, mesmo
        // hash), a sobrescrita e inofensiva no conteudo - o que se perde e o
        // trace da primeira gravacao. Se isso importar, a saida e uma tabela
        // no DynamoDB com escrita condicional, nao o S3 sozinho.
        if (await ExisteAsync(chave))
        {
            ctx.Logger.LogInformation(
                $"[{trace[..8]}] comprovante ja existe, duplicata ignorada: {chave}");
            return;
        }

        await S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = chave,
            ContentBody = texto,
            ContentType = "text/plain; charset=utf-8",
            // Sem isto, o objeto fica sem criptografia gerenciada por chave
            // propria e a policy de KMS da role nao e usada (guia 05).
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
        });

        ctx.Logger.LogInformation($"[{trace[..8]}] comprovante gravado: s3://{Bucket}/{chave}");
    }

    private static async Task<bool> ExisteAsync(string chave)
    {
        try
        {
            await S3.GetObjectMetadataAsync(Bucket, chave);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
