using Microsoft.AspNetCore.Mvc;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ABCRetailers.Models;
using Azure.Storage.Queues;
using System.Text;

namespace ABCRetailers.Controllers
{
    public class UploadController : Controller
    {
        private readonly BlobContainerClient _blobContainer;
        private readonly QueueClient _eventsQueue;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;

        public UploadController(IConfiguration config, IWebHostEnvironment environment)
        {
            _configuration = config;
            _environment = environment;

            string connectionString = config.GetConnectionString("Storage")
                                     ?? config["StorageConnectionString"];

            // Blob container for file uploads
            var blobServiceClient = new BlobServiceClient(connectionString);
            _blobContainer = blobServiceClient.GetBlobContainerClient("file-uploads");
            _blobContainer.CreateIfNotExists(PublicAccessType.Blob);

            // Queue for file upload events
            _eventsQueue = new QueueClient(connectionString, "file-events");
            _eventsQueue.CreateIfNotExists();
        }

        // GET: /Upload
        public IActionResult Index()
        {
            return View(new FileUploadModel());
        }

        // POST: /Upload
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(FileUploadModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            try
            {
                var result = await UploadFileAsync(model);

                if (result.Success)
                {
                    TempData["SuccessMessage"] = $"File '{result.FileName}' uploaded successfully!";
                    TempData["FileUrl"] = result.FileUrl;
                    return RedirectToAction(nameof(Success));
                }
                else
                {
                    ModelState.AddModelError("", result.Message);
                    return View(model);
                }
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Upload failed: {ex.Message}");
                return View(model);
            }
        }

        // GET: /Upload/Success
        public IActionResult Success()
        {
            return View();
        }

        // GET: /Upload/Files
        public async Task<IActionResult> Files()
        {
            var files = new List<FileUploadModel>();

            try
            {
                await foreach (var blobItem in _blobContainer.GetBlobsAsync())
                {
                    var blobClient = _blobContainer.GetBlobClient(blobItem.Name);
                    var properties = await blobClient.GetPropertiesAsync();

                    files.Add(new FileUploadModel
                    {
                        FileName = blobItem.Name,
                        FileSize = properties.Value.ContentLength,
                        ContentType = properties.Value.ContentType,
                        FileUrl = blobClient.Uri.ToString(),
                        UploadDate = properties.Value.CreatedOn.UtcDateTime,
                        Category = properties.Value.Metadata.ContainsKey("Category") ? properties.Value.Metadata["Category"] : "",
                        Tags = properties.Value.Metadata.ContainsKey("Tags") ? properties.Value.Metadata["Tags"] : "",
                        Description = properties.Value.Metadata.ContainsKey("Description") ? properties.Value.Metadata["Description"] : ""
                    });
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Error retrieving files: {ex.Message}";
            }

            return View(files);
        }

        // GET: /Upload/Download/{fileName}
        public async Task<IActionResult> Download(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return BadRequest();

            try
            {
                var blobClient = _blobContainer.GetBlobClient(fileName);
                var properties = await blobClient.GetPropertiesAsync();

                var stream = await blobClient.OpenReadAsync();
                return File(stream, properties.Value.ContentType, fileName);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Download failed: {ex.Message}";
                return RedirectToAction(nameof(Files));
            }
        }

        // POST: /Upload/Delete/{fileName}
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return BadRequest();

            try
            {
                var blobClient = _blobContainer.GetBlobClient(fileName);
                await blobClient.DeleteIfExistsAsync();

                // Enqueue deletion event
                var msg = $"File '{fileName}' deleted";
                await _eventsQueue.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(msg)));

                TempData["SuccessMessage"] = $"File '{fileName}' deleted successfully!";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Delete failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Files));
        }

        private async Task<FileUploadResult> UploadFileAsync(FileUploadModel model)
        {
            var result = new FileUploadResult();

            try
            {
                // Validate file
                if (model.File == null || model.File.Length == 0)
                {
                    result.Success = false;
                    result.Message = "No file selected";
                    return result;
                }

                // Check file size (limit to 10MB)
                if (model.File.Length > 10 * 1024 * 1024)
                {
                    result.Success = false;
                    result.Message = "File size cannot exceed 10MB";
                    return result;
                }

                // Validate file type
                var allowedExtensions = new[] { ".pdf", ".doc", ".docx", ".txt", ".jpg", ".jpeg", ".png", ".gif", ".xlsx", ".xls" };
                var fileExtension = Path.GetExtension(model.File.FileName).ToLowerInvariant();

                if (!allowedExtensions.Contains(fileExtension))
                {
                    result.Success = false;
                    result.Message = "File type not allowed. Allowed types: PDF, DOC, DOCX, TXT, JPG, PNG, GIF, XLSX, XLS";
                    return result;
                }

                // Generate unique filename
                var fileName = $"{Guid.NewGuid()}_{Path.GetFileName(model.File.FileName)}";
                var blobClient = _blobContainer.GetBlobClient(fileName);

                // Set metadata
                var metadata = new Dictionary<string, string>
                {
                    { "OriginalFileName", model.File.FileName },
                    { "ContentType", model.File.ContentType },
                    { "UploadDate", DateTime.UtcNow.ToString("O") },
                    { "Category", model.Category },
                    { "Tags", model.Tags },
                    { "Description", model.Description }
                };

                // Upload file to blob storage
                using var stream = model.File.OpenReadStream();
                await blobClient.UploadAsync(stream, overwrite: true);

                // Set metadata after upload
                await blobClient.SetMetadataAsync(metadata);

                // Enqueue upload event
                var msg = $"File '{model.File.FileName}' uploaded as '{fileName}'";
                await _eventsQueue.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(msg)));

                // Set result
                result.Success = true;
                result.Message = "File uploaded successfully";
                result.FileUrl = blobClient.Uri.ToString();
                result.FileName = model.File.FileName;
                result.FileSize = model.File.Length;
                result.ContentType = model.File.ContentType;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Upload failed: {ex.Message}";
            }

            return result;
        }
    }
}

