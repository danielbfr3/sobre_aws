// Arquivo completo. Mantenha os usings/namespace atuais.
// Mudanças: pendente por conta (V/PV já resolvidos), dedup por arquivo,
// lock por cnpj:conta, pipeline checado antes de reservar sequencial,
// sequencial por (cnpj, conta) com devolução em caso de erro, datas em BRT,
// JSON em Debug, uma única injeção de IPastaOrigemArquivosRetorno.

public sealed record ArquivoProcessado(
    ResultadoArquivo Resultado, string? Cnpj = null, string? Identificador = null, string? Motivo = null);

public class ProcessadorArquivoRetornoService(
    IPastaOrigemArquivosRetorno origem,
    IControleIngestaoRetornoRepository controleIngestao,
    ControlePendenciasReportadasDiario controlePendencias,
    PendenciasParaTitulosConvertidosFactory pendenciasFactory,
    MesclagemDadosConvertidos mesclagem,
    IArquivoRepository arquivos,
    ISequencialArquivoRepository sequenciais,
    ILayoutConversaoApiClient conversor,
    PadraoPipelineRetornoService padraoPipelineRetornoService,
    ILogger<ProcessadorArquivoRetornoService> logger)
{
    private enum Duplicidade { Nenhuma, Parcial, Total }

    public async Task<ArquivoProcessado> ProcessarAsync(ArquivoRetornoPendente pendente, CancellationToken ct)
    {
        var conta = pendente.Conta;
        var principal = pendente.Principal;
        var secundario = pendente.Secundario;

        LogComposicao(pendente);

        var conteudoPrincipal = await origem.LerAsync(principal.Caminho, ct);
        var conteudoSecundario = secundario is null ? null : await origem.LerAsync(secundario.Caminho, ct);

        var md5Principal = Md5(conteudoPrincipal);
        var md5Secundario = conteudoSecundario is null ? null : Md5(conteudoSecundario);

        switch (await VerificarDuplicidadeAsync(md5Principal, md5Secundario, ct))
        {
            case Duplicidade.Total:
                logger.LogWarning(
                    "Conta {Conta}: arquivo(s) já processado(s) hoje - movendo pra Backup sem reprocessar", conta);
                await MoverParaBackupAsync(pendente, ct);
                return new ArquivoProcessado(ResultadoArquivo.Duplicado, Identificador: principal.Nome);

            case Duplicidade.Parcial:
                // Um dos arquivos já foi processado hoje e o outro não: reprocessamento com arquivo alterado.
                // Não move nada; os arquivos ficam na pasta para análise manual.
                logger.LogError(
                    "Conta {Conta}: V e PV com situação de processamento diferente hoje - verificar manualmente", conta);
                return Falha(pendente, null, "V e PV com situação de processamento diferente hoje");
        }

        var cnpj = Cnab240Campos.ExtrairCnpjHeaderArquivo(conteudoPrincipal);
        await using var lockConta = await controlePendencias.AdquirirLockAsync($"{cnpj}:{conta}", ct);

        try
        {
            // Checado antes de reservar sequencial / registrar arquivo: falhar aqui não deixa rastro.
            var pipelineClienteId = await padraoPipelineRetornoService.ChecarAsync(cnpj, ct);
            if (string.IsNullOrEmpty(pipelineClienteId))
                return Falha(pendente, cnpj, $"Pipeline do cliente {cnpj} não encontrado");

            var idCorrelacaoSync = Guid.NewGuid().ToString();
            var syncPrincipal = await conversor.ConverterCnabParaJsonAsync(
                conteudoPrincipal, principal.Nome, idCorrelacaoSync, ct);
            var dadosSecundario = conteudoSecundario is null
                ? null
                : (await conversor.ConverterCnabParaJsonAsync(
                    conteudoSecundario, secundario!.Nome, idCorrelacaoSync, ct)).Data;

            var hoje = DataNegocio.Hoje();
            var (pendenciasConvertidas, chaves) =
                await pendenciasFactory.ObterPendenciasConvertidasAsync(cnpj, conta, hoje.AddDays(-1), ct);

            var dadosMesclados = mesclagem.Mesclar(syncPrincipal.Data, dadosSecundario, pendenciasConvertidas);

            var sequencial = await sequenciais.ReservarProximoAsync(cnpj, conta, ct);
            var retornoGravado = false;
            try
            {
                var dadosFinais = mesclagem.AplicarSequencial(dadosMesclados, sequencial);
                var json = JsonSerializer.SerializeToUtf8Bytes(dadosFinais, JsonOpcoesSaida);
                var nomeArquivoRetorno = MontarNomeArquivoRetorno(cnpj, conta, hoje);
                var arquivoId = await arquivos.RegistrarEnvioParaConversaoAsync(
                    nomeArquivoRetorno, cnpj, dadosFinais, ct);

                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Conteúdo JSON enviado para conversão do arquivo {Arquivo}: {Json}",
                        nomeArquivoRetorno, Encoding.UTF8.GetString(json));

                ConvertAsyncUploadIniciado resultadoConversao;
                try
                {
                    resultadoConversao = await conversor.ConverterJsonParaCnabAsync(
                        json, $"{nomeArquivoRetorno}.json", arquivoId.ToString(), pipelineClienteId, ct);

                    // convertendo localmente para salvar na pasta
                    var resultadoConversaoCnab = await conversor.Converter(
                        json, $"{nomeArquivoRetorno}.json", arquivoId.ToString(), pipelineClienteId, ct);

                    await origem.EscreverRetornoClienteAsync(
                        nomeArquivoRetorno, resultadoConversaoCnab.ConteudoArquivo, ct);
                }
                catch
                {
                    await arquivos.RemoverAsync(arquivoId, CancellationToken.None);
                    throw;
                }

                // A partir daqui o retorno já está na pasta do cliente: o sequencial foi usado de verdade.
                retornoGravado = true;

                await controleIngestao.RegistrarProcessadoAsync(md5Principal, principal.Nome, conta, ct);
                if (md5Secundario is not null)
                    await controleIngestao.RegistrarProcessadoAsync(md5Secundario, secundario!.Nome, conta, ct);
                controlePendencias.RegistrarReportadas(chaves);
                await MoverParaBackupAsync(pendente, ct);

                logger.LogInformation(
                    "Conta {Conta} processada - Arquivo {Nome}, Cnpj {Cnpj}, Sequencial {Sequencial}, " +
                    "ArquivoID {ArquivoID}, JobId {JobId}, Pendências {QtdPendencias}",
                    conta, nomeArquivoRetorno, cnpj, sequencial, arquivoId, resultadoConversao.JobId, chaves.Count);

                return new ArquivoProcessado(ResultadoArquivo.Processado, cnpj, principal.Nome);
            }
            catch when (!retornoGravado)
            {
                await LiberarSequencialAsync(cnpj, conta, sequencial);
                throw;
            }
        }
        catch (Exception ex) when (ex is ConversaoCnabFalhouException
                                      or DadosConvertidosDivergentesException
                                      or SequencialIndisponivelException)
        {
            logger.LogError(ex, "Falha ao processar conta {Conta} (Cnpj {Cnpj})", conta, cnpj);
            return Falha(pendente, cnpj, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<Duplicidade> VerificarDuplicidadeAsync(string md5Principal, string? md5Secundario, CancellationToken ct)
    {
        var jaProcessados = new List<bool> { await controleIngestao.JaProcessadoHojeAsync(md5Principal, ct) };
        if (md5Secundario is not null)
            jaProcessados.Add(await controleIngestao.JaProcessadoHojeAsync(md5Secundario, ct));

        if (jaProcessados.All(x => x)) return Duplicidade.Total;
        if (jaProcessados.Any(x => x)) return Duplicidade.Parcial;
        return Duplicidade.Nenhuma;
    }

    private async Task LiberarSequencialAsync(string cnpj, string conta, long sequencial)
    {
        try
        {
            if (!await sequenciais.LiberarAsync(cnpj, conta, sequencial, CancellationToken.None))
                logger.LogError(
                    "Sequencial {Sequencial} da conta {Conta} (Cnpj {Cnpj}) não pôde ser devolvido - verificar lacuna",
                    sequencial, conta, cnpj);
        }
        catch (Exception ex)
        {
            // Não mascara a exceção original.
            logger.LogError(ex,
                "Erro ao devolver sequencial {Sequencial} da conta {Conta} (Cnpj {Cnpj}) - verificar lacuna",
                sequencial, conta, cnpj);
        }
    }

    private async Task MoverParaBackupAsync(ArquivoRetornoPendente pendente, CancellationToken ct)
    {
        await origem.MoverParaBackupAsync(pendente.Principal.Caminho, ct);
        if (pendente.Secundario is { } secundario)
            await origem.MoverParaBackupAsync(secundario.Caminho, ct);
    }

    private void LogComposicao(ArquivoRetornoPendente pendente)
    {
        switch (pendente)
        {
            case { V: not null, Pv: not null }:
                logger.LogInformation("Conta {Conta}: V {V} + PV {Pv}", pendente.Conta, pendente.V.Nome, pendente.Pv.Nome);
                break;
            case { V: not null }:
                logger.LogInformation("Conta {Conta}: só V ({V})", pendente.Conta, pendente.V.Nome);
                break;
            default:
                logger.LogInformation("Conta {Conta}: só PV ({Pv})", pendente.Conta, pendente.Pv!.Nome);
                break;
        }
    }

    private static ArquivoProcessado Falha(ArquivoRetornoPendente pendente, string? cnpj, string motivo) =>
        new(ResultadoArquivo.Falha, cnpj, pendente.Principal.Nome, motivo);

    private static string Md5(byte[] conteudo) => Convert.ToHexString(MD5.HashData(conteudo));

    // ATENÇÃO: confirmar com o cliente/negócio a mudança no nome do arquivo.
    internal static string MontarNomeArquivoRetorno(string cnpj, string conta, DateOnly data) =>
        $"RETORNO-{cnpj}-{conta}-{data:yyyyMMdd}.ret";

    private static readonly JsonSerializerOptions JsonOpcoesSaida = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
