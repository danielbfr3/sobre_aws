// Package awsx concentra como este lab constroi configuracao do AWS SDK for
// Go v2.
//
// Regra que atravessa as tres linguagens: NENHUMA credencial no codigo.
// O que muda entre local, EKS, ECS e Lambda e o AMBIENTE.
package awsx

import (
	"context"
	"os"
	"strings"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/config"
	"github.com/aws/aws-sdk-go-v2/service/secretsmanager"
	"github.com/aws/aws-sdk-go-v2/service/sns"
	"github.com/aws/aws-sdk-go-v2/service/sqs"
	"github.com/aws/aws-sdk-go-v2/service/sts"
)

// Endpoint devolve o endpoint do emulador, ou "" em AWS de verdade.
//
// O SDK Go v2 recente ja le AWS_ENDPOINT_URL sozinho, mas a versao em que
// isso passou a valer varia entre SDKs. Ler a variavel na mao e passar
// explicitamente deixa as tres linguagens deste lab com o mesmo
// comportamento, independente da versao instalada.
func Endpoint() string {
	return strings.TrimSpace(os.Getenv("AWS_ENDPOINT_URL"))
}

// Config monta a configuracao padrao.
//
// config.LoadDefaultConfig e o equivalente Go do FallbackCredentialsFactory
// do .NET e da cadeia do botocore: ele percorre os provedores em ordem e para
// no primeiro que responder. Repare no que NAO passamos: nenhum
// aws.Credentials, nenhum profile fixo, nenhum if de ambiente.
func Config(ctx context.Context) (aws.Config, error) {
	return config.LoadDefaultConfig(ctx)
}

// A partir daqui, um construtor por servico. Todos aplicam BaseEndpoint da
// mesma forma - e nenhum recebe credencial.
//
// Guarde o client que sai daqui: no Go v2 o cache de credenciais mora no
// aws.Config (dentro do CredentialsCache). Recriar a config por mensagem
// forca um AssumeRoleWithWebIdentity novo a cada vez.

func endpointOuNil() *string {
	if ep := Endpoint(); ep != "" {
		return aws.String(ep)
	}
	return nil // nil = "resolva o endpoint real da regiao"
}

func SQS(cfg aws.Config) *sqs.Client {
	return sqs.NewFromConfig(cfg, func(o *sqs.Options) { o.BaseEndpoint = endpointOuNil() })
}

func SNS(cfg aws.Config) *sns.Client {
	return sns.NewFromConfig(cfg, func(o *sns.Options) { o.BaseEndpoint = endpointOuNil() })
}

func STS(cfg aws.Config) *sts.Client {
	return sts.NewFromConfig(cfg, func(o *sts.Options) { o.BaseEndpoint = endpointOuNil() })
}

func SecretsManager(cfg aws.Config) *secretsmanager.Client {
	return secretsmanager.NewFromConfig(cfg, func(o *secretsmanager.Options) {
		o.BaseEndpoint = endpointOuNil()
	})
}
