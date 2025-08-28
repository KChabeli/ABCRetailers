using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Azure.Data.Tables;
using ABCRetailers.Models;
using Azure.Storage.Queues;

namespace ABCRetailers.Controllers
{
    public class OrderController : Controller
    {
        private readonly TableClient _orderTable;
        private readonly TableClient _customerTable;
        private readonly TableClient _productTable;
        private readonly QueueClient _eventsQueue;

        public OrderController(IConfiguration config)
        {
            string connectionString = config.GetConnectionString("Storage")
                                     ?? config["StorageConnectionString"];

            var serviceClient = new TableServiceClient(connectionString);

            _orderTable = serviceClient.GetTableClient("Orders");
            _orderTable.CreateIfNotExists();

            _customerTable = serviceClient.GetTableClient("Customers");
            _customerTable.CreateIfNotExists();

            _productTable = serviceClient.GetTableClient("Products");
            _productTable.CreateIfNotExists();

            // Queue client for order/inventory events
            _eventsQueue = new QueueClient(connectionString, "events");
            _eventsQueue.CreateIfNotExists();
        }

        // GET: /Order
        public async Task<IActionResult> Index()
        {
            var orders = new List<Order>();
            await foreach (var entity in _orderTable.QueryAsync<Order>(o => o.PartitionKey == "Order"))
            {
                orders.Add(entity);
            }

            // Populate customer and product names for display
            var customerNames = new Dictionary<string, string>();
            var productNames = new Dictionary<string, string>();

            try
            {
                // Get customer names
                await foreach (var customer in _customerTable.QueryAsync<Customer>(x => x.PartitionKey == "Customer"))
                {
                    customerNames[customer.RowKey] = $"{customer.FirstName} {customer.LastName}";
                }

                // Get product names
                await foreach (var product in _productTable.QueryAsync<Product>(x => x.PartitionKey == "Product"))
                {
                    productNames[product.RowKey] = product.ProductName;
                }
            }
            catch
            {
                // If there's an error fetching names, continue with empty dictionaries
            }

            ViewBag.CustomerNames = customerNames;
            ViewBag.ProductNames = productNames;

            return View(orders);
        }

        // GET: /Order/Create
        public async Task<IActionResult> Create()
        {
            ViewBag.Customers = await GetCustomerSelectList();
            ViewBag.Products = await GetProductSelectList();
            var order = new Order();
            // Ensure the default date is UTC
            EnsureDateTimeIsUtc(order);
            return View(order);
        }

