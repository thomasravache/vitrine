using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.Domain.Workspaces;
using Vitrine.Identity.IntegrationEvents;

namespace Vitrine.Identity.Application.Register;

/// <summary>
/// Cadastro: cria a conta, o workspace (com o usuário como Owner) e publica <see cref="WorkspaceCreated"/> pelo
/// outbox — tudo na mesma transação (ADR-0014). Não abre a sessão: isso é da borda HTTP.
/// </summary>
public sealed class RegisterCommandHandler(
    IUserAccounts users,
    IWorkspaceRepository workspaces,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<RegisterResult> HandleAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        var errors = RegisterCommandValidator.Validate(command);
        if (errors.Count > 0)
        {
            return new RegisterResult.Invalid(errors);
        }

        var name = command.Name.Trim();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var creation = await users.CreateAsync(name, command.Email.Trim(), command.Password, cancellationToken);
        switch (creation)
        {
            case UserCreation.EmailTaken:
                return new RegisterResult.EmailTaken();
            case UserCreation.Rejected rejected:
                return new RegisterResult.Invalid(rejected.Errors);
        }

        var userId = ((UserCreation.Created)creation).UserId;
        var workspace = Workspace.CreateFor(new RegisteredUser(userId, name), clock.GetUtcNow());
        workspaces.Add(workspace);
        foreach (var created in workspace.DomainEvents.OfType<WorkspaceCreatedDomainEvent>())
        {
            await unitOfWork.PublishAsync(new WorkspaceCreated(created.WorkspaceId, created.OwnerUserId, created.CreatedAt));
        }

        workspace.ClearDomainEvents();

        await transaction.CommitAsync(cancellationToken);
        return new RegisterResult.Registered(userId, workspace.Id);
    }
}
