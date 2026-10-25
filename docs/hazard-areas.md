# Zones militaires et météo (`hazard-areas.json`)

StormATC peut refuser un direct ou une route qui traverse une zone active, et détourner un vol qui va y entrer
(cap à collationner, puis « reprenez votre navigation »). Il ne connaît aucune zone par lui-même : une liste embarquée
deviendrait fausse (les zones changent chaque jour). Vous donnez les zones qui comptent pour votre vol dans un fichier
facultatif.

## Où le mettre

`%LocalAppData%\StormATC\hazard-areas.json` (le dossier des données de l'application, le même que celui des profils VFR
personnels). Le fichier est relu quand il change : inutile de relancer le programme. Sans fichier, rien ne change.

## Format

```json
{
  "areas": [
    {
      "name": "R 45",
      "kind": "military",
      "latitude": 48.10,
      "longitude": 2.30,
      "radiusNm": 10,
      "floorFeet": 0,
      "ceilingFeet": 19500,
      "active": true
    },
    {
      "name": "orages",
      "kind": "weather",
      "latitude": 47.00,
      "longitude": 3.00,
      "radiusNm": 15
    }
  ]
}
```

| Champ | Sens |
| --- | --- |
| `name` | Le nom que dit le contrôleur (« la zone militaire R 45 », « la zone de mauvais temps orages »). |
| `kind` | `military` (par défaut) ou `weather`. |
| `latitude`, `longitude` | Le centre, en degrés décimaux. |
| `radiusNm` | Le rayon, en milles nautiques. Une zone est un cercle. |
| `floorFeet`, `ceilingFeet` | Les niveaux couverts, en pieds (0 et 99 999 par défaut). Un vol hors de ces niveaux n'est pas concerné. |
| `active` | `false` pour désactiver une zone sans la supprimer. |

Une zone sans nom, sans centre valide ou sans rayon est ignorée.

## Ce que fait le contrôleur

- **Demande du pilote** : un direct ou une nouvelle route dont le chemin passe à moins de 3 nm de la zone, au niveau du
  vol, est refusé (« négatif, la zone militaire R 45 est active sur cette route »).
- **Initiative du contrôleur** : un vol IFR dont la route, sur les 30 prochains milles, traverse la zone reçoit un cap
  qui la contourne par le côté opposé à son centre, une seule fois par zone, à collationner. Quand la zone est derrière
  lui, il reçoit « la zone est dégagée, reprenez votre navigation ».
- Le direct que le contrôleur donne de lui-même n'est jamais donné à travers une zone.

Le cercle est une approximation d'une zone réelle. Les distances de contournement et de marge (3 nm) sont des choix
de StormATC, pas des valeurs réglementaires.
