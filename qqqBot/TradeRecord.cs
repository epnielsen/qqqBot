
// CSV record for report export
class TradeRecord
{
    public string Timestamp { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? FilledPrice { get; set; }
    public decimal? FilledValue { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
