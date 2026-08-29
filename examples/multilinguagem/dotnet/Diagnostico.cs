// Diagnostico da cadeia de credenciais - .NET / AWSSDK.
//
// E o programa do guia 03 §6, aqui como subcomando. Responde duas perguntas,
// nesta ordem:
//
//   1. QUAL PROVEDOR DA CADEIA VENCEU?
//   2. QUEM EU SOU, na visao da AWS?
//
//   ./run.sh diagnostico dotnet
//
// Equivalentes: ../python/diagnostico.py e ../go/cmd/diagnostico/main.go.

using Amazon.Runtime;
using Amazon.SecurityToken.Model;

namespace Lab;

public static class Diagnostico
{
    // No AWS SDK for .NET o provedor vencedor se identifica pelo TIPO do
    // objeto devolvido pela cadeia. Estes sao os que interessam num worker.
    private static readonly Dictionary<string, string> Provedores = new()
    {
        ["EnvironmentVariablesAWSCredentials"] = "variaveis de ambiente (AWS_ACCESS_KEY_ID...)",
        ["AppConfigAWSCredentials"] = "app.config / web.config (.NET Framework)",
        ["SharedCredentialsFile"] = "~/.aws/credentials",
        ["StoredProfileAWSCredentials"] = "perfil do ~/.aws",
        ["AssumeRoleAWSCredentials"] = "perfil com role_arn + source_profile",
        ["SSOAWSCredentials"] = "AWS SSO / IAM Identity Center",
        ["AssumeRoleWithWebIdentityCredentials"] = "IRSA / web identity (EKS)",
        ["URIBasedRefreshingCredentialHelper"] = "endpoint de container (ECS ou EKS Pod Identity)",
        ["GenericContainerCredentials"] = "endpoint de container (ECS ou EKS Pod Identity)",
        ["DefaultInstanceProfileAWSCredentials"] = "IMDS - a role do NO EC2, nao a do seu workload",
        ["BasicAWSCredentials"] = "credencial fixa passada no codigo",
    };

    private static readonly string[] Interessantes =
    [
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN",
        "AWS_PROFILE", "AWS_ROLE_ARN", "AWS_WEB_IDENTITY_TOKEN_FILE",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "AWS_CONTAINER_CREDENTIALS_FULL_URI",
        "AWS_REGION", "AWS_DEFAULT_REGION", "AWS_ENDPOINT_URL",
    ];

    private static void Secao(string titulo) => Console.WriteLine($"\n--- {titulo}");

