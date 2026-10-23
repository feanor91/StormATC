# La partie militaire de StormATC, expliquée

StormATC peut jouer le contrôleur d'une **base aérienne militaire française** (Saint-Dizier-Robinson, Cazaux,
Mont-de-Marsan, Orange, Avord, Nancy-Ochey, Luxeuil, Évreux, Landivisiau, Lanvéoc…), avec la phraséologie de
l'**Instruction 1950 DSAÉ/DIRCAM** (version 1.1, 19 octobre 2023), le document public qui fixe les expressions
radio des pilotes et des contrôleurs militaires. C'est de la **simulation de vol** : rien ici ne sert à voler pour
de vrai, et StormATC n'est pas un document officiel.

## 1. Les régimes : CAM V, CAM I, CAM T

**CAM** veut dire *circulation aérienne militaire*. Un avion militaire n'est pas toujours régi par les règles
civiles (la **CAG**, circulation aérienne générale) : il vole selon un *régime*, que vous choisissez sur la page
**Plan de vol** de StormATC ou que vous annoncez à la radio.

| Régime | Nom | Ce que c'est | Équivalent civil |
| --- | --- | --- | --- |
| **CAM V** | Victor | Vol **à vue** : vous voyez et évitez les autres, le contrôleur vous guide dans le circuit | VFR |
| **CAM I** | India | Vol **aux instruments** : clairance, procédures, départ et arrivée contrôlés | IFR |
| **CAM T** | Tango | Vol **tactique** (mission, entraînement) : hors du service de la circulation aérienne | rien |

