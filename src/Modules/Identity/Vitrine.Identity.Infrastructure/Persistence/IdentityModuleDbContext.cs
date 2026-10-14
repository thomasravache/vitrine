using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Vitrine.Identity.Domain.Workspaces;
using Vitrine.Identity.Infrastructure.Users;

namespace Vitrine.Identity.Infrastructure.Persistence;

/// <summary>
/// DbContext do módulo Identity: tabelas do ASP.NET Core Identity (sem papéis globais — o papel é por workspace)
/// e workspaces, todas no schema <c>identity</c> (ADR-0001, ADR-0011), com nomes em snake_case.
/// </summary>
public sealed class IdentityModuleDbContext(DbContextOptions<IdentityModuleDbContext> options)
    : IdentityUserContext<User, Guid>(options)
{
    public const string Schema = "identity";

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(Schema);

        builder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.Name).HasMaxLength(100).IsRequired();
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        builder.Entity<Workspace>(workspace =>
        {
            workspace.ToTable("workspaces");
            workspace.HasKey(w => w.Id);
            workspace.Property(w => w.Id).ValueGeneratedNever();
            workspace.Property(w => w.Name).HasMaxLength(100).IsRequired();
            workspace.Ignore(w => w.DomainEvents);
            workspace.HasMany(w => w.Members).WithOne().HasForeignKey(m => m.WorkspaceId);
            workspace.Navigation(w => w.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<WorkspaceMember>(member =>
        {
            member.ToTable("workspace_members");
            member.HasKey(m => new { m.WorkspaceId, m.UserId });
            member.HasIndex(m => m.UserId);
            member.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
            member.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
        });

        UseSnakeCaseColumns(builder);
    }

    private static void UseSnakeCaseColumns(ModelBuilder builder)
    {
        foreach (var property in builder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    private static string ToSnakeCase(string name)
    {
        var snake = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && !char.IsUpper(name[i - 1]))
            {
                snake.Append('_');
            }

            snake.Append(char.ToLowerInvariant(name[i]));
        }

        return snake.ToString();
    }
}
