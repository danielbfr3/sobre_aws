-- Sequencial passa a ser por (Documento, Conta).
-- Ajuste nomes de constraint/índice conforme sql/004_cobranca_sequencial_arquivo.sql.

-- 1) Coluna nova
ALTER TABLE Cobranca.SequencialArquivo ADD Conta VARCHAR(10) NULL;
GO

-- 2) Backfill - DEFINIR COM O NEGÓCIO de onde parte o sequencial de cada conta.
--    Se o cliente já recebeu retornos, cada conta precisa continuar a numeração dele.
--    Exemplo para um CNPJ com uma única conta (herda o sequencial atual):
-- UPDATE Cobranca.SequencialArquivo SET Conta = '0000278221' WHERE Documento = '<cnpj>';
--    Linhas que ficarem sem conta precisam ser tratadas antes do passo 3.
GO

-- 3) Troca da chave
-- ALTER TABLE Cobranca.SequencialArquivo DROP CONSTRAINT <PK_ou_UQ_atual_em_Documento>;
ALTER TABLE Cobranca.SequencialArquivo ALTER COLUMN Conta VARCHAR(10) NOT NULL;
GO
ALTER TABLE Cobranca.SequencialArquivo
    ADD CONSTRAINT UQ_SequencialArquivo_Documento_Conta UNIQUE (Documento, Conta);
-- Se a PK atual for em Documento, recriar como PRIMARY KEY (Documento, Conta) em vez do UNIQUE.
GO
