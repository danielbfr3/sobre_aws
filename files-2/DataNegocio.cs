/// <summary>
/// "Hoje" do negócio em horário de Brasília. Substitui DateOnly.FromDateTime(DateTime.UtcNow),
/// que vira o dia às 21h BRT.
/// Requer tzdata na imagem do container (imagens chiseled/alpine podem não ter);
/// sem ela, FindSystemTimeZoneById lança na inicialização.
/// </summary>
internal static class DataNegocio
{
    private static readonly TimeZoneInfo FusoSaoPaulo =
        TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static DateOnly Hoje() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, FusoSaoPaulo));
}
