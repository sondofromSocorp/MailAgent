# MailAgent

Agent .NET 8 qui lit une boite Gmail (IMAP), **trie** les mails via **Claude**
(modele Haiku), **notifie sur Telegram** les mails importants, **dialogue** avec toi
(lecture, resume, reponse avec validation) et **ajoute a Google Agenda** les
evenements dates qu'il detecte.

```
IMAP (Gmail) --> Claude (classification) --> +-- Telegram (notif si important)
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

## 2. Secrets a fournir

L'agent lit ces valeurs depuis les **variables d'environnement** (en local) ou les
**GitHub Secrets** (en CI).

| Variable               | Description                                          |
|------------------------|------------------------------------------------------|
| `IMAP_USER`            | ton adresse Gmail (sert aussi a l'envoi SMTP)        |
| `IMAP_PASS`            | **mot de passe d'application** Gmail (16 car.)       |
| `ANTHROPIC_API_KEY`    | cle API Anthropic (`sk-ant-...`)                     |
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
    ├── EmailClassifier        classification via Claude
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
