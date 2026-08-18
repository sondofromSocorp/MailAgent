namespace MailAgent.Services;

/// <summary>
/// Cascade de fournisseurs LLM gratuits (Llm:Provider = "free") : essaie chacun dans l'ordre
/// configure et bascule au suivant des qu'un fournisseur echoue (quota gratuit epuise, cle
/// invalide, serveur injoignable). La bascule est definitive pour le PROCESSUS courant (une
/// passe GitHub Actions = un processus) : un quota epuise ne se libere pas en cours de passe,
/// inutile de le retenter mail apres mail. Tout redevient disponible a la passe suivante.
/// </summary>
public sealed class FallbackLlmClient(IReadOnlyList<(string Label, ILlmClient Client)> providers) : ILlmClient
{
    private int current;

    public async Task<string> CompleteAsync(string system, string userContent, int maxTokens, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var allQuota = true;
        while (current < providers.Count)
        {
            var (label, client) = providers[current];
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
                errors.Add(ex.Message);
                allQuota &= (ex as LlmException)?.Quota ?? false;
                current++;
                if (current < providers.Count)
                    Console.WriteLine($"    [LLM] {label} indisponible ({ex.Message}) -> bascule vers {providers[current].Label}.");
            }
        }

        // Tous epuises : fatal (stoppe la passe, les mails seront repris plus tard). Si ce ne
        // sont que des quotas, Quota=true evite l'alerte Telegram (ils se reinitialisent seuls).
        throw new LlmException(
            "Tous les fournisseurs LLM gratuits ont echoue : " + string.Join(" | ", errors),
            fatal: true, quota: allQuota);
    }
}
