using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Representative;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Representative results.
    //
    //   Total Voters               = unique submitted ballots (distinct voters in RepresentativeVotes)
    //   Total Candidate Selections = vote rows (one per candidate ticked on a ballot)
    //   Candidate Vote Count       = selections that candidate received
    //   Percent                    = votes / BALLOTS (never / selections: a ballot can tick several candidates, so a share of
    //                                selections would invite the wrong reading)
    //
    //  Every roster entry is listed, including candidates with zero votes (the roster drives the result, the votes are joined onto it).
    //  Ranking and winners come from RepresentativeRanking (pure, unit-tested): ties share a rank; a tie for the LAST seat is reported,
    //  not decided - the count alone cannot elect either candidate. The seat count is the position's TotalSeats.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    public sealed record RepresentativeFile(string FileName, byte[] Content);

    public interface IRepresentativeResultsService
    {
        Task<ServiceResult<RepResultsResponse>> GetResultsAsync(int eventId, CancellationToken ct);
        Task<ServiceResult<RepresentativeFile>> ExportCsvAsync(int eventId, CancellationToken ct);
    }

    public sealed class RepresentativeResultsService : IRepresentativeResultsService
    {
        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;

        public RepresentativeResultsService(ApiDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        public async Task<ServiceResult<RepResultsResponse>> GetResultsAsync(int eventId, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId, ct);
            if (ev == null) return ServiceResult<RepResultsResponse>.Failure(StatusCodes.Status404NotFound, "Election not found.", RepCodes.EventNotFound);
            if (ev.EventType != EventTypes.Representative)
                return ServiceResult<RepResultsResponse>.Failure(StatusCodes.Status409Conflict,
                    "This is a General Assembly election; use its own results endpoint.", RepCodes.WrongEventType,
                    $"GET /api/v1/events/{eventId}/results.");

            var rules = await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == eventId).OrderBy(r => r.Id).ToListAsync(ct);

            // The roster drives the result (every entry appears, voted for or not); the vote counts are joined onto it.
            var roster = await _db.RepresentativeEventCandidates.AsNoTracking().Where(c => c.EventId == eventId)
                .Select(c => new { Id = c.RepresentativeEventCandidateId, c.Position, c.BallotNumber, Code = c.Profile!.CandidateCode, Name = c.Profile.CandidateName })
                .ToListAsync(ct);
            var votesByCandidate = (await _db.RepresentativeVotes.AsNoTracking().Where(v => v.EventId == eventId)
                    .GroupBy(v => v.RepresentativeEventCandidateId)
                    .Select(g => new { Id = g.Key, Votes = g.Count() }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x.Votes);

            // Distinct ballots per position (one voter ticking three delegates is ONE ballot at that position, three selections).
            var perPosition = (await (from v in _db.RepresentativeVotes.AsNoTracking()
                                      join c in _db.RepresentativeEventCandidates.AsNoTracking()
                                          on new { Id = v.RepresentativeEventCandidateId, v.EventId } equals new { Id = c.RepresentativeEventCandidateId, c.EventId }
                                      where v.EventId == eventId
                                      group v by c.Position into g
                                      select new { Position = g.Key, Selections = g.Count(), Ballots = g.Select(x => x.MemberId).Distinct().Count() })
                    .ToListAsync(ct))
                .ToDictionary(x => x.Position.Trim(), x => (x.Selections, x.Ballots), StringComparer.OrdinalIgnoreCase);

            var totalSelections = await _db.RepresentativeVotes.AsNoTracking().CountAsync(v => v.EventId == eventId, ct);
            var totalVoters = await _db.RepresentativeVotes.AsNoTracking().Where(v => v.EventId == eventId).Select(v => v.MemberId).Distinct().CountAsync(ct);
            var inLogs = await _db.VoterLogs.AsNoTracking().CountAsync(v => v.EventId == eventId && v.HasVoted, ct);
            var branchIds = await _db.EventBranches.AsNoTracking().Where(b => b.EventId == eventId).Select(b => b.BranchId).ToListAsync(ct);
            var eligible = await _db.Members.AsNoTracking().CountAsync(m => branchIds.Contains(m.BranchId) && m.IsEligibleToVote && m.IsActive, ct);

            // Positions: those with a rule (in the administrator's order), then any roster position without one (should not occur; never hide it).
            var ruleByPosition = new Dictionary<string, EventPositionRuleEntity>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var r in rules)
                if (ruleByPosition.TryAdd(r.Position.Trim(), r)) order.Add(r.Position.Trim());
            foreach (var extra in roster.Select(c => c.Position.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                if (!ruleByPosition.ContainsKey(extra) && !order.Contains(extra, StringComparer.OrdinalIgnoreCase)) order.Add(extra);

            var positions = new List<RepPositionResult>();
            foreach (var name in order)
            {
                ruleByPosition.TryGetValue(name, out var rule);
                var candidates = roster.Where(c => string.Equals(c.Position.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
                var byId = candidates.ToDictionary(c => c.Id);
                var ranked = RepresentativeRanking.Rank(
                    candidates.Select(c => new RankInput(c.Id, c.Name, c.Code, votesByCandidate.GetValueOrDefault(c.Id))),
                    rule?.TotalSeats ?? 0);
                perPosition.TryGetValue(name, out var stats);

                positions.Add(new RepPositionResult
                {
                    Position = rule?.Position ?? name,
                    MinVotes = rule?.MinVotes ?? 0,
                    MaxVotes = rule?.MaxVotes ?? 0,
                    TotalSeats = rule?.TotalSeats ?? 0,
                    MaxCandidates = rule?.MaxCandidates,
                    Candidates = candidates.Count,
                    Selections = stats.Selections,
                    BallotsWithSelection = stats.Ballots,
                    SeatsFilled = ranked.SeatsFilled,
                    HasUnresolvedTie = ranked.HasUnresolvedTie,
                    Results = ranked.Ranked.Select(o =>
                    {
                        var c = byId[o.Id];
                        return new RepCandidateResult
                        {
                            Id = c.Id, CandidateCode = c.Code, CandidateName = c.Name, Position = c.Position, BallotNumber = c.BallotNumber,
                            Votes = o.Votes, Rank = o.Rank, IsWinner = o.IsWinner, IsTieForLastSeat = o.IsTieForLastSeat,
                            PercentOfBallots = RepresentativeRanking.PercentOfBallots(o.Votes, totalVoters)
                        };
                    }).ToList()
                });
            }

            return ServiceResult<RepResultsResponse>.Success(new RepResultsResponse
            {
                EventId = ev.EventId,
                EventTitle = ev.Title,
                EventStatus = ev.Status,
                GeneratedAt = DateTime.UtcNow,
                TotalVoters = totalVoters,
                TotalSelections = totalSelections,
                EligibleVoters = eligible,
                TurnoutPercent = RepresentativeRanking.Turnout(totalVoters, eligible),
                BallotsInVoterLogs = inLogs,
                IsReconciled = inLogs == totalVoters,
                Positions = positions
            });
        }

        public async Task<ServiceResult<RepresentativeFile>> ExportCsvAsync(int eventId, CancellationToken ct)
        {
            var r = await GetResultsAsync(eventId, ct);
            if (!r.Ok) return r.As<RepresentativeFile>();
            var x = r.Value!;

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append('﻿');                                              // UTF-8 BOM so Excel reads accents correctly
            sb.Append("Election,").Append(Csv(x.EventTitle)).Append("\r\n");
            sb.Append("Status,").Append(Csv(x.EventStatus)).Append("\r\n");
            sb.Append("Generated (UTC),").Append(x.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append("\r\n");
            sb.Append("Total voters (unique ballots),").Append(x.TotalVoters.ToString(inv)).Append("\r\n");
            sb.Append("Total candidate selections,").Append(x.TotalSelections.ToString(inv)).Append("\r\n");
            sb.Append("Eligible voters (current),").Append(x.EligibleVoters.ToString(inv)).Append("\r\n");
            sb.Append("Turnout %,").Append(x.TurnoutPercent.ToString("0.00", inv)).Append("\r\n");
            sb.Append("Percent column = votes as a share of ballots cast (a ballot may select several candidates)\r\n\r\n");
            sb.Append("Position,Rank,Ballot No,Candidate Code,Candidate Name,Votes,Percent Of Ballots,Seats,Result\r\n");
            foreach (var p in x.Positions)
                foreach (var c in p.Results)
                {
                    var result = c.IsWinner ? "Elected" : c.IsTieForLastSeat ? "Tie for last seat" : "Not elected";
                    sb.Append(string.Join(",", new[]
                    {
                        Csv(p.Position), c.Rank.ToString(inv), c.BallotNumber?.ToString(inv) ?? "", Csv(c.CandidateCode), Csv(c.CandidateName),
                        c.Votes.ToString(inv), c.PercentOfBallots.ToString("0.00", inv), p.TotalSeats.ToString(inv), result
                    })).Append("\r\n");
                }

            await _audit.WriteAsync(AuditActions.ExportReport, AuditEntities.Event, eventId,
                $"Representative results of '{x.EventTitle}' exported ({x.Positions.Sum(p => p.Results.Count)} row(s)).", ct: ct);
            return ServiceResult<RepresentativeFile>.Success(new RepresentativeFile($"representative-results-{eventId}.csv", Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static string Csv(string? value)
        {
            var s = RepText.Safe(value);
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
