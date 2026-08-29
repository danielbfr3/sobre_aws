// Secrets Manager com cache e TTL - Go / aws-sdk-go-v2.  (guia 04)
//
// Demonstra, contra o Floci, as duas coisas que o guia 04 §3 diz que quase
// toda primeira implementacao erra:
//
//  1. Buscar o segredo A CADA USO. E chamada paga, tem limite de taxa, e sob
//     carga o worker cai por nao conseguir ler uma senha.
//
//  2. Cachear PARA SEMPRE. Funciona por semanas e quebra numa madrugada de
//     rotacao, voltando so com restart.
//
//     ./run.sh segredos go
//
// Equivalentes: ../../python/segredos.py e ../../dotnet/Segredos.cs.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"strings"
	"sync"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/secretsmanager"
	smtypes "github.com/aws/aws-sdk-go-v2/service/secretsmanager/types"

	"lab/awsx"
)

const (
	segredoID = "asa/dev/cash-cobranca/postgres"
	ttl       = 5 * time.Second // no lab, 5s. Em producao, minutos - e MENOR que a rotacao.
)

type credenciaisBanco struct {
	Host     string `json:"host"`
	Port     int    `json:"port"`
	Username string `json:"username"`
	Password string `json:"password"`
}

type entrada struct {
	valor    credenciaisBanco
	expiraEm time.Time
}

// SegredoProvider e o cache com TTL sobre o Secrets Manager.
//
// O equivalente do ISegredoProvider do guia 04 §7. A interface importa mais
// que a implementacao: em teste local voce troca por uma versao que le de um
// arquivo, e o codigo de negocio nao muda.
//
// O mutex existe porque o worker e concorrente: sem ele, duas goroutines
// perdendo o TTL ao mesmo tempo fariam duas chamadas ao Secrets Manager.
type SegredoProvider struct {
	cliente      *secretsmanager.Client
	ttl          time.Duration
	mu           sync.Mutex
	cache        map[string]entrada
	ChamadasAaws int
}

func NovoProvider(c *secretsmanager.Client, ttl time.Duration) *SegredoProvider {
	return &SegredoProvider{cliente: c, ttl: ttl, cache: map[string]entrada{}}
}

func (p *SegredoProvider) Obter(ctx context.Context, id string) (credenciaisBanco, error) {
	p.mu.Lock()
	defer p.mu.Unlock()

	if e, ok := p.cache[id]; ok && time.Now().Before(e.expiraEm) {
		return e.valor, nil
	}

	p.ChamadasAaws++
	saida, err := p.cliente.GetSecretValue(ctx, &secretsmanager.GetSecretValueInput{
		SecretId: aws.String(id),
	})
	if err != nil {
		return credenciaisBanco{}, err
	}

	var v credenciaisBanco
	if err := json.Unmarshal([]byte(aws.ToString(saida.SecretString)), &v); err != nil {
		return credenciaisBanco{}, err
	}
	p.cache[id] = entrada{valor: v, expiraEm: time.Now().Add(p.ttl)}
	return v, nil
}

// Invalidar deve ser chamado quando o BANCO recusar a senha - e so uma vez.
//
// Cobre a janela entre a rotacao acontecer e o TTL expirar. Repetir
// indefinidamente com senha errada bloqueia a conta no banco, entao a regra
// e: invalida, tenta MAIS UMA vez, e se falhar propaga o erro.
func (p *SegredoProvider) Invalidar(id string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	delete(p.cache, id)
}

func preparar(ctx context.Context, c *secretsmanager.Client) error {
	valor := `{"host":"postgres","port":5432,"username":"app","password":"senha-v1"}`
	_, err := c.CreateSecret(ctx, &secretsmanager.CreateSecretInput{
		Name: aws.String(segredoID), SecretString: aws.String(valor),
	})
	if err == nil {
		fmt.Printf("  segredo %s criado\n", segredoID)
		return nil
	}

	var jaExiste *smtypes.ResourceExistsException
	if !errors.As(err, &jaExiste) {
		return err
	}
	_, err = c.PutSecretValue(ctx, &secretsmanager.PutSecretValueInput{
		SecretId: aws.String(segredoID), SecretString: aws.String(valor),
	})
	if err == nil {
		fmt.Printf("  segredo %s ja existia, valor restaurado\n", segredoID)
	}
	return err
}

func main() {
	ctx := context.Background()
	cfg, err := awsx.Config(ctx)
	if err != nil {
		log.Fatal(err)
	}
	sm := awsx.SecretsManager(cfg)

	fmt.Println(strings.Repeat("=", 67))
	fmt.Println(" Secrets Manager: cache com TTL - Go / aws-sdk-go-v2")
	fmt.Println(strings.Repeat("=", 67))
	fmt.Println()
	if err := preparar(ctx, sm); err != nil {
		log.Fatal(err)
	}

	p := NovoProvider(sm, ttl)

	fmt.Printf("\n--- 1. Cinco leituras seguidas, TTL de %s\n", ttl)
	for i := 1; i <= 5; i++ {
		v, err := p.Obter(ctx, segredoID)
		if err != nil {
			log.Fatal(err)
		}
		fmt.Printf("  leitura %d: password=%s   (idas a AWS ate agora: %d)\n",
			i, v.Password, p.ChamadasAaws)
		time.Sleep(400 * time.Millisecond)
	}
	fmt.Println("\n  -> 5 leituras, 1 ida a AWS. Sem cache seriam 5 GetSecretValue:")
	fmt.Println("     5x o custo, 5x a latencia, e 5 eventos no CloudTrail.")

	fmt.Println("\n--- 2. A rotacao acontece enquanto o processo esta no ar")
	if _, err := sm.PutSecretValue(ctx, &secretsmanager.PutSecretValueInput{
		SecretId: aws.String(segredoID),
		SecretString: aws.String(
			`{"host":"postgres","port":5432,"username":"app","password":"senha-v2-ROTACIONADA"}`),
	}); err != nil {
		log.Fatal(err)
	}
	fmt.Println("  senha rotacionada no Secrets Manager (novo AWSCURRENT)")
	v, _ := p.Obter(ctx, segredoID)
	fmt.Printf("  leitura logo em seguida: password=%s\n", v.Password)
	fmt.Println("  -> o cache quente ainda devolve a senha VELHA. Ate aqui, tudo bem:")
	fmt.Println("     e a janela do TTL, e ela e limitada de proposito.")

	fmt.Println("\n--- 3. O banco recusa a senha -> invalidar e tentar UMA vez")
	p.Invalidar(segredoID)
	v, _ = p.Obter(ctx, segredoID)
	fmt.Printf("  apos invalidar: password=%s   (idas a AWS: %d)\n", v.Password, p.ChamadasAaws)

	fmt.Printf("\n--- 4. Ou simplesmente esperar o TTL (%s)\n", ttl)
	fmt.Println("  aguardando...")
	time.Sleep(ttl + 500*time.Millisecond)
	v, _ = p.Obter(ctx, segredoID)
	fmt.Printf("  apos o TTL: password=%s   (idas a AWS: %d)\n", v.Password, p.ChamadasAaws)

	fmt.Println()
	fmt.Println("  Um cache ETERNO (populado uma vez na subida) travaria na")
	fmt.Println("  senha-v1 para sempre. O worker so voltaria com restart - e o")
	fmt.Println("  incidente aconteceria de madrugada, no dia da rotacao.")
	fmt.Println()
}
