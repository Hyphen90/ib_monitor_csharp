namespace IBMonitor.Models
{
    public class ExecutionDetails
    {
        public int OrderId { get; set; }
        public decimal Quantity { get; set; }
        public double Price { get; set; }
        public DateTime ExecutionTime { get; set; }
    }
}
