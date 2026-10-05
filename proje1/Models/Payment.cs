namespace proje1.Models
{
    public class Payment
    {
        public int PaymentId { get; set; }
        public int OrderId { get; set; }
        public string PaymentMethod { get; set; } = "";
        public decimal Amount { get; set; }
        public string PaymentStatus { get; set; } = "";
        public DateTime PaymentDate { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}