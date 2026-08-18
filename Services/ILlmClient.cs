namespace MailAgent.Services;

/// <summary>
/// Abstraction d'un modele de langage (LLM). Une seule operation : a partir d'un prompt systeme
/// et d'un message utilisateur, renvoyer le texte de la reponse (souvent du JSON, que l'appelant
/// parse). Implementations : <see cref="ClaudeLlmClient"/> (API Anthropic, cloud, payant),
/// <see cref="OllamaLlmClient"/> (modele local via Ollama, gratuit), <see cref="OpenAiCompatLlmClient"/>
/// (tiers gratuits parlant l'API OpenAI : GitHub Models, Groq, Gemini) et <see cref="FallbackLlmClient"/>
/// (cascade de fournisseurs gratuits). Le fournisseur est choisi par configuration (Llm:Provider).
/// </summary>
public interface ILlmClient
{
    /// <param name="system">Prompt systeme (instructions).</param>
    /// <param name="userContent">Message utilisateur.</param>
    /// <param name="maxTokens">Longueur maximale de la reponse generee.</param>
    /// <returns>Le texte brut de la reponse du modele.</returns>
    Task<string> CompleteAsync(string system, string userContent, int maxTokens, CancellationToken ct = default);
}

/// <summary>
/// Erreur renvoyee par le fournisseur LLM. <see cref="Fatal"/> = probleme d'INFRASTRUCTURE qui
/// touche TOUS les mails (credits Claude epuisses, cle invalide, serveur Ollama injoignable ou
/// modele absent) : inutile de tenter les autres mails de la passe en cours.
/// <see cref="Quota"/> = limite d'un tier gratuit atteinte (429) : elle se reinitialise seule
/// (minute/jour), donc on attend la passe suivante SANS alerter Telegram (sinon spam).
/// </summary>
public sealed class LlmException(string message, bool fatal, bool quota = false) : Exception(message)
{
    public bool Fatal { get; } = fatal;
    public bool Quota { get; } = quota;
}
