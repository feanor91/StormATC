# Panneau StormATC dans MSFS (barre d'outils, réalité virtuelle)

StormATC ajoute une fenêtre à la barre d'outils de Microsoft Flight Simulator. Elle a trois onglets :

- **Dialogue ATC** : les échanges avec le contrôleur, du plus ancien au plus récent (vous en vert,
  le contrôleur en bleu). La liste suit les nouveaux messages tant que vous n'avez pas remonté.
- **Phraséologie** : des exemples d'appels pour la **phase de vol en cours** (clairance, roulage,
  circuit VFR, approche, etc.), avec ce que le contrôleur répond, puis les appels toujours possibles
  (« répétez », « quelle est la fréquence de la Tour ? »…). L'onglet change tout seul avec la phase.
- **Cartes** : les cartes (fichiers PDF) que vous avez déposées dans le dossier des cartes de StormATC,
  affichées dans le simulateur, en VR comme à l'écran. Voir [les cartes](#les-cartes-pdf).

En bas de l'onglet Dialogue ATC, la **voix du contrôleur** qui vient de parler est indiquée, avec le
bouton **Je n'aime pas cette voix**. Il écarte cette voix (ou ce locuteur d'un modèle multi-voix) :
elle est ajoutée à `PiperExcludedVoices`, comme avec le bouton du tableau de bord, et le contrôleur
en reçoit une autre à sa prochaine transmission. Le bouton reste grisé tant qu'une autre voix n'a pas
parlé, pour ne pas écarter deux fois la même. La voix choisie dans les paramètres ne peut pas être
écartée d'ici (le panneau le dit) : elle se change dans StormATC. Cela évite l'aller-retour entre le
simulateur et StormATC, surtout en VR. La barre n'apparaît que si le moteur de voix local (Piper)
est actif.

Le panneau lit aussi **le plan de vol de l'EFB** et le transmet à StormATC quand vous l'envoyez à
l'ATC ou à l'avionique : voir [vol VFR](vfr.md). C'est le moyen de secours du [module WASM](msfs-module.md),
qui fait la même chose sans panneau et avant le vol ; pour le panneau, il doit être ouvert (il peut être
réduit).

Dans l'onglet Phraséologie, des boutons permettent de lire **n'importe quelle phase** : **Auto** suit
le vol (la phase en cours porte un ●), un autre bouton fixe la phase affichée. Utile en VFR si la
session est restée en règles IFR, ou pour préparer la suite. La phase « automatique » vient de la
session ATC, corrigée par le mouvement réel de l'avion (vous roulez : Roulage ; vous êtes en
l'air : circuit VFR ou montée).

Le titre de la fenêtre est l'immatriculation de l'avion indiquée par le simulateur (à défaut,
l'indicatif de la session). Le libellé sous l'icône de la barre d'outils reste « StormATC ».

La langue des exemples est celle de la radio réglée dans StormATC. Les boutons **A−** et **A+** en
haut à droite changent la taille du texte ; le choix est mémorisé.

## Les cartes (PDF)

L'onglet **Cartes** montre vos cartes VAC (ou toute autre carte, au format PDF) sans quitter le
simulateur, ce qui est pratique en VR.

**Où les mettre.** Dans le dossier **`Cartes VAC`** de l'application, à côté de `StormATC.exe`
(`%LOCALAPPDATA%\Programs\StormATC\Cartes VAC` avec l'installeur). Il est créé par l'installeur et,
au besoin, par StormATC au démarrage ; le bouton **Ouvrir le dossier des cartes** des Réglages
l'ouvre. Seuls les PDF placés directement dans ce dossier sont montrés. Ils restent chez vous : StormATC
ne les envoie nulle part et l'installeur ne les contient pas (les cartes du SIA ne sont pas
redistribuables). Le dossier n'est jamais supprimé à la désinstallation.

Dans un dépôt cloné (développement), le dossier `Cartes VAC` à la racine du dépôt, à côté de
`StormATC.sln`, est utilisé quand celui de l'application est vide. Il est ignoré par git. Le réglage
`ToolbarPanel:ChartsDirectory` désigne un autre dossier.

**Avec un plan de vol**, l'onglet ouvre tout seul la carte de l'aérodrome d'**arrivée** (celle dont le
nom de fichier contient son code OACI), une seule fois par destination : **← Cartes** ramène à la
liste sans la rouvrir, et une nouvelle destination ouvre sa carte. Sans carte pour la destination, la
liste s'affiche.

