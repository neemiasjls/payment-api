namespace PaymentGateway.Services;

/// <summary>Configurações do gateway, vindas do appsettings.json (seção "Gateway").</summary>
public class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>
    /// Taxa cobrada por transação em basis points (1 bps = 0,01%).
    /// 250 bps = 2,5%, próximo do MDR médio de cartão de crédito no Brasil.
    /// </summary>
    public int FeeBps { get; set; } = 250;
}
