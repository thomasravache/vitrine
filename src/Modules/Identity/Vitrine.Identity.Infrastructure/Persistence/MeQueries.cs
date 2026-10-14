using Microsoft.EntityFrameworkCore;
using Vitrine.Identity.Application.CurrentUser;

namespace Vitrine.Identity.Infrastructure.Persistence;

internal sealed class MeQueries(IdentityModuleDbContext db) : IMeQueries
{
    public Task<MeView?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        (from member in db.WorkspaceMembers.AsNoTracking()
         where member.UserId == userId
         join workspace in db.Workspaces on member.WorkspaceId equals workspace.Id
         join user in db.Users on member.UserId equals user.Id
         orderby workspace.CreatedAt
         select new MeView(
             user.Id,
             user.Name,
             user.Email!,
             new MeWorkspaceView(workspace.Id, workspace.Name, member.Role.ToString())))
        .FirstOrDefaultAsync(cancellationToken);
}
