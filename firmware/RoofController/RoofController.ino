/* =============================================================================
 *  RoofController.ino  —  Contrôleur de toit coulissant d'observatoire
 *  Cible : Arduino Nano (ATmega328P), 16 MHz
 *  Protocole série : voir docs/PROTOCOLE.md — version 1.0 (contrat avec le driver ASCOM)
 *
 *  PRINCIPE
 *  --------
 *  Le toit est entraîné par un moteur de portail à ENTRÉE IMPULSIONNELLE UNIQUE
 *  fonctionnant en mode séquentiel :
 *
 *        OUVRE  →  STOP  →  FERME  →  STOP  →  OUVRE  →  …
 *
 *  Le firmware ne peut donc pas « commander un sens » : il ne peut qu'appuyer sur
 *  le bouton. Il maintient un modèle logiciel de l'automate du portail (`next` =
 *  effet de la prochaine impulsion) et le recale en permanence sur la réalité
 *  physique grâce aux deux capteurs de fin de course (invariant capteurs, §4).
 *
 *  ARCHITECTURE
 *  ------------
 *  Tout est NON BLOQUANT : aucun delay() nulle part. Une seule boucle principale
 *  cadencée par millis(), avec des sous-machines à états :
 *      - relayUpdate()        relâchement du relais (priorité absolue)
 *      - sensorsUpdate()      anti-rebond des deux capteurs
 *      - applySensorInvariant() recalage du modèle sur les capteurs (§4)
 *      - supervisionUpdate()  suivi d'une course commandée + timeouts
 *      - sequencerUpdate()    émission des impulsions (1 ou 3) espacées de INTER
 *      - serialUpdate()       lecture ligne par ligne + réponse
 *      - ledUpdate()          LED d'état (cosmétique)
 *
 *  Toutes les comparaisons de temps utilisent la forme (millis() - t0) >= duree,
 *  seule forme immunisée contre le débordement de millis() (~49,7 jours).
 *
 *  ---------------------------------------------------------------------------
 *  ⚠  SÉCURITÉ MATÉRIELLE — RÉSISTANCE DE TIRAGE 10 kΩ ENTRE D5 ET GND  ⚠
 *  ---------------------------------------------------------------------------
 *  Avec un relais ACTIF HIGH, la broche D5 est FLOTTANTE pendant le reset et le
 *  bootloader (~1 s). Or le Nano est remis à zéro À CHAQUE OUVERTURE DU PORT
 *  SÉRIE (signal DTR), donc à chaque connexion de NINA. Sans résistance de
 *  tirage à la masse, le relais peut coller pendant ce laps de temps et
 *  déclencher le toit. Cette résistance est INDISPENSABLE. Voir docs/CABLAGE.md.
 *  ---------------------------------------------------------------------------
 *
 *  BROCHAGE (modifiable ci-dessous sans toucher à la logique)
 *      D5   RELAY_PIN          → module relais (contact sec vers l'entrée du portail)
 *      D3   SENSOR_CLOSED_PIN  → capteur « toit fermé »  (NO vers GND, INPUT_PULLUP)
 *      D2   SENSOR_OPEN_PIN    → capteur « toit ouvert »  (NO vers GND, INPUT_PULLUP)
 *      D13  LED_PIN            → LED d'état (optionnelle)
 * ============================================================================= */

#include <Arduino.h>
#include <EEPROM.h>

/* =============================================================================
 *  1. CONFIGURATION MATÉRIELLE — broches et polarités
 * ============================================================================= */

/* Broche pilotant le module relais (contact sec vers l'entrée impulsionnelle). */
#define RELAY_PIN            5

/* Polarité du module relais.
 *   1 = le relais colle quand la broche est à HIGH (module « actif haut »)
 *   0 = le relais colle quand la broche est à LOW  (module opto « actif bas »)
 * Un module actif bas est plus sûr au reset (la broche est tirée haut par le
 * pull-up interne), mais nécessite d'inverser cette valeur. */
#define RELAY_ACTIVE_HIGH    1

/* Capteurs de fin de course. Contacts NO ramenés à GND, lus en INPUT_PULLUP.
 *
 * ⚠ CONVENTION : D3 = fermé, D2 = ouvert. Toute carte doit être câblée ainsi.
 *   Une permutation de ces deux fils ne provoque aucune erreur : le toit paraît simplement
 *   fonctionner à l'envers, et le client annonce « fermé » alors qu'il est ouvert. C'est une
 *   inversion SILENCIEUSE, donc le mode de défaillance le plus dangereux de ce montage.
 *   Contrôle en une commande, à faire sur chaque carte : activer le capteur « toit fermé »
 *   doit donner SWC=1 dans DIAG, jamais SWO=1. */
#define SENSOR_CLOSED_PIN    3      /* capteur « toit fermé »  */
#define SENSOR_OPEN_PIN      2      /* capteur « toit ouvert » */

/* Polarité des capteurs.
 *   1 = contact fermé  ⇒  broche LOW   (câblage NO vers GND + INPUT_PULLUP)
 *   0 = contact fermé  ⇒  broche HIGH  (capteur à sortie active haute) */
#define SENSOR_ACTIVE_LOW    1

/* LED d'état. Mettre LED_ENABLED à 0 pour libérer complètement D13. */
#define LED_PIN              13
#define LED_ENABLED          1

/* Niveaux logiques dérivés de la polarité du relais. */
#if RELAY_ACTIVE_HIGH
  #define RELAY_LEVEL_ACTIVE     HIGH
  #define RELAY_LEVEL_INACTIVE   LOW
#else
  #define RELAY_LEVEL_ACTIVE     LOW
  #define RELAY_LEVEL_INACTIVE   HIGH
#endif

/* =============================================================================
 *  2. VERSIONS ET CONSTANTES DE SÉCURITÉ
 * ============================================================================= */

#define PROTOCOL_VERSION   "1.0"      /* §6 — doit commencer par « 1. » */
#define FIRMWARE_VERSION   "1.1.0"

/* §7.4 — borne DURE de la durée d'impulsion, indépendante de la configuration.
 * Aucune impulsion ne peut dépasser cette durée, même si l'EEPROM est corrompue. */
#define MAX_PULSE_MS       2000UL

/* §1 — longueur maximale d'une ligne reçue. */
#define LINE_MAX           80

/* §4 — durée pendant laquelle une fin de course doit rester relâchée EN CONTINU, à l'arrêt,
 * avant que SENSOR_LOST ne soit levée. Un fil coupé (relâchement permanent) reste détecté en
 * 3 s ; une vibration du toit sur sa butée (vent, dilatation) ne l'est plus. Avec le seul
 * DEBOUNCE (50 ms), un capteur qui frémissait la nuit suffisait à bloquer la fermeture du matin. */
#define SENSOR_LOST_CONFIRM_MS   3000UL

/* §4 — après le relâchement du capteur de départ, sa réactivation pendant ce délai est un
 * rebond, pas une arrivée. Sans cette garde, un capteur qui rebondit au démarrage était pris
 * pour une arrivée sur la butée opposée (WRONG_DIRECTION) alors que le toit roulait normalement. */
#define DEPART_REARM_MS          2000UL

/* =============================================================================
 *  3. TYPES — vocabulaire d'état du protocole (§3)
 * ============================================================================= */

/* Position du toit. */
enum Pos : uint8_t {
  POS_CLOSED = 0,        /* capteur « fermé » actif                             */
  POS_OPEN,              /* capteur « ouvert » actif                            */
  POS_INTERMEDIATE,      /* aucun capteur, position connue par l'automate       */
  POS_UNKNOWN            /* aucun capteur, automate désynchronisé               */
};

/* Mouvement en cours. Invariant maintenu : motion == MOTION_ERROR  ⟺  err != ERR_NONE. */
enum Motion : uint8_t {
  MOTION_IDLE = 0,
  MOTION_OPENING,
  MOTION_CLOSING,
  MOTION_ERROR
};

