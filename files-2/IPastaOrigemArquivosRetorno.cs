// Assinaturas de LerAsync/MoverParaBackupAsync/EscreverRetornoClienteAsync inferidas do uso;
// mantenha as atuais e quaisquer outros membros que a interface já tenha.
// A implementação LOCAL também precisa ser adaptada (ListarPendentesAsync, sem LocalizarPvCorrespondente).

public interface IPastaOrigemArquivosRetorno
{
    Task<IReadOnlyList<ArquivoRetornoPendente>> ListarPendentesAsync(CancellationToken ct);
    Task<byte[]> LerAsync(string caminho, CancellationToken ct);
    Task MoverParaBackupAsync(string caminho, CancellationToken ct);
    Task EscreverRetornoClienteAsync(string nomeArquivo, byte[] conteudo, CancellationToken ct);

    // Removido: string? LocalizarPvCorrespondente(string clientId);
}
