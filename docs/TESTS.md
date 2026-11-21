# Procédure de recette

> **Règle absolue : la sortie du relais reste débranchée du moteur jusqu'à l'étape 4.**
> Les trois premières étapes valident le firmware et le câblage à vide. Tant qu'elles ne sont pas
> toutes vertes, le moteur ne doit pas être connecté.

Cocher chaque case avant de passer à la suivante. En cas d'échec, ne pas contourner : corriger.

---

## Étape 0 — Contrôle du câblage, hors tension

Voir `CABLAGE.md` pour le détail des mesures.

- [ ] Nano **débranché**, moteur **hors tension**.
- [ ] Continuité vérifiée au multimètre entre chaque capteur et sa broche.
- [ ] **Résistance de tirage 10 kΩ entre `D5` et `GND` présente et mesurée.**
      C'est le point de sécurité n°1 : sans elle, le relais peut coller pendant le reset du Nano —
      qui se produit **à chaque connexion de NINA**.
- [ ] Sortie du relais (contact sec) **non reliée** au moteur.
- [ ] Alimentation du module relais vérifiée (5 V, masse commune avec le Nano).

---

## Étape 1 — Firmware à blanc, dialogue série

Téléverser le firmware :

```bash
powershell -ExecutionPolicy Bypass -File tools\flash.ps1 -Port COM3
```

> Si le téléversement échoue sur un clone Nano, relancer avec
> `-Fqbn arduino:avr:nano:cpu=atmega328old` (ancien bootloader).

Ouvrir le moniteur série de l'IDE Arduino à **115200 bauds**, fin de ligne « Nouvelle ligne ».

- [ ] Au démarrage, la ligne `# ROOF CONTROLLER <version> BOOT` s'affiche.
- [ ] `PING` → `PONG ROOF 1.0 <version>`
- [ ] `DIAG` → affiche l'état brut des broches, cohérent avec la réalité physique.
- [ ] `CFG?` → affiche les 5 temporisations par défaut.

**Test des capteurs** — relier temporairement la broche du capteur à `GND` avec un fil :

- [ ] Capteur « fermé » activé → `STATE` renvoie `pos = CLOSED` et `next = OPEN`.
- [ ] Capteur « ouvert » activé → `STATE` renvoie `pos = OPEN` et `next = CLOSE`.