/* Sens : sert à la fois pour `next` (modèle de l'automate) et pour la cible. */
enum Dir : uint8_t {
  DIR_OPEN = 0,
  DIR_CLOSE
};

/* Codes d'erreur (§3). */
enum Err : uint8_t {
  ERR_NONE = 0,
  ERR_TIMEOUT_START,
  ERR_TIMEOUT_TRAVEL,
  ERR_BOTH_SENSORS,
  ERR_WRONG_DIRECTION,
  ERR_INTERRUPTED,
  ERR_SENSOR_LOST
};

/* Phases d'une course surveillée. */
enum MovePhase : uint8_t {
  MP_NONE = 0,             /* pas de course en cours                             */
  MP_SEQUENCE,             /* émission de la séquence d'impulsions (1 ou 3)      */
  MP_WAIT_START_RELEASE,   /* attente du relâchement du capteur de départ        */
  MP_TRAVEL                /* attente du capteur cible                           */
};

/* Nature d'une séquence d'impulsions programmée. */
enum SeqKind : uint8_t {
  SEQ_NONE = 0,
  SEQ_MOVE,                /* course surveillée (OPEN / CLOSE / RECOVER)         */
  SEQ_STOP,                /* impulsion d'arrêt (ABORT)                          */
  SEQ_RAW                  /* impulsion brute de diagnostic (PULSE)              */
};

/* Repère du capteur actif au lancement d'une séquence / d'une course. */
enum SensorRef : uint8_t {
  SEN_NONE = 0,
  SEN_CLOSED,
  SEN_OPEN
};

/* État du bloc EEPROM au démarrage, remonté par DIAG. */
enum EeStatus : uint8_t {
  EE_OK = 0,               /* bloc valide                                        */
  EE_INIT,                 /* octet magique absent : EEPROM vierge, initialisée  */
  EE_BAD                   /* octet magique présent mais somme de contrôle fausse*/
};

/* Temporisations configurables (§6, commande CFG). */
struct Config {
  uint32_t pulseMs;        /* durée de fermeture du contact relais               */
  uint32_t interMs;        /* délai entre la FIN d'une impulsion et le DÉBUT de la suivante */
  uint32_t startMs;        /* délai max pour que le capteur de départ se relâche  */
  uint32_t travelMs;       /* durée max d'une course complète                     */
  uint32_t debounceMs;     /* anti-rebond des capteurs                            */
};

/* Un capteur de fin de course avec son anti-rebond. */
struct Sensor {
  uint8_t  pin;
  bool     raw;            /* lecture instantanée normalisée (true = contact fermé) */
  bool     stable;         /* état retenu après anti-rebond                       */
  bool     candidate;      /* dernière valeur brute observée                      */
  uint32_t lastChangeMs;   /* instant du dernier changement de `candidate`        */
};

/* =============================================================================
 *  4. VALEURS PAR DÉFAUT ET PLAGES DE CONFIGURATION (§6)
 * ============================================================================= */

#define CFG_PULSE_DEF      600UL
#define CFG_PULSE_MIN      100UL
#define CFG_PULSE_MAX      2000UL

#define CFG_INTER_DEF      600UL
#define CFG_INTER_MIN      200UL
#define CFG_INTER_MAX      5000UL

#define CFG_START_DEF      5000UL
#define CFG_START_MIN      1000UL
#define CFG_START_MAX      30000UL

#define CFG_TRAVEL_DEF     60000UL
#define CFG_TRAVEL_MIN     5000UL
#define CFG_TRAVEL_MAX     300000UL

#define CFG_DEBOUNCE_DEF   50UL
#define CFG_DEBOUNCE_MIN   5UL
#define CFG_DEBOUNCE_MAX   200UL

/* =============================================================================
 *  5. BLOC EEPROM (§5) — octet magique + somme de contrôle
 * ============================================================================= */

#define EE_ADDR     0        /* adresse de base du bloc                          */
#define EE_MAGIC    0x52     /* 'R' comme Roof                                   */
#define EE_LAYOUT   1        /* version de la structure ; à incrémenter si elle change */

/* Sur AVR aucun octet de bourrage n'est inséré : la structure fait 26 octets et
 * la somme de contrôle porte sur tous les octets qui précèdent `sum`. */
struct EeBlock {
  uint8_t  magic;
  uint8_t  layout;
  uint8_t  pos;            /* Pos persistée                                      */
  uint8_t  next;           /* Dir persisté                                       */
  uint8_t  moving;         /* 1 si une course surveillée était en cours          */
  uint32_t pulseMs;
  uint32_t interMs;
  uint32_t startMs;
  uint32_t travelMs;
  uint32_t debounceMs;
  uint8_t  sum;            /* somme de contrôle 8 bits                           */
};

/* =============================================================================
 *  6. ÉTAT GLOBAL
 * ============================================================================= */

static Config    g_cfg;

static Pos       g_pos           = POS_UNKNOWN;
static Motion    g_motion        = MOTION_IDLE;
static Dir       g_next          = DIR_OPEN;
static Err       g_err           = ERR_NONE;

/* §4 — modèle « le moteur est supposé en train de tourner ». Conditionne ABORT. */
static bool      g_motorRunning  = false;

/* Capteurs. */
static Sensor    g_swClose = { SENSOR_CLOSED_PIN, false, false, false, 0 };
static Sensor    g_swOpen  = { SENSOR_OPEN_PIN,   false, false, false, 0 };

/* Relais. */
static bool      g_relayActive   = false;
static uint32_t  g_relayOnMs     = 0;

/* Séquenceur d'impulsions. */
static SeqKind   g_seqKind       = SEQ_NONE;
static uint8_t   g_pulsesLeft    = 0;
static uint32_t  g_lastPulseEndMs= 0;
static bool      g_havePulsed    = false;
static SensorRef g_seqRefSensor  = SEN_NONE;   /* capteur actif au lancement de la séquence */

/* Supervision d'une course. */
static MovePhase g_phase         = MP_NONE;
static Dir       g_moveDir       = DIR_OPEN;   /* sens commandé                  */
static uint32_t  g_moveStartMs   = 0;          /* pour le champ <elapsed>        */
static uint32_t  g_phaseStartMs  = 0;          /* pour les timeouts START/TRAVEL */
static SensorRef g_departSensor  = SEN_NONE;   /* capteur actif au départ        */
static bool      g_departReleased= true;       /* le capteur de départ s'est relâché */
static uint32_t  g_departReleaseMs = 0;        /* instant de ce relâchement      */

/* Confirmation de SENSOR_LOST : fin de course relâchée à l'arrêt depuis g_lossSinceMs. */
static bool      g_lossPending   = false;
static uint32_t  g_lossSinceMs   = 0;

/* Impulsion brute (PULSE) : la position devient UNKNOWN dès que le toit
 * quitte la fin de course sur laquelle il se trouvait éventuellement. */
static bool      g_rawPulsePending = false;
static uint32_t  g_rawPulseMs      = 0;

/* Divers. */
static EeStatus  g_eeStatus      = EE_INIT;

/* Tampon de réception série (§1 : 80 caractères max). */
static char      g_line[LINE_MAX + 1];
static uint8_t   g_lineLen       = 0;
static bool      g_lineOverflow  = false;

/* LED d'état. */
static bool      g_ledOn         = false;
static uint32_t  g_ledLastMs     = 0;

/* =============================================================================
 *  7. UTILITAIRES BAS NIVEAU
 * ============================================================================= */

/* Lecture normalisée d'un capteur : true = contact fermé, quelle que soit la polarité. */
static bool sensorRawRead(uint8_t pin) {
#if SENSOR_ACTIVE_LOW
  return (digitalRead(pin) == LOW);
#else
  return (digitalRead(pin) == HIGH);
#endif
}