**Les utiliser.** La liste montre les cartes par nom ; celles de l'aérodrome de **départ** et de
l'**arrivée** du plan de vol en cours passent en tête, avec une étiquette (le nom du fichier doit
contenir le code OACI, par exemple `AD-2.LFPL.pdf`). Touchez une carte pour l'ouvrir : **Précédente** et
**Suivante** changent de page, **Zoom +** et **Zoom −** agrandissent (la page est redessinée à la
bonne définition), **Ajuster** revient à la largeur de la fenêtre, **← Cartes** revient à la liste. On
déplace la page en la faisant glisser (ou avec les barres de défilement). Un fichier enregistré deux
fois (`carte (1).pdf`) n'est listé qu'une fois.

Les pages sont dessinées par StormATC avec le moteur PDF de Windows, puis envoyées au panneau comme des
images : le simulateur n'a donc rien à décoder. Un PDF protégé par mot de passe ou abîmé affiche « Cette
carte n'a pas pu être affichée ».

## Installer le panneau

Le plus simple : l'installeur de StormATC le fait (case « Installer le panneau dans la barre d'outils
de MSFS 2024 »), et la désinstallation le retire. Sinon :

1. Dans StormATC, **Settings › Voice & PTT › Panneau dans le simulateur › Installer le panneau dans
   MSFS**. StormATC copie le dossier `stormatc-ingamepanel` dans le dossier **Community** de MSFS 2024
   (Microsoft Store ou Steam, trouvé d'après `UserCfg.opt`).
2. Redémarrez MSFS.
3. Dans la barre d'outils du simulateur, cliquez sur l'icône **StormATC** (deux bulles). Si elle
   n'apparaît pas, ouvrez la liste des panneaux (l'engrenage de la barre d'outils) et activez-la.

Si le dossier Community n'est pas trouvé, copiez à la main le dossier
`msfs-panel\stormatc-ingamepanel` de l'installation de StormATC dans votre dossier Community.

StormATC doit être lancé : le panneau lui demande l'état une fois par seconde. Sinon il affiche
« StormATC ne répond pas ».

## Avec Flow Pro (sans compilation)

Si vous utilisez [Flow Pro](https://parallel42.com/products/flow-pro) (Parallel 42), le même panneau
existe en widget **Custom Script**, qui n'a rien à compiler :

1. Dans MSFS, ouvrez la molette Flow et ajoutez un widget **Custom Script**.
2. **Edit**, puis collez : `code.html` dans l'onglet HTML, `code.css` dans l'onglet CSS et `code.js`
   dans l'onglet JS. Les trois fichiers sont dans `msfs-panel\flowpro` (dans le dépôt et dans le
   dossier d'installation de StormATC).
3. Donnez-lui un nom (« StormATC »), **Save**, puis **Compile**.

C'est le plus simple à essayer. Comme pour le widget des cartes d'aéroport, ce qu'un widget Flow a le
droit de faire sur le réseau n'est pas documenté : si le widget affiche « StormATC ne répond pas »
alors que StormATC tourne, le widget n'a pas accès à `127.0.0.1` et il faut utiliser le paquet de la
barre d'outils ou la page web. Les fichiers sont générés par `scripts\Build-FlowProWidget.ps1` à
partir de `msfs-panel\web` ; ne les modifiez pas à la main.

## En réalité virtuelle

Le panneau est conçu pour la VR : gros caractères, boutons de la taille d'un doigt, aucun survol
nécessaire, fenêtre redimensionnable.

- **Barre d'outils de MSFS en VR** : ouvrez la barre d'outils avec le menu de la manette, puis
  l'icône StormATC. Déplacez et redimensionnez la fenêtre comme les autres panneaux. La barre d'outils
  en VR a eu des défauts selon les versions du simulateur ; si elle ne s'ouvre pas chez vous, utilisez
  la solution suivante.
- **Superposition VR** : la même page est servie par StormATC à l'adresse
  `http://127.0.0.1:48765/panel`. Les applications de superposition (OpenKneeboard, OVR Toolkit,
  XSOverlay, fpsVR…) savent afficher une page web dans le casque : ajoutez cette adresse. Le bouton
  **Ouvrir le panneau dans un navigateur** l'ouvre sur le PC pour vérifier.

## Réglages

Section `ToolbarPanel` de `appsettings.json` (ou `usersettings.json`) :

| Réglage | Défaut | Rôle |
| --- | --- | --- |
| `Enabled` | `true` | `false` désactive le serveur local, donc le panneau |
| `Port` | `48765` | Port sur 127.0.0.1. Le panneau installé dans MSFS utilise le port par défaut ; changez-le seulement pour la page web |
| `ChartsDirectory` | *(vide)* | Dossier des cartes PDF. Vide : `Cartes VAC` à côté de l'application |

Si le port est déjà pris par un autre programme, StormATC démarre quand même (le journal dit que le
panneau est indisponible).

## Comment ça marche

Le panneau est une page web dans le simulateur. Elle ne peut pas lire la session de StormATC
directement : StormATC héberge donc un petit serveur local (`ToolbarPanelServer`), qui n'écoute que
sur l'interface de boucle locale (127.0.0.1), ne répond qu'aux requêtes de lecture et refuse un autre
nom d'hôte. Les seules requêtes qui changent quelque chose sont `POST /api/voice/reject` (le bouton
ci-dessus) et `POST /api/flightplan` (le plan de vol lu par le panneau) : elle exige l'en-tête `X-StormATC` et est refusée si elle vient d'une page web d'un autre
site, pour qu'un site visité dans le navigateur ne puisse pas écarter vos voix.

| Adresse | Contenu |
| --- | --- |
| `/api/state` | L'état en JSON : vol, contrôleur, fréquence, phase, derniers échanges (80 au plus), exemples de la phase |
| `/panel` | La page, pour un navigateur ou une superposition VR |
| `POST /api/voice/reject` | Écarte la voix du contrôleur qui vient de parler |
| `POST /api/flightplan` | Reçoit le plan de vol lu dans le simulateur (`{source, initial, route}`) |

Les exemples viennent de `PhraseologyCatalog` (français et anglais, pour chaque phase et pour le
vol VFR) ; ce sont des exemples de formation, le moteur décide seul de ce qui est autorisé.

## Compiler le paquet

```powershell
.\scripts\Build-MsfsPanel.ps1
```

Le script écrit `artifacts\msfs-panel\stormatc-ingamepanel` (page, script, icône, `layout.json`,
`manifest.json`). La publication de l'application (`Publish-StormATC.ps1`, la CI) le lance avant de
construire, et l'installeur l'embarque.

La seule partie compilée est la définition du panneau (`InGamePanel_StormATC.spb`), faite à partir de
`msfs-panel\package\PackageSources\InGamePanels\*.xml` par le compilateur du simulateur. Elle est
versionnée dans `msfs-panel\prebuilt`, pour construire sans le SDK ni le simulateur. Il ne faut la
refaire que si l'on modifie ce XML (nom, taille, icône du panneau). Dans MSFS (mode développeur) :
**File › Open project**, choisir `msfs-panel\package\stormatc-ingamepanel.xml` copié dans un dossier
de travail, **Build All in project**, puis recopier `InGamePanels\InGamePanel_StormATC.spb` produit
dans `msfs-panel\prebuilt`. Ne pas enregistrer le projet depuis l'éditeur : l'enregistrement a fait
planter le simulateur lors de nos essais (le paquet porte déjà son `PackageOrderHint`, donc MSFS
n'a rien à enregistrer). L'outil en ligne de commande `fspackagetool` n'a rien produit chez nous ;
`Build-MsfsPanel.ps1 -Recompile` l'essaie, MSFS lancé, mais ne marche pas toujours.

La page et le style du panneau sont dans `msfs-panel\web` ; ce sont les mêmes fichiers pour la
fenêtre de MSFS et pour la page web.

## Limites

- Le panneau affiche et ne pilote pas StormATC, à une exception : le bouton « Je n'aime pas cette
  voix ». Le push-to-talk reste celui de StormATC.
- Pas de choix manuel de la phase affichée dans l'onglet Phraséologie : elle suit le vol.
- Le serveur écoute en HTTP sur la boucle locale ; une autre page web ouverte sur le même PC peut lire
  l'état (dialogue radio, rien de secret). Les requêtes qui ne viennent pas de `127.0.0.1` ou
  `localhost` sont refusées.
