using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ABCRetailers.Models;
using Azure.Data.Tables;

namespace ABCRetailers.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly TableClient _productTable;

    public HomeController(ILogger<HomeController> logger, IConfiguration config)
    {
        _logger = logger;

        // Initialize Azure Table Storage client for products
        string connectionString = config.GetConnectionString("Storage")
                                     ?? config["StorageConnectionString"];

        var serviceClient = new TableServiceClient(connectionString);
        _productTable = serviceClient.GetTableClient("Products");
        _productTable.CreateIfNotExists();
    }

    public async Task<IActionResult> Index()
    {
        var products = new List<Product>();

        try
        {
            // Fetch all products from Azure Table Storage
            await foreach (var entity in _productTable.QueryAsync<Product>(x => x.PartitionKey == "Product"))
            {
                products.Add(entity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching products for home page");
            // Continue with empty list if there's an error
        }

        return View(products);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