        // POST: /Order/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Order model)
        {
            // Log the received model for debugging
            System.Diagnostics.Debug.WriteLine($"Received Order Model:");
            System.Diagnostics.Debug.WriteLine($"  Product ID: {model.ProductId}");
            System.Diagnostics.Debug.WriteLine($"  Unit Price: {model.UnitPrice}");
            System.Diagnostics.Debug.WriteLine($"  Quantity: {model.Quantity}");
            System.Diagnostics.Debug.WriteLine($"  Total Price: {model.TotalPrice}");

            if (!ModelState.IsValid)
            {
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Fetch product to get price
            var product = await _productTable.GetEntityAsync<Product>("Product", model.ProductId);
            if (product == null || product.Value == null)
            {
                ModelState.AddModelError("ProductId", "Selected product not found.");
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Validate that the product has a valid price
            if (product.Value.Price <= 0)
            {
                ModelState.AddModelError("ProductId", "Selected product has an invalid price.");
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Set the unit price from the product and calculate total
            model.UnitPrice = product.Value.Price;
            model.TotalPrice = model.UnitPrice * model.Quantity;

            // Ensure DateTime properties are UTC for Azure Table Storage
            EnsureDateTimeIsUtc(model);

            // Validate that the date is now properly UTC
            if (model.OrderDate.Kind != DateTimeKind.Utc)
            {
                ModelState.AddModelError("OrderDate", "Order date must be in UTC format for Azure storage.");
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            model.RowKey = Guid.NewGuid().ToString();
            model.PartitionKey = "Order";

            // Log the final order details before saving
            System.Diagnostics.Debug.WriteLine($"Final Order Details:");
            System.Diagnostics.Debug.WriteLine($"  Product ID: {model.ProductId}");
            System.Diagnostics.Debug.WriteLine($"  Unit Price: {model.UnitPrice}");
            System.Diagnostics.Debug.WriteLine($"  Quantity: {model.Quantity}");
            System.Diagnostics.Debug.WriteLine($"  Total Price: {model.TotalPrice}");

            await _orderTable.AddEntityAsync(model);

            // Reduce product stock and update
            var prod = await _productTable.GetEntityAsync<Product>("Product", model.ProductId);
            prod.Value.StockQuantity = Math.Max(0, prod.Value.StockQuantity - model.Quantity);
            await _productTable.UpsertEntityAsync(prod.Value);

            // Enqueue processing messages
            var msg = $"Processing order '{model.RowKey}' for product '{model.ProductId}' qty {model.Quantity}";
            await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(msg)));

            TempData["Message"] = "Order created successfully!";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Order/Edit/{id}
        public async Task<IActionResult> Edit(string id)
        {
            if (id == null) return BadRequest();

            var entity = await _orderTable.GetEntityAsync<Order>("Order", id);

            ViewBag.Customers = await GetCustomerSelectList();
            ViewBag.Products = await GetProductSelectList();

            return View(entity.Value);
        }

        // POST: /Order/Edit
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Order model)
        {
            // Log the received model for debugging
            System.Diagnostics.Debug.WriteLine($"Received Edit Order Model:");
            System.Diagnostics.Debug.WriteLine($"  Product ID: {model.ProductId}");
            System.Diagnostics.Debug.WriteLine($"  Unit Price: {model.UnitPrice}");
            System.Diagnostics.Debug.WriteLine($"  Quantity: {model.Quantity}");
            System.Diagnostics.Debug.WriteLine($"  Total Price: {model.TotalPrice}");

            if (!ModelState.IsValid)
            {
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Update price/total in case product/qty changed
            var product = await _productTable.GetEntityAsync<Product>("Product", model.ProductId);
            if (product == null || product.Value == null)
            {
                ModelState.AddModelError("ProductId", "Selected product not found.");
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Validate that the product has a valid price
            if (product.Value.Price <= 0)
            {
                ModelState.AddModelError("ProductId", "Selected product has an invalid price.");
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Set the unit price from the product and calculate total
            model.UnitPrice = product.Value.Price;
            model.TotalPrice = model.UnitPrice * model.Quantity;

            // Ensure DateTime properties are UTC for Azure Table Storage
            EnsureDateTimeIsUtc(model);

            // Validate that the date is now properly UTC
            if (model.OrderDate.Kind != DateTimeKind.Utc)
            {
                ModelState.AddModelError("OrderDate", "Order date must be in UTC format for Azure storage.");
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Validate that the price is set correctly
            if (model.UnitPrice <= 0)
            {
                ModelState.AddModelError("UnitPrice", "Product price must be greater than zero.");
                ViewBag.Customers = await GetCustomerSelectList();
                ViewBag.Products = await GetProductSelectList();
                return View(model);
            }

            // Log the final order details before saving
            System.Diagnostics.Debug.WriteLine($"Final Edit Order Details:");
            System.Diagnostics.Debug.WriteLine($"  Product ID: {model.ProductId}");
            System.Diagnostics.Debug.WriteLine($"  Unit Price: {model.UnitPrice}");
            System.Diagnostics.Debug.WriteLine($"  Quantity: {model.Quantity}");
            System.Diagnostics.Debug.WriteLine($"  Total Price: {model.TotalPrice}");

            await _orderTable.UpsertEntityAsync(model);

            // Recalculate inventory on edit
            var current = await _productTable.GetEntityAsync<Product>("Product", model.ProductId);
            // For simplicity, ensure stock is at least 0
            current.Value.StockQuantity = Math.Max(0, current.Value.StockQuantity);
            await _productTable.UpsertEntityAsync(current.Value);

            await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"Updated order '{model.RowKey}'")));
            TempData["Message"] = "Order updated successfully!";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Order/Details/{id}
        public async Task<IActionResult> Details(string id)
        {
            if (id == null) return BadRequest();

            var entity = await _orderTable.GetEntityAsync<Order>("Order", id);
            if (entity == null || entity.Value == null)
            {
                return NotFound();
            }

            // Fetch product details to show product name
            try
            {
                var product = await _productTable.GetEntityAsync<Product>("Product", entity.Value.ProductId);
                if (product != null && product.Value != null)
                {
                    ViewBag.ProductName = product.Value.ProductName;
                }
            }
            catch
            {
                // If product not found, just show the ID
                ViewBag.ProductName = entity.Value.ProductId;
            }

            // Fetch customer details to show customer name
            try
            {
                var customer = await _customerTable.GetEntityAsync<Customer>("Customer", entity.Value.CustomerId);
                if (customer != null && customer.Value != null)
                {
                    ViewBag.CustomerName = $"{customer.Value.FirstName} {customer.Value.LastName}";
                }
            }
            catch
            {
                // If customer not found, just show the ID
                ViewBag.CustomerName = entity.Value.CustomerId;
            }

            return View(entity.Value);
        }

        // POST: /Order/Delete/{id}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null) return BadRequest();

            await _orderTable.DeleteEntityAsync("Order", id);
            await _eventsQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"Deleted order '{id}'")));
            TempData["Message"] = "Order deleted successfully!";
            return RedirectToAction(nameof(Index));
        }

