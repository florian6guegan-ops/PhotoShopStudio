# Plan d'implémentation — le montage des agrandissements

Demande de l'exploitant, 12/08/2026. Le plan de la 7ᵉ passe est archivé dans `task.md`.

Point de départ : `main`, **1505/1508 essais verts** (les 3 restants sont
`De100BridgeIntegrationTests`, qui démarrent leur propre relais sur un tube à instance unique
déjà tenu par l'application en service — défaut d'environnement, pas de code). Version
publiée : 1.4.6, les trois boutiques y sont ou peuvent y aller.

---

## Ce qui est demandé

> « si j'ai 2 24×30, qu'il fasse le montage directement en fonction du papier choisi si ça
> rentre dans la taille de papier, sinon il les met une par une »

Aujourd'hui, un agrandissement rend **un fichier par tirage**. Deux 24×30 donnent deux
fichiers, que l'opérateur imprime l'un après l'autre — deux feuilles de 40×60, dont la
moitié part à la chute à chaque fois. Or deux 24×30 tiennent exactement dans un 40×60
(240 × 600 dans 400 × 600).

**Trois décisions ont été prises par l'exploitant** avant d'écrire une ligne :

| | Décision |
|---|---|
| Circuit | **Grand format (Epson) UNIQUEMENT.** Pas le minilab : son papier le plus large fait 210 mm, deux 24×30 n'y tiendront jamais. Pas la DNP : son cas 15×20 → deux 10×15 est déjà traité depuis la 1.3.15 |
| Prix | **Inchangé — N × le prix du format.** Le montage est une économie de papier pour la boutique, invisible du client. Deux 24×30 restent deux 24×30 au ticket |
| Papier | **L'opérateur le choisit.** Il sait quel rouleau est chargé ; on ne le devine pas |

⚠ **La décision de prix est la plus importante du lot, et elle diverge de l'existant.** La
tuile « Personnalisé » de l'impression rapide facture LE PAPIER (`CustomSheetLayout.Choose`
départage sur le prix, règle posée le 02/08/2026). Ici, non : les lignes de commande restent
ce qu'elles sont, seul le RENDU change. Il ne faut donc surtout pas réutiliser `Choose` pour
décider quoi que ce soit — uniquement `Capacity` et `Distribute`.

---

## Ce qui existe déjà, et qu'il ne faut pas réécrire

Toute la géométrie est faite, éprouvée et couverte d'essais :

- `CustomSheetLayout.CapacityDetaillee(papier, cellW, cellH, gap)` → combien de cases par
  planche, avec rotation de la case ET de la planche ;
- `CustomSheetLayout.Distribute(quantités, parPlanche)` → la répartition sur N planches,
  en gardant les exemplaires d'une même photo groupés ;
- `ImagePipeline.RenderCustomSheetToFile(...)` → le rendu d'une planche de photos
  DIFFÉRENTES à la même taille, avec traits de coupe.

Elles ne servent aujourd'hui qu'à `CustomSizeView` (impression rapide → Personnalisé).

---

## 1. Le choix du papier, dans le parcours agrandissement

`PrintFormatView` mène aujourd'hui de la tuile de format directement au choix des photos.
On intercale, **pour la famille `Enlargement` seulement**, le choix de la feuille.

- [ ] 1.1 `FeuilleGrandFormat` : les produits `ManualFile` du catalogue, comme candidats
      de feuille — 30×40, 40×50, 40×60, 50×70, 60×90, 70×100…
- [ ] 1.2 Un écran (ou un volet de `PrintFormatView`) qui, pour le format retenu, annonce
      **par feuille candidate** : combien de tirages y tiennent, et combien de feuilles il
      faudra. C'est ce qui permet d'annoncer la consommation avant d'engager quoi que ce soit
- [ ] 1.3 ⚠ **« Une par feuille » reste un choix explicite**, en tête de liste. C'est le
      comportement actuel, et il doit rester atteignable en un geste : un opérateur qui
      découpe lui-même ne veut pas d'un montage
