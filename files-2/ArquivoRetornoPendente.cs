// Substitui ArquivoVPendente.

public sealed record ArquivoRetorno(string Caminho, string Nome);

/// <summary>
/// Um pendente por conta. Em produção: só V, só PV ou V + PV.
/// Com V e PV, o V é o principal e o PV o secundário.
/// </summary>
public sealed record ArquivoRetornoPendente(string Conta, ArquivoRetorno? V, ArquivoRetorno? Pv)
{
    public ArquivoRetorno Principal =>
        V ?? Pv ?? throw new InvalidOperationException($"Conta {Conta} sem arquivo V nem PV.");

    public ArquivoRetorno? Secundario => V is null ? null : Pv;
}
