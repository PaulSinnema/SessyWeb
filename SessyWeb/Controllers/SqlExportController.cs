using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SessyWeb.Controllers
{
    [ApiController]
    [Route("api/sqlexport")]
    public class SqlExportController : ControllerBase
    {
        private readonly IMemoryCache _cache;

        public SqlExportController(IMemoryCache cache)
        {
            _cache = cache;
        }

        [HttpGet("file/{token}")]
        public IActionResult DownloadFile(string token)
        {
            if (!_cache.TryGetValue<byte[]>(token, out var bytes) || bytes == null)
                return NotFound("Export token expired or not found.");

            // Remove from cache after serving — single use.
            _cache.Remove(token);

            var contentType = token.EndsWith(".xlsx")
                ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                : "text/csv";

            var fileName = token.EndsWith(".xlsx") ? "query_export.xlsx" : "query_export.csv";

            return File(bytes, contentType, fileName);
        }
    }
}