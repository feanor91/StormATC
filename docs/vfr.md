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

**Avant de démarrer le vol** (carte du monde, écran « Démarrer le vol ») : le panneau StormATC
n'existe pas encore, car les panneaux de la barre d'outils ne vivent que pendant le vol. Le
simulateur écrit alors le plan que vous avez réglé (terrains, piste, VFR ou IFR) dans un fichier à côté
du vol, et StormATC le lit **au moment où il se connecte au vol** (ou quand le simulateur annonce
qu'un vol est chargé). Il remplace le plan d'un vol précédent, jamais un plan SimBrief. Un
« Envoyer à l'ATC » cliqué à cet écran n'est pas lisible en lui-même : seul ce qui est réglé au
démarrage du vol est repris.

**Pendant le vol, en temps réel, avec le panneau StormATC.** Le simulateur donne le plan aux
programmes JavaScript (Planned Route API). Le panneau StormATC de la barre d'outils le lit et le
transmet à StormATC :

- quand vous cliquez sur **Envoyer à l'ATC** ou **Envoyer à l'ATC et à l'avionique** dans l'EFB, le
  plan est chargé dans la session et la page Plan de vol se remplit ;
- le plan que l'EFB montre mais que vous n'avez pas envoyé est seulement mémorisé (il change à
  chaque modification) : le bouton **Importer le plan de vol du simulateur** de la page Plan de vol le
  charge à la demande ;
- un plan qui était déjà dans l'EFB à l'ouverture du panneau est mémorisé, pas appliqué tout seul.

Il faut que **le panneau soit ouvert** (il peut être réduit) et que StormATC tourne. Il n'est pas
nécessaire d'enregistrer le plan dans un fichier. En bas du panneau, une ligne dit où en est la
lecture : « à l'écoute », « plan lu (ATC) : LFPL > LFPT, envoyé à StormATC », ou le problème.

**Un fichier `.PLN`** (enregistré depuis l'EFB, un planificateur…) : page **Plan de vol ›
Plan de vol du simulateur › Ouvrir un fichier de plan de vol (.pln)…**. Le bouton **Importer le plan
de vol du simulateur** cherche tout seul le plus récent dans les dossiers du simulateur, sur le
bureau et dans les documents. L'EFB n'écrit un `.PLN` que si vous enregistrez le plan.

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

## Limites

- Pas de séquencement du trafic : jamais de « numéro deux, suivez… ». Chaque avion est « numéro
  un » tant qu'aucune piste n'est signalée occupée.
- Pas de mode sans ATS (auto-information) ni d'AFIS : seul le contrôle d'aérodrome est simulé.
- Pas de transit VFR en route, de zones, ni d'information de vol.
- Pas de départ VFR avec compte rendu à un point de sortie.
- Le sens du circuit de LFPL est lu sur le dessin de la carte d'atterrissage, pas sur un texte.
