using System.Windows;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

internal static class SuiteTicketApprovalWorkflow
{
    public static async Task OpenAsync(Window owner, LocalDataService data, Guid approvalId)
    {
        var editor = await data.GetSuiteTicketApprovalEditorAsync(approvalId, loadSuiteData: false);
        var window = new SuiteTicketApprovalWindow(
            editor,
            () => data.GetSuiteTicketApprovalEditorAsync(approvalId))
        {
            Owner = owner
        };
        if (window.ShowDialog() == true)
            await data.ResolveApprovalAsync(approvalId, true, window.Submission);
    }
}
