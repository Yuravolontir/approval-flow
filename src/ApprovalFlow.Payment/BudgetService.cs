using System.Security.Cryptography;
using System.Text;
using ApprovalFlow.Shared.Models;
using Serilog;

public class BudgetService(IBudgetStore store)
{
    private const int MaxRetries = 8;

    public async Task<BudgetReserveResponse> ReserveAsync(BudgetReserveRequest request)
    {
        var log = Log.ForContext("CorrelationId", request.InvoiceId);
        var reservationId = CreateReservationId(request.IdempotencyKey);

        var existingReservation = await store.GetReservationIdAsync(request.IdempotencyKey);
        if (!string.IsNullOrEmpty(existingReservation))
        {
            log.Information("Idempotent reservation already exists: {ReservationId}", existingReservation);
            return new BudgetReserveResponse { ReservationId = existingReservation, Success = true };
        }

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var (budget, etag) = await store.GetAsync(request.Department);
            if (budget == null)
            {
                return new BudgetReserveResponse
                {
                    Success = false,
                    Error = $"Department {request.Department} not found"
                };
            }

            budget.Reservations ??= new Dictionary<string, decimal>();

            if (budget.Reservations.ContainsKey(reservationId))
            {
                await store.SaveReservationIdAsync(request.IdempotencyKey, reservationId);
                log.Information("Idempotent reservation already exists: {ReservationId}", reservationId);
                return new BudgetReserveResponse { ReservationId = reservationId, Success = true };
            }

            if (budget.Available < request.Amount)
            {
                log.Warning("Insufficient budget for {Department}: available={Available:C}, requested={Amount:C}",
                    request.Department, budget.Available, request.Amount);
                return new BudgetReserveResponse
                {
                    Success = false,
                    Error = $"Insufficient budget. Available: ${budget.Available:F2}, Requested: ${request.Amount:F2}"
                };
            }

            budget.Available -= request.Amount;
            budget.Reservations[reservationId] = request.Amount;

            if (await store.TrySaveAsync(request.Department, budget, etag))
            {
                await store.SaveReservationIdAsync(request.IdempotencyKey, reservationId);
                log.Information("Reserved {Amount:C} as {ReservationId}. Remaining: {Available:C}",
                    request.Amount, reservationId, budget.Available);
                return new BudgetReserveResponse { ReservationId = reservationId, Success = true };
            }
        }

        return new BudgetReserveResponse
        {
            Success = false,
            Error = "Could not reserve budget under concurrent contention, retry limit reached"
        };
    }

    public async Task<BudgetReleaseResult> ReleaseAsync(BudgetReleaseRequest request)
    {
        var log = Log.ForContext("CorrelationId", request.ReservationId);

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var (budget, etag) = await store.GetAsync(request.Department);
            if (budget == null)
            {
                return new BudgetReleaseResult
                {
                    Success = false,
                    Error = $"Department {request.Department} not found"
                };
            }

            budget.Reservations ??= new Dictionary<string, decimal>();

            if (!budget.Reservations.TryGetValue(request.ReservationId, out var reservedAmount))
            {
                log.Warning("Reservation {ReservationId} not found (already released?)", request.ReservationId);
                return new BudgetReleaseResult
                {
                    Success = true,
                    ReleasedAmount = 0m,
                    Available = budget.Available
                };
            }

            budget.Available = Math.Min(budget.TotalBudget, budget.Available + reservedAmount);
            budget.Reservations.Remove(request.ReservationId);

            if (await store.TrySaveAsync(request.Department, budget, etag))
            {
                log.Information("Released {Amount:C} from {ReservationId}. Available now: {Available:C}",
                    reservedAmount, request.ReservationId, budget.Available);
                return new BudgetReleaseResult
                {
                    Success = true,
                    ReleasedAmount = reservedAmount,
                    Available = budget.Available
                };
            }
        }

        return new BudgetReleaseResult
        {
            Success = false,
            Error = "Could not release budget under concurrent contention, retry limit reached"
        };
    }

    private static string CreateReservationId(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return $"R-{Convert.ToHexString(hash)[..8]}";
    }
}

public class BudgetReleaseResult
{
    public bool Success { get; set; }
    public decimal ReleasedAmount { get; set; }
    public decimal Available { get; set; }
    public string? Error { get; set; }
}
