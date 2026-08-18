using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportTicketing.Domain.Organizations;

namespace SupportTicketing.Infrastructure.Persistence.Configurations;

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations");

        builder.Property(o => o.Name).HasMaxLength(200).IsRequired();
        builder.Property(o => o.Code).HasMaxLength(20).IsRequired();
        builder.Property(o => o.Description).HasMaxLength(1000);
        builder.Property(o => o.LogoUrl).HasMaxLength(500);
        builder.Property(o => o.ContactEmail).HasMaxLength(256);
        builder.Property(o => o.ContactPhone).HasMaxLength(50);
        builder.Property(o => o.TimeZoneId).HasMaxLength(100).IsRequired();
        builder.Property(o => o.TicketPrefix).HasMaxLength(10).IsRequired();

        // Filtered so an archived organization's code can be reused.
        builder.HasIndex(o => o.Code)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Organizations_Code");

        builder.HasMany(o => o.Offices)
            .WithOne(x => x.Organization!)
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(o => o.Departments)
            .WithOne(x => x.Organization!)
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OfficeConfiguration : IEntityTypeConfiguration<Office>
{
    public void Configure(EntityTypeBuilder<Office> builder)
    {
        builder.ToTable("Offices");

        builder.Property(o => o.Name).HasMaxLength(200).IsRequired();
        builder.Property(o => o.Code).HasMaxLength(20).IsRequired();
        builder.Property(o => o.AddressLine1).HasMaxLength(200);
        builder.Property(o => o.AddressLine2).HasMaxLength(200);
        builder.Property(o => o.City).HasMaxLength(100);
        builder.Property(o => o.State).HasMaxLength(100);
        builder.Property(o => o.Country).HasMaxLength(100);
        builder.Property(o => o.PostalCode).HasMaxLength(20);
        builder.Property(o => o.TimeZoneId).HasMaxLength(100).IsRequired();

        builder.HasIndex(o => new { o.OrganizationId, o.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Offices_Org_Code");
    }
}

public sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments");

        builder.Property(d => d.Name).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Code).HasMaxLength(20).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(1000);

        builder.HasIndex(d => new { d.OrganizationId, d.Code })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UX_Departments_Org_Code");

        builder.HasOne(d => d.ParentDepartment)
            .WithMany(d => d.ChildDepartments)
            .HasForeignKey(d => d.ParentDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.Office)
            .WithMany()
            .HasForeignKey(d => d.OfficeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
