# Installation sur le PC de l'observatoire

Ce dossier contient tout le nécessaire pour installer le driver et le firmware sur une machine
**sans SDK .NET ni sources** : la DLL et le `.hex` sont déjà compilés.

---

## Prérequis

| Prérequis | Vérification |
|---|---|
| **ASCOM Platform 7** | <https://ascom-standards.org/> — `install.ps1` refuse de continuer sans elle |
| **.NET Framework 4.8** | intégré à Windows 10 (1903+) et Windows 11 — rien à faire en général |
| **IDE Arduino** *(recommandé)* | <https://www.arduino.cc/en/software> — pour flasher la carte et surtout pour le **moniteur série**, outil de diagnostic principal à la mise en service |

Aucun SDK .NET n'est nécessaire.

---

## 1. Récupérer le paquet

```bash
git clone https://github.com/astro-mb/ascom-roof-mb.git
```

Pour une mise à jour ultérieure, `git pull` suffit : les binaires de ce dossier sont versionnés et
suivent les sources.

---

## 2. Installer le driver

Dans un terminal **PowerShell administrateur**, depuis ce dossier :

```bash
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Le script copie la DLL dans `C:\Program Files\ASCOM ArduinoRoof\` puis l'enregistre auprès de COM en
**32 et 64 bits** — les deux sont nécessaires, NINA étant 64 bits et le Chooser ASCOM 32 bits.

> **Pourquoi un dossier d'installation séparé des sources** : `regasm /codebase` inscrit un chemin
> **absolu** dans le registre. Enregistrer depuis un dossier de compilation rendrait le driver
> dépendant de l'arborescence de développement — dépôt déplacé, nettoyé ou recompilé, et le driver
> disparaît en pleine nuit.

L'avertissement `RA0000` sur l'assembly non signée est normal : c'est le cas de la quasi-totalité
des drivers ASCOM.

---

## 3. Flasher l'Arduino

### ⚠ Identifier la bonne carte

Un observatoire compte souvent plusieurs Arduino (focuser, roue à filtres, contrôleur
d'alimentation…). Les clones à puce **CH340 ne portent aucun numéro de série** : ils se présentent
tous sous le même nom, et rien ne les distingue dans la liste des ports.

Téléverser sur la mauvaise carte **écraserait définitivement le firmware d'un autre équipement**.

Le script interroge donc chaque port et indique lequel répond :

```bash
powershell -ExecutionPolicy Bypass -File .\flash-firmware.ps1 -ListPorts
```

```
  COM3     USB-SERIAL CH340 (COM3)
           -> repond, mais ce n'est PAS le toit : OK FOCUSER v2
  COM4     USB-SERIAL CH340 (COM4)
           -> CONTROLEUR DE TOIT : PONG ROOF 1.0 1.1.0
  COM7     USB-SERIAL CH340 (COM7)
           -> muet (carte neuve, autre firmware, ou port occupe)
