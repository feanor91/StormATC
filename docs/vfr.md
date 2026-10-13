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

Les règles de vol sont un attribut du vol. Elles se règlent de deux façons :

- sur la page **Plan de vol**, liste « Règles de vol » (IFR ou VFR) ;
- à la radio : dire « VFR » ou « vol local » dans la première transmission (demande de roulage,
  appel d'arrivée) passe le vol en VFR.

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
