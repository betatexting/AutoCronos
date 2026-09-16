using AutoCronos.Desktop.Domain;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoCronos.Desktop.Services;

public sealed class WhatsAppMonitoringService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly Suite360IntegrationService _suite360;
    private readonly WhatsAppMonitorSettingsService _settings;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _monitorTask;
    private volatile bool _isActive;
    private DateTime _nextRefreshAtUtc;

    public WhatsAppMonitoringService(Suite360IntegrationService suite360, WhatsAppMonitorSettingsService settings)
    {
        _suite360 = suite360;
        _settings = settings;
    }

    public WhatsAppMonitorSnapshot Snapshot { get; private set; } = WhatsAppMonitorSnapshot.Empty;
    public event EventHandler? SnapshotChanged;

    public void Start() => _monitorTask ??= MonitorAsync(_stopping.Token);

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
            _ = RefreshNowAsync();
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_isActive || DateTime.UtcNow < _nextRefreshAtUtc)
            return;
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
            return;

        try
        {
            var settings = _settings.Current;
            var conversations = await _suite360.GetOpenWhatsAppConversationsAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var views = conversations
                .Select(item => CreateView(item, settings.ResponseTimeMinutes, now))
                .Where(item => item is not null)
                .Cast<WhatsAppConversationView>()
                .OrderByDescending(item => item.IsOverdue)
                .ThenByDescending(item => now - item.LastMessageAtUtc)
                .ToList();
            var sectors = views
                .GroupBy(item => new { item.SectorId, item.SectorName })
                .Select(group => new WhatsAppSectorView(group.Key.SectorId, group.Key.SectorName, group.Count()))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Snapshot = new WhatsAppMonitorSnapshot(
                views,
                sectors,
                views.Count(item => !item.IsTemplate && item.Status == "aguardando"),
                views.Count(item => !item.IsTemplate && item.Status == "em_atendimento"),
                views.Count(item => item.IsTemplate),
                views.Count(item => item.IsOverdue),
                now);
            _nextRefreshAtUtc = DateTime.MinValue;
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var retryMatch = Regex.Match(exception.Message, @"novamente em\s+(\d+)s", RegexOptions.IgnoreCase);
            if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out var retrySeconds))
                _nextRefreshAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(30, retrySeconds));
            Snapshot = Snapshot with { ErrorMessage = exception.Message };
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_isActive)
                    await RefreshNowAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static WhatsAppConversationView? CreateView(SuiteWhatsAppConversation item, int responseTimeMinutes, DateTime now)
    {
        var protocolStartedAtUtc = GetCurrentProtocolStartUtc(item);
        var isTemplate = string.Equals(item.LastMessageType, "template", StringComparison.OrdinalIgnoreCase);
        // A mensagem que dispara a criação do protocolo pode ser gravada poucos segundos antes dele.
        var hasMessageInActiveProtocol = !string.IsNullOrWhiteSpace(item.LastMessage) &&
                                         item.LastMessageAtUtc >= protocolStartedAtUtc.AddSeconds(-5);
        if (!hasMessageInActiveProtocol)
            return null;

        var activityReferenceAtUtc = hasMessageInActiveProtocol
            ? item.LastMessageAtUtc
            : protocolStartedAtUtc;
        var elapsed = isTemplate ? TimeSpan.Zero : now - activityReferenceAtUtc;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        var elapsedLabel = isTemplate
            ? "-"
            : elapsed.TotalDays >= 1
            ? $"{(int)elapsed.TotalDays}d {elapsed.Hours}h"
            : elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}min"
                : $"{Math.Max(0, (int)elapsed.TotalMinutes)}min";
        return new WhatsAppConversationView(
            item.Id,
            item.HashId,
            item.Protocol,
            item.Status,
            isTemplate ? "Template" : item.Status == "aguardando" ? "Aguardando" : "Em atendimento",
            item.SectorId,
            item.SectorName,
            item.ContactId,
            item.ContactName,
            item.Phone,
            item.CustomerId,
            item.AttendantId,
            item.UnreadCount,
            hasMessageInActiveProtocol ? item.LastMessage : string.Empty,
            activityReferenceAtUtc,
            elapsedLabel,
            !isTemplate && elapsed >= TimeSpan.FromMinutes(responseTimeMinutes),
            isTemplate);
    }

    private static DateTime GetCurrentProtocolStartUtc(SuiteWhatsAppConversation item)
    {
        if (item.Protocol.Length >= 8 && DateTime.TryParseExact(
                item.Protocol[..8],
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var protocolDate))
        {
            var protocolDayStartUtc = DateTime.SpecifyKind(protocolDate, DateTimeKind.Local).ToUniversalTime();
            return protocolDayStartUtc > item.ProtocolStartedAtUtc
                ? protocolDayStartUtc
                : item.ProtocolStartedAtUtc;
        }

        return item.ProtocolStartedAtUtc;
    }

    public void Dispose()
    {
        _stopping.Cancel();
    }
}
