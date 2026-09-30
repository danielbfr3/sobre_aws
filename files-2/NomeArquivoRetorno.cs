using System.Text.RegularExpressions;

/// <summary>
/// Padrões conhecidos:
///   V  + conta (10) + 3 dígitos           -> V0000278221208.TXT
///   PV + conta (10) + 3 dígitos + _NNN    -> PV0000206823009_618.TXT
/// </summary>
public static partial class NomeArquivoRetorno
{
    [GeneratedRegex(@"^(?<tipo>PV|V)(?<conta>\d{10})\d{3}(?:_\d+)?\.txt$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Padrao();

    public static bool TentarExtrairClientId(string nome, out string conta, out TipoArquivoRetorno tipo)
    {
        var m = Padrao().Match(nome);
        if (!m.Success)
        {
            conta = "";
            tipo = default;
            return false;
        }

        conta = m.Groups["conta"].Value;
        tipo = m.Groups["tipo"].Value.Equals("PV", StringComparison.OrdinalIgnoreCase)
            ? TipoArquivoRetorno.PV
            : TipoArquivoRetorno.V;
        return true;
    }
}
