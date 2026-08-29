// Amostra deterministica - .NET.
//
// Renderiza UM comprovante a partir de um payload fixo, com host, message id
// e horario tambem fixos. Nada aqui varia entre execucoes.
//
// E o que torna a afirmacao "as tres implementacoes sao equivalentes"
// VERIFICAVEL em vez de so escrita no capitulo:
//
//   ./run.sh comparar
//
// roda esta amostra nas tres linguagens e faz `diff`.
//
// Equivalentes: ../python/amostra.py e ../go/cmd/amostra/main.go.

namespace Lab;

public static class Amostra
{
    // O mesmo evento de exemplo que aparece no README secao 3.
    private static readonly Evento EventoExemplo = new(
        NossoNumero: "4827193",
        TipoEvento: "cobranca.baixada",
        ValorCentavos: 123_456,
        OcorridoEm: new DateTimeOffset(2026, 8, 8, 17, 42, 3, TimeSpan.Zero));

    private const string Worker = "baixa";
    private const string Host = "consumer-baixa-7d9f8b4c2-x2k4l";
    private const string MessageId = "9f3a1c22-5e88-4b1d-a0f7-1c9e2b6d4a51";
    private const string Tentativa = "1";
    // Trace FIXO: a amostra tem que ser identica a cada execucao para o
    // ./run.sh comparar poder fazer diff. Em producao o trace nunca se repete.
    private const string Trace = "a1b2c3d4e5f6071829304a5b6c7d8e9f";
    private static readonly DateTimeOffset RegistradoEm =
        new(2026, 8, 8, 17, 42, 4, TimeSpan.Zero);

    public static Task<int> RodarAsync(CancellationToken ct)
    {
        var corpo = EventoExemplo.ParaJson();
        var hash32 = Comprovante.HashPayload(corpo);
        var caminho = Comprovante.Caminho("/data", Worker, EventoExemplo, hash32);

        // Console.Out com "\n" e nao Environment.NewLine: em Windows o
        // WriteLine emitiria \r\n e o diff com o Python e o Go acusaria uma
        // diferenca que nao existe no conteudo.
        Console.Out.Write($"payload : {corpo}\n");
        Console.Out.Write($"hash    : {hash32}\n");
        Console.Out.Write($"caminho : {caminho}\n");
        Console.Out.Write("---\n");
        Console.Out.Write(Comprovante.Renderizar(
            EventoExemplo, Worker, Host, MessageId, Tentativa, Trace, hash32, RegistradoEm));

        return Task.FromResult(0);
    }
}
