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

Les cartes VAC du SIA ne sont pas livrées : StormATC les télécharge **telles que publiées** dans `Cartes VAC/`
(Paramètres → Cartes VAC), avec leur source et leur cycle AIRAC (`_cycle.txt`, `LISEZ-MOI.txt`) : voir « Données et
cartes aéronautiques » dans le README.
Les profils ne contiennent que des faits retranscrits.

**Profils fournis** (AIP France AD 2, relus à la main depuis les fiches du SIA) : Lognes (LFPL),
Melun-Villaroche (LFPM, points N, SM et B), Paris Saclay Versailles / Toussus (LFPN, points S et NH,
circuit à 1500 ft), Chartres-Métropole (LFOR, circuit au nord des pistes), Étampes-Mondésir (LFOX),
Orléans-Saint-Denis-de-l'Hôtel (LFOZ), Le Puy-Loudes (LFHP), Pithiviers (LFFP), Buno-Bonnevaux (LFFB),
Paris-Orly (LFPO, pour les règles d'accès VFR) et la base militaire de Saint-Dizier-Robinson (LFSI, régimes CAM).
Le moteur n'en utilise que les points de report, la
hauteur et le côté du circuit et la piste préférée ; le reste (services, horaires, consignes) est de la
documentation. La main du circuit n'est renseignée que si la fiche la donne en texte : les autres cas se
lisent sur le dessin de la carte, qu'il faut vérifier à l'œil.

### Profils extraits automatiquement

Pour tous les autres aérodromes dont une carte VAC existe, `scripts/extract_vac_profiles.py` lit le **texte
imprimé en en-tête et dans le tableau des pistes** de la carte (poppler `pdftotext` requis) :
services et fréquences (APP, TWR, GND, ATIS, AFIS, A/A), altitude, position, variation magnétique, pistes et
leur QFU. Ces profils portent `"extraction": "automatic"` dans `source`. Ils servent surtout à savoir si un
aérodrome est **sans ATS** (StormATC ne répond alors pas) et quelle est sa fréquence air-air. Le circuit et les
points de report sont des **dessins** : ils ne sont pas extraits, le moteur garde ses valeurs par défaut. Un
profil écrit à la main n'est jamais écrasé.

```
python scripts/extract_vac_profiles.py "<dossier Cartes VAC>" --overwrite
```

Les **bases militaires** sans carte VAC (Cazaux, Mont-de-Marsan, Solenzara, Orange, Avord, Villacoublay,
Nancy-Ochey, Luxeuil, Évreux, Landivisiau, Lanvéoc) sont extraites de la page eAIP AD 2 du SIA par
`scripts/extract_military_profiles.py` : position, altitude, pistes et fréquences **VHF** (MSFS n'a pas de radio
UHF), en régimes CAM V et CAM I. Creil (LFPC), Cambrai-Épinoy (LFQI), Valdahon (LFXH) et Coëtquidan (LFXQ) n'ont
pas de page exploitable.

### Mettre à jour les cartes

Le SIA publie un nouveau cycle AIRAC tous les 28 jours. `scripts/Update-VacCharts.ps1` retrouve le cycle en
vigueur, télécharge les cartes que vous avez déjà et ne remplace un fichier que s'il a changé :

```
.\scripts\Update-VacCharts.ps1 -DryRun           # ce qui changerait, sans rien écrire
.\scripts\Update-VacCharts.ps1                   # met à jour
.\scripts\Update-VacCharts.ps1 -IncludeMissing   # ajoute aussi les cartes de data/aerodromes-france.csv
```

Les cartes restent telles que publiées ; le cycle et la source sont écrits dans `_cycle.txt` du dossier (le SIA
autorise la réutilisation sans altération, avec mention de la source et de la date).

### Ajouter un aérodrome à partir d'une carte

Les profils sont écrits à la main à partir des fiches AIP (AD 2) : on lit le texte et les pages en image
(les tableaux sortent décalés du texte, il faut les vérifier sur l'image) et on ne retranscrit que ce que la
carte dit. Pour en ajouter, déposez les PDF dans `Cartes VAC/` à la racine du dépôt
et demandez au mainteneur de les traiter, ou copiez `LFPL.json` et remplissez-le (voir plus haut).

Un profil placé dans `%LOCALAPPDATA%\StormATC\vfr-profiles\<OACI>.json` est lu **avant** ceux qui sont fournis
avec StormATC : c'est le moyen d'essayer un profil sans réinstaller, ou de corriger un aérodrome pour soi. Il est
utilisé au prochain chargement d'un vol, et une mise à jour ne le touche pas.

## Le suivi VFR (service d'information de vol)

En vol, un vol VFR peut demander à être suivi par le service d'information de vol de la région : *Paris Information* ou
*Marseille Information*. Il suffit d'afficher la fréquence sur la radio et d'appeler :

> « Paris Information, F-HAST, PA 28, VFR de Lognes vers Pontoise, deux mille cinq cents pieds, demande de suivi. »

Le service répond avec son nom et un code transpondeur (« affichez deux trois quatre cinq »). Ensuite :

- au passage d'un secteur à l'autre (Nord, Sud, Ouest), il vous donne la nouvelle fréquence (« contactez Paris Information
  cent vingt-cinq décimale sept ») et le suivi continue ;
- à moins de 15 nm de la destination, il met fin au suivi et vous passe l'approche ou la tour de l'aérodrome ;
- si vous quittez la zone couverte, ou si vous affichez une autre fréquence, le suivi prend fin.

Les fréquences sont celles de l'AIP France (GEN 3.4) : Paris Nord 125,700, Sud 126,100, Ouest 129,625 ; Marseille Nord
124,500, Sud 120,550. L'AIP ne publie aucune fréquence d'information de vol pour l'ouest (Bretagne) ni le sud-ouest
(Bordeaux) : là, StormATC répond qu'aucun service n'est disponible. Les limites des secteurs sont dessinées sur une carte
de l'AIP (ENR 6.1), pas écrites : StormATC les approche par des latitudes et des longitudes. Elles ne servent pas à la
navigation réelle.

## Limites

- Pas de séquencement du trafic : jamais de « numéro deux, suivez… ». Chaque avion est « numéro
  un » tant qu'aucune piste n'est signalée occupée.
- Pas de simulation de l'auto-information ni de l'AFIS : seul le contrôle d'aérodrome est simulé. Sur un
  aérodrome que le profil déclare **sans ATS** (`service.kind` : `None`, par exemple Chartres LFOR, Étampes,
  Pithiviers, Buno-Bonnevaux), StormATC **ne répond pas** : il le dit dans le dialogue au chargement du plan
  (« n'a pas de service de la circulation aérienne, faites vos comptes rendus à l'aveugle sur 119,205 ») et
  rappelle la même chose si vous émettez. Les comptes rendus d'auto-information ne sont pas encore
  vérifiés ni répondus.
- Pas de transit VFR en route, de zones, ni d'information de vol.
- Pas de départ VFR avec compte rendu à un point de sortie.
- Le sens du circuit de LFPL est lu sur le dessin de la carte d'atterrissage, pas sur un texte.
