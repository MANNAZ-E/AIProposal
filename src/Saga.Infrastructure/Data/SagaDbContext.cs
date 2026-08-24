using Microsoft.EntityFrameworkCore;
using Saga.Core.Domain;

namespace Saga.Infrastructure.Data;

public class SagaDbContext(DbContextOptions<SagaDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<ProposalMember> ProposalMembers => Set<ProposalMember>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ArtifactVersion> ArtifactVersions => Set<ArtifactVersion>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AiUsageRecord> AiUsage => Set<AiUsageRecord>();
    public DbSet<MannazVoiceSettings> MannazVoiceSettings => Set<MannazVoiceSettings>();
    public DbSet<FinalProposalVersion> FinalProposalVersions => Set<FinalProposalVersion>();
    public DbSet<FinalProposalFile> FinalProposalFiles => Set<FinalProposalFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.DisplayName).HasMaxLength(256);
            b.Property(u => u.EntraObjectId).HasMaxLength(64);
            b.HasIndex(u => u.Email).IsUnique();
            b.HasIndex(u => u.EntraObjectId).IsUnique().HasFilter("[EntraObjectId] IS NOT NULL");
        });

        modelBuilder.Entity<Proposal>(b =>
        {
            b.Property(p => p.Title).HasMaxLength(500);
            b.Property(p => p.ClientName).HasMaxLength(500);
            b.Property(p => p.ContentLanguage).HasMaxLength(16);
            b.Property(p => p.ResearchClientName).HasMaxLength(500);
            b.Property(p => p.ClientWebsite).HasMaxLength(500);
            b.HasOne(p => p.Owner).WithMany().HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProposalMember>(b =>
        {
            b.HasIndex(m => new { m.ProposalId, m.UserId }).IsUnique();
            b.HasOne(m => m.Proposal).WithMany(p => p.Members).HasForeignKey(m => m.ProposalId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(m => m.User).WithMany(u => u.Memberships).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentType>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(200);
            // Names are the user's handle on a category, so they have to stay distinct per proposal.
            b.HasIndex(t => new { t.ProposalId, t.Name }).IsUnique();
            b.HasOne(t => t.Proposal).WithMany(p => p.DocumentTypes).HasForeignKey(t => t.ProposalId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Document>(b =>
        {
            b.Property(d => d.Name).HasMaxLength(500);
            b.Property(d => d.OriginalFilePath).HasMaxLength(1024);
            b.HasOne(d => d.Proposal).WithMany(p => p.Documents).HasForeignKey(d => d.ProposalId).OnDelete(DeleteBehavior.Cascade);
            // Restrict, not Cascade: deleting a proposal already cascades from Proposal, and a
            // type may only be removed once it holds no documents.
            b.HasOne(d => d.DocumentType).WithMany(t => t.Documents).HasForeignKey(d => d.DocumentTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentVersion>(b =>
        {
            b.HasIndex(v => new { v.DocumentId, v.CreatedAt });
            b.HasOne(v => v.Document).WithMany(d => d.Versions).HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(v => v.CreatedBy).WithMany().HasForeignKey(v => v.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FinalProposalVersion>(b =>
        {
            b.Property(v => v.Label).HasMaxLength(500);
            b.HasIndex(v => new { v.ProposalId, v.Number }).IsUnique();
            b.HasOne(v => v.Proposal).WithMany(p => p.FinalProposalVersions).HasForeignKey(v => v.ProposalId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(v => v.CreatedBy).WithMany().HasForeignKey(v => v.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FinalProposalFile>(b =>
        {
            b.Property(f => f.Name).HasMaxLength(500);
            b.Property(f => f.OriginalFilePath).HasMaxLength(1024);
            b.HasOne(f => f.Version).WithMany(v => v.Files).HasForeignKey(f => f.VersionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Artifact>(b =>
        {
            b.HasIndex(a => new { a.ProposalId, a.Type }).IsUnique();
            b.Property(a => a.RowVersion).IsRowVersion();
            b.HasOne(a => a.Proposal).WithMany(p => p.Artifacts).HasForeignKey(a => a.ProposalId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArtifactVersion>(b =>
        {
            b.HasIndex(v => new { v.ArtifactId, v.CreatedAt });
            b.HasOne(v => v.Artifact).WithMany(a => a.Versions).HasForeignKey(v => v.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(v => v.CreatedBy).WithMany().HasForeignKey(v => v.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChatSession>(b =>
        {
            b.HasOne(s => s.Proposal).WithMany(p => p.ChatSessions).HasForeignKey(s => s.ProposalId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessage>(b =>
        {
            b.HasIndex(m => new { m.ChatSessionId, m.CreatedAt });
            b.HasOne(m => m.ChatSession).WithMany(s => s.Messages).HasForeignKey(m => m.ChatSessionId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(m => m.Author).WithMany().HasForeignKey(m => m.AuthorId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AiUsageRecord>(b =>
        {
            b.Property(r => r.Model).HasMaxLength(100);
            b.Property(r => r.Label).HasMaxLength(256);
            b.Property(r => r.ErrorMessage).HasMaxLength(1024);
            b.Property(r => r.EstimatedCostUsd).HasPrecision(18, 6);
            // RequestText/ResponseText stay unconstrained (nvarchar(max)); SQL Server keeps
            // them off-row, and the aggregation queries project scalars only.
            b.HasIndex(r => new { r.ProposalId, r.StartedAt });
            b.HasIndex(r => r.OperationId);
            b.HasOne(r => r.Proposal).WithMany(p => p.AiUsage).HasForeignKey(r => r.ProposalId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(r => r.StartedBy).WithMany().HasForeignKey(r => r.StartedById).OnDelete(DeleteBehavior.SetNull);
        });

        // Stable ids so the default users exist in every environment (spec: elv@ and sda@).
        modelBuilder.Entity<User>().HasData(
            new User
            {
                Id = Guid.Parse("6f9e2f9e-0001-4a7e-9f10-000000000001"),
                Email = "elv@mannaz.com",
                DisplayName = "Emil",
                CreatedAt = DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
            },
            new User
            {
                Id = Guid.Parse("6f9e2f9e-0001-4a7e-9f10-000000000002"),
                Email = "sda@mannaz.com",
                DisplayName = "sda",
                CreatedAt = DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
            });

        modelBuilder.Entity<MannazVoiceSettings>().HasData(new MannazVoiceSettings
        {
            Id = Guid.Parse("6f9e2f9e-0002-4a7e-9f10-000000000001"),
            UpdatedAt = DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
        });
    }
}