        // =====================
        // Helpers
        // =====================
        private void EnsureDateTimeIsUtc(Order order)
        {
            // Log the original date for debugging
            var originalDate = order.OrderDate;
            var originalKind = order.OrderDate.Kind;

            // Ensure DateTime properties are UTC for Azure Table Storage
            if (order.OrderDate.Kind == DateTimeKind.Unspecified)
            {
                order.OrderDate = DateTime.SpecifyKind(order.OrderDate, DateTimeKind.Utc);
            }
            else if (order.OrderDate.Kind == DateTimeKind.Local)
            {
                order.OrderDate = order.OrderDate.ToUniversalTime();
            }

            // Additional safety check - if somehow the date is still not UTC, force it
            if (order.OrderDate.Kind != DateTimeKind.Utc)
            {
                order.OrderDate = DateTime.SpecifyKind(order.OrderDate, DateTimeKind.Utc);
            }

            // Log the conversion for debugging
            if (originalKind != DateTimeKind.Utc)
            {
                System.Diagnostics.Debug.WriteLine($"DateTime converted from {originalKind} to UTC: {originalDate} -> {order.OrderDate}");
            }
        }

        private async Task<List<SelectListItem>> GetCustomerSelectList()
        {
            var customers = new List<Customer>();
            await foreach (var c in _customerTable.QueryAsync<Customer>(x => x.PartitionKey == "Customer"))
                customers.Add(c);

            return customers.Select(c => new SelectListItem
            {
                Value = c.RowKey,
                Text = $"{c.FirstName} {c.LastName} ({c.Email})"
            }).ToList();
        }

        private async Task<List<SelectListItem>> GetProductSelectList()
        {
            var products = new List<Product>();
            await foreach (var p in _productTable.QueryAsync<Product>(x => x.PartitionKey == "Product"))
                products.Add(p);

            return products.Select(p => new SelectListItem
            {
                Value = p.RowKey,
                Text = $"{p.ProductName} - {p.Price:C}"
            }).ToList();
        }
    }
}

