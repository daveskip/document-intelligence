using DocumentIntelligence.Domain.Entities;
using DocumentIntelligence.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocumentIntelligence.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<ExtractionResult> ExtractionResults => Set<ExtractionResult>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Document>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).ValueGeneratedNever();
            e.Property(d => d.FileName).IsRequired().HasMaxLength(512);
            e.Property(d => d.BlobPath).IsRequired().HasMaxLength(1024);
            e.Property(d => d.ContentType).IsRequired().HasMaxLength(256);
            e.Property(d => d.Status).HasConversion<string>();
            e.Property(d => d.UploadedByUserId).IsRequired().HasMaxLength(450);
            e.HasOne(d => d.ExtractionResult)
                .WithOne(r => r.Document)
                .HasForeignKey<ExtractionResult>(r => r.DocumentId);
            e.HasIndex(d => d.UploadedByUserId);
            e.HasIndex(d => d.UploadedAt);
        });

        builder.Entity<ExtractionResult>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.ModelVersion).IsRequired().HasMaxLength(128);
        });
    }
}
