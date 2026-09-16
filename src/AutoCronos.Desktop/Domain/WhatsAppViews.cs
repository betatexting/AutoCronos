namespace AutoCronos.Desktop.Domain;

public sealed record WhatsAppConversationView(
    long Id,
    string HashId,
    string Protocol,
    string Status,
    string StatusLabel,
    long? SectorId,
    string SectorName,
    long? ContactId,
    string ContactName,
    string Phone,
    long? CustomerId,
    long? AttendantId,
    int UnreadCount,
    string LastMessage,
    DateTime LastMessageAtUtc,
    string ElapsedLabel,
    bool IsOverdue);

public sealed record WhatsAppSectorView(long? Id, string Name, int OpenCount);

public sealed record WhatsAppMonitorSnapshot(
    IReadOnlyList<WhatsAppConversationView> Conversations,
    IReadOnlyList<WhatsAppSectorView> Sectors,
    int WaitingCount,
    int InServiceCount,
    int OverdueCount,
    DateTime UpdatedAtUtc,
    string? ErrorMessage = null)
{
    public static WhatsAppMonitorSnapshot Empty { get; } = new([], [], 0, 0, 0, DateTime.MinValue);
    public int OpenCount => WaitingCount + InServiceCount;
}

public sealed record WhatsAppMonitorSettings(int ResponseTimeMinutes, IReadOnlySet<long> HiddenSectorIds)
{
    public const int DefaultResponseTimeMinutes = 20;
    public static WhatsAppMonitorSettings Default { get; } = new(DefaultResponseTimeMinutes, new HashSet<long>());
}
