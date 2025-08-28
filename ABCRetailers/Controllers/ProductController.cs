using Microsoft.AspNetCore.Mvc;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using ABCRetailers.Models;
using Azure.Storage.Queues;
using Azure.Storage.Blobs.Models;

namespace ABCRetailers.Controllers
{
    public class ProductController : Controller
    {
        private readonly TableClient _tableClient;
        private readonly BlobContainerClient _blobContainer;
        private readonly QueueClient _eventsQueue;

        public ProductController(IConfiguration config)
        {
            string connectionString = config.GetConnectionString("Storage")
                                        ?? config["StorageConnectionString"];

            // Table
            var serviceClient = new TableServiceClient(connectionString);
            _tableClient = serviceClient.GetTableClient("Products");
            _tableClient.CreateIfNotExists();

            // Blob (for product images)
            var blobServiceClient = new BlobServiceClient(connectionString);
            _blobContainer = blobServiceClient.GetBlobContainerClient("product-images");
            _blobContainer.CreateIfNotExists(PublicAccessType.Blob);

            // Queue for events (e.g., uploads)
            _eventsQueue = new QueueClient(connectionString, "events");
            _eventsQueue.CreateIfNotExists();
        }

        // GET: /Product
        public async Task<IActionResult> Index()
        {
            var products = new List<Product>();
            await foreach (var product in _tableClient.QueryAsync<Product>(x => x.PartitionKey == "Product"))
            {
                products.Add(product);
            }

            return View(products);
        }

        // GET: /Product/Create
        public IActionResult Create()
        {
            return View(new Product());
        }

        // POST: /Product/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Product model, IFormFile? imageFile)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Upload image if provided
            if (imageFile != null && imageFile.Length > 0)
            {
                var blobClient = _blobContainer.GetBlobClient(imageFile.FileName);
                using var stream = imageFile.OpenReadStream();
                await blobClient.UploadAsync(stream, overwrite: true);
                model.ImageUrl = blobClient.Uri.ToString();

                // Enqueue event for observability
                var msg = $"Uploading image '{imageFile.FileName}' for product '{model.ProductName}'";
                await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(msg)));
            }

            model.RowKey = Guid.NewGuid().ToString();
            model.PartitionKey = "Product";

            await _tableClient.AddEntityAsync(model);
            await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"Created product '{model.ProductName}' ({model.RowKey})")));
            TempData["Message"] = "Product created successfully!";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Product/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (id == null) return BadRequest();

            var entity = await _tableClient.GetEntityAsync<Product>("Product", id);
            return View(entity.Value);
        }

        // POST: /Product/Edit
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Product model, IFormFile? imageFile)
        {
            if (!ModelState.IsValid)
                return View(model);

            // If new image uploaded, replace existing
            if (imageFile != null && imageFile.Length > 0)
            {
                var blobClient = _blobContainer.GetBlobClient(imageFile.FileName);
                using var stream = imageFile.OpenReadStream();
                await blobClient.UploadAsync(stream, overwrite: true);
                model.ImageUrl = blobClient.Uri.ToString();

                var msg = $"Replaced image with '{imageFile.FileName}' for product '{model.ProductName}'";
                await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(msg)));
            }

            await _tableClient.UpsertEntityAsync(model);
            await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"Updated product '{model.ProductName}' ({model.RowKey})")));
            TempData["Message"] = "Product updated successfully!";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Product/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (id == null) return BadRequest();

            var entity = await _tableClient.GetEntityAsync<Product>("Product", id);
            return View(entity.Value);
        }

        // POST: /Product/Delete/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null) return BadRequest();

            await _tableClient.DeleteEntityAsync("Product", id);
            await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"Deleted product '{id}'")));
            TempData["Message"] = "Product deleted successfully!";
            return RedirectToAction(nameof(Index));
        }
    }
}