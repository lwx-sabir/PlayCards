using Khela.Common.Auth;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DeviceController : ControllerBase
    {
        private readonly AppDbContext _dbContext;

        public DeviceController(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] DeviceRegisterRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Fingerprint) && string.IsNullOrWhiteSpace(request.AppSetId))
            {
                return BadRequest(new { message = "Fingerprint or AppSetId required." });
            }

            var now = DateTime.UtcNow;
            var candidates = new List<DeviceRegistration>();
            if (!string.IsNullOrWhiteSpace(request.AppSetId))
            {
                candidates.AddRange(await _dbContext.DeviceRegistrations.Where(d => d.AppSetId == request.AppSetId).ToListAsync());
            }

            if (!string.IsNullOrWhiteSpace(request.Fingerprint))
            {
                candidates.AddRange(await _dbContext.DeviceRegistrations.Where(d => d.Fingerprint == request.Fingerprint).ToListAsync());
            }

            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            var best = GetBestMatch(candidates, request, remoteIp, out var bestScore);

            DeviceRegistration device;

            if (best == null || bestScore < 80)
            {
                device = new DeviceRegistration
                {
                    DeviceId = Guid.NewGuid(),
                    Fingerprint = request.Fingerprint,
                    AppSetId = request.AppSetId,
                    GameVersion = request.GameVersion,
                    UserId = request.UserId,
                    TimeZone = request.TimeZone,
                    LastIp = remoteIp
                };
                _dbContext.DeviceRegistrations.Add(device);
            }
            else
            {
                device = best;
                if (string.IsNullOrWhiteSpace(device.Fingerprint) && !string.IsNullOrWhiteSpace(request.Fingerprint))
                    device.Fingerprint = request.Fingerprint;
                if (string.IsNullOrWhiteSpace(device.AppSetId) && !string.IsNullOrWhiteSpace(request.AppSetId))
                    device.AppSetId = request.AppSetId;
                if (!string.IsNullOrWhiteSpace(request.GameVersion))
                    device.GameVersion = request.GameVersion;
                if (!string.IsNullOrWhiteSpace(request.UserId))
                    device.UserId = request.UserId;
                if (!string.IsNullOrWhiteSpace(request.TimeZone))
                    device.TimeZone = request.TimeZone;
                device.LastIp = remoteIp;
                bestScore = Math.Max(bestScore, ComputeScore(device, request, remoteIp));
            }

            device.LastSeen = now;
            await _dbContext.SaveChangesAsync();

            var response = new DeviceRegisterResponse
            {
                DeviceId = device.DeviceId.ToString(),
                UserId = device.UserId,
                MatchScore = bestScore,
                IsSameDevice = bestScore >= 80
            };

            return Ok(response);
        }

        private static DeviceRegistration GetBestMatch(
            IEnumerable<DeviceRegistration> candidates,
            DeviceRegisterRequest request,
            string remoteIp,
            out int bestScore)
        {
            DeviceRegistration best = null;
            bestScore = 0;
            foreach (var c in candidates)
            {
                var score = ComputeScore(c, request, remoteIp);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        private static int ComputeScore(DeviceRegistration device, DeviceRegisterRequest request, string remoteIp)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(request.Fingerprint) && device.Fingerprint == request.Fingerprint)
                score += 80;
            if (!string.IsNullOrWhiteSpace(request.AppSetId) && device.AppSetId == request.AppSetId)
                score += 100;
            if (SameIpRange(device.LastIp, remoteIp))
                score += 10;
            if (!string.IsNullOrWhiteSpace(device.TimeZone) && !string.IsNullOrWhiteSpace(request.TimeZone) && device.TimeZone == request.TimeZone)
                score += 5;
            return score;
        }

        private static bool SameIpRange(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

            if (IPAddress.TryParse(a, out var ipA) && IPAddress.TryParse(b, out var ipB))
            {
                var bytesA = ipA.GetAddressBytes();
                var bytesB = ipB.GetAddressBytes();
                if (bytesA.Length != bytesB.Length) return false;

                // compare first two octets for a rough range match
                return bytesA.Length >= 2 && bytesA[0] == bytesB[0] && bytesA[1] == bytesB[1];
            }

            return false;
        }
    }
}
