namespace MailAgent.Services;

/// <summary>
/// Cascade de fournisseurs LLM gratuits (Llm:Provider = "free") : essaie chacun dans l'ordre
/// configure et bascule au suivant des qu'un fournisseur echoue (quota gratuit epuise, cle
/// invalide, serveur injoignable). Un fournisseur en echec est mis de cote TEMPORAIREMENT
/// (periode de retenue) puis retente : un quota epuise ne se libere pas en quelques secondes
/// (inutile de le retenter mail apres mail), mais il se libere en minutes/heures — en mode
/// boucle continue (VPS), le processus vit des jours et une mise a l'ecart definitive
/// degraderait la cascade pour toujours vers ses derniers maillons.
/// </summary>
public sealed class FallbackLlmClient(IReadOnlyList<(string Label, ILlmClient Client)> providers) : ILlmClient
{
    // 15 min : couvre les quotas par minute (Groq TPM) et laisse une chance reguliere aux
    // quotas par jour ou aux pannes passageres, sans marteler un fournisseur en echec.
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    private readonly DateTimeOffset[] _retryAt = new DateTimeOffset[providers.Count];

    public async Task<string> CompleteAsync(string system, string userContent, int maxTokens, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var allQuota = true;
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < providers.Count; i++)
        {
            if (_retryAt[i] > now) continue; // en retenue apres un echec recent
            var (label, client) = providers[i];
            try
            {
                return await client.CompleteAsync(system, userContent, maxTokens, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // LlmException (quota, cle...) mais aussi tout crash inattendu d'un client
                // (reponse inattendue, parsing) : un fournisseur defaillant ne doit jamais
                // faire tomber la passe tant qu'il en reste un autre a essayer.
                errors.Add($"{label}: {ex.Message}");
                allQuota &= (ex as LlmException)?.Quota ?? false;
                _retryAt[i] = DateTimeOffset.UtcNow.Add(Cooldown);
                Console.WriteLine($"    [LLM] {label} indisponible ({ex.Message}) : mis de cote {Cooldown.TotalMinutes:0} min.");
            }
        }

        // Plus aucun fournisseur disponible : fatal (stoppe la passe, les mails seront repris
        // plus tard). Si ce ne sont que des quotas — ou si tous etaient deja en retenue —
        // Quota=true evite l'alerte Telegram (la situation se resout seule, sinon spam a
        // chaque passe en boucle continue).
        throw new LlmException(
            errors.Count > 0
                ? "Tous les fournisseurs LLM gratuits ont echoue : " + string.Join(" | ", errors)
                : $"Tous les fournisseurs LLM gratuits sont en periode de retenue apres des echecs recents (reessai automatique sous {Cooldown.TotalMinutes:0} min).",
            fatal: true, quota: allQuota);
    }
}