```

L'interrogation ouvre chaque port sans activer DTR ni RTS : les autres cartes ne redémarrent pas.

**Avant tout téléversement, le port cible est interrogé.** Si le contrôleur de toit ne s'y identifie
pas, le script **refuse** et n'écrit rien.

### Première installation

Une carte neuve ne contient pas encore le firmware : elle ne peut donc pas s'identifier, et
`-Force` sera nécessaire. Pour être certain du port :

1. Carte du toit **débranchée** → `-ListPorts`, noter les ports présents
2. Brancher la carte du toit
3. Relancer `-ListPorts` : **le port apparu est le bon**

```bash
powershell -ExecutionPolicy Bypass -File .\flash-firmware.ps1 -Port COM4 -Force
```

Les fois suivantes, la carte s'identifiant d'elle-même, `-Force` devient inutile :

```bash
powershell -ExecutionPolicy Bypass -File .\flash-firmware.ps1 -Port COM4
```

Le script vérifie après coup que la carte répond bien `PONG ROOF`.

Si le téléversement échoue, réessayer avec `-OldBootloader` : les clones de Nano à puce CH340
utilisent très souvent l'ancien bootloader.

> ⚠ **Ne pas brancher le Nano derrière un concentrateur USB.** Le périphérique s'énumère
> correctement mais les transferts de données échouent (`ERROR_GEN_FAILURE`), ce qui donne un port
> COM visible mais inutilisable. Le brancher **directement** sur le PC.

---

## 4. Configurer

Dans NINA : *Equipment → Dome → Choisir* → **« Drivers Ascom toit Obs principal Version 2026-02 »**, puis
bouton *Setup*.

À régler :

| Réglage | Valeur | Origine |
|---|---|---|
| Port COM | celui du Nano | relevé par `flash-firmware.ps1 -ListPorts` |
| `PULSE` | **1000 ms** | durée de fermeture du contact ; le portail de l'observatoire **ne démarre pas** avec 600 ms |
| `INTER` | **1000 ms** | au plus court **tout en restant fiable** (voir ci-dessous) ; 1000 ms validé sur le portail |
| `START` | **5000 ms** | ≈ 6 × le délai de démarrage mesuré (0,8 s après la fin de l'impulsion) |
| `TRAVEL` | **40000 ms** | ≈ 1,85 × la course la plus longue mesurée (21,6 s) |
| `DEBOUNCE` | 50 ms | à augmenter si un capteur vibre |
| Reprises automatiques | **3** | reprise des ouvertures / fermetures en échec ; 0 = aucune |
| Reprise depuis une position inconnue | **cochée** | autorise `RECOVER` automatique (course à contresens possible) |
| Trace | **cochée** | indispensable pour diagnostiquer un incident nocturne |

**`PULSE` doit être assez long pour que le portail prenne l'impulsion en compte** : trop court, le
relais claque mais rien ne se passe, et chaque ouverture finit en `TIMEOUT_START`. Le portail de
l'observatoire exige 1000 ms ; les 600 ms par défaut du firmware ne suffisent pas. Depuis la
version 1.2 du driver, 1000 ms est aussi le défaut de la boîte de configuration pour `PULSE` et
`INTER`.

**`START` et `TRAVEL` sont les deux délais liés au moteur.** Ils sont volontairement
généreux, car les deux erreurs ne se valent pas : une valeur trop courte déclenche une erreur alors
que tout fonctionne — séquence interrompue, position dégradée, intervention manuelle nécessaire, donc
nuit perdue si c'est à distance ; une valeur trop longue ne fait que retarder de quelques secondes la
détection d'un vrai blocage, sans conséquence.

Sur un autre moteur, les remesurer : délai avant démarrage effectif, et durée d'une course complète.
Attention aux motorisations qui **font clignoter un feu orange avant de démarrer**, ce délai pouvant
y atteindre 3 à 5 secondes.

**`INTER`** conditionne la séquence à 3 impulsions utilisée pour repartir dans le même sens après
un arrêt à mi-course (l'impulsion qui suit un arrêt inverse le sens du portail). Trop long, le toit
part loin dans le mauvais sens ; trop court, le portail ignore l'impulsion d'arrêt et le toit file
en butée du mauvais côté. À régler sur le matériel réel.

---

## 5. Recette

Dérouler `..\docs\TESTS.md` **avant de rebrancher le moteur**. Les étapes 0 à 3 se font moteur
débranché et valident tout le reste.

> ⚠ **Point de sécurité n° 1 : la résistance de tirage 10 kΩ** entre la broche de commande du relais
> et GND. Sans elle, le relais peut coller pendant le reset de la carte — qui se produit
> potentiellement **à chaque connexion du driver**. L'étape 3 de la recette sert précisément à le
> vérifier. Voir `..\docs\CABLAGE.md`.

---

## Débloquer une erreur

Avec les reprises automatiques (réglage par défaut), la prochaine ouverture ou fermeture acquitte
l'erreur et relance la course d'elle-même. La procédure manuelle ci-dessous ne sert que si les
reprises sont désactivées ou épuisées, ou en cas de `BOTH_SENSORS`.

Sinon, après une erreur (`TIMEOUT_TRAVEL`, `SENSOR_LOST`…), le toit refuse tout mouvement tant
qu'elle n'est pas acquittée. C'est voulu.

**L'onglet Dome de NINA n'a pas de bouton pour cela.** Le déblocage passe par la boîte de
configuration du driver :

1. Déconnecter le dôme dans NINA, puis bouton *Setup*.
2. **Activer d'abord la fin de course correspondant à la position réelle du toit** — la position
   sera alors recalée sur une preuve physique et conservée à l'acquittement, au lieu d'être dégradée
   en « inconnue ».
3. Panneau de test : **Connecter**, puis **Effacer erreur**.
4. **Cliquer Déconnecter dans la boîte avant de la fermer**, sinon elle garde le port COM.

Éviter `Reprise` (`RECOVER`) tant qu'une fin de course peut être atteinte autrement : une reprise
depuis une position inconnue peut faire parcourir au toit une course complète dans le sens non
désiré.

---

## Mettre à jour

Sur le PC de développement, après modification :

```bash
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

puis commiter et pousser. Sur le PC de l'observatoire :

```bash
git pull
```

puis, en administrateur, `install.ps1` (qui désenregistre proprement la version précédente) et
`flash-firmware.ps1` si le firmware a changé.

**Les réglages sont conservés** : `install.ps1` relit le port COM, les temporisations, les
reprises et la trace avant de désenregistrer l'ancienne version, puis les réécrit après
l'enregistrement de la nouvelle. Il affiche les valeurs conservées, et en garde une copie dans
`C:\Program Files\ASCOM ArduinoRoof\reglages-avant-installation.reg`. Après une mise à jour, un coup
d'œil à la boîte de configuration reste de bonne pratique.

> Les `install.ps1` livrés avec les drivers 1.0 et 1.1 ne faisaient pas cette sauvegarde : le
> désenregistrement de l'ancienne version efface les réglages du Profile ASCOM, et l'enregistrement
> de la nouvelle réécrit les valeurs par défaut (port `COM3`, `PULSE` 600 ms…). Après une mise à
> jour faite avec un ancien `install.ps1`, **vérifier et corriger les réglages** dans la boîte de
> configuration.

> Fermer **complètement NINA** avant toute réinstallation : déconnecter le dôme ne suffit pas, .NET
> ne sait pas décharger un assembly d'un domaine d'application et la DLL reste verrouillée.

---

## Désinstaller

```bash
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Retirer le driver du Chooser **supprime ses réglages** du Profile ASCOM. `uninstall.ps1` les
exporte donc d'abord dans le dossier *Documents* (`ArduinoRoof-reglages-<date>.reg`) et affiche la
commande pour les réimporter après une réinstallation, dans un terminal administrateur :

```bash
reg import "C:\Users\<utilisateur>\Documents\ArduinoRoof-reglages-<date>.reg"
```
