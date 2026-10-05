namespace proje1.Models
{
    public class Product
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal Price { get; set; }
        public int StockQuantity { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}