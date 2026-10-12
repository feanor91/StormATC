# Radio en français et voix locale (Whisper + Piper)

StormATC peut parler et comprendre la radio **en français**, selon la phraséologie DSNA, et
faire tourner la reconnaissance vocale et la voix du contrôleur **sur votre PC**, sans compte
ni connexion réseau.

## Interface en français

Réglage : **Settings › ATC behaviour › Languages › Interface** → `Français`, puis **Save
settings** et redémarrez StormATC. Par défaut (`Windows language`), l'interface suit la langue
d'affichage de Windows.

La langue de l'interface et la langue radio sont indépendantes : on peut avoir l'interface en
français et la radio en anglais, ou l'inverse.

Les traductions sont dans `src/StormATC.Application/Localization/fr.json`, indexées par le
texte anglais. Un texte absent du fichier reste simplement en anglais, et le test
`InterfaceTranslationTests` liste tout texte d'interface qui n'a pas encore de traduction (utile
après une mise à jour depuis le projet d'origine).

## Ce qui change avec la langue radio

Réglage : **Settings › ATC behaviour › Languages › Radio** → `Français (DSNA)`, puis **Save
settings**.
Le changement s'applique à la session en cours.

| Élément | Français |
| --- | --- |
| Contrôleurs | Prévol, Sol, Tour, Départ, Approche, Contrôle |
| Nombres | Cardinaux usuels : « trois mille cinq cents pieds », « niveau cent quatre-vingts », « QNH mille treize » |
| Transpondeur | Par paires : « transpondeur trente-deux cinquante-quatre » |
| Fréquences | « cent dix-huit décimale sept » |
| Pistes | « piste zéro cinq », « piste vingt-sept gauche » |
| ATIS | « Ici …, information Alfa, enregistrée à quatorze heures trente UTC, piste en service … » |
| Reconnaissance | Whisper ou Deepgram en `fr` |
| Voix | Voix française Piper, ou `aura-2-hector-fr` chez Deepgram |

Les décisions ne changent pas : clairances, valeurs et contrôle du collationnement sont pris
par le même moteur déterministe dans les deux langues. Seuls les mots changent.

### Ce que vous pouvez dire

Le pilote parle naturellement ; les nombres peuvent être dits en cardinaux ou chiffre par
chiffre (« piste dix-huit » comme « piste un huit »).

| Situation | Exemple |
| --- | --- |
| Clairance | « Lyon Prévol, bonjour, Air France un deux trois, demandons la clairance IFR » |
| Prêt à copier | « Air France 123, prêts à copier » |
| Collationnement | « Autorisés Lyon, départ OKIPA un Alpha, piste vingt-sept gauche, montée initiale cinq mille pieds, transpondeur quarante-cinq vingt et un, Air France 123 » |
| Repoussage | « Air France 123, demandons repoussage et mise en route » |
| Roulage | « Air France 123, demandons le roulage » |
| Collationnement roulage | « Roulons point d'attente piste vingt-sept gauche via Alpha, Charlie, maintenons avant piste vingt-sept gauche » |
| Point d'attente | « Air France 123, au point d'attente piste vingt-sept gauche » |
| Départ | « Air France 123, au point d'attente piste vingt-sept gauche, prêts au départ » |
| Décollage | « Piste vingt-sept gauche, autorisés décollage, Air France 123 » |
| Contact départ | « Paris Départ, bonjour, Air France 123, passant deux mille pieds en montée vers cinq mille » |
| Montée / descente | « Montons niveau cent vingt », « Descendons altitude trois mille pieds, QNH mille treize » |
| Cap | « Tournons à gauche cap deux cent soixante-dix » |
| Changement de fréquence | « Contactons Tour cent dix-huit décimale sept » |
| Demandes | « demandons direct NOVAR », « demandons à monter niveau 240 », « demandons l'ILS » |
| Approche / atterrissage | « établis ILS piste 36 », « autorisés atterrissage piste 36 », « piste dégagée » |
| Divers | « répétez », « quelle est la fréquence de la tour ? », « remise de gaz », « mayday » |

## Voix locale

Deux programmes gratuits, à télécharger une fois.

### Whisper (reconnaissance vocale)

1. Téléchargez la dernière version Windows de **whisper.cpp** :
   <https://github.com/ggml-org/whisper.cpp/releases>
   - `whisper-bin-x64.zip` pour le processeur ;
   - `whisper-cublas-…-bin-x64.zip` si vous avez une carte NVIDIA (beaucoup plus rapide).
2. Décompressez, par exemple dans `C:\StormATC\whisper\`. L'exécutable est `whisper-cli.exe`.
3. Téléchargez un modèle **multilingue** (pas un `*.en.bin`, qui ne comprend que l'anglais) :
   <https://huggingface.co/ggerganov/whisper.cpp/tree/main>
   - `ggml-large-v3-turbo-q5_0.bin` : le plus précis, recommandé avec une carte NVIDIA ;
   - `ggml-small.bin` : plus léger, pour le processeur seul.

### Piper (voix du contrôleur)

1. Téléchargez `piper_windows_amd64.zip` :
   <https://github.com/rhasspy/piper/releases> (version 2023.11.14-2).
   Décompressez, par exemple dans `C:\StormATC\piper\`. Gardez le dossier `espeak-ng-data` à côté
   de `piper.exe`.
2. Téléchargez une voix française et, si vous volez aussi en anglais, une voix anglaise :
   <https://huggingface.co/rhasspy/piper-voices/tree/main>
   - `fr/fr_FR/tom/medium/fr_FR-tom-medium.onnx` (voix masculine) ou
     `fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx` (voix féminine) ;
   - `en/en_GB/alan/medium/en_GB-alan-medium.onnx` pour l'anglais.

   Téléchargez **les deux fichiers** de chaque voix : le `.onnx` et le `.onnx.json`, dans le même
   dossier.

### Réglages

**Settings › Voice & PTT › Speech engines** :

- **Speech to text** : `Whisper` ;
- **Controller voice** : `Piper` ;
- les chemins de `whisper-cli.exe`, du modèle, de `piper.exe` et des voix (« Copier en tant que
  chemin » dans l'Explorateur fonctionne : les guillemets sont retirés) ;
- **Save settings**, puis **Test speech engines**.

Le choix s'applique sans redémarrage. Les deux moteurs fonctionnent aussi en mode hors ligne.

Réglages avancés dans `appsettings.json`, section `LocalVoice` : `WhisperThreads`,
`WhisperTimeout`, `PiperLengthScale` (vitesse d'élocution, 0,9 par défaut), `PiperTimeout`.

## Confidentialité

L'audio du micro n'est enregistré que pendant l'alternat. À la fin de la transmission, il est
écrit dans un fichier WAV temporaire, transcrit par Whisper puis supprimé. Rien ne quitte le PC.

## Limites connues

- Whisper transcrit la transmission entière au relâchement de l'alternat : pas de texte
  provisoire pendant que vous parlez. Le modèle est rechargé à chaque transmission ; avec
  `large-v3-turbo` sur processeur seul, comptez quelques secondes.
- En français, la reformulation par modèle de langage est désactivée : les réponses utilisent
  la phraséologie déterministe (le validateur de phraséologie est encore en anglais).
- Avec Deepgram en français, aucun mot-clé de reconnaissance n'est envoyé (les mots-clés sont
  anglais).
- Le trafic d'ambiance (chatter) reste en anglais.

## Compiler et tester

```powershell
dotnet build StormATC.sln -c Release
dotnet test StormATC.sln -c Release
```

Les tests de la radio française sont dans `FrenchPhraseologyTests` (Domain),
`FrenchRadioTests` (Application) et `LocalVoiceTests` (Infrastructure) ; ceux de l'interface
dans `InterfaceTranslationTests` (Application).

Le premier `dotnet restore` met à jour les fichiers `packages.lock.json` si besoin ; aucun
nouveau paquet NuGet n'est requis.