**Comment le régime est choisi.** Le planificateur de vol de MSFS propose un type de vol « militaire », mais il ne le
transmet pas (le plan qu'il donne à StormATC ne dit que VFR ou IFR). StormATC regarde donc **les aérodromes** : un plan au
départ ou à destination d'une **base militaire** qu'il connaît démarre en **CAM V** (plan VFR) ou **CAM I** (plan IFR). Pour
tout autre plan, choisissez le régime sur la page **Plan de vol** de StormATC (liste « IFR, VFR, CAM V, CAM I, CAM T »)
ou à la radio.

Pour changer de régime à la radio : « demandons CAM India », « demandons CAM Victor », « demandons CAM Tango ».
Le contrôleur répond « vous êtes maintenant en CAM India ». En **CAM Tango**, il répond que c'est un vol tactique,
hors service de la circulation aérienne, et de le rappeler pour reprendre CAM Victor ou India.

> L'Instruction 1950 dit elle-même qu'elle **ne décrit pas** les procédures radio tactiques (elles font l'objet de
> documents particuliers). StormATC n'invente donc pas de phraséologie de combat : il enregistre le régime, ne donne
> plus de clairance, et propose la **situation tactique** ci-dessous.

### La situation tactique (CAM T)

En CAM T, vous pouvez demander « **demande la situation** » (ou « situation », « picture », « bogey dope »). Le
contrôleur donne les **trois contacts les plus proches** vus par MSFS, de la forme : « deux contacts, premier,
gisement zéro quatre cinq, trente-huit milles, vingt-cinq mille pieds, chaud, deuxième… ».

- **Gisement** : le cap magnétique de l'avion vers le contact. **Distance** en milles nautiques, **altitude** arrondie
  à 100 pieds.
- **Aspect** : *chaud* = le contact se dirige vers vous, *froid* = il s'éloigne, *flanc* = il vous coupe la route.
- Les contacts sont les **avions IA** que MSFS signale, y compris ceux que la tablette du Rafale AzurPoly fait
  apparaître. En CAM T, StormATC lit le trafic jusqu'à **150 milles** (20 km dans les autres régimes). Il faut que
  **Paramètres → Intégrations en direct → « Enable live traffic awareness »** soit coché.
- StormATC **ne dit pas qui est ami ou ennemi** et ne donne aucune autorisation de tir : il décrit seulement ce que
  MSFS lui montre. Les mots *chaud/froid/flanc* et la forme du message sont des choix de StormATC, pas ceux d'un
  document officiel.
- **Pour vérifier** que les cibles sont vues : le journal (`%LOCALAPPDATA%\StormATC\logs`) écrit toutes les
  30 secondes en CAM T « Tactical traffic scan within … km : N aircraft » avec les trois premiers (type, altitude).

Le Rafale d'AzurPoly publie aussi, dans ses variables internes, la cible sélectionnée et des événements de tir
(MICA, Meteor, canon…). StormATC **ne les lit pas encore** : leur contenu doit d'abord être relevé en vol.

## 2. Le vocabulaire

- **QFE** : le calage altimétrique qui fait lire **zéro sur la piste** (l'altimètre donne la hauteur au-dessus du
  terrain). Les militaires l'utilisent à la place du **QNH** (altitude au-dessus de la mer). StormATC le calcule :
  QFE ≈ QNH − altitude du terrain ÷ 27 (environ 1 hPa pour 27 pieds). Il ne le dit **que si la météo fournit le QNH**.
- **Initiale** (*point initial*, IP) : le point, dans l'axe de la piste, où l'avion arrive pour l'atterrissage, à haute
  vitesse et à faible hauteur. On le « passe » à la radio : « initiale, QFE 1005 ».
- **Break** : le grand virage serré que fait un avion de combat au-dessus de la piste pour se mettre en vent arrière,
  train sorti, avant la base et la finale. « Demande le break » : arriver en initiale puis casser au-dessus de la
  piste, au lieu d'un circuit classique. Plusieurs avions : « break mi-bande » ou « fin de bande » pour les espacer.
- **Dernier virage** : le virage vers la finale. On y annonce « **train sorti verrouillé** » (le train d'atterrissage
  est sorti et verrouillé). Sans cette annonce, le contrôleur répond « vérifiez train sorti verrouillé » et
  n'autorise pas l'atterrissage.
- **Piste en vue** : vous voyez la piste (fin d'une approche radar, voir plus bas).
- **Patrouille** : un groupe d'avions qui volent ensemble. **Trail** : en file, l'un derrière l'autre, à une distance
  donnée (« trail trois milles »).

## 3. Les approches radar : PAR et SRA

Quand la météo est mauvaise ou que le pilote le demande, le contrôleur peut **guider l'avion avec son radar**
jusqu'à la piste (« approche contrôlée depuis le sol »). Le pilote **ne répond pas** : il écoute et il corrige.

- **PAR** (*Precision Approach Radar*, aussi appelé **GCA**) : approche radar **de précision**. Le contrôleur voit
  l'avion **sur l'axe** (gauche/droite) **et sur la pente** (haut/bas, 3°). Il dit par exemple : « légèrement à gauche
  de l'axe, route 288, sous la pente, distance trois milles ». À 2 milles il demande de vérifier le train, et à
  0,9 mille il donne l'autorisation d'atterrir.
- **SRA** (*Surveillance Radar Approach*, côté contrôleur **SRE**) : approche radar **de surveillance**. Le radar ne
  voit que la position horizontale : le contrôleur donne la **route** et, à chaque mille, l'**altitude recommandée** :
  « sur l'axe, distance quatre milles, altitude recommandée mille neuf cents pieds ». Moins précis qu'un PAR : il
  s'arrête à 2 milles (« fin d'approche de surveillance, annoncez piste en vue ou remise de gaz »). Vous répondez
  « piste en vue » et il vous autorise à atterrir ; sinon, remise de gaz.

Dans StormATC : « demande PAR piste 29 » (ou « demande SRA piste 29 »). Le contrôleur calcule ses messages à partir de la
**position réelle de votre avion** dans MSFS par rapport à l'axe de la piste, toutes les 4 à 8 secondes. La hauteur
utilisée est la hauteur radio-sonde au-dessus du sol. Un « remise de gaz » met fin à l'approche.

## 4. MARSA

**MARSA** = *Military Assumes Responsibility for Separation of Aircraft* : les pilotes militaires **assument eux-mêmes
la séparation** entre les avions de leur patrouille (ou avec un autre avion désigné) : le contrôleur n'a plus à les
séparer, il traite la patrouille comme un seul avion. Il se demande au **départ** ou à l'**arrivée** :

- « Storm un deux trois, patrouille de deux, demande MARSA départ piste 29 » → « MARSA départ piste 29 approuvé,
  initial cap 290 ».
- « Storm un deux trois, patrouille de deux, demande MARSA approche, trail trois milles » → « MARSA approche piste 29
  approuvé ».

StormATC ne l'accorde qu'à une patrouille **d'au moins deux avions** et à un vol militaire. Le MARSA d'une arrivée prend
fin à l'atterrissage. Il n'y a pas de deuxième avion simulé : le MARSA ne change que ce que dit le contrôleur.

## 5. Un vol type à Saint-Dizier (LFSI)

1. Page **Plan de vol** : régime **CAM V** (ou dites « CAM Victor » à la première demande).
2. « Sol, Storm un deux trois, CAM Victor, parking, demandons le roulage » → « roulez piste 29, QFE 1002 ».
3. Décollage, circuit ou navigation.
4. Retour : « Tour, Storm un deux trois, vingt nautiques au sud, demande le break » → « piste 29, vent …, QFE 1002,
   rappelez initiale ».
5. « Storm un deux trois, initiale, QFE 1002 » → « correct, numéro un, rappelez dernier virage, train sorti verrouillé ».
6. « Storm un deux trois, dernier virage piste 29, train sorti verrouillé » → « piste 29, autorisé atterrissage ».

Variante par mauvais temps : à l'étape 4, « demande PAR piste 29 », puis laissez-vous guider.

## 6. Ce que StormATC fait, et ne fait pas

| Fait | Pas encore / pas du tout |
| --- | --- |
| Régimes CAM V, CAM I, CAM T (choix, annonce, changement) | Procédures radio **tactiques** (hors de l'Instruction 1950) |
| Situation tactique en CAM T : gisement, distance, altitude, aspect des contacts de MSFS | Identifier ami ou ennemi, autoriser un tir, annoncer « Fox » ou « splash » (tirs du Rafale non lus) |
| Roulage et arrivée : QFE, initiale, break, train sorti verrouillé | Séquencer **plusieurs** avions au break (pas de trafic militaire simulé) |
| PAR et SRA depuis la position réelle de l'avion | PAR/SRA d'un vol **CAM I** suivi dans la séquence IFR |
| MARSA au départ et à l'arrivée (patrouille d'au moins 2) | Le deuxième avion de la patrouille |
| Onglet **Militaire** dans le panneau MSFS : régime, base, fréquences, appels | Les consignes locales de chaque base (circuit, zones) |

**Les fréquences.** Les bases publient leurs fréquences en **UHF** (225 à 400 MHz) et en VHF, dont certaines au-dessus
de 137 MHz. Les radios de MSFS ne vont que de **118 à 137 MHz** : StormATC n'affiche et n'utilise que celles-là. Les
autres ne peuvent pas être accordées dans le simulateur.

**Les profils des bases.** Les données (position, altitude, pistes, fréquences) viennent des pages AIP du SIA
(voir le README : « Données et cartes aéronautiques »). Le circuit de Saint-Dizier (côté sud, main droite pour la 11)
est une **hypothèse** signalée dans son profil.

**Rien de militaire n'a été validé en vol dans MSFS** : essayez avec un Rafale et dites ce qui ne colle pas.
