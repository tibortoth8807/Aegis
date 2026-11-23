using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Incidents;
using Aegis.Domain.News;
using Aegis.Domain.Organization;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aegis.Infrastructure.Persistence;

/// <summary>
/// Code-first model. Workflow statuses are stored as text (readable in SQL), every workflow's history is an owned
/// table, and the employee row carries a concurrency token.
/// </summary>
public sealed class AegisDbContext(DbContextOptions<AegisDbContext> options) : DbContext(options), IAegisDbContext
{
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<TargetSystem> Systems => Set<TargetSystem>();
    public DbSet<SystemRole> Roles => Set<SystemRole>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<AccessGrant> Grants => Set<AccessGrant>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<TrainingAssignment> Assignments => Set<TrainingAssignment>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<NewsItem> News => Set<NewsItem>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no date-time-offset type and cannot compare the text form; store UTC ticks instead
        if (Database.IsSqlite()) builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<string>().HaveMaxLength(400);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Country>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(2);
            e.Property(x => x.Name).HasMaxLength(80);
        });
        b.Entity<Location>(e => e.Property(x => x.CountryCode).HasMaxLength(2));
        b.Entity<Department>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Teams).WithOne(x => x.Department).HasForeignKey(x => x.DepartmentId);
        });

        b.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.EmployeeNumber).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => new { x.Status, x.LastName });
            e.Property(x => x.EmployeeNumber).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.NationalityCode).HasMaxLength(2);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Nationality).WithMany().HasForeignKey(x => x.NationalityCode).OnDelete(DeleteBehavior.Restrict);
            History(e, x => x.History, "EmployeeHistory");
        });

        b.Entity<TargetSystem>(e =>
        {
            e.ToTable("Applications");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Roles).WithOne(x => x.System).HasForeignKey(x => x.SystemId);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Course>().WithMany().HasForeignKey(x => x.RequiredTrainingId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<SystemRole>(e =>
        {
            e.ToTable("ApplicationRoles");
            e.HasIndex(x => new { x.SystemId, x.Name }).IsUnique();
        });

        b.Entity<AccessRequest>(e =>
        {
            e.HasIndex(x => x.Number);
            e.HasIndex(x => new { x.BeneficiaryId, x.Status });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Justification).HasMaxLength(2000);
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Beneficiary).WithMany().HasForeignKey(x => x.BeneficiaryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            History(e, x => x.History, "AccessRequestHistory");
        });
        b.Entity<AccessGrant>(e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.RoleId });
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccessRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Course>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Description).HasMaxLength(2000);
        });
        b.Entity<TrainingAssignment>(e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.CourseId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
            History(e, x => x.History, "TrainingAssignmentHistory");
        });

        b.Entity<Incident>(e =>
        {
            e.ToTable("Tickets");
            e.HasIndex(x => x.Number);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(4);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Resolution).HasMaxLength(2000);
            e.HasOne(x => x.Reporter).WithMany().HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            History(e, x => x.History, "TicketHistory");
        });

        b.Entity<NewsItem>(e => e.Property(x => x.Body).HasMaxLength(8000));
    }

    /// <summary>Each workflow keeps its state changes in its own history table.</summary>
    private static void History<T>(EntityTypeBuilder<T> e, System.Linq.Expressions.Expression<Func<T, IEnumerable<StateChange>?>> nav, string table)
        where T : class =>
        e.OwnsMany(nav, h =>
        {
            h.ToTable(table);
            h.WithOwner().HasForeignKey("OwnerId");
            h.HasKey(x => x.Id);
            h.Property(x => x.Action).HasMaxLength(40);
            h.Property(x => x.From).HasMaxLength(20);
            h.Property(x => x.To).HasMaxLength(20);
            h.Property(x => x.Comment).HasMaxLength(2000);
        });

    /// <summary>Bumps the employee version on every change, so two people editing the same record cannot overwrite each other.</summary>
    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<Employee>().Where(x => x.State == EntityState.Modified)) entry.Entity.Version++;
        return base.SaveChangesAsync(ct);
    }
}