/* Anti-rebond d'un capteur : la valeur `stable` ne suit `candidate` qu'après
 * DEBOUNCE ms de stabilité. */
static void sensorUpdate(Sensor &s, uint32_t now) {
  const bool r = sensorRawRead(s.pin);
  s.raw = r;
  if (r != s.candidate) {
    s.candidate    = r;
    s.lastChangeMs = now;
  } else if (s.stable != s.candidate && (now - s.lastChangeMs) >= g_cfg.debounceMs) {
    s.stable = s.candidate;
  }
}

static void sensorsUpdate(uint32_t now) {
  sensorUpdate(g_swClose, now);
  sensorUpdate(g_swOpen,  now);
}

/* Le relais est-il libre pour une nouvelle impulsion ? */
static void relayOff() {
  digitalWrite(RELAY_PIN, RELAY_LEVEL_INACTIVE);
  g_relayActive = false;
}

/* §7.3 — relâchement du relais piloté par millis(), jamais par delay().
 * Cette fonction est appelée EN PREMIER dans loop() : quoi qu'il arrive
 * ailleurs, le contact ne peut pas rester collé au-delà de la durée prévue. */
static void relayUpdate(uint32_t now) {
  if (!g_relayActive) {
    return;
  }
  /* §7.4 — la durée effective est bornée en dur par MAX_PULSE_MS. */
  uint32_t d = g_cfg.pulseMs;
  if (d > MAX_PULSE_MS) {
    d = MAX_PULSE_MS;
  }
  if ((now - g_relayOnMs) >= d) {
    relayOff();
    g_lastPulseEndMs = now;
    g_havePulsed     = true;
  }
}

/* Repère du capteur de fin de course actuellement actif (pour les gardes). */
static SensorRef activeSensorRef() {
  if (g_swClose.stable) {
    return SEN_CLOSED;
  }
  if (g_swOpen.stable) {
    return SEN_OPEN;
  }
  return SEN_NONE;
}

/* =============================================================================
 *  8. PERSISTANCE EEPROM (§5)
 * ============================================================================= */

static uint8_t eeChecksum(const EeBlock &b) {
  const uint8_t *p = (const uint8_t *)&b;
  uint8_t s = 0x5A;                               /* graine, pour ne pas valider un bloc de zéros */
  for (uint16_t i = 0; i < sizeof(EeBlock) - 1; i++) {
    s = (uint8_t)(s + p[i]);
    s = (uint8_t)((s << 1) | (s >> 7));           /* rotation : détecte les permutations */
  }
  return s;
}

static void eeRead(EeBlock &b) {
  uint8_t *p = (uint8_t *)&b;
  for (uint16_t i = 0; i < sizeof(EeBlock); i++) {
    p[i] = EEPROM.read(EE_ADDR + i);
  }
}

/* Écriture avec EEPROM.update() : aucun octet inchangé n'est réécrit, ce qui
 * supprime toute usure inutile de la cellule. */
static void eeWrite(const EeBlock &b) {
  const uint8_t *p = (const uint8_t *)&b;
  for (uint16_t i = 0; i < sizeof(EeBlock); i++) {
    EEPROM.update(EE_ADDR + i, p[i]);
  }
}

static void cfgDefaults() {
  g_cfg.pulseMs    = CFG_PULSE_DEF;
  g_cfg.interMs    = CFG_INTER_DEF;
  g_cfg.startMs    = CFG_START_DEF;
  g_cfg.travelMs   = CFG_TRAVEL_DEF;
  g_cfg.debounceMs = CFG_DEBOUNCE_DEF;
}

/* Une valeur hors plage (EEPROM corrompue) est ramenée à son défaut. */
static uint32_t cfgClamp(uint32_t v, uint32_t lo, uint32_t hi, uint32_t def) {
  return (v < lo || v > hi) ? def : v;
}

/* Compose le bloc à partir de l'état courant. `moving` est dérivé de la phase. */
static void eeFill(EeBlock &b) {
  b.magic      = EE_MAGIC;
  b.layout     = EE_LAYOUT;
  b.pos        = (uint8_t)g_pos;
  b.next       = (uint8_t)g_next;
  b.moving     = (g_phase != MP_NONE) ? 1 : 0;
  b.pulseMs    = g_cfg.pulseMs;
  b.interMs    = g_cfg.interMs;
  b.startMs    = g_cfg.startMs;
  b.travelMs   = g_cfg.travelMs;
  b.debounceMs = g_cfg.debounceMs;
  b.sum        = eeChecksum(b);
}

/* §5 — appelée UNIQUEMENT sur transition d'état (jamais en boucle). */
static void saveState() {
  EeBlock b;
  eeFill(b);
  eeWrite(b);
}

/* =============================================================================
 *  9. MODÈLE DE L'AUTOMATE DU PORTAIL (§4)
 * ============================================================================= */

/* Recalage complet du modèle sur une fin de course atteinte :
 *      capteur CLOSED  →  pos=CLOSED, next=OPEN,  moteur à l'arrêt
 *      capteur OPEN    →  pos=OPEN,   next=CLOSE, moteur à l'arrêt
 * C'est ce recalage qui rend l'automate auto-correcteur. */
static void latchOnSensor(Pos p) {
  g_pos             = p;
  g_next            = (p == POS_CLOSED) ? DIR_OPEN : DIR_CLOSE;
  g_motorRunning    = false;
  g_rawPulsePending = false;
}

static void cancelSequence() {
  g_pulsesLeft = 0;
  g_seqKind    = SEQ_NONE;
}

/* Programme une séquence de `n` impulsions. Le séquenceur se charge du timing :
 * jamais pendant une impulsion en cours, jamais avant INTER ms après la fin de
 * la précédente. */
static void seqStart(SeqKind kind, uint8_t n) {
  g_seqKind      = kind;
  g_pulsesLeft   = n;
  g_seqRefSensor = activeSensorRef();
}

/* Émission physique d'une impulsion.
 *
 * RÈGLE UNIFIÉE (§4) : l'effet d'une impulsion dépend uniquement de l'état
 * supposé du moteur au moment où elle part, ce qui est la traduction fidèle du
 * cycle OUVRE → STOP → FERME → STOP → OUVRE :
 *
 *   - moteur à l'arrêt  → impulsion de MOUVEMENT dans le sens `next`,
 *                         puis `next` bascule, moteur supposé en marche ;
 *   - moteur en marche  → impulsion d'ARRÊT, `next` reste tel quel,
 *                         moteur supposé arrêté.
 *
 * Vérification vis-à-vis de la table « Transitions de next » du §4 :
 *   OPENING : l'impulsion de mouvement OPEN avait déjà basculé next à CLOSE ;
 *             l'impulsion d'arrêt le laisse à CLOSE  → conforme (« → CLOSE »).
 *   CLOSING : idem, next reste à OPEN                → conforme (« → OPEN »).
 *   Séquence à 3 impulsions depuis next=CLOSE, commande OPEN :
 *             P1 mouvement CLOSE (next→OPEN), P2 arrêt (next reste OPEN),
 *             P3 mouvement OPEN (next→CLOSE) — le toit repart bien dans le
 *             bon sens, ce qui valide la règle.
 *
 * PRÉCISION PROTOCOLE (ratifiée) : la ligne « Toute impulsion émise → bascule (OPEN ⇄ CLOSE) »
 * du §4 est appliquée aux seules impulsions de MOUVEMENT. L'appliquer aussi aux
 * impulsions d'arrêt contredirait les deux lignes précédentes de la même table
 * et rendrait la séquence à 3 impulsions incohérente (le toit repartirait dans
 * le mauvais sens). Voir le rapport de livraison. */
static void emitPulse(uint32_t now) {
  if (g_motorRunning) {
    /* Impulsion d'arrêt : `next` inchangé. */
    g_motorRunning = false;
  } else {
    /* Impulsion de mouvement dans le sens `next`. */
    g_next         = (g_next == DIR_OPEN) ? DIR_CLOSE : DIR_OPEN;
    g_motorRunning = true;
  }
  digitalWrite(RELAY_PIN, RELAY_LEVEL_ACTIVE);
  g_relayActive = true;
  g_relayOnMs   = now;
}

