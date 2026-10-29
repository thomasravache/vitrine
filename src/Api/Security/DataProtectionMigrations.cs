using System.Xml.Linq;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;

namespace Vitrine.Api.Security;

/// <summary>
/// Migrações do schema <c>security</c> (tabela de chaves do Data Protection) como recurso com estado (JasperFx), como as
/// dos módulos: aplicadas só pelo modo <c>--migrate</c>, pelo host de teste e no desenvolvimento local (ADR-0011).
/// Durante a rotação da chave mestra (<c>DataProtection:PreviousMasterKey</c> configurada), o mesmo passo recifra com a
/// chave atual as chaves do anel ainda cifradas pela anterior.
/// </summary>
internal sealed class DataProtectionMigrations(IServiceScopeFactory scopes)
    : SystemPartBase("Chaves do Data Protection (EF Core)", new Uri("efcore://security"))
{
    public override ValueTask<IReadOnlyList<IStatefulResource>> FindResources() =>
        new([new MigrationsResource(scopes)]);

    private sealed class MigrationsResource(IServiceScopeFactory scopes)
        : StatefulResourceBase("EF Core migrations", DataProtectionKeysDbContext.Schema, new Uri("efcore://security"), new Uri("postgresql://security"))
    {
        private static readonly XName EncryptedSecret = XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection");

        public override async Task Setup(CancellationToken token)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();
            await db.Database.MigrateAsync(token);

            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            if (!string.IsNullOrWhiteSpace(configuration[$"{DataProtectionMasterKeyOptions.Section}:{nameof(DataProtectionMasterKeyOptions.PreviousMasterKey)}"]))
            {
                await ReencryptAsync(db, scope.ServiceProvider, token);
            }
        }

        // Rotação: toda chave legível só pela chave anterior é recifrada com a atual (as demais ficam como estão).
        private static async Task ReencryptAsync(DataProtectionKeysDbContext db, IServiceProvider services, CancellationToken token)
        {
            var masterKey = services.GetRequiredService<DataProtectionMasterKey>();
            var logger = services.GetRequiredService<ILogger<DataProtectionMasterKey>>();
            int reencrypted = 0, current = 0;

            foreach (var key in await db.DataProtectionKeys.ToListAsync(token))
            {
                if (string.IsNullOrEmpty(key.Xml))
                {
                    continue;
                }

                var document = XElement.Parse(key.Xml);
                var changed = false;
                foreach (var secret in document.Descendants(EncryptedSecret))
                {
                    if (secret.Element(MasterKeyXml.ElementName) is not { } encrypted)
                    {
                        continue;
                    }

                    var plaintext = MasterKeyXml.Decrypt(encrypted, masterKey, out var usedPrevious);
                    if (usedPrevious)
                    {
                        encrypted.ReplaceWith(MasterKeyXml.Encrypt(plaintext, masterKey.Current));
                        changed = true;
                    }
                }

                if (changed)
                {
                    key.Xml = document.ToString(SaveOptions.DisableFormatting);
                    reencrypted++;
                }
                else
                {
                    current++;
                }
            }

            await db.SaveChangesAsync(token);
            DataProtectionLog.Reencrypted(logger, reencrypted, current);
        }
    }
}
