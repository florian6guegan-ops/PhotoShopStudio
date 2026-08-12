# Exécution — 12/08/2026, soir : le montage des agrandissements

Suite de `implementation_plan.md`, validé par l'exploitant. Circuit grand format (Epson)
uniquement, prix inchangé, papier choisi par l'opérateur.

---

## A. Ce que la lecture du code a imposé, et que le plan ne disait pas ⚠

- [x] A1 ⛔ **FAUX, corrigé par les essais.** J'avais calculé à la main qu'il fallait coucher
      la CASE. C'est la **FEUILLE** qui se couche : `Capacity` essaie aussi les deux sens de
      la planche, ce que le calcul oubliait. Feuille couchée, ses 600 mm de large portent
      deux tirages DEBOUT côte à côte — le cadrage portrait est gardé tel quel, et le fichier
      sort en 60 × 40. Même erreur sur le 50×70, qui en porte **quatre** et non deux :
      480 mm + 2 d'écart rentrent dans 500.

      À retenir : ne pas refaire à la main ce que `CustomSheetLayout` fait déjà. Les deux
      erreurs venaient de là, et les essais les ont attrapées toutes les deux.
- [x] A2 **Le reste du raisonnement tient, et sert quand même.** Le circuit
      des agrandissements oriente sa toile PAR PHOTO (`CropMath.OrientCanvas`) : un portrait
      sort en 24×30, un paysage en 30×24. Rendre un portrait dans une case couchée le
      recadrerait dans le mauvais sens — sur un tirage grand format, c'est la panne chère.
      Le cas se produit dès qu'une sélection MÊLE portraits et paysages : l'empreinte est
      unique pour toute la grille, les photos non
- [x] A3 **Ce qu'on fait donc** : chaque photo est rendue à SON orientation, puis
      l'image rendue est TOURNÉE d'un quart de tour au moment de la poser sur la feuille.
      Le cadrage est intact ; l'opérateur massicote et redresse le tirage, qui retrouve son
      sens. C'est exactement ce que fait déjà la planche identité debout
      (`ImagePipeline`, « composée debout puis TOURNÉE »)
- [x] A4 Résultat : une sélection **mêlant portraits et paysages** se monte sans rien casser —
      toutes les cases occupent la même empreinte, chacune à son sens

## B. Le modèle : le montage n'est PAS une planche personnalisée ⚠

- [x] B1 `OrderLine.MontageSheetCode` : le code de la FEUILLE, ou null. Null = le
      comportement d'aujourd'hui, un fichier par tirage
- [x] B2 ⚠ **Ne pas toucher à `IsCustomSheet`.** Elle bascule `Total` sur le papier ; ici le
      prix reste `UnitPrice × TotalPrints`. Les deux mécaniques partagent la géométrie, pas
      la politique de prix
- [x] B3 `DraftItem.MontageSheetCode`, en dernier paramètre : les appelants passent les
      précédents par position
- [x] B4 `OrderService` le reporte sur la ligne, comme il le fait déjà pour `CustomSheet`

## C. La géométrie : `MontageFeuille`

- [x] C1 `PlanMontage` : feuille retenue, places par feuille, sens de la case, sens de la
      feuille. Le nombre de feuilles se déduit du nombre de tirages
- [x] C2 ⚠ **Capacité < 2 → pas de plan du tout** (`null`). Un montage à une case par
      feuille n'est pas un montage : c'est un tirage avec des traits de coupe en plus. C'est
      la garde du point 2.3 du plan, posée dans la géométrie plutôt que répétée partout
- [x] C3 `Candidats` : les feuilles où le format tient au moins deux fois, la plus petite
      d'abord — c'est celle qui gâche le moins

## D. Le rendu

- [x] D1 `ImagePipeline.RenderCustomSheetToFile` accepte une **empreinte** explicite, et
      tourne d'un quart de tour toute case qui arrive transposée. Paramètre facultatif en
      dernier : les appelants d'aujourd'hui (planches du minilab) ne changent pas d'un octet
- [x] D2 `PrintOrchestrator.RenderMontage` : capacité, répartition, une planche par fichier
- [x] D3 ⚠ **Repli silencieux sur le comportement d'avant** si la feuille est introuvable,
      hors circuit grand format, ou trop petite pour deux tirages. Une commande ne doit
      jamais échouer parce qu'un catalogue a bougé entre la prise et le tirage
- [x] D4 Traits de coupe et contour : l'opérateur massicote la feuille

## E. L'écran

- [x] E1 `MontageFeuilleView`, intercalé entre la tuile de format et le choix des photos,
      **famille agrandissement seulement**
- [x] E2 ⚠ **« Une par feuille » en tête de liste**, et c'est le choix par défaut. Un
      opérateur qui découpe lui-même ne veut pas d'un montage
- [x] E3 Chaque feuille annonce ce qu'elle donne : combien par feuille, et le gâchis
- [x] E4 Aucun candidat → l'écran ne s'affiche pas, on enchaîne comme avant
- [x] E5 `PhotoGridView` porte le choix jusqu'à la commande, et ne l'applique qu'aux photos
      du format pour lequel il a été fait

## F. Essais

- [x] F1 Capacité : 24×30 → 2 sur 40×50 et sur 40×60, 1 sur 30×40 (donc pas de montage),
      **4** sur 50×70. Les deux dernières valeurs corrigent le plan (voir A1)
- [x] F7 La feuille retenue survit à une mise de côté : perdue, la reprise repartirait sur
      deux fois plus de papier sans que personne s'en aperçoive
- [x] F2 Répartition sur quantités mêlées
- [x] F3 ⚠ **Le prix, avec et sans montage** — l'essai qui garde la caisse honnête
- [x] F4 Capacité 1 → aucun plan, donc aucun changement de comportement
- [x] F5 Une sélection portrait + paysage se monte sur la même feuille
- [x] F6 Les planches du minilab d'aujourd'hui rendent exactement pareil (non-régression
      de `RenderCustomSheetToFile`)