/* Séquenceur : émet les impulsions programmées en respectant PULSE et INTER. */
static void sequencerUpdate(uint32_t now) {
  if (g_pulsesLeft == 0) {
    g_seqKind = SEQ_NONE;
    return;
  }
  if (g_relayActive) {
    return;                                       /* impulsion en cours          */
  }
  if (g_havePulsed && (now - g_lastPulseEndMs) < g_cfg.interMs) {
    return;                                       /* délai INTER non écoulé      */
  }
  /* Garde de sécurité : une impulsion d'arrêt n'a de sens que si le moteur est
   * encore supposé en marche. Sinon elle DÉMARRERAIT le toit. */
  if (g_seqKind == SEQ_STOP && !g_motorRunning) {
    cancelSequence();
    return;
  }
  emitPulse(now);
  g_pulsesLeft--;
  if (g_pulsesLeft == 0) {
    g_seqKind = SEQ_NONE;
  }
}

/* =============================================================================
 *  10. INVARIANT CAPTEURS (§4) — règle prioritaire évaluée en permanence
 * ============================================================================= */

/* Bascule en erreur sans jamais émettre d'impulsion (§7.1 : aucun mouvement
 * spontané). `g_motorRunning` est volontairement CONSERVÉ pour qu'un ABORT
 * opérateur puisse encore arrêter le moteur. */
static void raiseError(Err e) {
  cancelSequence();
  g_phase  = MP_NONE;
  g_motion = MOTION_ERROR;
  g_err    = e;
}

static void applySensorInvariant(uint32_t now) {
  const bool c = g_swClose.stable;
  const bool o = g_swOpen.stable;

  /* Les deux capteurs actifs : défaut de câblage, erreur prioritaire absolue. */
  if (c && o) {
    if (g_err != ERR_BOTH_SENSORS) {
      raiseError(ERR_BOTH_SENSORS);
      saveState();
    }
    return;
  }

  /* Pendant une course surveillée, les arrivées sont traitées par
   * supervisionUpdate() qui seul sait distinguer capteur cible et capteur
   * opposé, et qui doit tolérer le capteur de départ encore actif.
   *
   * PRÉCISION PROTOCOLE (ratifiée) : le §4 présente l'invariant comme applicable « en
   * permanence ». Appliqué littéralement pendant la phase de démarrage, il
   * remettrait `next` et `motorAssumedRunning` à leur valeur de repos alors que
   * le toit vient tout juste de recevoir son impulsion et n'a pas encore quitté
   * sa fin de course : la course serait immédiatement désynchronisée et ABORT
   * deviendrait inopérant. */
  if (g_phase != MP_NONE) {
    g_lossPending = false;
    return;
  }

  /* Séquence d'impulsions non surveillée en cours (ABORT ou PULSE) : on ne
   * recale pas le modèle, SAUF si le toit vient d'atteindre une fin de course
   * qui n'était pas active au lancement de la séquence. Dans ce cas le portail
   * s'est arrêté tout seul et l'impulsion restante deviendrait une impulsion de
   * DÉMARRAGE : on l'annule. */
  if (g_pulsesLeft > 0 || g_relayActive) {
    g_lossPending = false;
    const bool arrived = (c && g_seqRefSensor != SEN_CLOSED) ||
                         (o && g_seqRefSensor != SEN_OPEN);
    if (arrived) {
      cancelSequence();
      latchOnSensor(c ? POS_CLOSED : POS_OPEN);
      saveState();
    }
    return;
  }

  /* Suites d'une impulsion brute (§6, commande PULSE) : la position bascule en
   * UNKNOWN dès que le toit quitte la fin de course. Si au bout de START ms le
   * capteur n'a pas bougé, c'est que l'impulsion n'a rien produit : on reprend
   * le fonctionnement normal de l'invariant. */
  if (g_rawPulsePending) {
    if (!c && !o) {
      g_pos             = POS_UNKNOWN;
      g_rawPulsePending = false;
      saveState();
    } else if ((now - g_rawPulseMs) >= g_cfg.startMs) {
      g_rawPulsePending = false;
    }
    if (g_rawPulsePending) {
      g_lossPending = false;
      return;
    }
  }

  /* Recalage nominal sur la réalité physique. */
  const bool lossCandidate = !c && !o && g_motion == MOTION_IDLE &&
                             (g_pos == POS_CLOSED || g_pos == POS_OPEN);
  if (!lossCandidate) {
    g_lossPending = false;
  }

  if (c) {
    if (g_pos != POS_CLOSED || g_next != DIR_OPEN || g_motorRunning) {
      latchOnSensor(POS_CLOSED);
      saveState();
    }
  } else if (o) {
    if (g_pos != POS_OPEN || g_next != DIR_CLOSE || g_motorRunning) {
      latchOnSensor(POS_OPEN);
      saveState();
    }
  } else if (lossCandidate) {
    /* §4 — INCOHÉRENCE : la position affirme être sur une fin de course, aucune
     * fin de course n'est active, et rien n'a été commandé. Le toit a donc bougé
     * tout seul, ou le capteur / son câble est en panne.
     *
     * Ne rien faire ici serait dangereux : un simple fil de capteur coupé ferait
     * annoncer « toit fermé » indéfiniment, toit grand ouvert compris, et le
     * client croirait l'observatoire en sécurité. On dégrade donc la position et
     * on exige une intervention. `next` n'est pas fiable non plus : si le toit a
     * bougé, le portail a pu consommer une étape de son cycle.
     *
     * L'incohérence doit toutefois durer SENSOR_LOST_CONFIRM_MS sans interruption : un
     * capteur qui frémit sur sa butée n'est pas un toit qui a bougé. */
    if (!g_lossPending) {
      g_lossPending = true;
      g_lossSinceMs = now;
    } else if ((now - g_lossSinceMs) >= SENSOR_LOST_CONFIRM_MS) {
      g_lossPending = false;
      g_pos = POS_UNKNOWN;
      raiseError(ERR_SENSOR_LOST);
      saveState();
    }
  }
  /* Aucun capteur actif et position déjà INTERMEDIATE ou UNKNOWN : rien à faire. */
}

/* =============================================================================
 *  11. SUPERVISION D'UNE COURSE
 * ============================================================================= */

/* Arrivée du toit sur une fin de course pendant une course surveillée. */
static void onArrival(Pos reached) {
  cancelSequence();
  g_phase = MP_NONE;
  latchOnSensor(reached);

  const bool isTarget = (g_moveDir == DIR_OPEN) ? (reached == POS_OPEN)
                                                : (reached == POS_CLOSED);
  if (isTarget) {
    g_motion = MOTION_IDLE;
    g_err    = ERR_NONE;
  } else {
    /* Le modèle `next` était faux : latchOnSensor() vient de le corriger.
     * Le firmware s'arrête là, il n'enchaîne pas (§6, RECOVER). */
    g_motion = MOTION_ERROR;
    g_err    = ERR_WRONG_DIRECTION;
  }
  saveState();
}

/* Échec d'une course surveillée sur expiration d'un délai. */
static void failMove(Err e) {
  raiseError(e);
  /* TIMEOUT_START : le capteur de départ est toujours actif, la position reste
   * donc valide et l'invariant la confirmera au tour suivant.
   * TIMEOUT_TRAVEL : le toit est quelque part entre les deux, éventuellement
   * toujours en mouvement → la position n'est plus fiable. */
  if (!g_swClose.stable && !g_swOpen.stable) {
    g_pos = POS_UNKNOWN;
  }
  saveState();
}

