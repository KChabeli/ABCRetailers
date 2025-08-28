//Models/Prodcut.cs
using Azure;
using Azure.Data.Tables;    
using System.ComponentModel.DataAnnotations;

namespace ABCRetailers.Models
{
    public class Product : ITableEntity
    {
        public string PartitionKey { get; set; } = "Product";
        public string RowKey { get; set; } = Guid.NewGuid().ToString();
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        [Display(Name = "Product ID")]
        public string ProductId => RowKey;
        
        [Required(ErrorMessage = "Product name is required")]
        [Display(Name = "Product Name")]
        public string ProductName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Description is required")]
        [Display(Name = "Description")]
        public string Description { get; set; } = string.Empty;

        [Required(ErrorMessage = "Price is required")]
        [Range(0.01, double.MaxValue, ErrorMessage = "Price must be greater than zero")]
        [Display(Name = "Price")]
        public decimal Price { get; set; }

        //Used internally for Azure Table storage string conversion if needed

        public string PriceString
        {
            get => Price.ToString("F2");
            set
            {
                if (decimal.TryParse(value, out var result))
                    Price = result;
                else
                    Price = 0m;
            }
        }

        [Required(ErrorMessage = "Stock quantity is required")]
        [Display(Name = "Stock quantity")]
        public int StockQuantity { get; set; }
        [Display(Name = "Image URL")]
        public string ImageUrl { get; set; } = string.Empty;
    }
}
