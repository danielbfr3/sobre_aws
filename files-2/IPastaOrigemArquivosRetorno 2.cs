// Mantenha quaisquer outros membros que a interface já tenha.

public interface IPastaOrigemArquivosRetorno
{
    Task<IReadOnlyList<ArquivoRetornoPendente>> ListarPendentesAsync(CancellationToken ct);
    Task<byte[]> LerAsync(string caminho, CancellationToken ct);
    Task MoverParaBackupAsync(string caminho, CancellationToken ct);
    Task EscreverRetornoClienteAsync(string nomeArquivo, byte[] conteudo, CancellationToken ct);
    Task EscreverLogDiarioAsync(string nomeArquivo, byte[] conteudo, CancellationToken ct);

    // Removido: string? LocalizarPvCorrespondente(string clientId);
}
