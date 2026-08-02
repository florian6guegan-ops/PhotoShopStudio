# Copie de sauvegarde du catalogue de la boutique

**Ce dossier n'est PAS lu par l'application.** Le catalogue vivant est
`D:\PhotoStudioData\catalog\` — hors du dépôt, donc hors de git : modifier un produit dans
l'écran Catalogue ne se voit pas dans `git status`, et le seul filet est `products.json.bak`,
qui ne garde que la version précédente.

Ce dossier est le filet manquant. Il en garde une copie, versionnée, avec tout l'historique
de git derrière.

## Rafraîchir la copie

```
tools\Sauver-Catalogue.cmd
```

À lancer **après chaque changement du catalogue** — un prix, un format, une capture de
réglages pilote — puis à committer. Sans cela, la copie vieillit en silence et donne une
fausse impression de sécurité : c'est le seul vrai risque de ce dossier.

## Restaurer

Recopier `products.json` et les `devmode-*.bin` dans `D:\PhotoStudioData\catalog\`,
application fermée.

## Ce qui est sauvegardé

| Fichier | Pourquoi |
|---|---|
| `products.json` | le catalogue : formats, prix, paliers, sorties, planches |
| `devmode-*.bin` | les réglages du pilote capturés au dialogue — irremplaçables sans l'imprimante sous la main |

## Ce qui est délibérément EXCLU

**Le dépôt est public.** Ce qui est poussé ici est lisible par n'importe qui.

- **`config\wifi.json`** — il porte le nom du réseau **et son mot de passe**. Il ne doit
  jamais entrer dans git. C'est la seule vraie donnée sensible du dossier de données.
- **`icc\*.icc`** — profils couleur livrés par les pilotes (1,6 Mo pour celui de la DS620).
  Ce sont des fichiers du fabricant, et ils se réimportent en deux clics depuis
  Catalogue → Importer, qui va les chercher dans le dossier couleur de Windows.
- **`orders\`, `logs\`, `archive\`** — les commandes portent des noms de clients et les
  photos elles-mêmes. Ils grossissent tous les jours et n'ont rien à faire dans un dépôt.
- **`products.avant-*.json`** — d'anciennes copies horodatées du même fichier. Git fait
  déjà ce travail, et mieux.

Les prix ne sont pas un secret : ils sont affichés au comptoir, et le dépôt contient déjà
`catalog/products.diland.json` et `catalog/diland-prices.json`, qui les portent.
