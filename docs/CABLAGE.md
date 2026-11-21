# Câblage du contrôleur de toit coulissant

Document de montage du contrôleur Arduino Nano pilotant un moteur de portail
à entrée impulsionnelle unique.

Ce document décrit le **câblage de référence** correspondant aux `#define` en tête de
`firmware/RoofController/RoofController.ino`. Toute modification du câblage réel doit être
répercutée dans ces `#define` — et **uniquement** dans ces `#define`, la logique du firmware
n'a pas à être touchée.

---

## ⚠ AVERTISSEMENT N°1 — RÉSISTANCE DE TIRAGE 10 kΩ ENTRE D5 ET GND ⚠

```
 +--------------------------------------------------------------------------+
 |                                                                          |
 |   UNE RESISTANCE DE 10 kOhm ENTRE D5 ET GND EST ***INDISPENSABLE***.      |
 |                                                                          |
 |   SANS ELLE, LE TOIT PEUT SE DECLENCHER TOUT SEUL A CHAQUE FOIS QUE      |
 |   NINA SE CONNECTE.                                                      |
 |                                                                          |
 +--------------------------------------------------------------------------+
```

**Pourquoi c'est le point de sécurité numéro un du projet :**

1. Le module relais utilisé ici est **actif HIGH** : il colle quand `D5` est au niveau haut.
2. Pendant le **reset** et toute l'exécution du **bootloader** (environ **1 seconde**), la broche
   `D5` n'est pas encore configurée en sortie : elle est **flottante**. Son niveau dépend alors
   uniquement des fuites, des capacités parasites et de la longueur du câble — il peut très bien
   être interprété comme un niveau HAUT par le module relais.
3. Or l'Arduino Nano est **remis à zéro à chaque ouverture du port série** par le signal **DTR**.
   Autrement dit : **à chaque connexion du driver ASCOM, donc à chaque fois que NINA se connecte**,
   et aussi à chaque ouverture du moniteur série de l'IDE Arduino.
4. Résultat sans résistance : le relais peut coller pendant ~1 s à chaque connexion. Pour un moteur
   de portail séquentiel, **une impulsion = un ordre de mouvement**. Le toit part.

**La résistance R1 = 10 kΩ entre `D5` et `GND`** maintient la ligne au niveau bas tant que le
microcontrôleur ne la pilote pas activement. Elle est totalement transparente en fonctionnement
normal : la sortie du Nano (40 mA max) tire sans peine 0,5 mA dans 10 kΩ.

> **À vérifier au multimètre AVANT toute mise sous tension du moteur** — voir la checklist en fin
> de document. C'est la première mesure de la liste, et elle n'est pas négociable.

*Note : si votre module relais est **actif BAS** (modules opto-isolés courants), passez
`RELAY_ACTIVE_HIGH` à `0` dans le firmware et remplacez la résistance de tirage à la masse par une
résistance de tirage de 10 kΩ **vers le 5 V**. Le principe est identique : forcer l'état inactif
pendant que la broche est flottante.*

---

## 1. Tableau des broches

