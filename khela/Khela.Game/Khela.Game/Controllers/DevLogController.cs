using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// DEV client-log sink. A development-build device (client <c>DevLogRecorder</c>) POSTs its captured logs here as
    /// raw text and the server writes them to a fixed directory for inspection.
    ///
    /// Deliberately <see cref="AllowAnonymousAttribute"/>: the device's OWN auth may be exactly what's being debugged
    /// (a broken/absent JWT can't be required to upload the log that explains why it's broken). It is kept safe by
    /// four guards so an open write endpoint on a live server can't be abused for more than dropping bounded .log
    /// files: a config on/off switch (<c>DevLog:Enabled</c>), a shared dev-key header, a hard body-size cap, a
    /// SERVER-GENERATED filename (client input never touches the path — no traversal, only <c>*.log</c> in one dir),
    /// and a max-file retention prune (anti disk-fill). Turn <c>DevLog:Enabled</c> off (or delete this controller)
    /// before a public launch — it's a debugging tool, not a shipping feature.
    /// </summary>
    [ApiController]
    [Route("api/devlog")]
    [AllowAnonymous]
    public sealed class DevLogController : ControllerBase
    {
        private const long MaxBytes = 5 * 1024 * 1024;   // 5 MB per upload

        private readonly IConfiguration _config;
        private readonly ILogger<DevLogController> _logger;

        public DevLogController(IConfiguration config, ILogger<DevLogController> logger)
        {
            _config = config;
            _logger = logger;
        }

        [HttpPost("upload")]
        [RequestSizeLimit(MaxBytes)]
        public async Task<IActionResult> Upload()
        {
            if (!_config.GetValue("DevLog:Enabled", false))
                return NotFound();   // switched off ⇒ invisible

            // Light gate: a shared key the dev client sends. It lives in the APK (extractable), so it's not a real
            // secret — it just stops random internet scanners from writing files. The size cap + prune bound the rest.
            var expectedKey = _config.GetValue<string>("DevLog:Key");
            if (!string.IsNullOrEmpty(expectedKey) &&
                !string.Equals(Request.Headers["X-Khela-DevKey"].ToString(), expectedKey, StringComparison.Ordinal))
                return Unauthorized(new { message = "bad dev key" });

            string body;
            using (var reader = new StreamReader(Request.Body))
                body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return BadRequest(new { message = "empty log" });

            var dir = _config.GetValue("DevLog:Directory", "/var/khela/client_log");
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { _logger.LogError(ex, "DevLog: cannot create dir {Dir}", dir); return StatusCode(500, new { message = "server dir error" }); }

            // Filename is ALWAYS server-generated. The X-Device header is sanitized to a short alnum tag and only used
            // as a readability hint, never as a path segment — so there is no way for a client to escape the directory.
            var device = Sanitize(Request.Headers["X-Device"].ToString());
            var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{device}_{Guid.NewGuid():N}.log";
            var path = Path.Combine(dir, fileName);
            try { await System.IO.File.WriteAllTextAsync(path, body); }
            catch (Exception ex) { _logger.LogError(ex, "DevLog: write failed {Path}", path); return StatusCode(500, new { message = "server write error" }); }

            PruneOldFiles(dir, _config.GetValue("DevLog:MaxFiles", 500));

            _logger.LogInformation("DevLog saved {File} ({Bytes}B) from {Ip}", fileName, body.Length, HttpContext.Connection.RemoteIpAddress);
            return Ok(new { saved = fileName, bytes = body.Length });
        }

        // Bound disk usage: keep the newest maxFiles *.log, delete the rest. 0 = unbounded (not recommended on prod).
        private void PruneOldFiles(string dir, int maxFiles)
        {
            if (maxFiles <= 0) return;
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.CreationTimeUtc).ToList();
                for (int i = maxFiles; i < files.Count; i++)
                    try { files[i].Delete(); } catch { /* best effort */ }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "DevLog: prune failed in {Dir}", dir); }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "device";
            var clean = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            return clean.Length == 0 ? "device" : (clean.Length > 32 ? clean.Substring(0, 32) : clean);
        }
    }
}