static void supervisionUpdate(uint32_t now) {
  if (g_phase == MP_NONE) {
    return;
  }

  const bool c = g_swClose.stable;
  const bool o = g_swOpen.stable;

  /* Un capteur actif n'est une ARRIVÉE que s'il ne s'agit pas du capteur de
   * départ encore tenu — ni de ce même capteur qui rebondit juste après s'être
   * relâché (DEPART_REARM_MS). */
  const bool departGuard   = !g_departReleased ||
                             (now - g_departReleaseMs) < DEPART_REARM_MS;
  const bool arrivedClosed = c && !(g_departSensor == SEN_CLOSED && departGuard);
  const bool arrivedOpen   = o && !(g_departSensor == SEN_OPEN   && departGuard);
  if (arrivedClosed || arrivedOpen) {
    onArrival(arrivedClosed ? POS_CLOSED : POS_OPEN);
    return;
  }

  /* Le capteur de départ vient-il de se relâcher ? */
  if (!g_departReleased) {
    const bool stillHeld = (g_departSensor == SEN_CLOSED && c) ||
                           (g_departSensor == SEN_OPEN   && o);
    if (!stillHeld) {
      g_departReleased  = true;
      g_departReleaseMs = now;
      if (g_phase == MP_WAIT_START_RELEASE) {
        g_phase        = MP_TRAVEL;
        g_phaseStartMs = now;
      }
    }
  }

  switch (g_phase) {
    case MP_SEQUENCE:
      /* La séquence est terminée quand toutes les impulsions sont parties ET
       * que le relais est retombé. */
      if (g_pulsesLeft == 0 && !g_relayActive) {
        g_phase        = g_departReleased ? MP_TRAVEL : MP_WAIT_START_RELEASE;
        g_phaseStartMs = now;
      }
      break;

    case MP_WAIT_START_RELEASE:
      if ((now - g_phaseStartMs) >= g_cfg.startMs) {
        failMove(ERR_TIMEOUT_START);
      }
      break;

    case MP_TRAVEL:
      if ((now - g_phaseStartMs) >= g_cfg.travelMs) {
        failMove(ERR_TIMEOUT_TRAVEL);
      }
      break;

    case MP_NONE:
    default:
      break;
  }
}

/* Démarre une course surveillée de `pulses` impulsions dans le sens `dir`. */
static void startSupervisedMove(Dir dir, uint8_t pulses) {
  const uint32_t now = millis();
  g_moveDir         = dir;
  g_motion          = (dir == DIR_OPEN) ? MOTION_OPENING : MOTION_CLOSING;
  g_err             = ERR_NONE;
  g_phase           = MP_SEQUENCE;
  g_moveStartMs     = now;
  g_phaseStartMs    = now;
  g_departSensor    = activeSensorRef();
  g_departReleased  = (g_departSensor == SEN_NONE);
  g_rawPulsePending = false;
  seqStart(SEQ_MOVE, pulses);
  /* §5 — le drapeau `moving` est persisté AVANT la première impulsion : une
   * coupure secteur en pleine course sera correctement détectée au redémarrage. */
  saveState();
}

/* =============================================================================
 *  12. FORMATAGE DES RÉPONSES
 * ============================================================================= */

static const __FlashStringHelper *posName(Pos p) {
  switch (p) {
    case POS_CLOSED:       return F("CLOSED");
    case POS_OPEN:         return F("OPEN");
    case POS_INTERMEDIATE: return F("INTERMEDIATE");
    case POS_UNKNOWN:      return F("UNKNOWN");
  }
  return F("UNKNOWN");
}

static const __FlashStringHelper *motionName(Motion m) {
  switch (m) {
    case MOTION_IDLE:    return F("IDLE");
    case MOTION_OPENING: return F("OPENING");
    case MOTION_CLOSING: return F("CLOSING");
    case MOTION_ERROR:   return F("ERROR");
  }
  return F("ERROR");
}

static const __FlashStringHelper *dirName(Dir d) {
  return (d == DIR_OPEN) ? F("OPEN") : F("CLOSE");
}

static const __FlashStringHelper *errName(Err e) {
  switch (e) {
    case ERR_NONE:             return F("NONE");
    case ERR_TIMEOUT_START:    return F("TIMEOUT_START");
    case ERR_TIMEOUT_TRAVEL:   return F("TIMEOUT_TRAVEL");
    case ERR_BOTH_SENSORS:     return F("BOTH_SENSORS");
    case ERR_WRONG_DIRECTION:  return F("WRONG_DIRECTION");
    case ERR_INTERRUPTED:      return F("INTERRUPTED");
    case ERR_SENSOR_LOST:      return F("SENSOR_LOST");
  }
  return F("NONE");
}

/* §1 — toute réponse se termine par CRLF. */
static void endLine() {
  Serial.print(F("\r\n"));
}

static void sendLine(const __FlashStringHelper *s) {
  Serial.print(s);
  endLine();
}

static void reply3(const __FlashStringHelper *a, const __FlashStringHelper *b,
                   const __FlashStringHelper *c) {
  Serial.print(a);
  Serial.print(' ');
  Serial.print(b);
  Serial.print(' ');
  Serial.print(c);
  endLine();
}

static void printFlag(bool v) {
  Serial.print(v ? '1' : '0');
}

/* =============================================================================
 *  13. COMMANDES (§6)
 * ============================================================================= */

/* Millisecondes écoulées depuis le début de la course en cours (0 si à l'arrêt). */
static uint32_t elapsedMs() {
  if (g_motion == MOTION_OPENING || g_motion == MOTION_CLOSING) {
    return millis() - g_moveStartMs;
  }
  return 0;
}

static void cmdPing() {
  sendLine(F("PONG ROOF " PROTOCOL_VERSION " " FIRMWARE_VERSION));
}

/* STATE <pos> <motion> <swClose> <swOpen> <relay> <next> <elapsed> <err> */
static void cmdState() {
  Serial.print(F("STATE "));
  Serial.print(posName(g_pos));
  Serial.print(' ');
  Serial.print(motionName(g_motion));
  Serial.print(' ');
  printFlag(g_swClose.stable);
  Serial.print(' ');
  printFlag(g_swOpen.stable);
  Serial.print(' ');
  printFlag(g_relayActive);
  Serial.print(' ');
  Serial.print(dirName(g_next));
  Serial.print(' ');
  Serial.print(elapsedMs());
  Serial.print(' ');
  Serial.print(errName(g_err));
  endLine();
}

/* OPEN / CLOSE — démarrage d'une course. */
static void cmdMove(Dir dir) {
  const __FlashStringHelper *name = dirName(dir);

  if (g_motion == MOTION_ERROR) {
    reply3(F("NAK"), name, F("ERROR"));
    return;
  }
  /* Une séquence d'impulsions non surveillée (ABORT / PULSE) encore en vol
   * interdit également le démarrage : le portail n'a pas fini de digérer, et
   * tant qu'une impulsion brute n'a pas produit son effet observable le modèle
   * `next` n'est pas fiable. */
  if (g_motion == MOTION_OPENING || g_motion == MOTION_CLOSING ||
      g_phase != MP_NONE || g_pulsesLeft > 0 || g_relayActive ||
      g_rawPulsePending) {
    reply3(F("NAK"), name, F("BUSY"));
    return;
  }
  if (g_pos == POS_UNKNOWN) {
    reply3(F("NAK"), name, F("UNKNOWN_POSITION"));
    return;
  }

  const Pos target = (dir == DIR_OPEN) ? POS_OPEN : POS_CLOSED;
  if (g_pos == target) {
    reply3(F("ACK"), name, F("ALREADY"));
    return;
  }

  /* §4 — nombre d'impulsions :
   *   next == sens demandé  → 1 impulsion
   *   next == sens opposé   → 3 impulsions (démarre à l'envers, arrête, repart)
   * Sur une fin de course, l'invariant garantit que `next` pointe déjà vers le
   * sens opposé : le cas « 1 impulsion » du tableau est donc automatique. */
  const bool reverse = (g_next != dir);
  startSupervisedMove(dir, reverse ? 3 : 1);
  reply3(F("ACK"), name, reverse ? F("MOVING_REVERSE") : F("MOVING"));
}

