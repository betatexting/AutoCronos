using AutoCronos.Desktop.Domain;
using Microsoft.EntityFrameworkCore;

namespace AutoCronos.Desktop.Infrastructure;

public sealed class AutoCronosDbContext(DbContextOptions<AutoCronosDbContext> options) : DbContext(options)
{
    public DbSet<OperationDefinition> Operations => Set<OperationDefinition>();
    public DbSet<KanbanColumnDefinition> KanbanColumns => Set<KanbanColumnDefinition>();
    public DbSet<EmailRule> EmailRules => Set<EmailRule>();
    public DbSet<DeadlineRule> DeadlineRules => Set<DeadlineRule>();
    public DbSet<Process> Processes => Set<Process>();
    public DbSet<ProcessOccurrence> Occurrences => Set<ProcessOccurrence>();
    public DbSet<ProcessHistoryEntry> HistoryEntries => Set<ProcessHistoryEntry>();
    public DbSet<IncomingEmail> IncomingEmails => Set<IncomingEmail>();
    public DbSet<ApprovalItem> Approvals => Set<ApprovalItem>();
    public DbSet<CardFieldDefinition> CardFieldDefinitions => Set<CardFieldDefinition>();
    public DbSet<CardFieldValue> CardFieldValues => Set<CardFieldValue>();
    public DbSet<EmailCardLink> EmailCardLinks => Set<EmailCardLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OperationDefinition>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<KanbanColumnDefinition>().HasIndex(x => new { x.OperationDefinitionId, x.SortOrder }).IsUnique();
        modelBuilder.Entity<Process>().HasIndex(x => new { x.OperationDefinitionId, x.TaxId }).IsUnique();
        modelBuilder.Entity<ProcessOccurrence>().HasIndex(x => new { x.ProcessId, x.Number }).IsUnique();
        modelBuilder.Entity<IncomingEmail>().HasIndex(x => x.ProviderMessageId).IsUnique();
        modelBuilder.Entity<CardFieldDefinition>().HasIndex(x => new { x.OperationDefinitionId, x.SortOrder }).IsUnique();
        modelBuilder.Entity<CardFieldValue>().HasIndex(x => new { x.ProcessOccurrenceId, x.CardFieldDefinitionId }).IsUnique();
        modelBuilder.Entity<EmailCardLink>().HasIndex(x => x.IncomingEmailId).IsUnique();
        modelBuilder.Entity<OperationDefinition>().HasMany(x => x.Columns).WithOne().HasForeignKey(x => x.OperationDefinitionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OperationDefinition>().HasMany(x => x.EmailRules).WithOne().HasForeignKey(x => x.OperationDefinitionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OperationDefinition>().HasMany(x => x.DeadlineRules).WithOne().HasForeignKey(x => x.OperationDefinitionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OperationDefinition>().HasMany(x => x.CardFields).WithOne().HasForeignKey(x => x.OperationDefinitionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OperationDefinition>().HasMany(x => x.Processes).WithOne(x => x.Operation).HasForeignKey(x => x.OperationDefinitionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Process>().HasMany(x => x.Occurrences).WithOne(x => x.Process).HasForeignKey(x => x.ProcessId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProcessOccurrence>().HasMany(x => x.History).WithOne().HasForeignKey(x => x.ProcessOccurrenceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProcessOccurrence>().HasMany(x => x.FieldValues).WithOne().HasForeignKey(x => x.ProcessOccurrenceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CardFieldValue>().HasOne<CardFieldDefinition>().WithMany().HasForeignKey(x => x.CardFieldDefinitionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EmailCardLink>().HasOne<IncomingEmail>().WithMany().HasForeignKey(x => x.IncomingEmailId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EmailCardLink>().HasOne<ProcessOccurrence>().WithMany().HasForeignKey(x => x.ProcessOccurrenceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EmailCardLink>().HasOne<OperationDefinition>().WithMany().HasForeignKey(x => x.OperationDefinitionId).OnDelete(DeleteBehavior.Cascade);
    }
}
