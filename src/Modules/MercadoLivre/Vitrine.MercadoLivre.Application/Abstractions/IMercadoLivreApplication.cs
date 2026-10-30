namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>O app do Vitrine no DevCenter do ML (configuração <c>MercadoLivre:ClientId</c>).</summary>
public interface IMercadoLivreApplication
{
    /// <summary><c>application_id</c> esperado nas notificações; nulo quando <c>ClientId</c> não está configurado (ou não é numérico).</summary>
    long? ApplicationId { get; }
}
