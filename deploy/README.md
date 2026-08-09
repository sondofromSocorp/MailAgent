# Deploiement sur VPS (Oracle Cloud Always Free)

L'agent + Ollama tournent sur la meme VM : LLM local, **zero cle API, zero cout**.

## 1. Creer la VM (console Oracle Cloud)

1. Compte : https://www.oracle.com/cloud/free/ (region d'accueil : Paris ou Francfort —
   les instances ARM gratuites ne sont creables QUE dans la region d'accueil).
2. Menu **Compute > Instances > Create instance** :
   - **Image** : Ubuntu 24.04 (aarch64).
   - **Shape** : `VM.Standard.A1.Flex` — 4 OCPU / 24 Go RAM (le max Always Free).
     Si « Out of capacity » : reessayer avec 2 OCPU / 12 Go (suffisant pour un 7B),
     un autre Availability Domain, ou plus tard (la capacite ARM fluctue).
   - **Boot volume** : 100 Go (le modele fait ~5 Go, l'espace libre sert aux logs/updates).
   - **SSH keys** : colle ta cle publique (`type %USERPROFILE%\.ssh\id_ed25519.pub` ;
     si tu n'en as pas : `ssh-keygen -t ed25519`).
   - Laisser l'IP publique assignee.
3. Note l'**adresse IP publique** de l'instance.

Aucune ouverture de port n'est necessaire : l'agent ne fait que des connexions sortantes
(IMAP, SMTP, Telegram) et Ollama reste en localhost.

## 2. Installer

```bash
ssh ubuntu@IP_DE_LA_VM
curl -fsSL https://raw.githubusercontent.com/sondofromSocorp/MailAgent/main/deploy/setup-vps.sh | sudo bash
sudo nano /etc/mailagent.env      # remplir les secrets (memes valeurs que les GitHub Secrets)
sudo bash /opt/mailagent/src/deploy/setup-vps.sh   # relancer : installe le service + premiere passe
```

Le script est idempotent : le relancer met a jour le code (`git pull` + rebuild).

## 3. Verifier / exploiter

```bash
journalctl -u mailagent -f              # logs en direct
systemctl list-timers mailagent.timer   # prochaine execution
sudo systemctl start mailagent          # forcer une passe maintenant
```

## 4. Couper le cron GitHub Actions

Une fois le VPS valide (quelques passes OK), desactiver le workflow pour ne pas trier
en double : GitHub > repo **MailAgent** > Actions > workflow « agent » > "..." >
**Disable workflow** (reversible).
