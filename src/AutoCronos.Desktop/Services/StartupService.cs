using Microsoft.Win32;

namespace AutoCronos.Desktop.Services;

public sealed class StartupService
{
    public void Enable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key?.SetValue("AutoCronos", $"\"{executable}\"");
    }
}
