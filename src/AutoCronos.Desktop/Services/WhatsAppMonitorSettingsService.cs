using System.IO;
using System.Text.Json;
using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Services;

public sealed class WhatsAppMonitorSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _settingsPath;

    public WhatsAppMonitorSettingsService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCronos");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "whatsapp-monitor.json");
        Current = Load();
    }

    public WhatsAppMonitorSettings Current { get; private set; }
    public event EventHandler? Changed;

    public void SetResponseTime(int minutes)
    {
        if (minutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(minutes), "O tempo de retorno deve estar entre 1 e 1440 minutos.");
        Save(Current with { ResponseTimeMinutes = minutes });
    }

    public void SetSectorVisible(long sectorId, bool visible)
    {
        var hidden = Current.HiddenSectorIds.ToHashSet();
        if (visible)
            hidden.Remove(sectorId);
        else
            hidden.Add(sectorId);
        Save(Current with { HiddenSectorIds = hidden });
    }

    public void SetAllSectorsVisible(IEnumerable<long> sectorIds, bool visible)
    {
        var hidden = Current.HiddenSectorIds.ToHashSet();
        foreach (var sectorId in sectorIds)
        {
            if (visible)
                hidden.Remove(sectorId);
            else
                hidden.Add(sectorId);
        }
        Save(Current with { HiddenSectorIds = hidden });
    }

    private WhatsAppMonitorSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return WhatsAppMonitorSettings.Default;
            var value = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_settingsPath), JsonOptions);
            var minutes = value?.ResponseTimeMinutes is >= 1 and <= 1440
                ? value.ResponseTimeMinutes
                : WhatsAppMonitorSettings.DefaultResponseTimeMinutes;
            return new WhatsAppMonitorSettings(minutes, (value?.HiddenSectorIds ?? []).ToHashSet());
        }
        catch
        {
            return WhatsAppMonitorSettings.Default;
        }
    }

    private void Save(WhatsAppMonitorSettings settings)
    {
        Current = settings;
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(
            new SettingsFile(settings.ResponseTimeMinutes, settings.HiddenSectorIds.Order().ToArray()), JsonOptions));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record SettingsFile(int ResponseTimeMinutes, long[] HiddenSectorIds);
}
