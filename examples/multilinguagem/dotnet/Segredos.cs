// Secrets Manager com cache e TTL - .NET / AWSSDK.  (guia 04)
//
// Demonstra, contra o Floci, as duas coisas que o guia 04 §3 diz que quase
// toda primeira implementacao erra:
//
//   1. Buscar o segredo A CADA USO. E chamada paga, tem limite de taxa, e sob
//      carga o worker cai por nao conseguir ler uma senha.
//
//   2. Cachear PARA SEMPRE. Funciona por semanas e quebra numa madrugada de
//      rotacao, voltando so com restart.
//
//   ./run.sh segredos dotnet
//
// Equivalentes: ../python/segredos.py e ../go/cmd/segredos/main.go.

using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace Lab;

public sealed record CredenciaisBanco(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password);

/// <summary>
/// Cache com TTL sobre o Secrets Manager.
///
/// O ISegredoProvider do guia 04 §7. A interface importa mais que a
/// implementacao: em teste local voce registra uma versao que le de
/// appsettings.Development.json e o codigo de negocio nao muda.
///
/// O lock existe porque o worker e concorrente: sem ele, duas threads
/// perdendo o TTL ao mesmo tempo fariam duas chamadas ao Secrets Manager.
/// </summary>
public sealed class SegredoProvider(IAmazonSecretsManager cliente, TimeSpan ttl)
{
    private readonly Dictionary<string, (CredenciaisBanco Valor, DateTimeOffset ExpiraEm)> _cache = [];
    private readonly SemaphoreSlim _porta = new(1, 1);

    public int ChamadasAAws { get; private set; }

    public async Task<CredenciaisBanco> ObterAsync(string secretId, CancellationToken ct)
    {
        await _porta.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(secretId, out var e) && DateTimeOffset.UtcNow < e.ExpiraEm)
                return e.Valor;

            ChamadasAAws++;
            var resposta = await cliente.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = secretId }, ct);

            var valor = JsonSerializer.Deserialize<CredenciaisBanco>(resposta.SecretString)!;
            _cache[secretId] = (valor, DateTimeOffset.UtcNow + ttl);
            return valor;
        }
        finally { _porta.Release(); }
    }

    /// <summary>
    /// Chame quando o BANCO recusar a senha - e so uma vez.
    ///
    /// Cobre a janela entre a rotacao acontecer e o TTL expirar. Repetir
    /// indefinidamente com senha errada bloqueia a conta no banco, entao a
    /// regra e: invalida, tenta MAIS UMA vez, e se falhar propaga o erro.
    /// </summary>
    public void Invalidar(string secretId) => _cache.Remove(secretId);
}

public static class Segredos
{
    private const string SegredoId = "asa/dev/cash-cobranca/postgres";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    // No lab, 5s. Em producao, minutos - e MENOR que o intervalo de rotacao.

    private const string ValorV1 =
        """{"host":"postgres","port":5432,"username":"app","password":"senha-v1"}""";
    private const string ValorV2 =
        """{"host":"postgres","port":5432,"username":"app","password":"senha-v2-ROTACIONADA"}""";

    private static async Task PrepararAsync(IAmazonSecretsManager sm, CancellationToken ct)
    {
        try
        {
            await sm.CreateSecretAsync(
                new CreateSecretRequest { Name = SegredoId, SecretString = ValorV1 }, ct);
            Console.WriteLine($"  segredo {SegredoId} criado");
        }
        catch (ResourceExistsException)
        {
            await sm.PutSecretValueAsync(
                new PutSecretValueRequest { SecretId = SegredoId, SecretString = ValorV1 }, ct);
            Console.WriteLine($"  segredo {SegredoId} ja existia, valor restaurado");
        }
    }

    public static async Task<int> RodarAsync(CancellationToken ct)
    {
        using var sm = Aws.SecretsManager();

        Console.WriteLine(new string('=', 67));
        Console.WriteLine(" Secrets Manager: cache com TTL - .NET / AWSSDK");
        Console.WriteLine(new string('=', 67));
        Console.WriteLine();
        await PrepararAsync(sm, ct);

        var provider = new SegredoProvider(sm, Ttl);

        Console.WriteLine($"\n--- 1. Cinco leituras seguidas, TTL de {Ttl.TotalSeconds:0}s");
        for (var i = 1; i <= 5; i++)
        {
            var v = await provider.ObterAsync(SegredoId, ct);
            Console.WriteLine($"  leitura {i}: password={v.Password}   " +
                              $"(idas a AWS ate agora: {provider.ChamadasAAws})");
            await Task.Delay(400, ct);
        }
        Console.WriteLine("\n  -> 5 leituras, 1 ida a AWS. Sem cache seriam 5 GetSecretValue:");
        Console.WriteLine("     5x o custo, 5x a latencia, e 5 eventos no CloudTrail.");

        Console.WriteLine("\n--- 2. A rotacao acontece enquanto o processo esta no ar");
        await sm.PutSecretValueAsync(
            new PutSecretValueRequest { SecretId = SegredoId, SecretString = ValorV2 }, ct);
        Console.WriteLine("  senha rotacionada no Secrets Manager (novo AWSCURRENT)");
        var atual = await provider.ObterAsync(SegredoId, ct);
        Console.WriteLine($"  leitura logo em seguida: password={atual.Password}");
        Console.WriteLine("  -> o cache quente ainda devolve a senha VELHA. Ate aqui, tudo bem:");
        Console.WriteLine("     e a janela do TTL, e ela e limitada de proposito.");

        Console.WriteLine("\n--- 3. O banco recusa a senha -> invalidar e tentar UMA vez");
        provider.Invalidar(SegredoId);
        atual = await provider.ObterAsync(SegredoId, ct);
        Console.WriteLine($"  apos invalidar: password={atual.Password}   " +
                          $"(idas a AWS: {provider.ChamadasAAws})");

        Console.WriteLine($"\n--- 4. Ou simplesmente esperar o TTL ({Ttl.TotalSeconds:0}s)");
        Console.WriteLine("  aguardando...");
        await Task.Delay(Ttl + TimeSpan.FromMilliseconds(500), ct);
        atual = await provider.ObterAsync(SegredoId, ct);
        Console.WriteLine($"  apos o TTL: password={atual.Password}   " +
                          $"(idas a AWS: {provider.ChamadasAAws})");

        Console.WriteLine();
        Console.WriteLine("  Um cache ETERNO (uma static readonly populada no Startup)");
        Console.WriteLine("  travaria na senha-v1 para sempre. O worker so voltaria com");
        Console.WriteLine("  restart - e o incidente aconteceria de madrugada, no dia da");
        Console.WriteLine("  rotacao.");
        Console.WriteLine();
        return 0;
    }
}
