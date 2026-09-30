using Microsoft.EntityFrameworkCore;

public interface ISequencialArquivoRepository
{
    Task<long> ReservarProximoAsync(string documento, string conta, CancellationToken ct);

    /// <summary>Devolve o sequencial se ele ainda for o último da conta. True se devolveu.</summary>
    Task<bool> LiberarAsync(string documento, string conta, long sequencial, CancellationToken ct);
}

public sealed class SequencialIndisponivelException(string documento, string conta, int linhasAfetadas)
    : Exception(linhasAfetadas == 0
        ? $"Nenhuma linha em Cobranca.SequencialArquivo pro documento '{documento}', conta '{conta}' - sem SequencialAtual pra reservar."
        : $"{linhasAfetadas} linhas em Cobranca.SequencialArquivo pro documento '{documento}', conta '{conta}' - " +
          "esperada exatamente 1. Todas foram incrementadas; corrigir manualmente.")
{
    public string Documento { get; } = documento;
    public string Conta { get; } = conta;
    public int LinhasAfetadas { get; } = linhasAfetadas;
}

public class SequencialArquivoRepository(CobrancaDbContext db) : ISequencialArquivoRepository
{
    public async Task<long> ReservarProximoAsync(string documento, string conta, CancellationToken ct)
    {
        // MERGE com HOLDLOCK: sem o hint, duas execuções concorrentes pra uma chave nova
        // podem cair ambas no WHEN NOT MATCHED. Ver sql/005_cobranca_sequencial_arquivo_conta.sql.
        var sequenciais = await db.Database
            .SqlQuery<long>($"""
                MERGE Cobranca.SequencialArquivo WITH (HOLDLOCK) AS destino
                USING (SELECT {documento} AS Documento, {conta} AS Conta) AS origem
                    ON destino.Documento = origem.Documento AND destino.Conta = origem.Conta
                WHEN MATCHED THEN
                    UPDATE SET SequencialAtual = destino.SequencialAtual + 1,
                               DataHoraAtualizacaoUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (Documento, Conta, SequencialAtual) VALUES (origem.Documento, origem.Conta, 1)
                OUTPUT INSERTED.SequencialAtual AS Value;
                """)
            .ToListAsync(ct);

        if (sequenciais.Count != 1)
            throw new SequencialIndisponivelException(documento, conta, sequenciais.Count);

        return sequenciais[0];
    }

    public async Task<bool> LiberarAsync(string documento, string conta, long sequencial, CancellationToken ct)
    {
        // Só devolve se ninguém reservou outro número depois (garantido pelo lock cnpj:conta).
        var linhas = await db.Database.ExecuteSqlAsync($"""
            UPDATE Cobranca.SequencialArquivo
               SET SequencialAtual = SequencialAtual - 1,
                   DataHoraAtualizacaoUtc = SYSUTCDATETIME()
             WHERE Documento = {documento}
               AND Conta = {conta}
               AND SequencialAtual = {sequencial};
            """, ct);

        return linhas == 1;
    }
}
