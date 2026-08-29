// Log estruturado com trace_id - .NET.
//
// Duas saidas para o mesmo evento, de proposito:
//
//   stdout  linha legivel, para o `docker compose logs`
//   arquivo NDJSON em /data/logs/<servico>-<host>.ndjson
//
// Em producao voce escreveria SO no stdout, em JSON, e um coletor
// (fluent-bit, CloudWatch agent, Datadog) leria dali. O arquivo no volume e o
// substituto de laboratorio para esse coletor.
//
// UM ARQUIVO POR ESCRITOR - o nome carrega o host. Mesmo raciocinio da escrita
// dos comprovantes: em vez de disputar append no mesmo arquivo (que sobre NFS
// tem semantica frouxa), cada processo escreve no seu e a juncao acontece na
// leitura.
//
// Equivalentes: ../python/log.py e ../go/logx/logx.go.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Lab;

public sealed class Log
{
    public const string SemTrace = "--------";

    private readonly string _servico;
    private readonly string _worker;
    private readonly string _linguagem;
    private readonly string _host;
    private readonly string? _arquivo;
    private readonly Lock _trava = new();

    // Sem BOM e sem escape de nao-ASCII: a mesma armadilha dos comprovantes.
    // Com BOM, cada linha do NDJSON comecaria com 3 bytes invisiveis e o
    // parser do visualizador engasgaria na primeira.
    private static readonly UTF8Encoding SemBom = new(false);
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 32 caracteres hexadecimais minusculos - o mesmo formato do trace-id do
    /// W3C Trace Context. Em producao isso viria do OpenTelemetry, pelo
    /// cabecalho `traceparent`; aqui esta na mao para deixar o mecanismo a vista.
    /// </summary>
    public static string NovoTrace() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// O trace de uma mensagem que chegou SEM o atributo traceId.
    ///
    /// Acontece quando alguem publica direto na fila (`aws sqs send-message`),
    /// que e exatamente o que a etapa 9 do ROTEIRO faz para exercitar a DLQ.
    ///
    /// Derivado do MessageId, NUNCA sorteado. O MessageId e estavel entre
    /// reentregas, entao as 3 tentativas da mesma mensagem caem no mesmo trace
    /// e voce enxerga a cadeia inteira ate a DLQ. Um id sorteado a cada receive
    /// quebraria justamente o caso em que rastrear vale mais.
    /// </summary>
    public static string TraceDoMessageId(string messageId) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(messageId))).ToLowerInvariant();

    public Log(string servico, string worker, string linguagem = "dotnet", string? dataPath = null)
    {
        _servico = servico;
        _worker = worker;
        _linguagem = linguagem;
        _host = Environment.MachineName;

        var raiz = dataPath ?? Environment.GetEnvironmentVariable("DATA_PATH") ?? "/data";
        try
        {
            var pasta = Path.Combine(raiz, "logs");
            Directory.CreateDirectory(pasta);
            _arquivo = Path.Combine(pasta, $"{servico}-{_host}.ndjson");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Sem volume montado o log vai so para o stdout. Nao e motivo para
            // o worker deixar de subir.
            _arquivo = null;
        }
    }

    public void Evento(string evento, string msg, string? trace = null,
                       IReadOnlyDictionary<string, object?>? campos = null) =>
        Escrever("info", evento, msg, trace, campos);

    public void Erro(string evento, string msg, string? trace = null,
                     IReadOnlyDictionary<string, object?>? campos = null) =>
        Escrever("erro", evento, msg, trace, campos);

    private void Escrever(string nivel, string evento, string msg, string? trace,
                          IReadOnlyDictionary<string, object?>? campos)
    {
        var agora = DateTimeOffset.UtcNow;

        // A ORDEM DAS CHAVES IMPORTA e "ts" vem primeiro.
        //
        // Os arquivos das tres linguagens sao concatenados e ordenados com um
        // `sort` de texto puro - sem jq, sem parser. Isso so funciona porque o
        // timestamp e a primeira chave e tem LARGURA FIXA: 6 casas decimais
        // sempre, sempre UTC, sempre com o Z no fim.
        //
        // Microssegundos e nao milissegundos de proposito. Com 3 casas, dois
        // eventos do mesmo processo caem no mesmo instante com frequencia, e
        // ai o `sort` desempata pelo RESTO da linha - em ordem alfabetica do
        // nome do evento. O resultado e uma cadeia mostrando
        // "mensagem.falhou" antes de "mensagem.recebida".
        //
        // Dictionary<string,object> preserva a ordem de insercao na
        // serializacao do System.Text.Json, entao montar na ordem basta.
        var registro = new Dictionary<string, object?>
        {
            ["ts"] = agora.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture),
            ["nivel"] = nivel,
            ["servico"] = _servico,
            ["worker"] = _worker,
            ["linguagem"] = _linguagem,
            ["host"] = _host,
            ["traceId"] = trace ?? "",
            ["evento"] = evento,
            ["msg"] = msg,
        };
        if (campos is not null)
            foreach (var (k, v) in campos)
                registro[k] = v;

        var linha = JsonSerializer.Serialize(registro, Json);
        var curto = trace is { Length: >= 8 } ? trace[..8] : SemTrace;

        lock (_trava)
        {
            // stdout legivel: e o que o ROTEIRO manda voce ler.
            Console.Out.Write(
                $"[{agora.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] " +
                $"{_worker} {curto}  {msg}\n");

            if (_arquivo is null) return;
            try
            {
                File.AppendAllText(_arquivo, linha + "\n", SemBom);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // log que derruba o worker e pior que log perdido
            }
        }
    }
}
