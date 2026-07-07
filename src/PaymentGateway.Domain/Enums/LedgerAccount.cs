namespace PaymentGateway.Domain.Enums;

/// <summary>
/// Contas do razão (ledger). Em cada movimento financeiro, a soma dos débitos
/// deve ser igual à soma dos créditos (partidas dobradas).
/// </summary>
public enum LedgerAccount
{
    /// <summary>Caixa do adquirente — dinheiro que entrou ao capturar a transação.</summary>
    AcquirerCash,

    /// <summary>Valor a repassar ao lojista (saldo dele no gateway).</summary>
    MerchantPayable,

    /// <summary>Receita do gateway — a taxa (MDR) cobrada por transação.</summary>
    AcquirerRevenue
}
