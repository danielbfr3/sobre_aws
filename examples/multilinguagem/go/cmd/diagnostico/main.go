// Diagnostico da cadeia de credenciais - Go / aws-sdk-go-v2.
//
// O equivalente do examples/dotnet-credenciais do guia 03, em Go.
// Responde duas perguntas, nesta ordem:
//
//  1. QUAL PROVEDOR DA CADEIA VENCEU?
//
//  2. QUEM EU SOU, na visao da AWS?
//
//     ./run.sh diagnostico go
//
// Equivalentes: ../../python/diagnostico.py e ../../dotnet/Diagnostico.cs.
package main

import (
	"context"
	"fmt"
	"os"
	"strings"

	"github.com/aws/aws-sdk-go-v2/service/sts"

	"lab/awsx"
)

// No SDK Go v2 o provedor vencedor se identifica pelo campo Source do
// aws.Credentials. Cada provedor tem um nome constante proprio.
var provedores = map[string]string{
	"EnvConfigCredentials":        "variaveis de ambiente (AWS_ACCESS_KEY_ID...)",
	"SharedConfigCredentials":     "~/.aws/credentials ou ~/.aws/config",
	"AssumeRoleProvider":          "perfil com role_arn + source_profile",
	"WebIdentityCredentials":      "IRSA / web identity (EKS)",
	"SSOProvider":                 "AWS SSO / IAM Identity Center",
	"EndpointCredentialsProvider": "endpoint de container (ECS ou EKS Pod Identity)",
	"EC2RoleProvider":             "IMDS - a role do NO EC2, nao a do seu workload",
	"ProcessProvider":             "credential_process",
	"StaticCredentials":           "credencial fixa passada no codigo",
}

var interessantes = []string{
	"AWS_ACCESS_KEY_ID",
	"AWS_SECRET_ACCESS_KEY",
	"AWS_SESSION_TOKEN",
	"AWS_PROFILE",
	"AWS_ROLE_ARN",
	"AWS_WEB_IDENTITY_TOKEN_FILE",
	"AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
	"AWS_CONTAINER_CREDENTIALS_FULL_URI",
	"AWS_REGION",
	"AWS_DEFAULT_REGION",
	"AWS_ENDPOINT_URL",
}

func secao(titulo string) { fmt.Printf("\n--- %s\n", titulo) }

func main() {
	ctx := context.Background()

	fmt.Println(strings.Repeat("=", 67))
	fmt.Println(" Cadeia de credenciais - Go / aws-sdk-go-v2")
	fmt.Println(strings.Repeat("=", 67))

	// -----------------------------------------------------------------------
	secao("1. O ambiente")
	// -----------------------------------------------------------------------
	presentes := map[string]string{}
	for _, nome := range interessantes {
		if v := os.Getenv(nome); v != "" {
			presentes[nome] = v
			mostrado := v
			// Nunca imprima segredo inteiro, nem em ferramenta de diagnostico.
			if strings.Contains(nome, "SECRET") || strings.Contains(nome, "TOKEN") {
				mostrado = "***"
			}
			fmt.Printf("  %s=%s\n", nome, mostrado)
		}
	}
	if len(presentes) == 0 {
		fmt.Println("  nenhuma variavel AWS_* definida")
	}

	// O bug do guia 03 §2, detectado antes de qualquer chamada.
	if presentes["AWS_ACCESS_KEY_ID"] != "" && presentes["AWS_ROLE_ARN"] != "" {
		fmt.Println()
		fmt.Println("  (!) AWS_ACCESS_KEY_ID e AWS_ROLE_ARN presentes ao mesmo tempo.")
		fmt.Println("      Variavel de ambiente vence IRSA na cadeia. O IRSA esta sendo IGNORADO.")
	}

	// -----------------------------------------------------------------------
	secao("2. Qual provedor venceu")
	// -----------------------------------------------------------------------
	cfg, err := awsx.Config(ctx)
	if err != nil {
		fmt.Printf("  nao consegui montar a configuracao: %v\n", err)
		os.Exit(1)
	}

	// Retrieve() e o momento em que a cadeia REALMENTE decide. Ate aqui ela
	// so foi montada - o SDK Go v2 tambem e preguicoso, como o .NET.
	cred, err := cfg.Credentials.Retrieve(ctx)
	if err != nil {
		fmt.Printf("  NENHUMA credencial encontrada na cadeia: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("  Source .......: %s\n", cred.Source)
	significa, ok := provedores[cred.Source]
	if !ok {
		significa = "provedor nao mapeado"
	}
	fmt.Printf("  significa ....: %s\n", significa)
	fmt.Printf("  expira .......: %v\n", descreverExpiracao(cred.CanExpire, cred.Expires.String()))
	fmt.Printf("  session token : %s\n", simNao(cred.SessionToken != ""))

	// A presenca do SessionToken e o sinal de que a credencial e TEMPORARIA,
	// emitida pelo STS. Sem ele, e credencial permanente de usuario IAM - o
	// que o guia 01 §4 chama de falha grave num workload.
	if cred.SessionToken == "" && cred.Source != "StaticCredentials" {
		fmt.Println("  (!) sem SessionToken: credencial PERMANENTE.")
	}

	if strings.Contains(cred.Source, "EC2Role") {
		fmt.Println()
		fmt.Println("  (!) IMDS venceu. Num pod do EKS isso significa que voce esta")
		fmt.Println("      usando a ROLE DO NO, nao a do seu ServiceAccount.")
	}

	// -----------------------------------------------------------------------
	secao("3. Quem eu sou (sts:GetCallerIdentity)")
	// -----------------------------------------------------------------------
	eu, err := awsx.STS(cfg).GetCallerIdentity(ctx, &sts.GetCallerIdentityInput{})
	if err != nil {
		fmt.Printf("  falhou: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("  Account ......: %s\n", *eu.Account)
	fmt.Printf("  Arn ..........: %s\n", *eu.Arn)
	fmt.Printf("  UserId .......: %s\n", *eu.UserId)
	fmt.Println()
	switch {
	case strings.Contains(*eu.Arn, ":assumed-role/"):
		fmt.Println("  -> assumed-role: algum AssumeRole funcionou. Confira se o nome")
		fmt.Println("     da role e o do SEU workload e nao o do nodegroup.")
	case strings.Contains(*eu.Arn, ":user/"):
		fmt.Println("  -> usuario IAM: credencial PERMANENTE. Num workload isso e o")
		fmt.Println("     que o guia 01 §4 chama de falha grave.")
	}

	// -----------------------------------------------------------------------
	secao("4. A regiao e uma segunda cadeia, independente")
	// -----------------------------------------------------------------------
	// Erro comum: achar que credencial e regiao vem juntas. Credencial resolve
	// e a aplicacao estoura com "an AWS region is required" - que nao e
	// problema de IAM.
	if cfg.Region == "" {
		fmt.Println("  regiao resolvida: (nenhuma!)")
		fmt.Println("  (!) sem AWS_REGION a primeira chamada estoura por falta de regiao.")
		fmt.Println("      No EKS o webhook do IRSA NAO injeta AWS_REGION - " +
			"voce declara no Deployment.")
	} else {
		fmt.Printf("  regiao resolvida: %s\n", cfg.Region)
	}
	fmt.Println()
}

func simNao(b bool) string {
	if b {
		return "sim (credencial temporaria, do STS)"
	}
	return "nao"
}

func descreverExpiracao(podeExpirar bool, quando string) string {
	if !podeExpirar {
		return "nunca (credencial estatica)"
	}
	return quando
}
