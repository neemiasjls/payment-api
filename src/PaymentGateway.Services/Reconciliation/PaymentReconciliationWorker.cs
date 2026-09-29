using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentGateway.Data;
using PaymentGateway.Domain.Enums;

namespace PaymentGateway.Services.Reconciliation;

/// <summary>
/// Checks Stripe payments even when an inbound webhook was missed. A stable
/// (creation time, payment ID) cursor rotates through all nonterminal records.
/// Each refresh uses a fresh scope so one failure cannot poison later updates.
/// </summary>
public sealed class PaymentReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentReconciliationWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;
    private DateTime? _cursorCreatedAtUtc;
    private Guid _cursorId;

    public PaymentReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PaymentReconciliationOptions> options,
        ILogger<PaymentReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        if (options.Value.Interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "O intervalo de reconciliação deve ser positivo.");
        if (options.Value.BatchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(options), "O lote de reconciliação deve ter entre 1 e 1000 itens.");

        _interval = options.Value.Interval;
        _batchSize = options.Value.BatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            await ReconcileBatchAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha na varredura de reconciliação Stripe");
        }
    }

    /// <summary>Processes one bounded batch; also callable by a test or operator.</summary>
    public async Task<int> ReconcileBatchAsync(CancellationToken ct = default)
    {
        var candidates = await LoadCandidatesAsync(ct);
        var refreshed = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();
                await payments.RefreshAsync(candidate.MerchantId, candidate.Id, ct);
                refreshed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reconciliar pagamento {PaymentId}", candidate.Id);
            }
        }

        return refreshed;
    }

    private async Task<List<PaymentCandidate>> LoadCandidatesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var eligible = db.Payments.AsNoTracking()
            .Where(p => p.Provider == "stripe" && p.ProviderPaymentId != null &&
                        (p.Status == PaymentStatus.Pending ||
                         p.Status == PaymentStatus.RequiresAction ||
                         p.Status == PaymentStatus.Authorized ||
                         p.Status == PaymentStatus.Captured));

        var candidates = await ReadAfterCursorAsync(eligible, ct);
        if (candidates.Count == 0 && _cursorCreatedAtUtc is not null)
        {
            _cursorCreatedAtUtc = null;
            _cursorId = Guid.Empty;
            candidates = await ReadAfterCursorAsync(eligible, ct);
        }

        if (candidates.Count > 0)
        {
            var last = candidates[^1];
            _cursorCreatedAtUtc = last.CreatedAtUtc;
            _cursorId = last.Id;
        }

        return candidates;
    }

    private async Task<List<PaymentCandidate>> ReadAfterCursorAsync(
        IQueryable<PaymentGateway.Domain.Entities.Payment> eligible, CancellationToken ct)
    {
        var candidates = new List<PaymentCandidate>(_batchSize);
        if (_cursorCreatedAtUtc is { } cursorTime)
        {
            // EF SQLite cannot compare Guid values in a translated expression.
            // Equal-time IDs are sorted and filtered in memory; only this small
            // tie group is materialized, then the regular query continues.
            var sameTime = await eligible
                .Where(p => p.CreatedAtUtc == cursorTime)
                .Select(p => new PaymentCandidate(p.Id, p.MerchantId, p.CreatedAtUtc))
                .ToListAsync(ct);
            candidates.AddRange(sameTime
                .Where(p => p.Id.CompareTo(_cursorId) > 0)
                .OrderBy(p => p.Id)
                .Take(_batchSize));
        }

        var remaining = _batchSize - candidates.Count;
        if (remaining > 0)
        {
            var later = eligible;
            if (_cursorCreatedAtUtc is { } laterThan)
                later = later.Where(p => p.CreatedAtUtc > laterThan);

            candidates.AddRange(await later
                .OrderBy(p => p.CreatedAtUtc)
                .ThenBy(p => p.Id)
                .Select(p => new PaymentCandidate(p.Id, p.MerchantId, p.CreatedAtUtc))
                .Take(remaining)
                .ToListAsync(ct));
        }

        return candidates;
    }

    private sealed record PaymentCandidate(Guid Id, Guid MerchantId, DateTime CreatedAtUtc);
}