| Broche Nano | `#define` firmware | Direction | Niveau actif | Raccordement |
|---|---|---|---|---|
| `D5`  | `RELAY_PIN` (`RELAY_ACTIVE_HIGH = 1`) | sortie | `HIGH` = relais collé | Entrée `IN` du module relais **+ R1 10 kΩ vers GND** |
| `D3`  | `SENSOR_CLOSED_PIN` (`SENSOR_ACTIVE_LOW = 1`) | entrée `INPUT_PULLUP` | `LOW` = contact fermé | Capteur « toit fermé », contact NO vers `GND` |
| `D2`  | `SENSOR_OPEN_PIN` (`SENSOR_ACTIVE_LOW = 1`) | entrée `INPUT_PULLUP` | `LOW` = contact fermé | Capteur « toit ouvert », contact NO vers `GND` |
| `D13` | `LED_PIN` (`LED_ENABLED = 1`) | sortie | `HIGH` = allumée | LED intégrée à la carte (aucun câblage) |
| `5V`  | — | alimentation | — | `VCC` du module relais (voir §4 sur l'alimentation) |
| `GND` | — | masse | — | `GND` du module relais + commun des deux capteurs + R1 |
| `USB` | — | — | — | PC (liaison série 115200 bauds + alimentation de la carte) |

Codes couleur suggérés pour le câblage terrain : rouge = `5V`, noir = `GND`, jaune = `D5`,
vert = `D3` (fermé), bleu = `D2` (ouvert).

> ⚠ **`D3` = fermé, `D2` = ouvert.** Ne pas permuter ces deux fils.
>
> Une inversion ne provoque **aucune erreur** : le toit paraît simplement fonctionner à l'envers, et
> le client annonce « fermé » alors qu'il est ouvert. C'est une panne **silencieuse**, donc le mode
> de défaillance le plus dangereux de ce montage.
>
> À vérifier sur **chaque** carte, à l'étape 1 de `TESTS.md` : activer le capteur « toit fermé » doit
> donner `SWC=1` dans `DIAG`, jamais `SWO=1`.

---

## 2. Schéma — Nano ↔ relais ↔ moteur

```
         ARDUINO NANO                              MODULE RELAIS 1 VOIE
                                                     (actif HIGH)
    +-------------------+                       +-------------------------+
    |                   |                       |                         |
    |               5V o+-----------------------+o VCC                    |
    |                   |                       |                         |
    |               D5 o+---------+-------------+o IN                     |
    |                   |         |             |                         |
    |                   |        +-+            |                         |
    |                   |    R1  | |            |   o       o       o     |
    |                   |   10 k | |            |  COM      NO      NC    |
    |                   |        +-+            +---+-------+-------------+
    |                   |         |                 |       |
    |              GND o+---------+-------------+   |       |
    |                   |                       |   |       |
    +---------+---------+                       |   |       |
              |                                 |   |       |
        cable USB                          o GND|   |       |
        vers le PC                     (module) +---+       |
                                                    |       |
                                                    |       |
                              ......................|.......|......................
                              :  ISOLATION GALVANIQUE  (contact sec, cf. §5)     :
                              :.................|.......|........................:
                                                |       |
                                          +-----+-------+-----------------------+
                                          |    COM     NO                       |
                                          |                                     |
                                          |   Entree impulsionnelle "PAS A PAS" |
                                          |   du coffret du moteur de portail   |
                                          |                                     |
                                          |   (borne bouton-poussoir / START)   |
                                          +-------------------------------------+
```

* `R1` (10 kΩ) est soudée **au plus près de la broche `D5`**, entre `D5` et `GND`. Ne pas la
  reporter à l'autre bout d'un câble de 3 m : elle doit maintenir la ligne bas *localement*.
* Le contact utilisé côté relais est **`COM` + `NO`** (normalement ouvert) : au repos le circuit
  est ouvert, aucune impulsion n'est envoyée. `NC` reste inutilisé.
* Chaque fermeture de `COM`/`NO` dure `PULSE` ms (600 ms par défaut, bornée en dur à 2000 ms par
  `MAX_PULSE_MS` dans le firmware).

---

## 3. Schéma — Nano ↔ capteurs de fin de course

Les deux capteurs sont des contacts **NO** (normalement ouverts) ramenés à la masse. Les broches
sont configurées en `INPUT_PULLUP` : le pull-up interne (~30 kΩ) maintient l'entrée à `HIGH` tant
que le contact est ouvert, et le contact fermé tire l'entrée à `LOW`.

```
         ARDUINO NANO
    +-------------------+
    |                   |          Capteur "TOIT FERME"  (contact NO)
    |               D3 o+----------o        /o------------------+
    |                   |                  /                    |
    |                   |          (ferme lorsque le toit       |
    |                   |           est en butee fermeture)     |
    |                   |                                       |
    |                   |          Capteur "TOIT OUVERT" (contact NO)
    |               D2 o+----------o        /o------------------+
    |                   |                  /                    |
    |                   |          (ferme lorsque le toit       |
    |                   |           est en butee ouverture)     |
    |                   |                                       |
    |              GND o+---------------------------------------+
    |                   |
    +-------------------+
```

**Convention retenue par le firmware et par le protocole :**

| Réalité physique | Broche | `swClose` / `swOpen` remontés par `STATE` et `DIAG` |
|---|---|---|
| contact **fermé** (toit en butée) | `LOW` | `1` |
| contact **ouvert** (toit ailleurs) | `HIGH` | `0` |

La normalisation est faite dans le firmware (`SENSOR_ACTIVE_LOW`) : le driver ASCOM reçoit
toujours `1 = contact fermé`, quelle que soit la polarité électrique réelle.

**Recommandations de pose :**

* Préférer des **contacts à galet** ou des **détecteurs inductifs NPN NO** aux ILS (interrupteurs
  à lame souple), plus sensibles aux vibrations et au gel.
* Câbler en **paire torsadée blindée** si la longueur dépasse 5 m ; relier le blindage à `GND`
  côté Nano **uniquement** (un seul point de masse, pour éviter les boucles).
* Le firmware applique un anti-rebond de `DEBOUNCE` ms (50 ms par défaut, réglable de 5 à 200 ms
  par `CFG DEBOUNCE <ms>`). La commande `DIAG` affiche `SWC=<brut>/<filtré>` : un écart persistant
  entre les deux valeurs signale un capteur qui vibre ou un câble parasité.
* Régler la position mécanique des capteurs de sorte qu'ils se ferment **légèrement avant** la
  butée réelle du portail, jamais après.

---

## 4. Alimentation du module relais

La bobine d'un relais 5 V consomme typiquement **60 à 90 mA**. Le régulateur 5 V du Nano, surtout
alimenté par l'USB via le régulateur de la carte, est déjà chargé.

**Deux montages acceptables :**

### 4.a — Alimentation par le Nano (montage simple, un seul relais)

```
   Nano 5V  ----> VCC module
   Nano GND ----> GND module
   Nano D5  ----> IN module      (+ R1 10 kOhm de D5 vers GND)
```

Acceptable pour **un seul module relais**, à condition que le cavalier `VCC`/`JD-VCC` du module
soit en place. Ajouter un **condensateur de découplage de 100 µF** entre `VCC` et `GND` au plus
près du module : l'appel de courant à l'enclenchement peut sinon faire chuter le 5 V et
**redémarrer le Nano** — ce qui, sur un contrôleur de toit, est exactement ce qu'on veut éviter.

### 4.b — Alimentation séparée (recommandé, montage définitif)

```
   Alim 5 V externe (>= 500 mA)  ----> JD-VCC module
   GND alim externe              ----> GND module  ----> GND Nano   (masse commune obligatoire)
   Nano 5V                       ----> VCC module  (alimente uniquement l'optocoupleur)
   Nano D5                       ----> IN module   (+ R1 10 kOhm de D5 vers GND)
```

Retirer le cavalier `VCC`/`JD-VCC` pour que la bobine soit alimentée par la source externe et que
**seul l'optocoupleur** soit relié au Nano. La masse doit **rester commune** entre le Nano et la
partie commande du module, sinon l'optocoupleur ne conduit pas.

C'est le montage à privilégier : aucun appel de courant sur le rail du microcontrôleur.

---

## 5. Isolation du contact sec côté moteur

Le coffret du moteur de portail possède sa propre électronique et sa propre masse, souvent
référencée au secteur. **Il ne faut jamais la relier à la masse de l'Arduino.**

* N'utiliser que les bornes **`COM` et `NO` du relais**, qui forment un **contact sec** :
  deux fils, aucune référence de potentiel commune avec l'Arduino.
* Ne **jamais** relier `GND` du Nano à une borne du coffret de portail, même « juste pour tester ».
* Le contact `COM`/`NO` remplace strictement le **bouton-poussoir** de commande du portail
  (entrée « PAS À PAS », « START » ou « CMD » selon les marques). Le repérer dans la notice du
  coffret : c'est la borne sur laquelle on brancherait un bouton mural.
* Vérifier que le calibre du relais couvre l'entrée du coffret. Ces entrées sont en général en
  **très basse tension (12 à 24 V, quelques mA)** : n'importe quel relais de module Arduino
  convient très largement. Si l'entrée est en 230 V — cas rare et à proscrire — **arrêtez-vous et
  faites intervenir un électricien**.
* Câbler cette liaison en **2 fils séparés du reste**, éloignés des câbles capteurs.

---

## 6. Option — supprimer complètement l'auto-reset DTR

La résistance R1 rend le reset DTR **inoffensif**. Si l'on souhaite en plus le **supprimer**
(pour éviter la coupure de ~1 s à chaque connexion de NINA et le message de boot) :

```
   Nano RESET  o----+
                    |
                   ===  C1 = 10 uF electrolytique
                    |    (+ cote RESET, - cote GND)
   Nano GND    o----+
```

Le condensateur maintient la ligne `RESET` chargée et absorbe l'impulsion DTR.

> ### ⚠ CE CONDENSATEUR DOIT ÊTRE RETIRÉ POUR TÉLÉVERSER ⚠
>
> Avec `C1` en place, `avrdude` ne peut plus faire redémarrer la carte en bootloader et le
> téléversement échoue systématiquement (`stk500_getsync(): not in sync`).
> **Le retirer avant chaque `flash.ps1 -Port COMx`, le remettre après.**
>
> En pratique, monter `C1` sur un **bornier à vis ou un connecteur enfichable** plutôt que le
> souder, ou l'installer seulement une fois le firmware définitivement validé.

**Conséquence côté protocole :** avec `C1`, le firmware ne redémarre plus à la connexion et
n'émet plus sa ligne `# ROOF CONTROLLER 1.0.0 BOOT`. Ce n'est pas un problème : le driver ASCOM
ignore les lignes commençant par `#` et se synchronise avec `PING`. Le firmware conserve son état
en RAM, l'EEPROM n'est plus sollicitée à chaque connexion — c'est même un gain de fiabilité.

**Sans `C1`,** le driver respecte l'attente de 2500 ms imposée par le protocole après l'ouverture
du port, ce qui laisse largement le temps au bootloader et au démarrage du firmware.

---

## 7. Checklist de vérification au multimètre

> **À dérouler intégralement AVANT la première mise sous tension du moteur de portail.**
> Le contact `COM`/`NO` doit rester **débranché du coffret de portail** jusqu'à la fin de l'étape 9.

### Phase A — hors tension, tout débranché

| # | Mesure | Instrument | Résultat attendu |
|---|---|---|---|
| 1 | Résistance entre `D5` et `GND` du Nano | ohmmètre | **≈ 10 kΩ** (± 10 %). Si « infini » : **R1 absente ou mal soudée → NE PAS CONTINUER** |
| 2 | Résistance entre `5V` et `GND` du Nano | ohmmètre | quelques kΩ à quelques dizaines de kΩ, **jamais 0 Ω** (0 Ω = court-circuit) |
| 3 | Continuité `GND` Nano ↔ `GND` module relais | continuité | **bip** (masse commune) |
| 4 | Continuité `D3` ↔ `GND`, toit **fermé** | continuité | **bip** (capteur fermeture actionné) |
| 5 | Continuité `D3` ↔ `GND`, toit **ailleurs** | continuité | **pas de bip** |
| 6 | Continuité `D2` ↔ `GND`, toit **ouvert** | continuité | **bip** (capteur ouverture actionné) |
| 7 | Continuité `D2` ↔ `GND`, toit **ailleurs** | continuité | **pas de bip** |
| 8 | Isolation `GND` Nano ↔ borne `COM` **et** ↔ borne `NO` du relais | ohmmètre calibre max | **circuit ouvert / > 1 MΩ**. Si continuité : le contact n'est pas sec → **NE PAS CONTINUER** |
| 9 | Continuité `COM` ↔ `NO` du relais, au repos | continuité | **pas de bip** (contact normalement ouvert) |

### Phase B — Nano alimenté par l'USB, moteur toujours débranché

| # | Mesure | Instrument | Résultat attendu |
|---|---|---|---|
| 10 | Tension `D5` → `GND`, au repos | voltmètre DC | **< 0,5 V** et LED du relais éteinte |
| 11 | **Test du reset DTR** : brancher/débrancher l'USB, ou ouvrir le moniteur série, en observant `D5` et la LED du relais | voltmètre DC + œil | `D5` **reste sous 0,5 V**, le relais **ne claque pas**. Un claquement = R1 inopérante → **NE PAS CONTINUER** |
| 12 | Ouvrir le moniteur série à **115200 bauds**, fin de ligne « Nouvelle ligne » | — | la carte émet `# ROOF CONTROLLER 1.0.0 BOOT` |
| 13 | Envoyer `PING` | — | réponse `PONG ROOF 1.0 1.0.0` |
| 14 | Envoyer `DIAG`, toit fermé | — | `SWC=1/1 SWO=0/0 RELAY=0 ... POS=CLOSED` — vérifier que les capteurs ne sont pas **inversés** |
| 15 | Actionner chaque capteur à la main, `DIAG` après chaque | — | seul le champ concerné bascule ; `<brut>` et `<filtré>` identiques une fois le contact stable |
| 16 | Envoyer `PULSE`, chronomètre en main, en écoutant le relais | oreille / chrono | **un seul** claquement d'enclenchement puis de relâchement, **≈ 0,6 s** entre les deux |
| 17 | Pendant l'impulsion, continuité `COM` ↔ `NO` | continuité | **bip** pendant l'impulsion uniquement |
| 18 | Envoyer `STATE` après le `PULSE` | — | `POS` est passé à `UNKNOWN` (comportement normal : impulsion non surveillée) |
| 19 | Envoyer `CLEARERR` puis `STATE` | — | l'automate se recale sur les capteurs |

### Phase C — première mise sous tension du moteur

| # | Action | Point de vigilance |
|---|---|---|
| 20 | Relier `COM`/`NO` à l'entrée impulsionnelle du coffret, **coffret hors tension** | 2 fils, rien d'autre |
| 21 | Mettre le coffret sous tension, **rester à portée de l'arrêt d'urgence du portail** | — |
| 22 | Toit fermé, envoyer `OPEN`, puis `STATE` toutes les secondes | le toit doit s'ouvrir ; `motion` passe à `OPENING` puis `IDLE`, `pos` à `OPEN` |
| 23 | Envoyer `ABORT` à mi-course lors d'un essai suivant | le toit doit s'arrêter ; `pos` passe à `INTERMEDIATE` |
| 24 | Depuis `INTERMEDIATE`, envoyer `CLOSE` | observer si le toit **repart d'abord dans le mauvais sens** pendant ~1,2 s (séquence à 3 impulsions, réponse `ACK CLOSE MOVING_REVERSE`) — c'est **normal** |
| 25 | Chronométrer une course complète | régler ensuite `CFG TRAVEL <ms>` à environ **1,5 fois** cette durée |
| 26 | Régler `CFG INTER <ms>` | le plus court possible **tout en restant supérieur au temps de prise en compte du portail** ; s'il est trop court, l'impulsion d'arrêt est ignorée et le toit part en butée dans le mauvais sens |

---

## 8. Nomenclature

| Repère | Composant | Quantité | Remarque |
|---|---|---|---|
| — | Arduino Nano (ATmega328P) | 1 | clone CH340 accepté — voir le bootloader ancien dans `tools/flash.ps1` |
| — | Module relais 1 voie 5 V | 1 | de préférence opto-isolé, avec cavalier `VCC`/`JD-VCC` |
| **R1** | **Résistance 10 kΩ 1/4 W** | **1** | **`D5` → `GND`, obligatoire, au plus près de la broche** |
| C2 | Condensateur 100 µF 16 V | 1 | découplage `VCC`/`GND` du module relais |
| C1 | Condensateur 10 µF 16 V | 0 ou 1 | **optionnel** — suppression de l'auto-reset DTR, cf. §6 |
| — | Capteur fin de course NO | 2 | contact à galet ou détecteur inductif NPN NO |
| — | Paire torsadée blindée | selon | liaison capteurs |
| — | Câble 2 conducteurs | selon | liaison contact sec vers le coffret de portail |
| — | Boîtier étanche IP54 minimum | 1 | l'électronique est en extérieur ou en abri non chauffé |

---

## 9. Rappel des règles de sécurité tenues par le firmware

Ces garanties sont implémentées dans `RoofController.ino` (§7 du protocole) et méritent d'être
connues au moment du câblage :

1. **Aucun mouvement spontané** : une impulsion n'est jamais émise à l'initiative du firmware,
   uniquement en réponse à une commande reçue.
2. `RELAY_PIN` est mis à l'état inactif en **toute première instruction** de `setup()`, avant même
   `Serial.begin()`.
3. Le relâchement du relais est piloté par `millis()`, **jamais** par `delay()`, et borné en dur à
   `MAX_PULSE_MS = 2000 ms` quelle que soit la configuration.
4. La **perte de la liaison série n'a aucun effet** sur le toit : une course en cours va à son
   terme, le moteur de portail étant autonome.
5. `ABORT` n'est **jamais** refusé, y compris en état `ERROR`.

Ces protections sont **logicielles**. Elles ne couvrent pas la fenêtre de reset/bootloader :
**seule la résistance R1 protège le montage pendant cette seconde-là.**