- [ ] 1.4 Le choix est retenu sur l'enveloppe, pas sur le produit : deux commandes du même
      format peuvent partir sur des feuilles différentes

## 2. Le rendu composé

- [ ] 2.1 `PrintOrchestrator.RenderEnvelope` : sur le circuit `ManualFile`, si une feuille
      est retenue et que la capacité est ≥ 2, composer au lieu de rendre une par une
- [ ] 2.2 `Distribute` répartit ; `RenderCustomSheetToFile` rend chaque planche
- [ ] 2.3 ⚠ **Capacité 1 = comportement d'avant, à l'octet près.** Un montage à une case par
      feuille n'est pas un montage : c'est un tirage avec des traits de coupe en plus, et une
      régression silencieuse pour tous les postes qui ne demandent rien
- [ ] 2.4 Les traits de coupe : indispensables ici, l'opérateur massicote la feuille

## 3. Le prix ne bouge pas

- [ ] 3.1 ⚠ Les lignes de l'enveloppe restent inchangées : `TotalPrints`, `Total`, le
      ticket, les statistiques. **Seul le nombre de FICHIERS rendus change**
- [ ] 3.2 Un essai qui le prouve : deux 24×30 montés sur une feuille coûtent exactement le
      même prix que deux 24×30 rendus séparément

## 4. Ce que l'écran grand format doit comprendre

- [ ] 4.1 `LargeFormatPrintView` reçoit une planche déjà composée : elle a la taille de la
      feuille, elle doit sortir à 100 %, sans « ajuster au support »
- [ ] 4.2 Vérifier que la densité posée par `ImagePipeline` survit au chemin (voir la note
      « Résolution absente = 300 ppp » : un fichier sans densité part trois fois trop grand)

## 5. Essais

- [x] 5.1 Capacité : 2 × 24×30 sur 40×60 → 2 ; sur 30×40 → 1 ; sur 50×70 → **4**.

      ⚠ **Deux erreurs de calcul à la main dans ce plan, corrigées par les essais :**

      - le 50×70 en porte **quatre**, pas deux : deux colonnes de 240 mm font 482 avec
        l'écart, et 482 rentre dans 500 ;
      - sur le 40×60, c'est la **FEUILLE** qui se couche, pas la case. `Capacity` essaie
        les deux sens de la planche, ce que le calcul à la main avait oublié. Couchée, la
        feuille offre 600 mm où deux tirages de 240 se posent DEBOUT, côte à côte : le
        cadrage portrait est gardé tel quel et le fichier sort en 60 × 40.

      La rotation de l'image à la pose reste nécessaire — mais pour les sélections
      **mêlant portraits et paysages**, pas pour le cas de base.
- [ ] 5.2 `Distribute` sur des quantités mêlées (3 photos × 2 exemplaires sur 2 par feuille)
- [ ] 5.3 ⚠ Le prix, avec et sans montage — l'essai qui garde la caisse honnête
- [ ] 5.4 Capacité 1 → aucun changement de comportement
- [ ] 5.5 Le catalogue réel des trois boutiques ne change de comportement nulle part tant
      qu'aucune feuille n'est choisie

---

## Ce que je ne fais PAS dans cette passe

- **Le minilab et la DNP** : hors périmètre, décidé.
- **Toucher au « Personnalisé » de l'impression rapide** : sa règle de prix est différente et
  assumée. Les deux mécaniques partagent la géométrie, pas la politique.
- **Choisir le papier automatiquement** : l'exploitant a tranché, c'est lui qui choisit.

## Le risque principal

Le circuit `ManualFile` est celui des grands tirages, ceux qui coûtent cher en papier et en
temps. Une régression y est chère. D'où le point 2.3 : **tant qu'aucune feuille n'est
retenue, pas une ligne de comportement ne change**, et c'est ce que vérifie l'essai 5.5.
