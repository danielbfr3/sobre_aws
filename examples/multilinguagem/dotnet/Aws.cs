// Como este lab constroi clients do AWS SDK for .NET.
//
// Regra que atravessa as tres linguagens: NENHUMA credencial no construtor.
// O que muda entre local, EKS, ECS e Lambda e o AMBIENTE, nunca o codigo -
// e e por isso que este mesmo binario roda nos quatro lugares sem um if.

using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecurityToken;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace Lab;

public static class Aws
{
    /// <summary>
    /// O endpoint do emulador, ou null em AWS de verdade.
    ///
    /// Diferente do boto3 e do SDK Go, o AWS SDK for .NET so ganhou suporte a
    /// AWS_ENDPOINT_URL bem depois. Ler a variavel na mao e atribuir a
    /// ServiceURL deixa as tres linguagens deste lab com o mesmo
    /// comportamento, independente da versao do pacote.
    /// </summary>
    public static string? Endpoint =>
        Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL") is { Length: > 0 } e
            ? e.Trim()
            : null;

    private static string Regiao =>
        Environment.GetEnvironmentVariable("AWS_REGION")
        ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
        ?? "us-east-1";

    /// <summary>
    /// Aplica o endpoint local, quando ele existe.
    ///
    /// AuthenticationRegion e obrigatorio junto com ServiceURL: sem regiao o
    /// SDK nao consegue montar a assinatura SigV4. E o mesmo sintoma do guia
    /// 03 §5 - "No RegionEndpoint or ServiceURL configured" nao e problema de
    /// IAM, e regiao faltando. Sao duas cadeias independentes.
    /// </summary>
    private static T Ajustar<T>(T config) where T : ClientConfig
    {
        if (Endpoint is { } url)
        {
            config.ServiceURL = url;
            config.AuthenticationRegion = Regiao;
        }
        return config;
    }

    // Um construtor por servico. Nenhum recebe AWSCredentials.
    //
    // Guarde o client que sai daqui - o cache de credenciais mora DENTRO do
    // objeto de credenciais, que mora dentro do client. Criar um client por
    // mensagem joga esse cache fora e forca um AssumeRoleWithWebIdentity novo
    // a cada vez: latencia, throttling do STS e milhares de eventos no
    // CloudTrail por hora (guia 03 §4).
    public static IAmazonSQS Sqs() => new AmazonSQSClient(Ajustar(new AmazonSQSConfig()));

    public static IAmazonSimpleNotificationService Sns() =>
        new AmazonSimpleNotificationServiceClient(Ajustar(new AmazonSimpleNotificationServiceConfig()));

    public static IAmazonSecurityTokenService Sts() =>
        new AmazonSecurityTokenServiceClient(Ajustar(new AmazonSecurityTokenServiceConfig()));

    public static IAmazonSecretsManager SecretsManager() =>
        new AmazonSecretsManagerClient(Ajustar(new AmazonSecretsManagerConfig()));
}
