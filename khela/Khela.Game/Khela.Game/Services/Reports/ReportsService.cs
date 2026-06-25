using Khela.Common.Reports;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Khela.Game.Services.Reports
{
    public interface IReportsService
    {
        Task<(bool ok, string error)> CreateMessageReportAsync(Guid reporterId, ReportMessageRequest req);
        Task<(bool ok, string error)> CreatePlayerReportAsync(Guid reporterId, ReportPlayerRequest req);
        Task<IReadOnlyList<ReportDto>> ListAsync(ReportStatus? status, int page, int pageSize);
        Task<(bool ok, string error)> ResolveAsync(Guid reportId, Guid adminId, ResolveReportRequest req);
    }

    /// <summary>
    /// Player/message reports for moderation. Validates self-report, dedupes identical OPEN reports from the same
    /// reporter, and rate-limits with the same Redis INCR+PEXPIRE pattern chat uses (5/min). Message reports must
    /// carry a client-captured <c>ContextSnapshot</c> because room/global chat is ephemeral. Admin list/resolve
    /// are exposed via the controller behind the pre-prod admin gate.
    /// </summary>
    public sealed class ReportsService : IReportsService
    {
        private const int RateLimitMax = 5;                              // reports per window
        private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

        // INCR + first-write PEXPIRE atomically (crash can't orphan a TTL-less key — same pattern as ChatService).
        private const string RateLimitLua =
            "local v = redis.call('INCR', KEYS[1]) " +
            "if v == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end " +
            "return v";

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;

        public ReportsService(AppDbContext db, IRedisService redis)
        {
            _db = db;
            _redis = redis;
        }

        public Task<(bool, string)> CreateMessageReportAsync(Guid reporterId, ReportMessageRequest req)
        {
            if (req == null || !Guid.TryParse(req.ReportedUserId, out var reportedId)) return Task.FromResult((false, "Invalid request."));
            if (string.IsNullOrWhiteSpace(req.ContextSnapshot)) return Task.FromResult((false, "Missing message context."));
            Guid? msgId = Guid.TryParse(req.TargetMessageId, out var m) ? m : (Guid?)null;
            return CreateAsync(reporterId, reportedId, ReportTargetType.Message, msgId, req.ContextSnapshot, req.Reason, req.Details);
        }

        public Task<(bool, string)> CreatePlayerReportAsync(Guid reporterId, ReportPlayerRequest req)
        {
            if (req == null || !Guid.TryParse(req.ReportedUserId, out var reportedId)) return Task.FromResult((false, "Invalid request."));
            return CreateAsync(reporterId, reportedId, ReportTargetType.Player, null, null, req.Reason, req.Details);
        }

        private async Task<(bool, string)> CreateAsync(Guid reporterId, Guid reportedId, ReportTargetType type,
            Guid? targetMessageId, string contextSnapshot, ReportReason reason, string details)
        {
            if (reporterId == reportedId) return (false, "You can't report yourself.");
            if (!await CheckRateLimitAsync(reporterId)) return (false, "You're reporting too fast — try again shortly.");

            // Dedupe: an identical OPEN report from the same reporter about the same target/message/reason.
            bool dup = await _db.Reports.AnyAsync(r =>
                r.ReporterUserId == reporterId && r.ReportedUserId == reportedId &&
                r.TargetType == type && r.Reason == reason && r.Status == ReportStatus.Open &&
                r.TargetMessageId == targetMessageId);
            if (dup) return (false, "You've already reported this — it's pending review.");

            _db.Reports.Add(new Report
            {
                ReporterUserId = reporterId,
                ReportedUserId = reportedId,
                TargetType = type,
                TargetMessageId = targetMessageId,
                ContextSnapshot = contextSnapshot,
                Reason = reason,
                Details = Trim(details, 500),
                Status = ReportStatus.Open,
                Source = ReportSource.User,
            });
            await _db.SaveChangesAsync();
            return (true, null);
        }

        public async Task<IReadOnlyList<ReportDto>> ListAsync(ReportStatus? status, int page, int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var q = _db.Reports.AsNoTracking();
            if (status.HasValue) q = q.Where(r => r.Status == status.Value);

            var rows = await q.OrderBy(r => r.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return rows.Select(ToDto).ToList();
        }

        public async Task<(bool, string)> ResolveAsync(Guid reportId, Guid adminId, ResolveReportRequest req)
        {
            if (req == null) return (false, "Invalid request.");
            if (req.Action != ReportStatus.Reviewing && req.Action != ReportStatus.ActionTaken && req.Action != ReportStatus.Dismissed)
                return (false, "Action must be Reviewing, ActionTaken, or Dismissed.");

            var r = await _db.Reports.FirstOrDefaultAsync(x => x.Id == reportId);
            if (r == null) return (false, "Report not found.");

            r.Status = req.Action;
            r.ActionNote = Trim(req.Note, 1000);
            if (req.Action == ReportStatus.ActionTaken || req.Action == ReportStatus.Dismissed)
            {
                r.ResolvedAt = DateTime.UtcNow;
                r.ResolvedByAdminId = adminId;
            }
            await _db.SaveChangesAsync();
            return (true, null);
        }

        private async Task<bool> CheckRateLimitAsync(Guid userId)
        {
            var n = (long)await _redis.GetDatabase().ScriptEvaluateAsync(RateLimitLua,
                new RedisKey[] { $"report:rl:{userId}" },
                new RedisValue[] { (long)RateLimitWindow.TotalMilliseconds });
            return n <= RateLimitMax;
        }

        private static ReportDto ToDto(Report r) => new ReportDto
        {
            Id = r.Id.ToString(),
            ReporterUserId = r.ReporterUserId.ToString(),
            ReportedUserId = r.ReportedUserId.ToString(),
            TargetType = r.TargetType,
            TargetMessageId = r.TargetMessageId?.ToString(),
            ContextSnapshot = r.ContextSnapshot,
            Reason = r.Reason,
            Details = r.Details,
            Status = r.Status,
            Source = r.Source,
            CreatedAt = r.CreatedAt,
            ResolvedAt = r.ResolvedAt,
            ResolvedByAdminId = r.ResolvedByAdminId?.ToString(),
            ActionNote = r.ActionNote,
        };

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
