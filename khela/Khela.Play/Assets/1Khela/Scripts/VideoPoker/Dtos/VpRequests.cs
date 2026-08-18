namespace PlayCard.VideoPoker.Dtos
{
    /// <summary>Start a hand. The stake (<c>Coins × Denomination</c>) is debited from the AUTHORITATIVE wallet — the
    /// client never sends a balance. <see cref="ClientRequestId"/> makes a retried deal idempotent (no double-charge on
    /// a flaky network); set it to a fresh GUID per intended deal.</summary>
    public sealed class DealVpRequest
    {
        public string VariantId { get; set; }
        public int Coins { get; set; }
        public decimal Denomination { get; set; }
        public string ClientSeed { get; set; }
        public string ClientRequestId { get; set; }
    }

    /// <summary>Complete a hand: the length-5 hold mask (true = keep that dealt card). The server draws replacements.</summary>
    public sealed class DrawVpRequest
    {
        public string HandId { get; set; }
        public bool[] Hold { get; set; }
    }
}
