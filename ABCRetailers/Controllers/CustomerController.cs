using Microsoft.AspNetCore.Mvc;
using Azure.Data.Tables;
using ABCRetailers.Models;

namespace ABCRetailers.Controllers
{
    public class CustomerController : Controller
    {
        private readonly TableClient _tableClient;

        public CustomerController(IConfiguration config)
        {
            // Get connection string from appsettings.json or Azure App Service config
            string connectionString = config.GetConnectionString("Storage")
                                     ?? config["StorageConnectionString"];

            // Table name = "Customers" (make sure to match your setup)
            var serviceClient = new TableServiceClient(connectionString);
            _tableClient = serviceClient.GetTableClient("Customers");
            _tableClient.CreateIfNotExists(); // creates table if not there
        }

        // GET: /Customer
        public async Task<IActionResult> Index()
        {
            // Query all customers (PartitionKey = "Customer")
            var customers = new List<Customer>();
            await foreach (var entity in _tableClient.QueryAsync<Customer>(c => c.PartitionKey == "Customer"))
            {
                customers.Add(entity);
            }
            return View(customers);
        }

        // GET: /Customer/Create
        public IActionResult Create()
        {
            return View(new Customer());
        }

        // POST: /Customer/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Customer model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // RowKey must be unique
            model.RowKey = Guid.NewGuid().ToString();
            model.PartitionKey = "Customer";

            await _tableClient.AddEntityAsync(model);
            TempData["Message"] = "Customer created successfully!";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Customer/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (id == null) return BadRequest();

            var entity = await _tableClient.GetEntityAsync<Customer>("Customer", id);
            return View(entity.Value);
        }

        // POST: /Customer/Edit
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Customer model)
        {
            if (!ModelState.IsValid)
                return View(model);

            await _tableClient.UpsertEntityAsync(model); // Update or Insert
            TempData["Message"] = "Customer updated successfully!";
            return RedirectToAction(nameof(Index));
        }

        // POST: /Customer/Delete/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null) return BadRequest();

            await _tableClient.DeleteEntityAsync("Customer", id);
            TempData["Message"] = "Customer deleted successfully!";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Customer/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (id == null) return BadRequest();

            var entity = await _tableClient.GetEntityAsync<Customer>("Customer", id);
            return View(entity.Value);
        }
    }
}