/* ABORT — arrêt d'urgence. §7.5 : ne doit JAMAIS être refusé, y compris en ERROR. */
static void cmdAbort() {
  /* Toute séquence encore programmée est annulée sans condition : après un
   * ABORT, plus aucune impulsion ne doit partir de sa propre initiative. */
  cancelSequence();

  const bool wasSupervised = (g_phase != MP_NONE);
  g_phase           = MP_NONE;
  g_departReleased  = true;
  g_rawPulsePending = false;

  const bool stopping = g_motorRunning;
  if (stopping) {
    /* L'impulsion d'arrêt part dès que le relais est libre et que INTER est
     * écoulé — jamais de delay(). */
    seqStart(SEQ_STOP, 1);
  }

  /* PRÉCISION PROTOCOLE (ratifiée) : en état ERROR, ABORT arrête bien le moteur mais ne
   * réarme pas l'automate. Le §6 indique « après un ABORT réussi : motion =
   * IDLE », mais le §3 impose que toute erreur reste active jusqu'à CLEARERR.
   * Effacer l'erreur ici permettrait de relancer immédiatement une course sur
   * un défaut non diagnostiqué : on conserve donc l'état ERROR. */
  if (g_motion != MOTION_ERROR) {
    g_motion = MOTION_IDLE;
  }

  /* §6 — pos = INTERMEDIATE. On ne l'applique évidemment pas si la position
   * était déjà UNKNOWN (un ABORT ne fait pas réapparaître une information
   * perdue), ni si une fin de course est active (l'invariant fait foi). */
  if (wasSupervised && g_pos != POS_UNKNOWN &&
      !g_swClose.stable && !g_swOpen.stable) {
    g_pos = POS_INTERMEDIATE;
  }

  saveState();
  sendLine(stopping ? F("ACK ABORT STOPPING") : F("ACK ABORT IDLE"));
}

/* CLEARERR — acquittement d'une erreur. */
static void cmdClearErr() {
  /* §6 — le sort d'une position intermédiaire dépend de l'erreur réellement
   * acquittée : on la mémorise avant de l'effacer. */
  const Err errAcquittee = g_err;

  cancelSequence();
  g_phase           = MP_NONE;
  g_departReleased  = true;
  g_rawPulsePending = false;
  g_err             = ERR_NONE;
  g_motion          = MOTION_IDLE;

  /* Recalcul de la position depuis les capteurs. `motorAssumedRunning` n'est
   * pas touché ici : si le moteur tournait encore, un ABORT doit rester
   * capable de l'arrêter. L'invariant le remettra à false dès qu'une fin de
   * course sera atteinte. */
  if (g_swClose.stable && g_swOpen.stable) {
    /* Le défaut est toujours présent : l'invariant relèvera l'erreur au tour
     * suivant. On répond avec la position courante. */
    g_pos = POS_UNKNOWN;
  } else if (g_swClose.stable) {
    latchOnSensor(POS_CLOSED);
  } else if (g_swOpen.stable) {
    latchOnSensor(POS_OPEN);
  } else if (errAcquittee != ERR_NONE) {
    /* §6 — aucune fin de course active et une erreur réelle vient d'être
     * acquittée : après une erreur, la position du toit n'est plus fiable. */
    g_pos = POS_UNKNOWN;
  }
  /* Sinon : rien à acquitter et aucune fin de course active — la position
   * courante est CONSERVÉE, typiquement INTERMEDIATE après un ABORT.
   * Effacer une erreur inexistante ne doit pas détruire une position connue :
   * cela imposerait un RECOVER, lequel peut lancer une course complète à
   * contresens. */

  saveState();
  Serial.print(F("ACK CLEARERR "));
  Serial.print(posName(g_pos));
  endLine();
}

/* RECOVER <OPEN|CLOSE> — reprise depuis une position inconnue. */
static void cmdRecover(const char *arg) {
  Dir d;
  if (arg == NULL || arg[0] == '\0') {
    sendLine(F("NAK RECOVER BAD_ARGS"));
    return;
  }
  if (strcmp_P(arg, PSTR("OPEN")) == 0) {
    d = DIR_OPEN;
  } else if (strcmp_P(arg, PSTR("CLOSE")) == 0) {
    d = DIR_CLOSE;
  } else {
    sendLine(F("NAK RECOVER BAD_ARGS"));
    return;
  }

  /* PRÉCISION PROTOCOLE (ratifiée) : le §6 ne liste pas de refus de RECOVER en état ERROR ni
   * pendant un mouvement. Le diagramme du §4 impose pourtant le chemin
   * ERROR --CLEARERR--> UNKNOWN --RECOVER--> OPENING, et le §3 interdit tout
   * mouvement tant que l'erreur n'est pas acquittée. On refuse donc avec les
   * codes NAK RECOVER ERROR et NAK RECOVER BUSY, calqués sur ceux d'OPEN. */
  if (g_motion == MOTION_ERROR) {
    sendLine(F("NAK RECOVER ERROR"));
    return;
  }
  if (g_phase != MP_NONE || g_pulsesLeft > 0 || g_relayActive ||
      g_rawPulsePending) {
    sendLine(F("NAK RECOVER BUSY"));
    return;
  }
  if (g_pos != POS_UNKNOWN) {
    sendLine(F("NAK RECOVER NOT_NEEDED"));
    return;
  }

  /* §6 — une SEULE impulsion, quel que soit le modèle `next` (il n'est pas
   * fiable), et surveillance des DEUX capteurs : onArrival() distingue le
   * capteur cible du capteur opposé. */
  startSupervisedMove(d, 1);
  reply3(F("ACK"), F("RECOVER"), dirName(d));
}

/* PULSE — impulsion brute de diagnostic. */
static void cmdPulse() {
  if (g_phase != MP_NONE || g_pulsesLeft > 0 || g_relayActive) {
    sendLine(F("NAK PULSE BUSY"));
    return;
  }

  seqStart(SEQ_RAW, 1);

  /* §6 — force pos = UNKNOWN si aucun capteur n'est actif. Si une fin de course
   * est encore tenue, la position reste momentanément valide : le drapeau
   * g_rawPulsePending fera basculer pos en UNKNOWN dès que le toit l'aura
   * quittée (le mouvement résultant n'étant pas surveillé). */
  g_rawPulsePending = true;
  g_rawPulseMs      = millis();
  if (!g_swClose.stable && !g_swOpen.stable) {
    g_pos             = POS_UNKNOWN;
    g_rawPulsePending = false;
  }

  saveState();
  sendLine(F("ACK PULSE"));
}

/* DIAG — diagnostic de câblage. */
static void cmdDiag() {
  Serial.print(F("DIAG SWC="));
  printFlag(g_swClose.raw);
  Serial.print('/');
  printFlag(g_swClose.stable);
  Serial.print(F(" SWO="));
  printFlag(g_swOpen.raw);
  Serial.print('/');
  printFlag(g_swOpen.stable);
  Serial.print(F(" RELAY="));
  printFlag(g_relayActive);
  Serial.print(F(" NEXT="));
  Serial.print(dirName(g_next));
  Serial.print(F(" POS="));
  Serial.print(posName(g_pos));
  Serial.print(F(" MOTION="));
  Serial.print(motionName(g_motion));
  Serial.print(F(" ERR="));
  Serial.print(errName(g_err));
  Serial.print(F(" RUN="));
  printFlag(g_motorRunning);
  Serial.print(F(" UP="));
  Serial.print(millis());
  Serial.print(F(" EE="));
  switch (g_eeStatus) {
    case EE_OK:   Serial.print(F("OK"));   break;
    case EE_INIT: Serial.print(F("INIT")); break;
    case EE_BAD:  Serial.print(F("BAD"));  break;
    default:      Serial.print(F("BAD"));  break;
  }
  endLine();
}