> ⚠ **Ces deux lignes vérifient que les capteurs ne sont pas permutés** (`D3` = fermé, `D2` =
> ouvert). Une permutation ne lève aucune erreur : le toit paraît simplement fonctionner à l'envers
> et le client annonce « fermé » alors qu'il est ouvert. Ne jamais sauter cette vérification sur une
> carte dont le câblage n'a pas été contrôlé.
- [ ] Aucun capteur → `pos` reste cohérent (`INTERMEDIATE` ou `UNKNOWN` selon l'historique).
- [ ] **Les deux capteurs en même temps** → `motion = ERROR`, `err = BOTH_SENSORS`.
- [ ] `CLEARERR` après avoir relâché → l'erreur disparaît.

**Test de perte de capteur** — c'est le garde-fou contre la panne silencieuse la plus dangereuse
(un fil de capteur coupé faisant annoncer « toit fermé » en permanence) :

- [ ] Capteur « fermé » actif, puis `CLEARERR` → `STATE` donne `CLOSED IDLE … NONE`.
- [ ] Relâcher le capteur **moins de 3 s** puis le réactiver → **aucune** erreur (vibration tolérée).
- [ ] **Relâcher le capteur sans rien commander, plus de 3 s** → `UNKNOWN ERROR … SENSOR_LOST`.
      La position ne doit **jamais** rester `CLOSED` au-delà de ces 3 s.
- [ ] `CLEARERR` sans capteur actif → `UNKNOWN`, erreur effacée (le client reste en `shutterError`).
- [ ] Réactiver le capteur « ouvert » → `OPEN IDLE … NONE`, reprise complète.

> Dans `DIAG`, les champs `raw` et `deb` doivent converger en moins de `DEBOUNCE` ms. Un écart qui
> persiste signale un capteur qui vibre ou un câble parasité — augmenter `DEBOUNCE`.

---

## Étape 2 — Impulsions à vide

Relais alimenté, **sortie toujours débranchée du moteur**. On valide au bruit du relais (clic) et à
sa LED.

- [ ] Capteur « fermé » maintenu actif, puis `OPEN` → réponse `ACK OPEN MOVING`, **1 seul clic**.
- [ ] Le capteur « fermé » étant toujours maintenu (le toit ne « bouge » pas), au bout de `START` ms
      (5 s par défaut) → `STATE` passe en `motion = ERROR`, `err = TIMEOUT_START`.
      **C'est le comportement attendu** : le firmware détecte qu'une impulsion n'a rien produit.
- [ ] `CLEARERR` → retour à `IDLE`.

**Séquence à 3 impulsions** — se placer en position intermédiaire (aucun capteur actif) avec
`next = CLOSE`, puis demander `OPEN` :

- [ ] Réponse `ACK OPEN MOVING_REVERSE`.
- [ ] **3 clics** espacés d'environ `INTER` ms.
- [ ] Au bout de `TRAVEL` ms sans capteur → `err = TIMEOUT_TRAVEL`.

**Abort** :

- [ ] Pendant un mouvement, `ABORT` → `ACK ABORT STOPPING`, **1 clic**, `pos = INTERMEDIATE`, et
      `next` a basculé.
- [ ] À l'arrêt, `ABORT` → `ACK ABORT IDLE`, **aucun clic**.

---

## Étape 3 — Reset, EEPROM et sécurité du relais

Le test le plus important de la série : il valide la résistance de tirage.

- [ ] Se placer en position `INTERMEDIATE`, noter la valeur de `next`.
- [ ] Débrancher puis rebrancher l'USB.
- [ ] **Pendant le reset, le relais ne doit émettre AUCUN clic.**
      S'il clique, **arrêter immédiatement la recette** : la résistance de tirage 10 kΩ est absente
      ou mal câblée. Ne jamais brancher le moteur tant que ce point n'est pas résolu.
- [ ] Après redémarrage, `STATE` restaure `pos = INTERMEDIATE` avec le **même** `next`.
- [ ] Depuis cette position, `OPEN` : le mouvement est surveillé pendant `TRAVEL` ms (moteur
      débranché, il finit en `TIMEOUT_TRAVEL`). **Pendant ce mouvement**, moniteur série ouvert,
      appuyer sur le bouton RESET du Nano (à défaut : fermer puis rouvrir le moniteur série, dont
      l'ouverture reset la carte) → `# ROOF CONTROLLER <version> BOOT`, puis `STATE` donne
      `pos = UNKNOWN`, `motion = ERROR`, `err = INTERRUPTED`. Aucun clic du relais pendant le reset.

  > Ne pas faire ce test en débranchant l'USB. Au rebranchement, la première ouverture du port
  > (moniteur série ou driver, même avec DTR inactif) reset la carte une seconde fois. Le premier
  > démarrage a déjà enregistré `moving = 0` : le second ne trouve plus qu'une position `UNKNOWN`
  > sans erreur (§5 du protocole), et `INTERRUPTED` n'apparaît jamais. La sécurité est intacte
  > (position inconnue, tout mouvement refusé), mais le test paraît échouer.
- [ ] Depuis cet état, `OPEN` → `NAK OPEN ERROR` (refus correct : l'erreur est vérifiée avant la
      position, `CLEARERR` est requis).
- [ ] Puis `CLEARERR` sans capteur actif → `pos = UNKNOWN`, et `OPEN` → `NAK OPEN UNKNOWN_POSITION`
      (refus correct : `RECOVER` est requis).

---

## Étape 4 — Raccordement du moteur

À partir d'ici le toit bouge réellement. **Rester à portée de la coupure d'urgence.**

- [ ] Vérifier qu'aucun instrument n'est en position de collision.
- [ ] Relier la sortie du relais à l'entrée de commande du portail.
- [ ] Depuis une fin de course, `OPEN` → le toit s'ouvre et s'arrête sur le capteur « ouvert ».
- [ ] `CLOSE` → le toit se ferme et s'arrête sur le capteur « fermé ».
- [ ] **Chronométrer** une course complète : ....... s
- [ ] Régler `TRAVEL` à environ **1,5 × la durée mesurée** :
      `CFG TRAVEL <valeur_ms>` puis vérifier avec `CFG?`.
- [ ] `ABORT` en pleine course → le toit s'arrête franchement.
- [ ] Depuis cet arrêt, repartir dans le sens **opposé** → 1 impulsion, ça repart correctement.
      Le cycle du portail est OUVRE → STOP → FERME → STOP : l'impulsion qui suit un arrêt inverse
      le sens.
- [ ] Depuis un arrêt à mi-course, repartir dans le **même** sens → séquence à 3 impulsions
      (`ACK … MOVING_REVERSE`) : le toit repart d'abord à l'envers, s'arrête, puis repart.
      Observer le déplacement parasite : s'il est trop important, réduire `INTER`.
      S'il arrive que le toit parte en butée dans le mauvais sens, c'est qu'`INTER` est **trop
      court** et que le portail ignore l'impulsion d'arrêt : l'augmenter.
- [ ] Valeur d'`INTER` finalement retenue : ....... ms

---

## Étape 5 — Compilation et enregistrement du driver

```bash
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

- [ ] Build en **0 erreur, 0 avertissement**, DLL produite.

Puis, dans un terminal **administrateur** :

```bash
powershell -ExecutionPolicy Bypass -File tools\register.ps1
```

- [ ] Enregistrement réussi en **32 bits ET 64 bits**.

---

## Étape 6 — Panneau de test du driver

- [ ] Le driver apparaît dans le Chooser ASCOM (liste des Dome) sous
      « Drivers Ascom toit Obs principal Version 2026-02 ».
- [ ] La boîte de configuration s'ouvre, le port COM du Nano est proposé dans la liste.
- [ ] Connexion réussie depuis le panneau de test.
- [ ] L'état s'affiche en direct et correspond à la réalité.
- [ ] Ouvrir / Fermer / Stop fonctionnent depuis le panneau.
- [ ] Les temporisations réglées dans la boîte de configuration sont bien poussées vers le firmware
      (vérifier avec `CFG?` au moniteur série après une connexion).

---

## Étape 6 bis — Reprises automatiques

**Sur simulateur, sans matériel** (PC de développement, aucune action sur le vrai toit) :

```bash
powershell -ExecutionPolicy Bypass -File tools\test-sim.ps1
```

- [ ] Les 12 scénarios sont `OK` : impulsion ignorée, obstacle à mi-course, rebond du capteur de
      départ, capteur qui frémit, `BOTH_SENSORS`, reprises désactivées, demandes répétées ou
      inversées, `AbortSlew`, reset du Nano en pleine course.

**Sur le vrai toit**, monture parquée, **sous surveillance visuelle**, trace du driver activée :

- [ ] Toit ouvert. **Débrancher la sortie du relais** de l'entrée du portail, puis *Close Shutter*
      dans NINA. Au bout de `START` ms, l'action `Roof:Mission` indique `reprise 1/3 après
      TimeoutStart`, et NINA affiche **toujours `Closing`**. Rebrancher la sortie du relais : la
      reprise suivante ferme le toit, NINA passe à `Closed` **sans jamais afficher `Error`**.
- [ ] Toit ouvert, réglage « Reprises automatiques » à **0** : même manipulation → NINA passe en
      `Error` dès la première erreur (comportement d'origine). Remettre le réglage à **3**.
- [ ] *(Optionnel)* Couper l'alimentation du moteur à mi-fermeture, la rétablir après `TRAVEL` ms :
      le driver attend `TRAVEL` ms sans fin de course, puis `RECOVER CLOSE`. Le toit peut d'abord
      repartir **en ouverture** (sens dicté par le portail) : il est alors refermé par la reprise
      suivante.

---

## Étape 7 — Conformance ASCOM

**ConformU 4.5** est installé sur le PC de développement (`C:\Program Files\ASCOM\ConformU`).

**Passage sur simulateur** (PC de développement) : régler le port du driver sur **`SIM`** dans la
boîte de configuration, puis :

```bash
"C:\Program Files\ASCOM\ConformU\conformu.exe" conformance ASCOM.ArduinoRoof.Dome
```

- [ ] **0 erreur, 0 issue.**
- [ ] Les propriétés non implémentées (`Azimuth`, `Altitude`, `Park`…) sont rapportées comme
      « not implemented », **pas** comme des exceptions inattendues.
- [ ] Remettre le port réel dans la boîte de configuration.

**Passage sur le vrai toit** (PC de l'observatoire) : ConformU **ouvre et ferme le toit plusieurs
fois**. Monture parquée, rien dans le gabarit du toit, surveillance visuelle, coupure d'urgence à
portée.

- [ ] Même commande, port COM réel : **0 erreur, 0 issue**.

---

## Étape 8 — NINA

- [ ] *Options → General → Log level* : **Info** au minimum. En `Warning`, le journal NINA ne garde
      pas la chronologie des commandes du dôme, ce qui rend un incident nocturne indiagnosticable.
- [ ] Boîte de configuration du driver : **trace activée**, en permanence sur le PC de
      l'observatoire.
- [ ] *Equipment → Dome* : sélectionner le driver, connecter.
- [ ] Les contrôles d'**azimut sont grisés** → NINA a bien détecté un toit roulant
      (`CanSetAzimuth = false`).
- [ ] *Open Shutter* : le toit s'ouvre, l'état passe par `Opening` puis `Open`.
- [ ] *Close Shutter* : symétrique.
- [ ] Séquenceur avancé : les instructions « Open Dome Shutter » et « Close Dome Shutter »
      s'exécutent et **attendent bien la fin de la course** avant de passer à la suite.

---

## Étape 9 — Cas dégradés

- [ ] Débrancher l'USB **en pleine course** → NINA affiche `shutterError`, **et le toit termine sa
      course tout seul** (le moteur de portail est autonome, c'est le comportement voulu).
- [ ] Rebrancher → le driver se reconnecte et l'état se resynchronise sur la fin de course atteinte.
- [ ] Déconnecter le driver dans NINA alors que le toit est **ouvert** → **le toit reste ouvert**.
      Le driver ne doit jamais fermer le toit tout seul.
- [ ] Couper l'alimentation du Nano en pleine course, rebrancher → `shutterError`, `pos = UNKNOWN`.
      Le driver lit `err = NONE` et non `INTERRUPTED` : en se reconnectant, il ouvre le port, ce
      qui reset la carte une seconde fois (voir l'étape 3). C'est sans danger, la position reste
      inconnue.
- [ ] L'action `Roof:Mission` (ou la ligne « Avert. connexion » du panneau de test) signale
      `CONNEXION … position du toit inconnue, sans erreur` avec l'heure de la reconnexion, et la
      trace du driver contient le même avertissement (driver ≥ 1.2).
- [ ] *Close Shutter* dans NINA → le driver acquitte et ferme le toit tout seul (reprise
      automatique, §8.6 du protocole), **sous surveillance visuelle** : si aucune fin de course
      n'est active, la reprise passe par `RECOVER` et peut faire parcourir une course complète dans
      le sens non désiré avant de refermer.
- [ ] Avec « Reprises automatiques » à **0**, le même cas reste bloqué en `shutterError` jusqu'à un
      `Roof:ClearError` puis `Roof:Recover` explicites.

---

## Relevé des réglages finaux

À reporter ici une fois la recette terminée, ce sont les valeurs à restaurer après un
`CFGRESET` ou un remplacement de carte.

| Paramètre | Valeur retenue | Origine |
|---|---|---|
| Port COM | *(propre à chaque PC)* | relevé par `flash-firmware.ps1 -ListPorts` |
| `PULSE` | `1000` | le portail **ne démarre pas** avec 600 ms (défaut du firmware) ; 1000 ms démarre à coup sûr |
| `INTER` | `1000` | séquence à 3 impulsions fiable, déplacement parasite de 10 à 15 cm ; ne pas réduire sans essai |
| `START` | `5000` | ≈ 6 × le délai de démarrage mesuré (0,8 s) |
| `TRAVEL` | `40000` | ≈ 1,85 × la course la plus longue mesurée (21,6 s) |
| `DEBOUNCE` | `50` | défaut, aucun rebond constaté |
| Délai avant démarrage | **0,8 s** à l'ouverture, **0,1 à 0,3 s** à la fermeture | mesuré depuis la fin de l'impulsion, comme `START` (06/10/2026) |
| Durée d'une course complète | **21,6 s** à l'ouverture, **18,8 à 19,8 s** à la fermeture | mesuré d'une fin de course à l'autre (06/10/2026) |

Ces valeurs sont à restaurer après un `CFGRESET` ou un remplacement de carte. Elles sont poussées
vers le firmware par le driver à chaque connexion : c'est donc la **boîte de configuration** qui fait
foi, pas l'EEPROM de la carte.

> Le choix de `START` et `TRAVEL` est délibérément large. Une valeur trop courte provoque une erreur
> alors que tout fonctionne — et interrompt la nuit ; une valeur trop longue ne fait que retarder de
> quelques secondes la détection d'un vrai blocage. Voir le README pour le raisonnement complet.