    public static async Task<int> RodarAsync(CancellationToken ct)
    {
        Console.WriteLine(new string('=', 67));
        Console.WriteLine(" Cadeia de credenciais - .NET / AWSSDK");
        Console.WriteLine(new string('=', 67));

        // ---------------------------------------------------------------
        Secao("1. O ambiente");
        // ---------------------------------------------------------------
        var presentes = Interessantes
            .Select(n => (Nome: n, Valor: Environment.GetEnvironmentVariable(n)))
            .Where(v => !string.IsNullOrEmpty(v.Valor))
            .ToDictionary(v => v.Nome, v => v.Valor!);

        if (presentes.Count == 0) Console.WriteLine("  nenhuma variavel AWS_* definida");
        foreach (var (nome, valor) in presentes)
        {
            // Nunca imprima segredo inteiro, nem em ferramenta de diagnostico.
            var mostrado = nome.Contains("SECRET") || nome.Contains("TOKEN") ? "***" : valor;
            Console.WriteLine($"  {nome}={mostrado}");
        }

        // O bug do guia 03 §2, detectado antes de qualquer chamada.
        if (presentes.ContainsKey("AWS_ACCESS_KEY_ID") && presentes.ContainsKey("AWS_ROLE_ARN"))
        {
            Console.WriteLine();
            Console.WriteLine("  (!) AWS_ACCESS_KEY_ID e AWS_ROLE_ARN presentes ao mesmo tempo.");
            Console.WriteLine("      Variavel de ambiente vence IRSA na cadeia. " +
                              "O IRSA esta sendo IGNORADO.");
        }

        // ---------------------------------------------------------------
        Secao("2. Qual provedor venceu");
        // ---------------------------------------------------------------
        AWSCredentials credenciais;
        try
        {
            // Esta e a cadeia, exposta. O TIPO do objeto ja diz quem venceu.
            credenciais = FallbackCredentialsFactory.GetCredentials();
        }
        catch (AmazonServiceException e)
        {
            Console.WriteLine($"  NENHUMA credencial encontrada na cadeia: {e.Message}");
            return 1;
        }

        var tipo = credenciais.GetType().Name;
        Console.WriteLine($"  tipo .........: {tipo}");
        Console.WriteLine($"  significa ....: " +
            $"{(Provedores.TryGetValue(tipo, out var s) ? s : "provedor nao mapeado")}");

        // RefreshingAWSCredentials = o SDK renova sozinho ANTES de expirar.
        // E o que voce QUER ver num worker de longa duracao: credencial nao
        // renovavel num processo que vive semanas termina em ExpiredToken.
        var renovavel = credenciais is RefreshingAWSCredentials;
        Console.WriteLine($"  renovavel ....: {(renovavel ? "sim" : "nao (credencial estatica)")}");

        if (tipo == "DefaultInstanceProfileAWSCredentials")
        {
            Console.WriteLine();
            Console.WriteLine("  (!) IMDS venceu. Num pod do EKS isso significa que voce esta");
            Console.WriteLine("      usando a ROLE DO NO, nao a do seu ServiceAccount. Parte");
            Console.WriteLine("      das coisas funciona - e por isso que passa despercebido.");
        }

        // ---------------------------------------------------------------
        Secao("3. Quem eu sou (sts:GetCallerIdentity)");
        // ---------------------------------------------------------------
        GetCallerIdentityResponse eu;
        try
        {
            using var sts = Aws.Sts();
            eu = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.WriteLine($"  falhou: {e.Message}");
            return 1;
        }

        Console.WriteLine($"  Account ......: {eu.Account}");
        Console.WriteLine($"  Arn ..........: {eu.Arn}");
        Console.WriteLine($"  UserId .......: {eu.UserId}");
        Console.WriteLine();
        if (eu.Arn.Contains(":assumed-role/"))
        {
            Console.WriteLine("  -> assumed-role: algum AssumeRole funcionou. Confira se o nome");
            Console.WriteLine("     da role e o do SEU workload e nao o do nodegroup.");
        }
        else if (eu.Arn.Contains(":user/"))
        {
            Console.WriteLine("  -> usuario IAM: credencial PERMANENTE. Num workload isso e o");
            Console.WriteLine("     que o guia 01 §4 chama de falha grave.");
        }

        // ---------------------------------------------------------------
        Secao("4. A regiao e uma segunda cadeia, independente");
        // ---------------------------------------------------------------
        // Erro comum: achar que credencial e regiao vem juntas. Credencial
        // resolve e a aplicacao estoura com "No RegionEndpoint or ServiceURL
        // configured" - que NAO e problema de IAM.
        // (A cadeia completa ainda olha o perfil do ~/.aws/config e, por
        // ultimo, o IMDS. Nao consultamos o IMDS aqui de proposito: fora de
        // uma EC2 essa chamada so gasta o timeout.)
        var regiao = Environment.GetEnvironmentVariable("AWS_REGION")
                     ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        Console.WriteLine($"  regiao resolvida: {regiao ?? "(nenhuma!)"}");
        if (regiao is null)
        {
            Console.WriteLine("  (!) sem AWS_REGION a primeira chamada estoura com");
            Console.WriteLine("      \"No RegionEndpoint or ServiceURL configured\".");
            Console.WriteLine("      No EKS o webhook do IRSA NAO injeta AWS_REGION - " +
                              "voce declara no Deployment.");
        }

        Console.WriteLine();
        return 0;
    }
}
