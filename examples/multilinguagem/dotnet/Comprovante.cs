// Porta em .NET da implementacao de referencia que vive em
// ../python/comprovante.py.
//
// O contrato entre as tres linguagens: o MESMO payload gera o MESMO nome de
// arquivo e o MESMO conteudo, byte a byte. Se voce mexer em qualquer regra
// daqui - largura da linha, formato do valor, algoritmo do hash - tem que
// mexer nas tres ao mesmo tempo.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lab;

/// <summary>O payload que trafega no SNS e na SQS.</summary>
public sealed record Evento(
    string NossoNumero,
    string TipoEvento,
    long ValorCentavos,
    DateTimeOffset OcorridoEm)
{
    // Nullable de proposito: em C# um long nao distingue "veio 0" de "nao
    // veio". Com nullable, null == campo ausente, e da para recusar o payload.
    private sealed record Bruto(
        [property: JsonPropertyName("nossoNumero")] string? NossoNumero,
        [property: JsonPropertyName("tipoEvento")] string? TipoEvento,
        [property: JsonPropertyName("valorCentavos")] long? ValorCentavos,
        [property: JsonPropertyName("ocorridoEm")] string? OcorridoEm);

    /// <summary>
    /// Faz o parse do Body da mensagem SQS.
    ///
    /// Lanca em qualquer coisa que nao seja o payload esperado - e e disso
    /// que a etapa 9 do ROTEIRO depende. Mandar 'isto-nao-e-json' para a fila
    /// tem que estourar aqui, a mensagem NAO ser deletada, e depois de
    /// maxReceiveCount tentativas cair na DLQ.
    /// </summary>
    public static Evento DoJson(string corpo)
    {
        Bruto? bruto;
        try
        {
            bruto = JsonSerializer.Deserialize<Bruto>(corpo);
        }
        catch (JsonException e)
        {
            throw new FormatException($"corpo nao e JSON valido: {e.Message}", e);
        }

        if (bruto is null)
            throw new FormatException("corpo nao e um objeto JSON");

        var faltando = new List<string>();
        if (bruto.NossoNumero is null) faltando.Add("nossoNumero");
        if (bruto.TipoEvento is null) faltando.Add("tipoEvento");
        if (bruto.ValorCentavos is null) faltando.Add("valorCentavos");
        if (bruto.OcorridoEm is null) faltando.Add("ocorridoEm");
        if (faltando.Count > 0)
            throw new FormatException($"campos ausentes no payload: {string.Join(", ", faltando)}");

        return new Evento(
            bruto.NossoNumero!,
            bruto.TipoEvento!,
            bruto.ValorCentavos!.Value,
            DateTimeOffset.Parse(bruto.OcorridoEm!, CultureInfo.InvariantCulture,
                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
    }

    /// <summary>
    /// Serializacao COMPACTA e com as chaves nesta ordem.
    ///
    /// Nao e estilo: o hash de idempotencia sai destes bytes. Um espaco a
    /// mais muda o nome do arquivo la na frente, e duas publicacoes do
    /// "mesmo" evento deixariam de ser duplicatas.
    /// </summary>
    public string ParaJson() => JsonSerializer.Serialize(new Bruto(
        NossoNumero, TipoEvento, ValorCentavos,
        OcorridoEm.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
}

public static class Comprovante
{
    public const int Largura = 64;
    private const int LarguraRotulo = 18;

    private static readonly Dictionary<string, string> Titulos = new()
    {
        ["registro"] = "COMPROVANTE DE REGISTRO DE COBRANÇA",
        ["baixa"] = "COMPROVANTE DE BAIXA DE COBRANÇA",
        ["rejeicao"] = "COMPROVANTE DE REJEIÇÃO DE COBRANÇA",
    };

    private static readonly Regex NomeSeguro = new("[^A-Za-z0-9_-]", RegexOptions.Compiled);

    // Formato de numero montado na mao, sem depender de CultureInfo("pt-BR").
    // Imagens slim rodam em globalization-invariant mode e nao carregam dados
    // de cultura - depender de locale para formatar dinheiro e um bug que so
    // aparece em producao, no container.
    private static readonly NumberFormatInfo Milhar = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = [3],
    };

    /// <summary>
    /// MD5 do corpo bruto em hexadecimal MAIUSCULO.
    ///
    /// Isto NAO e seguranca, e deduplicacao: o hash so responde "ja vi
    /// exatamente estes bytes?". Se o seu caso for anti-fraude, troque por
    /// SHA-256 - nas tres linguagens ao mesmo tempo.
    /// </summary>
    public static string HashPayload(string corpo) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(corpo)));

    /// <summary>{nossoNumero}-{8 primeiros do hash}.txt - a chave de idempotencia.</summary>
    public static string NomeArquivo(string nossoNumero, string hash32) =>
        $"{NomeSeguro.Replace(nossoNumero, "_")}-{hash32[..8]}.txt";

    /// <summary>
    /// /data/comprovantes/&lt;worker&gt;/&lt;AAAA-MM-DD&gt;/&lt;arquivo&gt;
    ///
    /// A data da particao vem do OcorridoEm do evento, nao do relogio da
    /// maquina. Se viesse do relogio, uma mensagem reentregue depois da
    /// meia-noite cairia noutro diretorio, o teste de existencia nao acharia
    /// o arquivo anterior, e a idempotencia falharia uma vez por dia.
    /// </summary>
    public static string Caminho(string dataPath, string worker, Evento e, string hash32) =>
        Path.Combine(dataPath, "comprovantes", worker,
            e.OcorridoEm.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            NomeArquivo(e.NossoNumero, hash32));

    /// <summary>1234567 -> "R$ 12.345,67".</summary>
    public static string FormatarValor(long centavos)
    {
        var sinal = centavos < 0 ? "-" : "";
        var abs = Math.Abs(centavos);
        return $"R$ {sinal}{(abs / 100).ToString("#,##0", Milhar)},{abs % 100:00}";
    }

    public static string FormatarData(DateTimeOffset dt) =>
        dt.ToUniversalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    // PadRight conta CARACTERES. Importa porque "Nosso número" tem acento -
    // em Go isso vira contagem de runes, nao de len([]byte).
    private static string Campo(string rotulo, string valor) =>
        $"{rotulo.PadRight(LarguraRotulo, '.')}: {valor}";

    public static string Renderizar(
        Evento evento, string worker, string host, string messageId,
        string tentativa, string trace, string hash32, DateTimeOffset registradoEm)
    {
        var titulo = Titulos.TryGetValue(worker, out var t)
            ? t
            : $"COMPROVANTE DE {worker.ToUpperInvariant()}";
        var regua = new string('=', Largura);

        var linhas = new[]
        {
            regua,
            $"  {titulo}",
            regua,
            Campo("Nosso número", evento.NossoNumero),
            Campo("Evento", evento.TipoEvento),
            Campo("Valor", FormatarValor(evento.ValorCentavos)),
            Campo("Ocorrido em", FormatarData(evento.OcorridoEm)),
            new string('-', Largura),
            Campo("Processado por", worker),
            Campo("Pod / host", host),
            Campo("Message ID", messageId),
            // O trace no proprio documento e o que fecha o circuito: de um
            // comprovante no volume voce volta para a cadeia de log inteira
            // (./run.sh trace <id>). Sem ele, o artefato e um beco sem saida.
            Campo("Trace ID", trace),
            Campo("Tentativa", tentativa),
            Campo("Registrado em", FormatarData(registradoEm)),
            Campo("Hash do payload", hash32),
            regua,
            "Documento gerado automaticamente para fins de laboratório.",
            "Não possui valor fiscal ou probatório.",
        };
        return string.Join("\n", linhas) + "\n";
    }

    /// <summary>
    /// Grava o comprovante. Devolve true se gravou, false se ja existia.
    ///
    /// Duas armadilhas resolvidas aqui:
    ///
    /// 1. Escrita atomica. Escrever direto no destino final deixa um .txt
    ///    truncado se o processo morrer no meio - e como o nome ja existe, a
    ///    logica de idempotencia nunca mais tenta de novo.
    ///
    /// 2. O overwrite: false. E ele que resolve a corrida entre duas
    ///    replicas: quem perder recebe IOException, verifica que o destino
    ///    existe e trata como duplicata. Trocar por File.Move(a, b) com
    ///    overwrite: true - ou pelo os.rename() do Python, que sobrescreve em
    ///    silencio - faria as duas replicas "ganharem".
    /// </summary>
    public static bool GravarAtomico(string destino, string conteudo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        // Atalho barato: na maioria das duplicatas o arquivo ja esta la.
        if (File.Exists(destino)) return false;

        var tmp = Path.Combine(Path.GetDirectoryName(destino)!,
            $".{Path.GetFileName(destino)}.{Environment.ProcessId}.tmp");

        // UTF8Encoding(false) = sem BOM. Com BOM, os .txt do worker .NET
        // ficariam 3 bytes diferentes dos do Python e do Go, e o `grep` do
        // verify.sh na primeira linha do arquivo se comportaria de forma
        // estranha. Detalhe pequeno, efeito irritante.
        File.WriteAllText(tmp, conteudo, new UTF8Encoding(false));
        try
        {
            File.Move(tmp, destino, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(destino))
        {
            // Perdemos a corrida para outra replica. Nao e erro: o
            // comprovante existe, que era o objetivo.
            return false;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
