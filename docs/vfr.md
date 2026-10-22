# Vol VFR sur un aérodrome contrôlé

StormATC sait faire voler un circuit VFR sur un aérodrome avec Tour, en français et en anglais.
Le premier terrain décrit est **Lognes-Émerainville (LFPL)**, d'après la carte AIP France
AD 2 LFPL (AMDT 03/26, du 19 février 2026).

## Ce qui change pour un vol VFR

| | IFR | VFR |
| --- | --- | --- |
| Clairance IFR, SID, transpondeur assigné | oui | non |
| Premier appel | Prévol (ou Sol) | Sol, ou la Tour s'il n'y a pas de Sol |
| Après le décollage | transfert au Départ | on reste avec la Tour |
| Arrivée | approche aux instruments puis Tour | appel direct à la Tour, point de report, circuit |

## Démarrer un vol VFR local

Sur la page **Plan de vol**, la carte « Vol VFR local » ne demande ni plan SimBrief ni plan de vol :
indiquez le code OACI de l'aérodrome où vous êtes (par exemple LFPL) et votre indicatif ou
immatriculation (proposé d'après l'identifiant de l'avion dans MSFS), puis **Démarrer un vol VFR
local**. StormATC lit les fréquences et les pistes du terrain dans le simulateur, passe le vol en
VFR. Il ne touche pas à votre radio : **vous réglez la COM1 dans l'avion** et StormATC suit. Réglez
l'ATIS (écoute seule), Sol ou la Tour : le contrôleur qui répond est celui dont vous avez réglé la
fréquence publiée, dans l'ordre que vous voulez.

Le vol est mémorisé : à l'ouverture suivante de StormATC, **si MSFS tourne déjà**, il est relancé
tout seul et vous n'avez rien à refaire. Décochez la case « Relancer ce vol… » pour ne pas le
mémoriser. Importer un plan SimBrief arrête aussi ce redémarrage automatique. Si StormATC s'ouvre
avant MSFS, il démarre sur le vol d'exemple : utilisez alors le bouton.

Les règles de vol sont un attribut du vol. Elles se règlent de deux façons :

- sur la page **Plan de vol**, liste « Règles de vol » (IFR ou VFR) ;
- à la radio : dire « VFR » ou « vol local » dans la première transmission (demande de roulage,
  appel d'arrivée) passe le vol en VFR.

## Le plan de vol fait dans le simulateur (EFB)

StormATC reprend le plan de vol que vous faites dans l'EFB de MSFS : terrain de départ et
d'arrivée, VFR ou IFR, piste de départ, altitude de croisière, points de la route. Le nom de
l'avion (l'immatriculation) vient de l'avion lui-même.

**Le moyen principal : le module de StormATC dans le simulateur** ([détails](msfs-module.md)). Un petit
module WASM, chargé avec MSFS (menus compris), lit le plan de l'EFB par l'API officielle du simulateur
et le donne à StormATC **en direct** : dès que vous cliquez sur **Envoyer à l'ATC** ou **Envoyer à
l'ATC et à l'avionique**, avant « Démarrer le vol » comme pendant le vol, le plan est chargé dans la
session et la page Plan de vol se remplit. Rien à enregistrer, rien à ouvrir. Il faut avoir redémarré
MSFS une fois après l'installation de StormATC (un module se charge au démarrage du simulateur).

Le plan que l'EFB montre mais que vous n'avez pas envoyé est seulement mémorisé : le bouton
**Importer le plan de vol du simulateur** de la page Plan de vol le charge à la demande.

**Les moyens de secours**, si le module ne se charge pas :

- **Le panneau StormATC de la barre d'outils**, pendant le vol et ouvert (il peut être réduit), lit le
  même plan par le JavaScript du simulateur et le transmet à StormATC. Une ligne en bas du panneau dit
  où en est la lecture.
- **Le plan du vol qui vient d'être démarré** : le simulateur l'écrit dans un fichier à côté du vol, et
  StormATC le lit quand il se connecte. Il remplace le plan d'un vol précédent, jamais un plan SimBrief.
- **Un fichier `.PLN`** (enregistré depuis l'EFB, un planificateur…) : page **Plan de vol › Plan de
  vol du simulateur › Ouvrir un fichier de plan de vol (.pln)…**. Le bouton d'import cherche aussi le
  plus récent dans les dossiers du simulateur, sur le bureau et dans les documents. L'EFB n'écrit un
  `.PLN` que si vous enregistrez le plan.

Tout ce que StormATC reçoit du simulateur est écrit dans le journal (`%LOCALAPPDATA%\StormATC\logs`,
lignes « The simulator's panel sent the … route » et « The simulator announced a flight plan
event »). Réglages `SimFlightPlan` : `Enabled` (désactive la reprise automatique) et
`ImportOnFlightLoaded`.

Ce qui n'est pas garanti : le simulateur ne documente pas tout ce que l'EFB renvoie. Les identifiants
de points de route et les altitudes peuvent manquer, et un plan IFR n'apporte ni départ normalisé
(SID) ni arrivée dans StormATC : seuls les vols VFR sont validés. Si le plan de l'EFB n'est pas
repris, le journal dit ce qui est arrivé.

## Les phrases reconnues

La phraséologie suit le chapitre « Circuit d'aérodrome contrôlé » du manuel de phraséologie
(DSNA, version du 15 avril 2023).

| Vous | Le contrôleur |
| --- | --- |
| « Lognes Sol, … vol local, demande roulage » | roulage vers le point d'attente (comme en IFR) |
| « Lognes Tour, … VFR de Melun pour un toucher, 1 200 pieds, estimé Écho à 05, information India, demande consignes d'intégration » | « …, reçu, rappelez Écho » |
| « … passe Écho » | « …, entrez vent arrière main gauche piste 08, rappelez vent arrière » |
| « … vent arrière main gauche piste 08 » | « …, rappelez base main gauche piste 08 » |
| « … base main gauche piste 08 » | « …, rappelez finale piste 08 » |
| « … en finale piste 08 » | « …, piste 08, autorisé atterrissage » ou « …, piste 08, autorisé toucher » |
| « Demande toucher » (à tout moment dans le circuit) | accusé de réception ; l'autorisation vient en finale |
| « Sortie de circuit, je quitte la fréquence » | « …, reçu, au revoir » |

Répéter une instruction (« je rappelle vent arrière… », « j'entre vent arrière… ») n'est pas pris
pour un compte rendu de position. Les mêmes échanges existent en anglais (« join left-hand
downwind runway 08, report downwind », « cleared touch and go »).

Après un toucher, l'avion reprend sa montée sans instruction de dégagement ni transfert. Après un
atterrissage complet, le déroulement est celui du vol IFR : dégagement, transfert à Sol, roulage
vers le parking.

## Main gauche ou main droite

Le sens du circuit dépend de la piste en service. Le profil de l'aérodrome indique de quel côté
des pistes se trouve le circuit (un cap, 0 pour le nord) ; le moteur en déduit le sens pour chaque
piste. À LFPL, le circuit est au nord des pistes : main gauche pour la 08, main droite pour la 26.
Si le profil ne donne pas de côté, la main n'est pas annoncée.

## Les profils d'aérodrome

Ce que le simulateur ne sait pas (points de report, hauteur et côté du circuit, direction de piste
préférée) est décrit dans `data/vfr-profiles/<OACI>.json`. Les pistes, taxiways, parkings et
fréquences viennent de MSFS. Un aérodrome sans profil fonctionne quand même, avec une phraséologie
générique (pas de point de report nommé, pas de main annoncée).

Pour ajouter un aérodrome, copier `LFPL.json` et remplir : `icao`, `name`, `reportingPoints`
(désignation et nom parlé en français et en anglais), `circuit.heightFeetAmsl`,
`circuit.sideBearingDegrees` et `preferredQfu`. Les champs de texte libre ne sont que de la
documentation. Indiquer la source et la date d'application dans `source`.

Les cartes VAC du SIA sont © SIA : le dossier `Cartes VAC/` est ignoré par Git, et les profils ne
contiennent que des faits retranscrits.

### Faire lire une carte par StormATC

Plutôt que de les écrire à la main, vous pouvez demander à StormATC de lire une carte : **Réglages ›
Profils VFR à partir des cartes**.

1. Déposez le PDF dans le dossier des cartes (voir [le panneau](ingame-panel.md#les-cartes-pdf)), nommé avec
   le code OACI (`AD-2.LFPN.pdf`).
2. Dans les Réglages, choisissez un fournisseur de modèle de langage **qui lit les images** (OpenAI,
   OpenRouter, Groq ou Anthropic ; pas DeepSeek), un modèle capable de vision, sa clé, et acceptez les
   services cloud. Choisissez la carte, puis **Lire la carte**.
3. StormATC dessine les pages (douze au plus) et les envoie à ce fournisseur : **les images de la carte
   quittent donc votre PC**, comme le dit la page. Le modèle répond par un document strict : pour chaque
   fait, la valeur, **les mots de la carte dont elle vient** et la page.
4. StormATC contrôle la réponse (piste entre 1° et 360°, hauteur de circuit plausible, désignation de
   point de report de une à six lettres, aucune valeur sans citation, code OACI du nom du fichier). Vous
   voyez chaque fait avec sa citation, vous cochez ceux qui sont justes ; ceux dont le modèle n'est pas
   sûr, et le **côté du circuit** (souvent lu sur un dessin), sont décochés tant que vous ne les avez pas
   vérifiés sur la carte.
5. **Enregistrer le profil** l'écrit dans `%LOCALAPPDATA%\StormATC\vfr-profiles\<OACI>.json`. Ce dossier est
   lu **avant** les profils fournis avec StormATC et n'est jamais touché par une mise à jour ; l'ancien
   profil enregistré est gardé en `.bak`. Il est utilisé au prochain chargement d'un vol.

Le modèle ne fait que proposer : il n'écrit jamais un profil que vous n'avez pas vu, et il ne décide de
rien dans l'ATC (le moteur lit un profil enregistré comme les autres). Il peut se tromper, surtout sur les
tableaux et les dessins : vérifiez chaque fait. Le coût est de quelques centimes par carte chez votre
fournisseur ; StormATC n'en facture rien.

**Profils fournis** (AIP France AD 2, relus à la main depuis les fiches du SIA) : Lognes (LFPL),
Melun-Villaroche (LFPM, points N, SM et B), Paris Saclay Versailles / Toussus (LFPN, points S et NH,
circuit à 1500 ft), Étampes-Mondésir (LFOX), Orléans-Saint-Denis-de-l'Hôtel (LFOZ), Le Puy-Loudes
(LFHP), Pithiviers (LFFP), Buno-Bonnevaux (LFFB) et Paris-Orly (LFPO, pour les règles d'accès VFR).
Le moteur n'en utilise que les points de report, la hauteur et le côté du circuit et la piste
préférée ; le reste (services, horaires, consignes) est de la documentation. La main du circuit n'est
renseignée que si la fiche la donne en texte : les autres cas se lisent sur le dessin de la carte,
qu'il faut vérifier à l'œil.

## Limites

- Pas de séquencement du trafic : jamais de « numéro deux, suivez… ». Chaque avion est « numéro
  un » tant qu'aucune piste n'est signalée occupée.
- Pas de mode sans ATS (auto-information) ni d'AFIS : seul le contrôle d'aérodrome est simulé.
- Pas de transit VFR en route, de zones, ni d'information de vol.
- Pas de départ VFR avec compte rendu à un point de sortie.
- Le sens du circuit de LFPL est lu sur le dessin de la carte d'atterrissage, pas sur un texte.
