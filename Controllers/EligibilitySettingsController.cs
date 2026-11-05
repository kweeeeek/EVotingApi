using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Settings > Member Eligibility. The minimum share capital and minimum savings deposit a member needs to be eligible to vote.
    /// Everything that decides eligibility goes through <see cref="IEligibilityService"/>; this controller only edits its settings.
    /// Authorisation is enforced here, whatever the Admin application shows or hides.
    /// </summary>
    [Route("api/v1/admin/settings/eligibility")]
    public class EligibilitySettingsController : AdminControllerBase
    {
        private readonly IEligibilityService _eligibility;

        public EligibilitySettingsController(IEligibilityService eligibility) => _eligibility = eligibility;

        [HttpGet]
        [RequirePermission(PermissionCodes.SettingsEligibilityView, PermissionCodes.SettingsEligibilityEdit)]
        public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _eligibility.GetSettingsAsync(ct));

        /// <summary>"What would happen if I saved these values?" — counts only, nothing is written.</summary>
        [HttpPost("impact")]
        [RequirePermission(PermissionCodes.SettingsEligibilityView, PermissionCodes.SettingsEligibilityEdit)]
        public async Task<IActionResult> Impact([FromBody] EligibilityImpactRequest req, CancellationToken ct)
        {
            if (!MoneyRules.IsValid(req.MinShareCapital)) return Invalid("minShareCapital", MoneyRules.Message("Minimum share capital"));
            if (!MoneyRules.IsValid(req.MinSavingsDeposit)) return Invalid("minSavingsDeposit", MoneyRules.Message("Minimum savings deposit"));
            return Ok(await _eligibility.PreviewImpactAsync(req.MinShareCapital, req.MinSavingsDeposit, ct));
        }

        [HttpPut]
        [RequirePermission(PermissionCodes.SettingsEligibilityEdit)]
        public async Task<IActionResult> Save([FromBody] EligibilitySettingsUpdateRequest req, CancellationToken ct)
        {
            var outcome = await _eligibility.SaveAsync(req, ct);
            switch (outcome.Kind)
            {
                case EligibilitySaveKind.Invalid:
                    return Invalid(outcome.Field ?? "settings", outcome.Message ?? "The values are not valid.");
                case EligibilitySaveKind.VersionConflict:
                    return Fail(StatusCodes.Status409Conflict, outcome.Message ?? "The settings changed.", "CONCURRENCY", "Reload the settings and apply your change again.");
                case EligibilitySaveKind.ElectionOpen:
                    return Fail(StatusCodes.Status409Conflict, outcome.Message ?? "An election is open.", RepCodes.EventOpenLock,
                        "The eligibility rules cannot change while voting is open.");
                case EligibilitySaveKind.Unchanged:
                    return Ok(new EligibilitySaveResponse { Settings = outcome.Settings!, Unchanged = true, Message = outcome.Message ?? "" });
                default:
                    return Ok(new EligibilitySaveResponse { Settings = outcome.Settings!, Recompute = outcome.Recompute, Message = outcome.Message ?? "" });
            }
        }

        /// <summary>Re-runs the member recalculation for the saved thresholds (use after an interrupted save).</summary>
        [HttpPost("recompute")]
        [RequirePermission(PermissionCodes.SettingsEligibilityEdit)]
        public async Task<IActionResult> Recompute(CancellationToken ct)
        {
            var settings = await _eligibility.GetSettingsAsync(ct);
            if (settings.Locked)
                return Fail(StatusCodes.Status409Conflict, settings.LockReason ?? "An election is open.", RepCodes.EventOpenLock,
                    "Member eligibility cannot be recalculated while voting is open.");
            return Ok(await _eligibility.RecomputeAsync(CancellationToken.None));
        }
    }
}
