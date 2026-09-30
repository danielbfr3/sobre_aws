/// <summary>
/// Monta os pendentes por conta a partir da listagem da pasta.
/// Compartilhado entre a implementação Local e a SMB, para as duas agruparem igual.
/// Em produção há no máximo um V e um PV por conta; em homologação pode haver vários:
/// processa o primeiro por nome e os demais ficam na pasta para o próximo ciclo.
/// </summary>
internal static class MontagemPendentes
{
    private readonly record struct ArquivoReconhecido(
        string Caminho, string Nome, string Conta, TipoArquivoRetorno Tipo);

    public static IReadOnlyList<ArquivoRetornoPendente> Montar(
        IEnumerable<(string Caminho, string Nome)> arquivos, ILogger logger)
    {
        var reconhecidos = new List<ArquivoReconhecido>();
        foreach (var (caminho, nome) in arquivos)
        {
            if (NomeArquivoRetorno.TentarExtrairClientId(nome, out var conta, out var tipo))
                reconhecidos.Add(new ArquivoReconhecido(caminho, nome, conta, tipo));
            else
                logger.LogWarning("Arquivo {Arquivo} fora do padrão V/PV - ignorado", nome);
        }

        return reconhecidos
            .GroupBy(a => a.Conta, StringComparer.Ordinal)
            .Select(g => new ArquivoRetornoPendente(
                g.Key,
                Escolher(g, TipoArquivoRetorno.V, logger),
                Escolher(g, TipoArquivoRetorno.PV, logger)))
            .OrderBy(p => p.Conta, StringComparer.Ordinal)
            .ToList();
    }

    private static ArquivoRetorno? Escolher(
        IGrouping<string, ArquivoReconhecido> grupo, TipoArquivoRetorno tipo, ILogger logger)
    {
        var candidatos = grupo
            .Where(a => a.Tipo == tipo)
            .OrderBy(a => a.Nome, StringComparer.Ordinal)
            .ToList();

        if (candidatos.Count > 1)
            logger.LogWarning(
                "Conta {Conta} com {Qtd} arquivos {Tipo} - processando {Arquivo}, demais ficam pro próximo ciclo",
                grupo.Key, candidatos.Count, tipo, candidatos[0].Nome);

        return candidatos.Count == 0 ? null : new ArquivoRetorno(candidatos[0].Caminho, candidatos[0].Nome);
    }
}
