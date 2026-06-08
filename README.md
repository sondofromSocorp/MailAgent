# MailAgent

Agent .NET 8 qui lit les mails **non lus** d'une boite Gmail (IMAP), les trie via
**Claude** (modele Haiku), et envoie une notification **WhatsApp** (Twilio) pour
les mails juges importants.

```
IMAP (Gmail) --> Claude (tri important / pas important) --> WhatsApp (si important)
```

Anti-doublon : chaque mail traite est memorise dans `state.json` (par `Message-Id`),
donc aucune notification en double meme si l'agent tourne en boucle. La boite est
ouverte en **lecture seule** : rien n'est marque comme lu cote serveur.

---

## 1. Prerequis

- .NET 8 SDK
- Un compte Gmail avec **validation en 2 etapes** activee
- Une cle API Anthropic (`console.anthropic.com`)
- Un compte Twilio avec le **sandbox WhatsApp** active

## 2. Secrets a fournir

L'agent lit ces valeurs depuis les **variables d'environnement** (ou user-secrets) :

| Variable              | Description                                        |
|-----------------------|----------------------------------------------------|
| `IMAP_USER`           | ton adresse Gmail                                  |
| `IMAP_PASS`           | **mot de passe d'application** Gmail (16 car.)     |
| `ANTHROPIC_API_KEY`   | cle API Anthropic (`sk-ant-...`)                   |
| `TWILIO_ACCOUNT_SID`  | SID du compte Twilio                               |
| `TWILIO_AUTH_TOKEN`   | Auth token Twilio                                  |

Le **mot de passe d'application Gmail** se genere sur
`myaccount.google.com/apppasswords` (la validation en 2 etapes doit etre active avant).

### Numero WhatsApp

Dans `appsettings.json`, mets ton numero dans `WhatsApp:ToNumber` au format
`whatsapp:+33XXXXXXXXX`. Le `FromNumber` par defaut est le numero du **sandbox**
Twilio (`whatsapp:+14155238886`) — ne pas changer pour les tests.

> Sandbox : depuis ton WhatsApp perso, envoie le code `join <deux-mots>` au numero
> sandbox (visible dans la console Twilio > Messaging > Try it out > WhatsApp).
> Tu peux ensuite recevoir des messages pendant 24h apres ton dernier message.

## 3. Lancer (PowerShell, Windows)

```powershell
$env:IMAP_USER="ton.adresse@gmail.com"
$env:IMAP_PASS="mot_de_passe_application_16_car"
$env:ANTHROPIC_API_KEY="sk-ant-..."
$env:TWILIO_ACCOUNT_SID="ACxxxxxxxx"
$env:TWILIO_AUTH_TOKEN="xxxxxxxx"

dotnet restore
dotnet run
```

Par defaut `RunOnce = true` (une seule passe). Pour une surveillance continue,
passe `Agent:RunOnce` a `false` dans `appsettings.json` (intervalle = `PollIntervalSeconds`).

## 4. Structure

```
MailAgent/
├── Program.cs                 orchestration
├── appsettings.json           config non-sensible
├── Configuration/AgentConfig  config typee
├── Models/                    EmailItem, Classification
└── Services/
    ├── EmailReader            IMAP (MailKit)
    ├── EmailClassifier        appel Claude
    ├── INotifier              abstraction notif
    ├── WhatsAppNotifier       Twilio WhatsApp
    └── NotificationStore      anti-doublon (state.json)
```

## 5. Pistes suivantes

- **WhatsApp production** : remplacer le sandbox par un sender approuve + un
  template de message valide par Meta (notif 24/7 sans la fenetre de 24h).
- **Planification** : Azure Function (timer trigger) ou Task Scheduler Windows.
- **Autres canaux** : implementer `INotifier` pour SMS / appel Twilio / Telegram.
- **Tri plus fin** : enrichir le system prompt, ajouter des categories, ou une
  liste d'expediteurs prioritaires.
