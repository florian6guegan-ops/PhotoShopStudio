# PhotoShopStudio

Logiciel de borne photo pour laboratoire photo / magasin : import des photos du client,
recadrage au format du produit, photos d'identité, panier, impression et suivi des commandes.

Écrit en C# / .NET 8 (WPF, Windows) pour remplacer une borne DiLand Studio en production.

## État du projet

Fonctionnel et utilisé en boutique, mais jeune : il a été développé pour un poste précis
(imprimante DS620, minilab DE100) et n'a pas encore été éprouvé sur d'autres configurations.
Les rapports de bugs et les retours d'autres installations sont les bienvenus.

## Fonctionnalités

- Import depuis carte SD, clé USB, téléphone, ou upload depuis le mobile du client (QR code)
- Recadrage au ratio du produit : glisser, zoomer (molette / pincement), rotation, Remplir ou Entier
- Photos d'identité : détection du visage, pré-cadrage 35×45 conforme, gabarit de contrôle, planche à imprimer
- Catalogue de produits et de planches configurable
- Panier, impression, ticket de caisse (ESC/POS)
- Stockage local des commandes et sauvegarde

## Compiler

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download) sur Windows.

```
git clone https://github.com/florian6guegan-ops/PhotoShopStudio.git
cd PhotoShopStudio
dotnet build
dotnet test
dotnet run --project src/Studio.App
```

## Architecture

| Projet | Rôle |
| --- | --- |
| `Studio.App` | Interface WPF (vues, navigation) |
| `Studio.Core` | Domaine : produits, commandes, panier |
| `Studio.Imaging` | Recadrage, géométrie, rendu des images |
| `Studio.Printing` | Impression et tickets ESC/POS |
| `Studio.Sources` | Sources de photos (SD, USB, téléphone) |
| `Studio.Store` | Persistance locale et sauvegarde |
| `Studio.Web` | Upload depuis mobile, dialogue avec le minilab |

## Licence

[MIT](LICENSE) — libre d'utilisation, de modification et de redistribution, y compris
commercialement, à condition de conserver la mention de copyright.

## Crédits

La détection de visage utilise le modèle [YuNet](https://github.com/opencv/opencv_zoo)
(`models/face_detection_yunet_2023mar.onnx`), via OpenCV Zoo.
