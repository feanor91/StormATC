# Logiciels tiers et voix : où les télécharger

StormATC ne les embarque pas. Whisper (reconnaissance vocale) et Piper (voix du contrôleur) sont
des programmes gratuits à télécharger une fois, hors de StormATC. Ils sont **facultatifs** : sans
eux, l'application fonctionne avec Windows (voix système) ou avec un service en ligne (Deepgram,
OpenRouter), voir [privacy-and-api-keys.md](privacy-and-api-keys.md).

Le réglage dans l'application et le détail des options sont dans
[french-and-local-voice.md](french-and-local-voice.md).

## Ce qu'il faut, en bref

| Élément | Rôle | Taille | Téléchargement |
| --- | --- | --- | --- |
| whisper.cpp (`whisper-cli.exe`) | Reconnaissance vocale | ~10 à 20 Mo | <https://github.com/ggml-org/whisper.cpp/releases> |
| Modèle Whisper `ggml-small-q5_1.bin` | Reconnaissance, rapide, processeur seul | ~180 Mo | <https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin> |
| Modèle Whisper `ggml-large-v3-turbo-q5_0.bin` | Reconnaissance, plus précise | ~550 Mo | <https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin> |
| Piper (`piper.exe`) | Synthèse vocale | ~20 Mo | <https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip> |
| Voix Piper (`.onnx` + `.onnx.json`) | Voix des contrôleurs | 60 à 75 Mo chacune | voir ci-dessous |

Prenez un modèle Whisper **multilingue** (le nom ne se termine pas par `.en.bin`) : un modèle
anglais seul ne comprend pas le français.

Pour whisper.cpp, choisissez dans la dernière version :

- `whisper-bin-x64.zip` pour le processeur ;
- `whisper-cublas-…-bin-x64.zip` avec une carte NVIDIA (beaucoup plus rapide).

Une voix Piper, c'est **deux fichiers** à garder dans le même dossier : `<nom>.onnx` et
`<nom>.onnx.json`.

## Où mettre les fichiers

Le dossier est libre ; les chemins se règlent dans **Settings › Voice & PTT**. Une organisation
simple :

```
C:\StormATC\whisper\bin\Release\whisper-cli.exe
C:\StormATC\whisper\models\ggml-small-q5_1.bin
C:\StormATC\piper\piper.exe          (avec le dossier espeak-ng-data à côté)
C:\StormATC\voices\*.onnx + *.onnx.json
```

Ne décompressez pas dans le dossier d'installation de StormATC : une mise à jour pourrait
l'écraser.

## Voix Piper

Toutes les voix sont dans <https://huggingface.co/rhasspy/piper-voices/tree/main>. L'adresse d'un
fichier suit toujours le modèle :

```
https://huggingface.co/rhasspy/piper-voices/resolve/main/<langue>/<langue_PAYS>/<voix>/<qualité>/<langue_PAYS>-<voix>-<qualité>.onnx
```

et le même avec `.onnx.json` à la fin. Par exemple
`https://huggingface.co/rhasspy/piper-voices/resolve/main/fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx`.

### Français