/* CFG? — relecture de toutes les temporisations. */
static void cmdCfgQuery() {
  Serial.print(F("CFG PULSE="));
  Serial.print(g_cfg.pulseMs);
  Serial.print(F(" INTER="));
  Serial.print(g_cfg.interMs);
  Serial.print(F(" START="));
  Serial.print(g_cfg.startMs);
  Serial.print(F(" TRAVEL="));
  Serial.print(g_cfg.travelMs);
  Serial.print(F(" DEBOUNCE="));
  Serial.print(g_cfg.debounceMs);
  endLine();
}

/* Conversion décimale stricte (chiffres uniquement, pas de dépassement). */
static bool parseU32(const char *s, uint32_t &out) {
  if (s == NULL || *s == '\0') {
    return false;
  }
  uint32_t v = 0;
  for (const char *p = s; *p != '\0'; p++) {
    if (*p < '0' || *p > '9') {
      return false;
    }
    if (v > 429496729UL) {
      return false;                               /* dépassement 32 bits         */
    }
    v = v * 10UL + (uint32_t)(*p - '0');
  }
  out = v;
  return true;
}

/* CFG <clé> <valeur> */
static void cmdCfgSet(char *args) {
  if (args == NULL || args[0] == '\0') {
    sendLine(F("NAK CFG BAD_ARGS"));
    return;
  }

  char *key = args;
  char *val = strchr(args, ' ');
  if (val == NULL) {
    sendLine(F("NAK CFG BAD_ARGS"));
    return;
  }
  *val = '\0';
  val++;
  while (*val == ' ') {
    val++;
  }
  /* Un éventuel troisième champ est ignoré. */
  char *extra = strchr(val, ' ');
  if (extra != NULL) {
    *extra = '\0';
  }

  uint32_t v;
  if (!parseU32(val, v)) {
    /* PRÉCISION PROTOCOLE (ratifiée, mineure) : une valeur non numérique est signalée
     * BAD_ARGS ; OUT_OF_RANGE est réservé aux nombres valides hors bornes. */
    sendLine(F("NAK CFG BAD_ARGS"));
    return;
  }

  uint32_t *dest = NULL;
  uint32_t  lo   = 0;
  uint32_t  hi   = 0;
  const __FlashStringHelper *label = F("");

  if (strcmp_P(key, PSTR("PULSE")) == 0) {
    dest = &g_cfg.pulseMs;    lo = CFG_PULSE_MIN;    hi = CFG_PULSE_MAX;    label = F("PULSE");
  } else if (strcmp_P(key, PSTR("INTER")) == 0) {
    dest = &g_cfg.interMs;    lo = CFG_INTER_MIN;    hi = CFG_INTER_MAX;    label = F("INTER");
  } else if (strcmp_P(key, PSTR("START")) == 0) {
    dest = &g_cfg.startMs;    lo = CFG_START_MIN;    hi = CFG_START_MAX;    label = F("START");
  } else if (strcmp_P(key, PSTR("TRAVEL")) == 0) {
    dest = &g_cfg.travelMs;   lo = CFG_TRAVEL_MIN;   hi = CFG_TRAVEL_MAX;   label = F("TRAVEL");
  } else if (strcmp_P(key, PSTR("DEBOUNCE")) == 0) {
    dest = &g_cfg.debounceMs; lo = CFG_DEBOUNCE_MIN; hi = CFG_DEBOUNCE_MAX; label = F("DEBOUNCE");
  } else {
    sendLine(F("NAK CFG BAD_KEY"));
    return;
  }

  if (v < lo || v > hi) {
    sendLine(F("NAK CFG OUT_OF_RANGE"));
    return;
  }

  *dest = v;
  saveState();                                    /* la config vit dans le même bloc */
  Serial.print(F("ACK CFG "));
  Serial.print(label);
  Serial.print('=');
  Serial.print(v);
  endLine();
}

static void cmdCfgReset() {
  cfgDefaults();
  saveState();
  sendLine(F("ACK CFGRESET"));
}

/* =============================================================================
 *  14. RÉCEPTION SÉRIE ET AIGUILLAGE
 * ============================================================================= */

static void handleLine(char *line) {
  /* Ligne vide ou commentaire : ignorée sans réponse. Le §2 réserve le préfixe
   * « # » au canal de trace ; renvoyer un NAK sur une ligne vide polluerait le
   * dialogue après une purge de tampon côté driver. */
  if (line[0] == '\0' || line[0] == '#') {
    return;
  }

  /* Découpage « premier mot » / « reste ». */
  char *args = strchr(line, ' ');
  if (args != NULL) {
    *args = '\0';
    args++;
    while (*args == ' ') {
      args++;
    }
  }

  if (strcmp_P(line, PSTR("STATE")) == 0) {
    cmdState();
  } else if (strcmp_P(line, PSTR("PING")) == 0) {
    cmdPing();
  } else if (strcmp_P(line, PSTR("OPEN")) == 0) {
    cmdMove(DIR_OPEN);
  } else if (strcmp_P(line, PSTR("CLOSE")) == 0) {
    cmdMove(DIR_CLOSE);
  } else if (strcmp_P(line, PSTR("ABORT")) == 0) {
    cmdAbort();
  } else if (strcmp_P(line, PSTR("CLEARERR")) == 0) {
    cmdClearErr();
  } else if (strcmp_P(line, PSTR("RECOVER")) == 0) {
    /* Seul le premier argument est significatif. */
    if (args != NULL) {
      char *sp = strchr(args, ' ');
      if (sp != NULL) {
        *sp = '\0';
      }
    }
    cmdRecover(args);
  } else if (strcmp_P(line, PSTR("PULSE")) == 0) {
    cmdPulse();
  } else if (strcmp_P(line, PSTR("DIAG")) == 0) {
    cmdDiag();
  } else if (strcmp_P(line, PSTR("CFG?")) == 0) {
    cmdCfgQuery();
  } else if (strcmp_P(line, PSTR("CFGRESET")) == 0) {
    cmdCfgReset();
  } else if (strcmp_P(line, PSTR("CFG")) == 0) {
    cmdCfgSet(args);
  } else {
    sendLine(F("NAK ? UNKNOWN_COMMAND"));
  }
}

/* Lecture non bloquante : une ligne complète au plus par tour de boucle, pour
 * que le relais et la supervision restent servis même si le PC déverse un gros
 * paquet de commandes. */
static void serialUpdate() {
  while (Serial.available() > 0) {
    const int ci = Serial.read();
    if (ci < 0) {
      break;
    }
    char c = (char)ci;

    if (c == '\r') {
      continue;                                   /* §1 — les CR sont ignorés    */
    }
    if (c == '\n') {
      g_line[g_lineLen] = '\0';
      if (g_lineOverflow) {
        /* PRÉCISION PROTOCOLE (ratifiée, mineure) : le §6 ne définit pas de réponse pour une
         * ligne trop longue. On reste dans le vocabulaire documenté plutôt que
         * d'inventer un code que le driver ne saurait pas interpréter. */
        sendLine(F("NAK ? UNKNOWN_COMMAND"));
      } else {
        handleLine(g_line);
      }
      g_lineLen      = 0;
      g_lineOverflow = false;
      return;
    }
    if (c < 0x20 || c > 0x7E) {
      continue;                                   /* §1 — ASCII imprimable seul  */
    }
    if (c >= 'a' && c <= 'z') {
      c = (char)(c - 32);                         /* §1 — normalisation majuscules */
    }
    if (g_lineLen >= LINE_MAX) {
      g_lineOverflow = true;                      /* protection du tampon        */
      continue;
    }
    g_line[g_lineLen++] = c;
  }
}

/* =============================================================================
 *  15. LED D'ÉTAT (purement cosmétique, jamais bloquante)
 * ============================================================================= */

