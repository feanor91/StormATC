# Le module WASM de StormATC (lecture du plan de vol de l'EFB)

StormATC installe dans MSFS un petit module WASM, `stormatc_route.wasm`, qui lit le plan de vol de
l'EFB et le donne à StormATC **en direct**, avant le vol comme pendant le vol. C'est le moyen
principal de recevoir un plan : voir [vol VFR](vfr.md).

## Ce qu'il fait

Le module est chargé avec le simulateur (il n'a pas besoin du panneau ni d'un vol en cours). Il
écoute l'API officielle « Planned Route » et publie le plan **par deux chemins**, pour qu'un chemin qui
échoue ne perde pas le plan : une zone de données SimConnect (`StormATC.Route`) que StormATC lit, et une
requête HTTP vers le serveur local de StormATC (`http://127.0.0.1:48765/api/flightplan`, le même que celui
du panneau) par l'API réseau du simulateur. StormATC applique un plan une seule fois : la seconde copie
d'un plan déjà chargé ne change rien. Les routes publiées :

| Source | Quand |
| --- | --- |
| `atc` | Le simulateur annonce qu'un plan a été activé : **Envoyer à l'ATC** ou **Envoyer à l'ATC et à l'avionique**. Le plan envoyé est celui de l'EFB. |
| `avionics` | Le plan est poussé de l'EFB vers l'avionique. |
| `efb` | Le plan de l'EFB change (modification en cours). Mémorisé par StormATC, pas appliqué tout seul. |

StormATC applique `atc` et `avionics` : terrain de départ et d'arrivée, VFR ou IFR, pistes, altitude de
croisière, points de la route. Le module ne fait que lire : il ne modifie ni le plan, ni l'avion, ni
le simulateur.

## Installation

L'installeur de StormATC le place avec le panneau dans le dossier **Community** de MSFS
(`stormatc-ingamepanel\modules\stormatc_route.wasm`). Le bouton « Installer le panneau dans MSFS »
des paramètres le copie aussi. **Redémarrez MSFS** après la première installation : un module se
charge au démarrage du simulateur.

Le journal de StormATC (`%LOCALAPPDATA%\StormATC\logs`) montre ce qui arrive : au démarrage du
simulateur, « The StormATC module is running in the simulator » (le module est chargé), puis, à chaque
envoi, « StormATC received the atc route … from the simulator ». Si rien n'arrive après un
« Envoyer à l'ATC », le module n'est pas chargé : le panneau de la barre d'outils reste alors le moyen
de lecture en vol, et un fichier `.PLN` le moyen de secours.

## Compiler

Le module est du C++ compilé en WebAssembly avec les outils du SDK MSFS (`clang-cl` et `wasm-ld`,
sans Visual Studio) :

```powershell
.\scripts\Build-MsfsModule.ps1   # msfs-wasm\src\module.cpp -> msfs-wasm\prebuilt\stormatc_route.wasm
.\scripts\Test-MsfsModule.ps1    # tests du module, sans le simulateur
.\scripts\Build-MsfsPanel.ps1    # assemble le paquet, avec le module
```

Le `.wasm` est versionné dans `msfs-wasm\prebuilt` : le paquet et l'installeur se construisent sans le
SDK. Les tests ont deux niveaux :

- `route_json_test.cpp` compile le convertisseur plan → JSON (`route_json.h`) avec le compilateur du
  PC et le vérifie : pistes, altitude, points, échappement, route trop longue, tampon trop petit ;
- `module_host_test.js` charge le module **compilé** dans un faux simulateur (Node.js) : il vérifie les
  appels SimConnect, la lecture de la structure `FsPlannedRoute` (disposition mémoire wasm32), la
  publication de chaque source, l'absence de doublons et le comportement avec un plan vide.

## Ce qui n'est pas garanti

Le module n'a été testé que contre ce faux simulateur. Restent à voir dans MSFS : qu'il se charge dans
les menus, que l'API renvoie bien le plan au moment de « Envoyer à l'ATC », et que les identifiants de
points de route et les altitudes y sont renseignés (des développeurs signalent qu'ils manquent parfois
en 2024). Les identifiants manquants n'empêchent pas la reprise : seuls le terrain de départ et
d'arrivée sont indispensables.
