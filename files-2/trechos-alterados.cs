// =====================================================================
// SmbPastaOrigemArquivosRetorno.cs
// ListarArquivosVAsync -> ListarPendentesAsync (topo do método igual até o QueryDirectory).
// LocalizarPvCorrespondente: apagar (e o sync-over-async junto).
// =====================================================================

    public async Task<IReadOnlyList<ArquivoRetornoPendente>> ListarPendentesAsync(CancellationToken ct)
    {
        // ... ObterFileStoreAsync / CreateFile / QueryDirectory / CloseFile iguais ao atual ...

        logger.LogInformation("Lendo arquivos da pasta SMB: {Caminho}", _smb.CaminhoRelativo);

        var regulares = entradas
            .OfType<FileDirectoryInformation>()
            .Where(EhArquivoRegular)
            .Select(e => (nome: e.FileName, caminho: CombinarCaminho(_smb.CaminhoRelativo, e.FileName)))
            .ToList();

        var arquivos = new List<(string caminho, string nome, string conta, TipoArquivoRetorno tipo)>();
        foreach (var (nome, caminho) in regulares)
        {
            if (NomeArquivoRetorno.TentarExtrairClientId(nome, out var conta, out var tipo))
                arquivos.Add((caminho, nome, conta, tipo));
            else
                // Visibilidade pra padrões novos (como o sufixo _618 do PV).
                logger.LogWarning("Arquivo {Arquivo} fora do padrão V/PV - ignorado", nome);
        }

        return MontarPendentes(arquivos);
    }

    /// <summary>
    /// Um pendente por conta, com o V e o PV já resolvidos.
    /// Em produção há no máximo um de cada; em homologação pode haver vários:
    /// processa o primeiro por nome e os demais ficam na pasta para o próximo ciclo.
    /// </summary>
    private List<ArquivoRetornoPendente> MontarPendentes(
        List<(string caminho, string nome, string conta, TipoArquivoRetorno tipo)> arquivos) =>
        arquivos
            .GroupBy(a => a.conta, StringComparer.Ordinal)
            .Select(g => new ArquivoRetornoPendente(
                g.Key,
                Escolher(g.Key, g, TipoArquivoRetorno.V),
                Escolher(g.Key, g, TipoArquivoRetorno.PV)))
            .OrderBy(p => p.Conta, StringComparer.Ordinal)
            .ToList();

    private ArquivoRetorno? Escolher(
        string conta,
        IEnumerable<(string caminho, string nome, string conta, TipoArquivoRetorno tipo)> doGrupo,
        TipoArquivoRetorno tipo)
    {
        var candidatos = doGrupo
            .Where(a => a.tipo == tipo)
            .OrderBy(a => a.nome, StringComparer.Ordinal)
            .ToList();

        if (candidatos.Count > 1)
            logger.LogWarning(
                "Conta {Conta} com {Qtd} arquivos {Tipo} - processando {Arquivo}, demais ficam pro próximo ciclo",
                conta, candidatos.Count, tipo, candidatos[0].nome);

        return candidatos.Count == 0 ? null : new ArquivoRetorno(candidatos[0].caminho, candidatos[0].nome);
    }


// =====================================================================
// ProcessarArquivosVePvPipeline.cs - dentro do ExecutarAsync
// =====================================================================

        var pendentes = await origem.ListarPendentesAsync(ct);
        logger.LogInformation("Encontradas {Qtd} conta(s) com arquivo(s) pendente(s)", pendentes.Count);

        // ... ResumoExecucao / ParallelOptions iguais ...

        await Parallel.ForEachAsync(pendentes, opcoesParalelo, async (pendente, tokenItem) =>
        {
            try
            {
                using var escopo = scopeFactory.CreateScope();
                var processador = escopo.ServiceProvider
                    .GetRequiredService<ProcessadorArquivoRetornoService>();

                var resultado = await processador.ProcessarAsync(pendente, tokenItem);
                resumo.Registrar(resultado);
            }
            catch (OperationCanceledException) when (tokenItem.IsCancellationRequested)
            {
                throw; // shutdown não é falha de arquivo
            }
            catch (Exception ex)
            {
                // pendente.Caminho/Nome eram nulos no caso "só PV"
                logger.LogError(ex, "Falha ao processar conta {Conta} ({Arquivo})",
                    pendente.Conta, pendente.Principal.Nome);
                resumo.Registrar(new ArquivoProcessado(
                    ResultadoArquivo.Falha,
                    Identificador: pendente.Principal.Nome,
                    Motivo: $"{ex.GetType().Name}: {ex.Message}"));
            }
        });


// =====================================================================
// PendenciasParaTitulosConvertidosFactory.cs
// =====================================================================

    public async Task<(IReadOnlyList<TituloConvertido> Titulos, IReadOnlyList<string> Chaves)>
        ObterPendenciasConvertidasAsync(string cnpj, string conta, DateOnly dataD1, CancellationToken ct)
    {
        // O repositório precisa filtrar pela conta (ClienteContaHeader) - conferir se o formato
        // bate com os 10 dígitos do nome do arquivo (zeros à esquerda, dígito verificador).
        var titulos = controlePendencias.FiltrarNaoReportados(
            await pendencias.ObterTitulosRecusadosOuComErroAsync(cnpj, conta, dataD1, ct));
        var instrucoes = controlePendencias.FiltrarNaoReportados(
            await pendencias.ObterInstrucoesRecusadasOuComErroAsync(cnpj, conta, dataD1, ct));

        // ... resto igual ...
    }

    // Em ConverterTitulo e ConverterInstrucao:
    //     DataOcorrencia = DataNegocio.Hoje().ToString("yyyy-MM-dd"),


// =====================================================================
// ControlePendenciasReportadasDiario.cs
// Registrar como SINGLETON (o ProcessadorArquivoRetornoService é resolvido
// por escopo no pipeline; se este controle for scoped, cada item tem seu
// próprio lock e o lock não protege nada).
// =====================================================================

/// <remarks>
/// Premissa: 1 réplica. Estado e locks ficam em memória / disco local do pod.
/// Com mais réplicas, mover o estado para o banco e usar lock distribuído.
/// </remarks>
public class ControlePendenciasReportadasDiario
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IAsyncDisposable> AdquirirLockAsync(string chave, CancellationToken ct)
    {
        var semaforo = _locks.GetOrAdd(chave, _ => new SemaphoreSlim(1, 1));
        await semaforo.WaitAsync(ct);
        return new Liberador(semaforo);
    }

    public void RegistrarReportadas(IEnumerable<string> chaves)
    {
        lock (_lock)
        {
            var hoje = DataNegocio.Hoje();
            if (!_estado.Data.Equals(hoje))
                _estado = new Estado(hoje, []);

            foreach (var chave in chaves) _estado.Chaves.Add(chave);
            Salvar(_caminhoArquivo, _estado);
        }
    }

    // Em CarregarOuNovo:
    //     var hoje = DataNegocio.Hoje();

    // JaReportada tem 0 referências: pode sair.
}
