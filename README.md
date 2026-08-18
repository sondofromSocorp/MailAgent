# MailAgent

Agent .NET 8 qui lit une boite Gmail (IMAP), **trie** les mails via un **LLM**
(Claude Haiku par l'API Anthropic, ou un modele local via **Ollama**), **notifie sur
Telegram** les mails importants, **dialogue** avec toi (lecture, resume, reponse avec
validation) et **ajoute a Google Agenda** les evenements dates qu'il detecte.

```
IMAP (Gmail) --> LLM (classification) -----> +-- Telegram (notif si important)
                                             +-- rangement par dossiers
                                             +-- Google Agenda (evenements dates)

Telegram <----> bot conversationnel (lis / resume / repond avec validation)
```

Tourne **dans le cloud** sur **GitHub Actions** (cron toutes les 5 min), pas sur ton
poste. Mode reel par defaut (`Agent:DryRun = false`).

**Anti-doublon portable** : chaque mail traite recoit un **keyword IMAP standard**
`MailAgentNotified` (pas de label Gmail proprietaire, pas de fichier d'etat — le disque
GitHub Actions est ephemere, l'etat vit dans la boite). Marche sur tout serveur IMAP.

---

## 1. Ce que fait l'agent

- **Classement** : chaque mail est range dans un dossier selon sa **nature**
  (`Factures`, `Banque`, `Immobilier`, `ReseauxSociaux`, `Pub`, `Communication`),
  avec sous-dossier par emetteur pour certaines natures (ex. `Factures/Bouygues`).
  Les mails ranges sont marques lus (fait baisser le compteur de non-lus).
- **Notification Telegram** : un mail est notifie s'il demande une **action** ou
  concerne une **personne/sujet prioritaire** (et qu'il n'a pas deja recu de reponse).
  Un mail important reste **toujours en boite**, jamais range.
- **Quarantaine, jamais de suppression definitive** : les indesirables vont dans le
  dossier `ASupprimer` (deplacement reversible). Les expediteurs listes dans
  `Classifier:BlockedSenders` partent direct a la corbeille Gmail (recuperable 30 j).
- **Heures silencieuses** (22:00–06:30, Europe/Paris) : les notifs sont reportees
  apres la plage ; le rangement n'est pas affecte.
- **Bot conversationnel Telegram** : tu peux interroger ta boite, demander un resume,
  ou faire **rediger une reponse**. Garde-fou : **aucun mail n'est envoye sans ta
  validation explicite** (« oui »).
- **Agenda auto** (Google Calendar) : detecte les evenements dates, cree l'evenement
  et te previent. *Code present mais inactif tant que les secrets Google ne sont pas
  fournis (voir §2).*

### Choix du LLM (`Llm:Provider`)

- `"claude"` (defaut) : API Anthropic, modele `Claude:Model`. Necessite
  `ANTHROPIC_API_KEY` et des credits.
- `"ollama"` : modele local via [Ollama](https://ollama.com), **gratuit et sans cle
  API** — pense pour un agent auto-heberge (VPS). Configure `Ollama:BaseUrl`
  (defaut `http://localhost:11434`) et `Ollama:Model` (defaut `qwen2.5:7b`, a
  installer avant : `ollama pull qwen2.5:7b`). Ne fonctionne pas sur GitHub Actions
  (pas de serveur Ollama) : ce mode suppose que l'agent tourne sur la machine qui
  heberge Ollama.
- `"free"` (**actif par defaut**) : cascade de **tiers gratuits** parlant l'API OpenAI,
  essayes dans l'ordre de `Free:Providers` avec bascule automatique quand un quota
  gratuit est epuise (429) :
  1. **GitHub Models** (`openai/gpt-4.1-mini`, ~150 req/jour) — sur GitHub Actions,
     **aucune cle a creer** : le `GITHUB_TOKEN` du job suffit (permission `models: read`
     dans le workflow). En local : un PAT classique avec le scope `models:read` dans
     `GITHUB_MODELS_TOKEN`.
  2. **Groq** (`llama-3.3-70b-versatile`) — cle gratuite sur `console.groq.com/keys`,
     a mettre dans le secret `GROQ_API_KEY`.
  3. **Gemini** (`gemini-2.5-flash`) — cle gratuite sur `aistudio.google.com/apikey`,
     a mettre dans le secret `GEMINI_API_KEY`.

  Un fournisseur sans cle est simplement **saute**. Le comportement de l'agent est
  identique quel que soit le fournisseur : meme prompt, meme parsing (voir
  `EmailClassifier`) — seul le transport change (`OpenAiCompatLlmClient` +
  `FallbackLlmClient`). Quotas tous epuises = la passe s'arrete en silence (pas de
  spam Telegram), les mails sont repris a la passe suivante.

### Plusieurs boites mail (`Accounts`)

Par defaut (liste `Accounts` vide), l'agent surveille la boite unique configuree par les
sections globales `Imap`/`Classifier` et les secrets `IMAP_USER`/`IMAP_PASS`. Pour
surveiller plusieurs boites, **chacune avec ses propres criteres**, declare-les :

```json
"Accounts": [
  { "Name": "perso", "UserEnv": "IMAP_USER", "PassEnv": "IMAP_PASS",
    "Classifier": { "PriorityTopics": [ "Nayeli" ] } },
  { "Name": "pro", "UserEnv": "IMAP_USER_PRO", "PassEnv": "IMAP_PASS_PRO",
    "Imap": { "Folders": [ "Clients", "Fournisseurs", "Pub" ], "MaxPerPass": 30 },
    "Classifier": { "ExtraInstructions": "Boite professionnelle : tout mail d'un client est prioritaire." } }
]
```

- Chaque boite a ses `Folders`, `PriorityTopics`/`PrioritySenders`, `BlockedSenders` et
  des consignes libres `ExtraInstructions` injectees dans le prompt du modele. Les champs
  omis prennent les valeurs standard (pas celles de la section globale).
- Les secrets (`UserEnv`/`PassEnv`) sont a creer dans GitHub et a exposer dans
  `agent.yml` (lignes d'exemple commentees). Une boite sans secrets est **sautee**.
- En multi-boites, les notifications Telegram sont prefixees `[nom]`.
- L'assistant conversationnel (reponses aux mails, desabonnements) reste lie a la
  **premiere** boite de la liste.

### L'assistant Telegram

En parlant au bot tu peux : poser une question / demander un resume (contexte : les 30
derniers mails), demander **tes mails importants** (« quels mails dois-je traiter ? » —
classe les non-repondus de la boite), **retrouver un mail** (« retrouve le mail de Mme X » —
recherche expediteur/objet sur tout le compte, archives comprises), **repondre a un mail**
(brouillon soumis a ta validation explicite), te **desabonner** d'une newsletter, et
**purger la conversation** (« efface nos messages », limite Telegram : 48h).

### Desabonnement a la demande (Telegram)

Dis au bot « desabonne-moi de X » : il retrouve le mail, lit son en-tete standard
`List-Unsubscribe` et effectue le desabonnement **one-click** (RFC 8058) ou envoie le
mail de desinscription (`mailto:`). S'il n'y a qu'un lien web manuel, il te l'envoie.
Jamais automatique sur simple classification : cliquer le lien d'un spam confirmerait
que l'adresse est active.

## 2. Secrets a fournir

L'agent lit ces valeurs depuis les **variables d'environnement** (en local) ou les
**GitHub Secrets** (en CI).

| Variable               | Description                                          |
|------------------------|------------------------------------------------------|
| `IMAP_USER`            | ton adresse Gmail (sert aussi a l'envoi SMTP)        |
| `IMAP_PASS`            | **mot de passe d'application** Gmail (16 car.)       |
| `ANTHROPIC_API_KEY`    | cle API Anthropic (`sk-ant-...`) — requise seulement si `Llm:Provider` = `claude` |
| `GROQ_API_KEY`         | (optionnel, mode `free`) cle Groq — 2e maillon de la cascade |
| `GEMINI_API_KEY`       | (optionnel, mode `free`) cle Google AI Studio — 3e maillon |
| `TELEGRAM_BOT_TOKEN`   | token du bot, fourni par @BotFather                  |
| `TELEGRAM_CHAT_ID`     | identifiant de ton chat (destinataire des notifs)    |

Optionnel — pour activer l'**agenda auto** :

| Variable               | Description                                          |
|------------------------|------------------------------------------------------|
| `GOOGLE_CLIENT_ID`     | client OAuth Google                                  |
| `GOOGLE_CLIENT_SECRET` | secret client OAuth Google                           |
| `GOOGLE_REFRESH_TOKEN` | refresh token (scope `calendar.events`)              |

Le **mot de passe d'application Gmail** se genere sur
`myaccount.google.com/apppasswords` (la validation en 2 etapes doit etre active avant).

### Creer le bot Telegram

1. Sur Telegram, parle a **@BotFather**, commande `/newbot`, recupere le **token**.
2. Ecris un message a ton nouveau bot.
3. Recupere ton `chat_id` via
   `https://api.telegram.org/bot<TOKEN>/getUpdates` (champ `chat.id`).

## 3. Lancer en local (PowerShell, Windows)

```powershell
$env:IMAP_USER="ton.adresse@gmail.com"
$env:IMAP_PASS="mot_de_passe_application_16_car"
$env:ANTHROPIC_API_KEY="sk-ant-..."
$env:TELEGRAM_BOT_TOKEN="123456:ABC-..."
$env:TELEGRAM_CHAT_ID="1234567890"

dotnet restore
dotnet run
```

Par defaut `RunOnce = true` (une passe, puis sortie — adapte au cron CI). Pour une
boucle continue en local, passe `Agent:RunOnce` a `false` dans `appsettings.json`
(intervalle = `PollIntervalSeconds`). Pour un essai sans rien modifier, mets
`Agent:DryRun` a `true`.

## 4. Deploiement (GitHub Actions)

Le workflow `.github/workflows/agent.yml` lance une passe **toutes les 5 minutes**
(cron, best-effort) et peut etre declenche a la main (`workflow_dispatch`). Renseigne
les secrets ci-dessus dans **Settings > Secrets and variables > Actions**.

> Pour activer l'agenda auto en CI, ajoute aussi les trois `GOOGLE_*` au bloc `env:`
> du workflow et aux secrets du repo.

## 5. Structure

```
MailAgent/
├── Program.cs                 orchestration d'une passe
├── appsettings.json           config non-sensible
├── Configuration/AgentConfig  config typee
├── Models/                    EmailItem, Classification
└── Services/
    ├── EmailReader            lecture IMAP (MailKit)
    ├── ILlmClient             abstraction LLM (+ ClaudeLlmClient / OllamaLlmClient)
    ├── EmailClassifier        classification via le LLM configure
    ├── EmailSender            envoi SMTP + brouillon en attente
    ├── INotifier              abstraction du canal de notif
    ├── TelegramNotifier       notifications Telegram
    ├── TelegramConversation   bot conversationnel (lire / resumer / repondre)
    └── GoogleCalendar         creation d'evenements (OAuth refresh token)
```

## 6. Pistes suivantes

- **Activer l'agenda** : generer le refresh token Google et brancher les `GOOGLE_*`
  dans le workflow CI.
- **Temps reel** : passer du cron a un webhook Telegram pour reduire la latence.
- **Multi-comptes** : l'anti-doublon (keyword IMAP) et l'absence de specifique Gmail
  permettent de brancher d'autres boites.