| Voix | Qualité | Remarque | Dossier sur Hugging Face |
| --- | --- | --- | --- |
| `fr_FR-siwis-medium` | medium | féminine | `fr/fr_FR/siwis/medium/` |
| `fr_FR-tom-medium` | medium | masculine | `fr/fr_FR/tom/medium/` |
| `fr_FR-upmc-medium` | medium | modèle à plusieurs locuteurs | `fr/fr_FR/upmc/medium/` |
| `fr_FR-mls-medium` | medium | 125 locuteurs, certains mauvais (voir l'exclusion) | `fr/fr_FR/mls/medium/` |
| `fr_FR-gilles-low` | low | écartée par défaut (hésite, saute des mots) | `fr/fr_FR/gilles/low/` |
| `fr_FR-mls_1840-low` | low | écartée par défaut | `fr/fr_FR/mls_1840/low/` |

### Anglais

| Voix | Dossier sur Hugging Face |
| --- | --- |
| `en_GB-alan-medium` | `en/en_GB/alan/medium/` |
| `en_GB-alba-medium` | `en/en_GB/alba/medium/` |
| `en_GB-cori-medium` | `en/en_GB/cori/medium/` |
| `en_GB-jenny_dioco-medium` | `en/en_GB/jenny_dioco/medium/` |
| `en_GB-northern_english_male-medium` | `en/en_GB/northern_english_male/medium/` |
| `en_GB-vctk-medium` (109 locuteurs) | `en/en_GB/vctk/medium/` |
| `en_US-amy-medium` | `en/en_US/amy/medium/` |
| `en_US-hfc_female-medium` | `en/en_US/hfc_female/medium/` |
| `en_US-hfc_male-medium` | `en/en_US/hfc_male/medium/` |
| `en_US-ryan-medium` | `en/en_US/ryan/medium/` |
| `en_US-libritts_r-medium` (904 locuteurs) | `en/en_US/libritts_r/medium/` |

Pour avoir **plusieurs voix de contrôleurs**, mettez-en plusieurs dans le même dossier : voir
« Plusieurs voix de contrôleurs » dans [french-and-local-voice.md](french-and-local-voice.md).

## Tout télécharger d'un coup

Ce script PowerShell télécharge Piper, un modèle Whisper et un jeu de voix dans `C:\StormATC`. Il
ne remplace pas un fichier déjà présent. Lancez-le tel quel, puis ajoutez `whisper-cli.exe` (voir
le tableau), dont le nom de fichier change à chaque version.

```powershell
$root = 'C:\StormATC'
New-Item -ItemType Directory -Force "$root\voices", "$root\whisper\models" | Out-Null

function Get-File($url, $dest) {
    if (Test-Path $dest) { return }
    Write-Host "Téléchargement $url"
    Invoke-WebRequest $url -OutFile $dest
}

# Piper
Get-File 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip' "$root\piper.zip"
if (-not (Test-Path "$root\piper\piper.exe")) { Expand-Archive "$root\piper.zip" $root -Force }

# Modèle Whisper
Get-File 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin' "$root\whisper\models\ggml-small-q5_1.bin"

# Voix (dossier Hugging Face, nom de la voix)
$base = 'https://huggingface.co/rhasspy/piper-voices/resolve/main'
$voices = @(
    @('fr/fr_FR/siwis/medium', 'fr_FR-siwis-medium'),
    @('fr/fr_FR/tom/medium',   'fr_FR-tom-medium'),
    @('en/en_GB/alan/medium',  'en_GB-alan-medium'),
    @('en/en_US/ryan/medium',  'en_US-ryan-medium')
)
foreach ($v in $voices) {
    foreach ($ext in '.onnx', '.onnx.json') {
        Get-File "$base/$($v[0])/$($v[1])$ext" "$root\voices\$($v[1])$ext"
    }
}
```

## Pour les contributeurs : version PDF

L'application embarque ce guide en PDF (`docs/guides/*.pdf`) et l'ouvre depuis **Settings › Voice &
PTT › Guide de téléchargement**. Après avoir modifié un guide Markdown, régénérez les PDF avec
`.\scripts\Build-Guides.ps1` (il faut Microsoft Edge) et commitez-les.

## Licences

Chaque programme et chaque voix a sa propre licence ; StormATC n'en redistribue aucun.

- **whisper.cpp** et les modèles Whisper : MIT.
- **Piper** : MIT.
- **Voix Piper** : la licence dépend du jeu de données d'origine de chaque voix. Elle est écrite
  dans le fichier `MODEL_CARD` qui accompagne la voix sur Hugging Face. Lisez-la avant de
  redistribuer une voix ou de l'inclure dans un produit.
- **SimConnect** (SDK de MSFS) : voir [simconnect-setup.md](simconnect-setup.md). Il n'est pas
  dans le dépôt ; l'installeur le copie depuis votre propre SDK.
