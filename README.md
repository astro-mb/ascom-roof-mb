# Toit coulissant ASCOM — pilotage NINA via Arduino

Driver **ASCOM Dome** et firmware **Arduino Nano** permettant de piloter depuis NINA le toit
coulissant d'un observatoire astronomique entraîné par une **motorisation de portail**.

**Version actuelle : driver 2.0** (protocole série 1.0). Le firmware n'a pas changé : passer du
driver 1.2 au 2.0 ne demande pas de reflasher l'Arduino.

### Nouveautés de la 2.0

- Le nom, la description et le *Driver info* affichés dans NINA et le Chooser ASCOM sont raccourcis à
  « Drivers Ascom toit Obs principal Version 2026-02 ».
- **Toit ouvert ou fermé à la main** (télécommande du portail, débrayage) : le firmware lève
  `SENSOR_LOST` quand le toit quitte sa fin de course sans commande. Jusqu'en 1.2 l'erreur restait
  affichée dans NINA une fois l'autre fin de course atteinte. En 2.0, dès qu'une fin de course est
  active, le driver envoie lui-même `CLEARERR` (qui recale aussi le cycle du portail) et NINA affiche
  `Open` ou `Closed`. Entre les deux fins de course, le toit reste en `Error`.
- Banc de test : scénario 13 de `tools\test-sim.ps1` pour ce cas.

---

## Le problème

La motorisation de portail n'expose qu'**une seule entrée de commande** : un contact sec
impulsionnel, sans indication de sens. Chaque impulsion fait avancer d'un cran dans le cycle :

```
OUVRE → STOP → FERME → STOP → OUVRE → …
```

Un Arduino Nano ferme ce contact par un relais et lit deux fins de course (toit fermé / toit ouvert).
Tout l'enjeu du projet est de transformer cette commande « aveugle » en un véritable automate
`OUVRIR` / `FERMER` / `ARRÊTER` fiable, puis de l'exposer à NINA au standard ASCOM.

### Là où c'est subtil

Depuis une fin de course, le sens de la prochaine impulsion est **certain** — le toit ne peut aller
que dans un sens. Mais après un arrêt à mi-course, il dépend entièrement de l'historique. Le firmware
**modélise donc l'état interne du moteur** (variable `next`) et se resynchronise automatiquement dès
qu'une fin de course est atteinte.

