// Ponto de entrada unico. Um binario, quatro subcomandos:
//
//   dotnet Lab.dll publisher     publica eventos no SNS
//   dotnet Lab.dll consumer      consome a fila e grava comprovantes
//   dotnet Lab.dll diagnostico   qual provedor de credenciais venceu (guia 03)
//   dotnet Lab.dll segredos      cache com TTL no Secrets Manager (guia 04)
//   dotnet Lab.dll amostra       comprovante deterministico (./run.sh comparar)
//
// Em Go isso vira quatro binarios em cmd/; em Python, quatro arquivos com
// __main__. E a mesma ideia com a ergonomia de cada linguagem.

using System.Runtime.InteropServices;
using Lab;

// Ctrl+C (SIGINT) e SIGTERM viram cancelamento cooperativo. Num pod isso
// importa: o Kubernetes manda SIGTERM e espera o terminationGracePeriodSeconds
// antes do SIGKILL. Um worker que ignora o sinal e morto no meio do
// processamento - e aqui isso significaria mensagem voltando para a fila sem
// necessidade.
//
// PosixSignalRegistration, e NAO AppDomain.ProcessExit. O ProcessExit dispara
// DEPOIS que os `using` do escopo ja rodaram, entao um handler que chama
// cts.Cancel() ali estoura ObjectDisposedException - excecao nao tratada
// durante o encerramento, que trava o processo em vez de derrubar. Custou uma
// depuracao para achar, e vale registrar.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, contexto =>
{
    contexto.Cancel = true;   // nao deixa o runtime derrubar o processo na hora
    cts.Cancel();
});

try
{
    return (args.Length > 0 ? args[0] : "") switch
    {
        "publisher" => await Publisher.RodarAsync(cts.Token),
        "consumer" => await Consumer.RodarAsync(cts.Token),
        "diagnostico" => await Diagnostico.RodarAsync(cts.Token),
        "segredos" => await Segredos.RodarAsync(cts.Token),
        "amostra" => await Amostra.RodarAsync(cts.Token),
        var outro => Uso(outro),
    };
}
catch (OperationCanceledException)
{
    return 0;
}

static int Uso(string recebido)
{
    if (!string.IsNullOrEmpty(recebido))
        Console.Error.WriteLine($"subcomando desconhecido: {recebido}");
    Console.Error.WriteLine(
        "uso: dotnet Lab.dll [publisher|consumer|diagnostico|segredos|amostra]");
    return 2;
}