static void ledUpdate(uint32_t now) {
#if LED_ENABLED
  uint16_t onMs;
  uint16_t offMs;

  if (g_motion == MOTION_ERROR) {
    onMs = 100;  offMs = 100;                     /* clignotement rapide         */
  } else if (g_motion == MOTION_OPENING || g_motion == MOTION_CLOSING) {
    onMs = 250;  offMs = 250;                     /* clignotement lent           */
  } else if (g_pos == POS_UNKNOWN) {
    onMs = 60;   offMs = 400;                     /* position inconnue           */
  } else {
    onMs = 40;   offMs = 1960;                    /* battement de cœur           */
  }

  const uint16_t d = g_ledOn ? onMs : offMs;
  if ((now - g_ledLastMs) >= d) {
    g_ledOn     = !g_ledOn;
    g_ledLastMs = now;
    digitalWrite(LED_PIN, g_ledOn ? HIGH : LOW);
  }
#else
  (void)now;
#endif
}

/* =============================================================================
 *  16. DÉMARRAGE (§5)
 * ============================================================================= */

/* Charge la configuration et détermine l'état initial. */
static void bootRestoreState() {
  EeBlock b;
  eeRead(b);

  bool valid = false;
  if (b.magic != EE_MAGIC || b.layout != EE_LAYOUT) {
    g_eeStatus = EE_INIT;                         /* EEPROM vierge ou format ancien */
  } else if (b.sum != eeChecksum(b)) {
    g_eeStatus = EE_BAD;
  } else {
    g_eeStatus = EE_OK;
    valid      = true;
  }

  if (valid) {
    g_cfg.pulseMs    = cfgClamp(b.pulseMs,    CFG_PULSE_MIN,    CFG_PULSE_MAX,    CFG_PULSE_DEF);
    g_cfg.interMs    = cfgClamp(b.interMs,    CFG_INTER_MIN,    CFG_INTER_MAX,    CFG_INTER_DEF);
    g_cfg.startMs    = cfgClamp(b.startMs,    CFG_START_MIN,    CFG_START_MAX,    CFG_START_DEF);
    g_cfg.travelMs   = cfgClamp(b.travelMs,   CFG_TRAVEL_MIN,   CFG_TRAVEL_MAX,   CFG_TRAVEL_DEF);
    g_cfg.debounceMs = cfgClamp(b.debounceMs, CFG_DEBOUNCE_MIN, CFG_DEBOUNCE_MAX, CFG_DEBOUNCE_DEF);
  } else {
    cfgDefaults();
  }

  /* Lecture immédiate des capteurs : au démarrage il n'y a pas d'historique,
   * la valeur brute est prise telle quelle comme valeur stable. */
  const uint32_t now = millis();
  g_swClose.raw = g_swClose.stable = g_swClose.candidate = sensorRawRead(g_swClose.pin);
  g_swOpen.raw  = g_swOpen.stable  = g_swOpen.candidate  = sensorRawRead(g_swOpen.pin);
  g_swClose.lastChangeMs = now;
  g_swOpen.lastChangeMs  = now;

  const bool c = g_swClose.stable;
  const bool o = g_swOpen.stable;

  g_phase          = MP_NONE;
  g_departReleased = true;
  g_motorRunning   = false;

  if (c && o) {
    /* Défaut de câblage détecté dès l'allumage. */
    g_pos    = POS_UNKNOWN;
    g_next   = DIR_OPEN;
    g_motion = MOTION_ERROR;
    g_err    = ERR_BOTH_SENSORS;
    return;
  }

  if (c || o) {
    /* §5 — un capteur actif : position déduite du capteur, EEPROM ignorée. */
    latchOnSensor(c ? POS_CLOSED : POS_OPEN);
    g_motion = MOTION_IDLE;
    g_err    = ERR_NONE;
    return;
  }

  /* Aucun capteur actif. */
  if (!valid || b.moving != 0) {
    /* §5 — EEPROM invalide, ou mouvement interrompu par une coupure. */
    g_pos    = POS_UNKNOWN;
    g_next   = (valid && b.next <= (uint8_t)DIR_CLOSE) ? (Dir)b.next : DIR_OPEN;
    g_motion = MOTION_ERROR;
    g_err    = ERR_INTERRUPTED;
    return;
  }

  /* §5 — EEPROM valide et moving=0 : le modèle `next` est restauré. */
  g_next   = (b.next <= (uint8_t)DIR_CLOSE) ? (Dir)b.next : DIR_OPEN;
  g_motion = MOTION_IDLE;
  g_err    = ERR_NONE;

  /* PRÉCISION PROTOCOLE (ratifiée) : le §5 impose pos=INTERMEDIATE dans ce cas. On conserve
   * UNKNOWN si c'est ce qui avait été persisté (typiquement après une commande
   * PULSE de diagnostic), car annoncer INTERMEDIATE ferait croire au driver
   * que la position est connue alors qu'elle ne l'est pas : le toit serait
   * piloté avec un modèle `next` non fiable. Dans tous les autres cas la règle
   * du §5 est appliquée telle quelle. */
  g_pos = (b.pos == (uint8_t)POS_UNKNOWN) ? POS_UNKNOWN : POS_INTERMEDIATE;
}

void setup() {
  /* -------------------------------------------------------------------------
   * §7.2 — TOUT PREMIER GESTE : mettre le relais à l'état INACTIF, avant même
   * Serial.begin(). L'ordre digitalWrite → pinMode → digitalWrite est le seul
   * qui soit exempt de glitch pour les DEUX polarités :
   *   - actif haut : le premier digitalWrite(LOW) désarme le pull-up interne,
   *     le port est déjà à 0 quand pinMode() le passe en sortie ;
   *   - actif bas  : le premier digitalWrite(HIGH) arme le pull-up interne, ce
   *     qui maintient déjà la ligne au niveau inactif AVANT le passage en
   *     sortie ; sans cela la broche partirait brièvement à 0 = relais collé.
   * Rappel : pendant le reset et le bootloader la broche est FLOTTANTE, seule
   * la résistance externe de 10 kΩ vers GND protège réellement le montage.
   * ----------------------------------------------------------------------- */
  digitalWrite(RELAY_PIN, RELAY_LEVEL_INACTIVE);
  pinMode(RELAY_PIN, OUTPUT);
  digitalWrite(RELAY_PIN, RELAY_LEVEL_INACTIVE);
  g_relayActive = false;

  /* Capteurs : contacts NO vers GND, pull-up interne. */
  pinMode(SENSOR_CLOSED_PIN, INPUT_PULLUP);
  pinMode(SENSOR_OPEN_PIN,   INPUT_PULLUP);

#if LED_ENABLED
  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, LOW);
#endif

  cfgDefaults();
  Serial.begin(115200);

  bootRestoreState();

  /* §5 — l'état reconstruit est persisté immédiatement : si la coupure se
   * reproduit avant toute commande, le redémarrage suivant repartira du bon
   * pied (en particulier moving repasse à 0). */
  saveState();

  /* §2 — trace de démarrage obligatoire. */
  sendLine(F("# ROOF CONTROLLER " FIRMWARE_VERSION " BOOT"));
}

/* =============================================================================
 *  17. BOUCLE PRINCIPALE — aucune instruction bloquante
 * ============================================================================= */

void loop() {
  const uint32_t now = millis();

  relayUpdate(now);            /* §7.3 — priorité absolue : relâcher le relais  */
  sensorsUpdate(now);          /* anti-rebond                                   */
  applySensorInvariant(now);   /* §4 — recalage du modèle sur la réalité        */
  supervisionUpdate(now);      /* suivi de course, timeouts START / TRAVEL      */
  sequencerUpdate(now);        /* émission des impulsions programmées           */
  serialUpdate();              /* §2 — une ligne reçue → une ligne de réponse   */
  ledUpdate(now);
}