Conséquence concrète : l'impulsion qui suit un arrêt à mi-course inverse le sens. Repartir dans le
**même** sens qu'avant l'arrêt coûte donc **3 impulsions** (démarrer à l'envers, arrêter, repartir),
avec un bref déplacement parasite. C'est inévitable avec une commande unique.

---

## Architecture

```
   NINA
     │  ASCOM Dome (COM in-process, .NET Framework 4.8)
     ▼
  ASCOM.ArduinoRoof.Dome
     │  USB série 115200 8N1, protocole ASCII ligne à ligne
     ▼
  Arduino Nano  ──── relais ────►  entrée de commande du portail
     ▲   ▲
     │   └── capteur fin de course « toit ouvert »
     └────── capteur fin de course « toit fermé »
```

**L'automate vit dans l'Arduino, pas dans le driver.** Si le PC plante, se met en veille ou perd
l'USB, une course en cours va à son terme et l'état reste cohérent. Le driver n'est qu'un client.

---

## Contenu du dépôt

| Chemin | Rôle |
|---|---|
| `docs/PROTOCOLE.md` | **Contrat** firmware ↔ driver : protocole série, automate, codes d'erreur |
| `docs/CABLAGE.md` | Schéma, brochage, avertissements de sécurité électrique |
| `docs/TESTS.md` | Procédure de recette pas à pas |
| `firmware/RoofController/` | Firmware Arduino Nano |
| `driver/ASCOM.ArduinoRoof.Dome/` | Driver ASCOM (C#, .NET Framework 4.8) |
| `tools/flash.ps1` | Compile et téléverse le firmware |
| `tools/build.ps1` | Compile le driver |
| `tools/register.ps1` / `unregister.ps1` | Enregistrement COM 32 + 64 bits (**administrateur**) |
| `tools/test-sim.ps1` | Scénarios de panne sur le simulateur intégré (port `SIM`), sans matériel |

`docs/PROTOCOLE.md` fait autorité : toute modification du dialogue série doit y être reportée
**avant** d'être codée d'un côté ou de l'autre.

---

## ⚠ Avant toute mise sous tension

Avec un **relais actif HIGH**, la broche de commande est *flottante* pendant le reset et le
bootloader du Nano — soit environ une seconde **à chaque fois que le driver ouvre le port série**
(le signal DTR reset la carte).

> **Une résistance de tirage de 10 kΩ entre la broche du relais et GND est indispensable.**
> Sans elle, le relais peut coller au démarrage et déclencher le toit à chaque connexion de NINA.

Le détail est dans `docs/CABLAGE.md`, et l'étape 3 de `docs/TESTS.md` sert précisément à le vérifier.

---

## Installation

### Prérequis

- ASCOM Platform 7 ([ascom-standards.org](https://ascom-standards.org/))
- SDK .NET 8 — `winget install Microsoft.DotNet.SDK.8`
- Arduino IDE (fournit l'`arduino-cli` utilisé par `flash.ps1`)

### Firmware

```bash
powershell -ExecutionPolicy Bypass -File tools\flash.ps1 -Port COM3
```

Sur un clone Nano à ancien bootloader, ajouter `-Fqbn arduino:avr:nano:cpu=atmega328old`.

### Driver

```bash
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

Puis dans un terminal **administrateur** :

```bash
powershell -ExecutionPolicy Bypass -File tools\register.ps1
```

Le driver apparaît alors dans le Chooser ASCOM sous
**« Drivers Ascom toit Obs principal Version 2026-02 »**.

> Ce mode enregistre le driver depuis `bin\Release`, ce qui convient à une machine de
> développement. Pour un PC d'exploitation, utiliser le paquet `release\` décrit ci-dessous : il
> installe la DLL dans un dossier stable, indépendant de l'arborescence des sources.

---

## Déploiement sur le PC de l'observatoire

Le dossier **`release/`** est un paquet autonome : il contient la DLL et le firmware **déjà
compilés**, plus les scripts d'installation. Le PC cible n'a donc besoin ni du SDK .NET ni des
sources — seulement d'ASCOM Platform 7.

Ses binaires sont **versionnés**, contrairement à toutes les autres sorties de compilation. C'est
délibéré : un simple `git pull` sur le PC de l'observatoire apporte une DLL et un firmware
cohérents avec le commit qui les a produits.

**Sur le PC de développement**, après toute modification :

```bash
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

puis commiter et pousser. **Sur le PC de l'observatoire** : `git pull`, puis suivre
[release/README-INSTALLATION.md](release/README-INSTALLATION.md).

> `install.ps1` copie la DLL dans `C:\Program Files\ASCOM ArduinoRoof\` avant de l'enregistrer.
> C'est important : `regasm /codebase` inscrit un chemin **absolu** dans le registre. Enregistrer
> depuis un dossier de compilation rendrait le driver dépendant de l'arborescence de développement
> — dépôt déplacé, nettoyé ou recompilé, et le driver disparaît en pleine nuit.

---

## Configuration dans NINA

1. *Equipment → Dome → Choisir* → sélectionner le driver.
2. Bouton *Setup* : choisir le port COM du Nano et régler les temporisations
   (elles sont poussées vers le firmware à chaque connexion).
3. Connecter.

Les contrôles d'azimut doivent apparaître **grisés** : c'est la signature d'un toit roulant
(`CanSetAzimuth = false`), NINA n'utilise alors que *Open Shutter* et *Close Shutter*.

Dans le séquenceur avancé, utiliser les instructions **« Open Dome Shutter »** et
**« Close Dome Shutter »**. `Park` n'est volontairement pas implémenté : un toit roulant n'a pas de
position de parking au sens ASCOM.

### Sécurité anti-collision

Le driver **ne vérifie pas** que la monture est parquée avant de fermer. Cette responsabilité est
laissée à NINA : parquer la monture **avant** de fermer le toit dans les séquences. Un verrou
matériel est listé dans les évolutions possibles ci-dessous.

### Reprise automatique des ouvertures et fermetures

Sur le firmware, toute erreur (`TIMEOUT_START`, `TIMEOUT_TRAVEL`, `SENSOR_LOST`…) bloque les
mouvements jusqu'à un acquittement — que NINA ne sait pas envoyer. Jusqu'à la version 1.0 du
driver, une seule anomalie suffisait donc à laisser le toit ouvert jusqu'au matin.

Depuis la version 1.1, chaque *Open Shutter* / *Close Shutter* est une **mission** que le driver
mène à terme. En cas d'erreur, ou si une erreur était déjà présente, il attend que le toit soit
immobile, acquitte, puis relance la course — au plus **3 fois** par défaut. Pendant ce temps NINA
voit `Opening` / `Closing` et continue d'attendre.

| Cas | Reprise |
|---|---|
| Impulsion ignorée par le portail (`TIMEOUT_START`) | relance immédiate depuis la butée |
| Toit lent, arrivé après `TRAVEL` | constat de l'arrivée, succès |
| Toit bloqué à mi-course (`TIMEOUT_TRAVEL`) | attente de `TRAVEL` ms, puis `RECOVER` dans le sens voulu |
| Arrivée sur la mauvaise butée (`WRONG_DIRECTION`) | relance depuis cette butée |
| Erreur levée pendant la nuit (`SENSOR_LOST`, `INTERRUPTED`) | acquittement puis course |
| `BOTH_SENSORS`, perte de liaison, `AbortSlew` | **aucune** reprise |

> ⚠ **Ce que la reprise peut faire bouger.** Depuis une position inconnue, `RECOVER` émet une
> impulsion dont le sens dépend du portail : le toit peut parcourir une course complète **dans le
> mauvais sens** avant que la reprise suivante ne le ramène. C'est le prix d'une fermeture garantie
> sans intervention. Les deux réglages de la boîte de configuration permettent de limiter ce
> comportement : nombre de reprises (0 = aucune), et reprise automatique depuis une position inconnue.

L'action ASCOM `Roof:Mission` décrit la mission en cours ou la dernière terminée, avec la
chronologie de chaque étape : c'est le premier réflexe après un incident.

### ⚠ Débloquer une erreur — à lire avant la première nuit

Les reprises automatiques acquittent elles-mêmes les erreurs lors de la prochaine ouverture ou
fermeture. Une erreur ne reste donc bloquante que si les reprises sont désactivées (réglage à 0),
épuisées, ou en cas de `BOTH_SENSORS`.

**L'onglet Dome de NINA n'a aucun bouton « effacer l'erreur ».** Le driver expose bien l'action
`Roof:ClearError`, mais NINA ne permet pas d'appeler une action ASCOM arbitraire depuis son
interface. La seule porte de sortie est donc la **boîte de configuration du driver** :

1. Déconnecter le dôme dans NINA, puis bouton *Setup*.
2. Dans le panneau de test : **Connecter**, puis **Effacer erreur**.
3. **Cliquer Déconnecter dans la boîte avant de la fermer** — sinon elle garde le port COM et NINA
   ne pourra plus se reconnecter.

Le meilleur réflexe avant d'acquitter : **activer la fin de course correspondant à la position
réelle du toit**. L'invariant capteurs recale alors la position sur une preuve physique, et
l'acquittement la conserve au lieu de la dégrader en `UNKNOWN` — ce qui évite un `RECOVER`, lequel
peut lancer une course complète à contresens.

### Régler `START` et `TRAVEL` sur le moteur réel

Ces deux délais dépendent du moteur, comme `PULSE` (voir plus bas). Ils doivent être **mesurés**,
pas devinés.

| Paramètre | Ce qu'il borne | À mesurer |
|---|---|---|
| `START` | temps accordé au capteur de départ pour se relâcher | délai entre l'impulsion et le début effectif du mouvement |
| `TRAVEL` | durée maximale d'une course complète | durée d'une ouverture ou fermeture entière |

**Être généreux, car les deux erreurs ne coûtent pas la même chose :**

* Valeur **trop courte** → erreur alors que tout fonctionne. La séquence s'interrompt, la position
  est dégradée, et le déblocage exige une intervention manuelle. En pleine nuit et à distance, c'est
  la nuit d'observation perdue.
* Valeur **trop longue** → un blocage réel met quelques secondes de plus à être signalé. Sans
  conséquence : le moteur de portail a ses propres fins de course et sa protection thermique.

Le risque étant asymétrique, prendre confortablement large : **environ 4 à 5 ×** le délai de
démarrage mesuré pour `START`, et **environ 1,75 ×** la durée de course pour `TRAVEL`. La marge
couvre aussi le froid, le givre et le vent, qui ralentissent une course sans qu'il y ait panne.

> Attention aux motorisations qui **font clignoter un feu orange avant de démarrer** : le délai de
> démarrage peut y atteindre 3 à 5 secondes.

Valeurs mesurées sur cette installation (06/10/2026) : démarrage **0,8 s** au plus après la fin de
l'impulsion, course complète **21,6 s** au plus → `START = 5000`, `TRAVEL = 40000`.

Un troisième réglage dépend du portail : **`PULSE`**, la durée de l'impulsion. Trop courte, le
relais claque mais le portail l'ignore, et chaque course finit en `TIMEOUT_START`. Celui de
l'observatoire ne démarre pas avec les 600 ms par défaut du firmware : `PULSE = 1000`, et
`INTER = 1000`, validé sur la séquence à 3 impulsions. Depuis la version 1.2, ce sont aussi les
**défauts du driver** : un Profile ASCOM réinitialisé ne bloque plus le toit. Le firmware garde
600 ms, sans effet en exploitation puisque le driver pousse ses valeurs à chaque connexion.

---

## États et affichage

| Réalité | `ShutterStatus` NINA |
|---|---|
| Toit ouvert, à l'arrêt | `Open` |
| Toit fermé, à l'arrêt | `Closed` |
| Ouverture en cours | `Opening` |
| Fermeture en cours | `Closing` |
| Arrêté à mi-course, position inconnue, erreur, liaison perdue | `Error` |
| Reprise automatique en cours après une erreur | `Opening` / `Closing` (inchangé) |

La norme ASCOM n'a pas d'état « partiellement ouvert ». Un toit arrêté à mi-course est donc rapporté
en `Error` : c'est volontaire, **un toit non fermé ne doit jamais être annoncé comme fermé**. Une
option de configuration permet de le rapporter en `Open` si ce comportement gêne.

---

## Dépannage

| Symptôme | Piste |
|---|---|
| Le driver ne se connecte pas | Vérifier le port COM. Le Nano est reseté à l'ouverture du port : le driver attend 2,5 s puis tente 3 handshakes. Vérifier au moniteur série que `PING` répond `PONG`. |
| `err = TIMEOUT_START` sur le moteur réel alors que le toit part bien | `START` est plus court que le délai de pré-clignotement du portail. Voir « Régler `START` sur le moteur réel » plus haut. |
| Port COM occupé alors que le dôme est déconnecté | La boîte de configuration a peut-être gardé la liaison : y cliquer **Déconnecter**. Sinon, une autre application (moniteur série de l'IDE Arduino) tient le port. |
| Le driver ne se connecte que par intermittence, ou pas du tout | Ne pas brancher le Nano derrière un concentrateur USB : le périphérique s'énumère correctement mais les transferts de données échouent (`ERROR_GEN_FAILURE`). Le brancher **directement** sur le PC. |
| Impossible de recompiler le driver : DLL verrouillée | Déconnecter le dôme **ne suffit pas** — NINA a chargé l'assembly dans son processus et .NET ne sait pas le décharger. **Fermer complètement NINA** avant de recompiler. |
| Le relais clique au branchement de l'USB | **Résistance de tirage 10 kΩ absente ou mal câblée.** Débrancher le moteur et corriger avant toute autre chose. |
| `Error` immédiat, `err = BOTH_SENSORS` | Les deux capteurs sont vus fermés : câblage inversé, capteur collé ou masse commune manquante. |
| `err = TIMEOUT_START` | L'impulsion n'a rien produit : moteur hors tension, contact non raccordé, relais HS, ou `PULSE` trop courte pour le portail. |
| `err = TIMEOUT_TRAVEL` | La course n'a pas abouti : obstacle, moteur bloqué, capteur de fin de course HS, ou `TRAVEL` réglé trop court. |
| Le toit part en butée dans le mauvais sens après un arrêt | `INTER` est **trop court** : le portail ignore l'impulsion d'arrêt de la séquence à 3 impulsions. L'augmenter. |
| Déplacement parasite trop important au changement de sens | `INTER` est trop long : le réduire, sans descendre sous le temps de prise en compte du portail. |
| `Error` avec `err = INTERRUPTED` | Coupure d'alimentation en pleine course. La position est perdue : la prochaine ouverture ou fermeture la reprend automatiquement (par `RECOVER`) ; sinon, action `Roof:Recover` **sous surveillance visuelle**. |
| `Roof:Mission` affiche `CONNEXION … position du toit inconnue, sans erreur` | À l'ouverture du port, la carte était en position inconnue sans erreur : en général une coupure en pleine course suivie de deux redémarrages, qui efface `INTERRUPTED` (driver ≥ 1.2, voir `docs/TESTS.md` étape 3). La prochaine ouverture ou fermeture reprend par `RECOVER`. |
| NINA : `Failed to close dome shutter. Current shutter state: ShutterError` | Lire l'action `Roof:Mission` (chronologie de la dernière mission) et la trace du driver. Reprises épuisées : vérifier le portail et les capteurs ; `BOTH_SENSORS` : câblage. |

Les traces du driver s'activent dans la boîte de configuration et s'écrivent dans
`Documents\ASCOM\Logs …`. Elles journalisent chaque commande et chaque réponse : c'est l'outil de
diagnostic principal. **Les laisser activées en permanence sur le PC de l'observatoire** : sans
elles, un incident nocturne ne laisse aucune trace exploitable. Côté NINA, régler le niveau de
journal sur `Info` au minimum.

Le port **`SIM`** de la boîte de configuration branche le driver sur un simulateur intégré
(firmware et portail). Il permet de passer ConformU et de rejouer des pannes (action `Sim:Fault`,
script `tools\test-sim.ps1`) sans matériel ni risque pour le toit.

Le **panneau de test** intégré à la boîte de configuration permet de tout valider sans lancer NINA.

---

## Évolutions possibles

- Verrou matériel anti-collision « monture parquée » (entrée contact sec supplémentaire sur le Nano)
- Capteur de pluie et driver `SafetyMonitor` associé
- Bouton poussoir manuel local sur l'Arduino
- Installateur Inno Setup via l'*ASCOM Installer Generator* de la Platform

---

## Références

- [ASCOM COM Developer](https://ascom-standards.org/COMDeveloper/Index.htm)
- [Interface IDomeV3](https://ascom-standards.org/newdocs/dome.html)
- [FAQ Dome ASCOM](https://ascom-standards.org/newdocs/dome-faq.html)
- [NINA — onglet Dome](https://nighttime-imaging.eu/docs/master/site/tabs/equipment/dome/)
