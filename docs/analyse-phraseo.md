# Analyse du site « Phraséologie aéronautique pour les pilotes virtuels »

Site : <https://phraseo.aeronautique.xyz/> · données : <https://github.com/pierrezNet/site_phraseo> (dossier `src/data`).
Analyse faite le 25 octobre 2026.

## Ce que contient le site

- Une application Vue qui fait **jouer une phraséologie** à l'écran : phrases du contrôleur et réponses du pilote, en
  **français et en anglais**, avec des étiquettes à remplacer par les données du vol (`[CAL]` indicatif, `[CAA]` indicatif
  abrégé, `[NIV]` niveau, `[SQU]` code transpondeur, `[RWY]` piste, `[HLD]` point d'attente, `[POS]` poste, `[SID]`,
  `[INF]` lettre ATIS, `[ARR]` destination…).
- Trois niveaux (débutant, intermédiaire, avancé), un mode quiz (réponse à la voix), une voix pour le contrôleur, et des
  scénarios de vol complets (`scenarios.json`).
- Les textes sont dans deux fichiers JSON : `phraseologieIFR.json` (**156 situations**, plus 27 variantes au choix) et
  `phraseologieVFR.json` (**65 situations**, plus 14 variantes au choix). Chaque situation a un identifiant, un nom, un
  niveau et la suite des phrases (contrôleur et pilote) dans les deux langues.
- La source est le **Manuel de phraséologie** de la DGAC (10e édition, à jour au 15 avril 2023), lié depuis le site.

## Licence

Le README du dépôt indique **Creative Commons Attribution 4.0 (CC BY 4.0)** : on peut reprendre et adapter les textes à
condition de citer l'auteur et le site. Le dépôt n'a pas de fichier de licence, seulement cette mention. Si StormATC reprend
des textes, il faut les attribuer (README et page d'information), comme pour le SIA.

## Une réserve sur la qualité des données

Le dépôt contient son propre audit (`docs/audit-phraseo-ch5.md`) : **40 erreurs et 50 manquements** relevés par rapport au
manuel pour le chapitre 5. Les plus fréquents : le pilote qui collationne « autorisé toucher » au lieu du verbe d'action
(« toucher piste XX »), « verticale terrain » au lieu de « verticale aérodrome », « montée initiale » à la place de
« niveau » dans la clairance de départ, et des fautes d'anglais. Il ne faut donc pas copier les textes tels quels : on
s'appuie sur le manuel pour trancher.

## Ce que StormATC fait déjà

Clairance IFR avec SID, code transpondeur et collationnement ; repoussage ; roulage avec point d'attente ; alignement et
décollage ; transferts Tour, Départ, Centre et Approche ; changements de niveau, de cap et direct ; descente ; approche
et atterrissage ; remise de gaz ; dégagement de piste ; traversée de piste ; roulage vers le parking ; en VFR : appel
d'arrivée, point de report, circuit (vent arrière, base, finale), toucher, complet, option, passage bas, sortie de
fréquence ; et depuis la 0.15.0 le suivi VFR par l'information de vol.

## Ce qui manque (relevé approximatif)

| Domaine | Situations du site que StormATC ne traite pas encore |
|---|---|
| Sol (IFR et VFR) | remontée de piste, maintien de position, suivez le trafic / priorité au trafic, ralentir ou accélérer le roulage, tractage, véhicule d'inspection (FLYCO), heure exacte, vérification du plan de vol, « prêt sous 2 minutes ? », demande de TORA à l'intersection, repoussage conditionnel |
| Alignement et décollage | alignement après un avion en finale (conditionnel), numéro 2 et multi-alignement, décollage immédiat ou dégagez, décollage annulé / interrompu / stoppé, « restez dans l'axe de piste », demande de virage |
| Départ et montée | contraintes de niveau ou de taux, « expédiez la montée », stable avant un point, annulation des restrictions du SID, guidage puis rejointe du SID, départ à vue, capacité RNAV, RVSM |
| En route | compte rendu de position, information trafic avec « je surveille » / « trafic en vue », séparation à vue, évitement météo, route refusée ou acceptée, annulation IFR, SELCAL, CPDLC |
| Arrivée IFR | descente expédiée, contrôle de vitesse (réduire, Mach, libre), attente publiée ou non publiée, interception ILS, approche indirecte (MVL), VPT, RNP, approche impossible, approches à vue successives |
| Atterrissage | autorisé numéro 2 derrière un trafic, dégagement rapide ou à grande vitesse |
| VFR | VFR spécial (départ, refus pour visibilité, arrivée, transit), transit d'un aérodrome (verticale, clairance IFR, 360), entrée sans ATIS, allonger la vent arrière, 360 par la droite, séquencement du circuit, attente à vue, continuez l'approche, clôture du plan de vol, fin de service (UNICOM) |
| Échanges généraux | turbulence de sillage, taux de montée, « parlez plus lentement », identification transpondeur |

Cette liste vient d'une comparaison par mots-clés avec le code, pas d'un test en vol : quelques points sont peut-être
déjà couverts en partie.

## Ce qu'on peut en faire

1. **Rapport d'écarts** (ce document) : fait.
2. **Reprise ciblée des situations les plus utiles**, dans l'ordre : sol et alignement (remontée de piste, alignement
   conditionnel, décollage immédiat, annulé), puis en route (position, trafic, annulation IFR), puis VFR (VFR spécial,
   transit, 360, entrée sans ATIS). Chaque situation demande une reconnaissance de l'appel du pilote (français et anglais),
   une règle du moteur qui décide de la réponse, et la phrase du contrôleur.
3. **Une source de vérification** : comparer nos phrases existantes à celles du site et du manuel, et corriger les écarts.
