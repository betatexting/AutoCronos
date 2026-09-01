using System.IO;
using System.Text.Json;
using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Services;

public sealed class LocalDataService
{
    private readonly string _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCronos", "workspace.json");
    public BoardModel Board { get; private set; } = new([]);
    public IReadOnlyList<WarningItem> Warnings { get; private set; } = [];

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        if (!File.Exists(_filePath)) File.WriteAllText(_filePath, JsonSerializer.Serialize(WorkspaceSnapshot.CreateInitial()));
        var snapshot = JsonSerializer.Deserialize<WorkspaceSnapshot>(File.ReadAllText(_filePath)) ?? WorkspaceSnapshot.CreateInitial();
        Board = new BoardModel(snapshot.Columns);
        Warnings = snapshot.Warnings;
    }

    private sealed record WorkspaceSnapshot(List<KanbanColumn> Columns, List<WarningItem> Warnings)
    {
        public static WorkspaceSnapshot CreateInitial() => new(
            [
                new("Informativo Recebido", [new("ABC LTDA", "12.345.678/0001-90", "Prazo: 01/10/2026")]),
                new("Iniciar Inativacao", [new("Horizonte Comercio", "98.765.432/0001-10", "Pronto para execucao")]),
                new("Em Andamento", []), new("Concluido", [])
            ],
            [
                new(WarningKind.Duplicate, "Possivel repeticao", "ABC LTDA", "12.345.678/0001-90", "Novo comunicado recebido em 15/09/2026.", new DateTime(2026, 9, 15)),
                new(WarningKind.CompetenceChange, "Alteracao de competencia", "Horizonte Comercio", "98.765.432/0001-10", "Competencia sugerida: 09/2026 para 10/2026.", new DateTime(2026, 9, 15))
            ]);
    }
}
