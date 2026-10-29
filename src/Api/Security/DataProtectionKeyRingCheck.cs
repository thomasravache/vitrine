using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Vitrine.Api.Security;

/// <summary>
/// Na subida, fora de Development (contrato v4 da SPEC-0008): se o anel de chaves no PostgreSQL tem chaves cifradas
/// pela chave mestra e nenhuma decifra com a atual (nem com a anterior, na rotação), o host não sobe — senão sessões e
/// campos cifrados se perderiam em silêncio e o Data Protection criaria um anel novo. A mensagem nunca traz a chave.
/// </summary>
internal sealed class DataProtectionKeyRingCheck(IServiceScopeFactory scopes, IHostEnvironment environment) : IHostedLifecycleService
{
    public const string WrongKeyMessage =
        "DataProtection:MasterKey (variável DataProtection__MasterKey) não decifra nenhuma chave do anel do Data Protection "
        + "gravado no banco: a chave mestra está errada. Use a chave com que o anel foi gravado (cofre pessoal) ou, durante "
        + "a rotação, informe-a em DataProtection__PreviousMasterKey (docs/runbooks/rotacao-chave-mestra.md).";

    public const string UnprotectedKeyMessage =
        "O anel do Data Protection no banco tem chave sem a cifra da chave mestra (DataProtection__MasterKey): entrada "
        + "gravada sem proteção ou por outro meio. Remova-a (docs/runbooks/rotacao-chave-mestra.md) antes de subir a Api.";

    private static readonly XName EncryptedSecret = XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection");

    // Antes de qualquer StartAsync: o serviço hospedado do Data Protection carrega o anel na subida e, sem chave que o
    // decifre, criaria (e gravaria) uma chave nova — que esconderia o erro.
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var masterKey = scope.ServiceProvider.GetRequiredService<DataProtectionMasterKey>();
        var db = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();

        List<string?> ring;
        try
        {
            ring = await db.DataProtectionKeys.AsNoTracking().Select(key => key.Xml).ToListAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            // Schema ainda não aplicado (o --migrate cria a tabela): não há anel para conferir.
            return;
        }

        // Toda chave do anel precisa estar cifrada pela chave mestra: entrada em claro (ou cifrada por outro meio) no
        // banco fora de Development é recusada.
        var keys = ring.Where(xml => !string.IsNullOrEmpty(xml)).Select(xml => XElement.Parse(xml!)).ToList();
        var encrypted = new List<XElement>(keys.Count);
        foreach (var key in keys)
        {
            var secret = key.Descendants(EncryptedSecret).Select(element => element.Element(MasterKeyXml.ElementName)).OfType<XElement>().FirstOrDefault();
            if (secret is null)
            {
                throw new InvalidOperationException(UnprotectedKeyMessage);
            }

            encrypted.Add(secret);
        }

        if (encrypted.Count > 0 && !encrypted.Any(element => CanDecrypt(element, masterKey)))
        {
            throw new InvalidOperationException(WrongKeyMessage);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool CanDecrypt(XElement element, DataProtectionMasterKey masterKey)
    {
        try
        {
            _ = MasterKeyXml.Decrypt(element, masterKey, out _);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// Anel de chaves só em memória: usado em Development quando a chave mestra é efêmera, para não acumular no banco
/// chaves que nenhum processo seguinte consegue ler.
/// </summary>
internal sealed class InMemoryXmlRepository : Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository
{
    private readonly List<XElement> _elements = [];
    private readonly Lock _lock = new();

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_lock)
        {
            return [.. _elements.Select(element => new XElement(element))];
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_lock)
        {
            _elements.Add(new XElement(element));
        }
    }
}
