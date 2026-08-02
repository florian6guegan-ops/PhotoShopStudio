# Plan d'implémentation — 4ᵉ passe

Cinq chantiers indépendants. Chacun peut être validé ou écarté séparément.
Le plan de la 3ᵉ passe est archivé dans `task.md` et `system_architecture.md`.

---

## 1. Photos d'identité sur la DNP DS620 : « ça n'a rien imprimé »

### Ce que le code fait aujourd'hui

`IdPhotoView.OnPrint` → `Orders.CreateOrder` → `PrintOrchestrator.PrintEnvelope` →
`RenderIdSheetToFile` (le PNG de la planche est bien écrit) → `PrintPages` →
`BitmapPrinter.Print(printerName: "DP-DS620", widthMm: 152, heightMm: 102, devMode)`.

`BitmapPrinter.Print` (`src/Studio.Printing/BitmapPrinter.cs`) :

```csharp
if (devModeBytes is not null) DevMode.Apply(...);          // ligne 38-39
...
doc.DefaultPageSettings.PaperSize =
    FindDriverPaperSize(doc.PrinterSettings, w100, h100)
    ?? new PaperSize("Format produit", w100, h100);        // ligne 53-55
```

### Diagnostic — cause trouvée, deux défauts qui se cumulent

**a) Le produit ID-FR-6 est à une taille que le pilote DS620 ne connaît pas.**

Relevé sur ce poste (`PrinterSettings.PaperSizes` de DP-DS620) : le pilote ne déclare que
onze formats, tous `Kind=Custom`, et **aucun** ne vaut 152 × 102 mm :

| Format pilote | centièmes de pouce | mm |
|---|---|---|
| (6x4) | 615 × 413 | **156,2 × 104,9** |
| PR (4x6) | 413 × 615 | 104,9 × 156,2 |
| (5x7), (6x8), (6x9), (6x4.5)… | | |

`ID-FR-6` est enregistré à **152 × 102 mm** = 598 × 402 centièmes.
Écart au (6x4) : 17 et 11 centièmes, pour une tolérance de **6** (`BitmapPrinter.cs:110`).
`FindDriverPaperSize` ne trouve donc rien et retombe sur `new PaperSize("Format produit", …)`.

Le `10x15-dnp` du catalogue, lui, est à **105 × 156,1 mm** — il tombe pile sur `PR (4x6)`,
et c'est pour cela que celui-là sort.

**b) La taille personnalisée écrase le DEVMODE.**

`DevMode.Apply` est appelé **avant** `DefaultPageSettings.PaperSize`. Le DEVMODE capturé
(`devmode-ID-FR-6.bin`) porte le bon média ; la ligne 53 le remplace par un format que le
pilote DS620 ne sait pas honorer. Le travail part au spouleur et **rien ne sort** — pas
d'exception, donc pas de message d'erreur : l'écran annonce « envoyée à l'impression ».

**c) Un seul format d'identité existe, et il ignore le document choisi.**

- Le catalogue ne contient qu'un produit planche : `ID-FR-6`, cellule **fixée à 35 × 45 mm**.
- `IdDocumentPickerView` laisse pourtant choisir parmi 274 documents (Espagne 26 × 32, USA
  51 × 51…). Le gabarit à l'écran suit la norme, mais **la planche imprimée reste en
  35 × 45** : `PrintOrchestrator` lit `product.Sheet.CellWidthMm`, jamais le document.
  Une planche espagnole sort donc au mauvais format.

### Corrections proposées

- **[MODIFY]** `src/Studio.Printing/BitmapPrinter.cs`
  - `FindDriverPaperSize` : tolérance portée de 6 à **20 centièmes (~5 mm)**, en choisissant
    le format déclaré **le plus proche** plutôt que le premier qui passe, et **jamais plus
    petit** que le tirage demandé (sinon on rogne). Un `(6x4)` de 156,2 × 104,9 accueille
    alors un 152 × 102 sans rien perdre.
  - Quand aucun format déclaré ne convient : **ne plus inventer un `PaperSize` custom en
    silence**. Sur une imprimante qui ne déclare que des formats `Custom` (cas DNP), lever
    une `InvalidOperationException` nommant les formats disponibles. Mieux vaut un message
    que douze planches fantômes.
  - Ne poser `DefaultPageSettings.PaperSize` **que si** le DEVMODE n'en a pas déjà fixé un
    compatible — le DEVMODE fait autorité, il vient du dialogue du pilote.
  - Journaliser (`Log`) le format retenu **avant** l'envoi, pas seulement dans `PrintPage`.

- **[MODIFY]** `D:\PhotoStudioData\catalog\products.json`
  - `ID-FR-6` : dimensions portées à **156,2 × 104,9 mm** (le (6x4) réel du pilote),
    `Output: "Printer"` écrit explicitement.
  - **[NEW]** quatre produits planche supplémentaires, un par média DS620 utile — pour que
    « tous les formats fonctionnent » :
    `ID-DNP-6x4` (156,2 × 104,9), `ID-DNP-5x7` (131,1 × 181,1),
    `ID-DNP-6x8` (156,2 × 206,2), `ID-DNP-6x9` (156,2 × 231,9).
    Chacun avec son `DevmodeFile` à capturer (bouton « Capturer réglages » du Catalogue) et
    son nombre de cases par défaut recalculé par `IdSheetLayout.MaxCopies`.

- **[MODIFY]** `src/Studio.Core/Domain/Order.cs`
  - `OrderItem` : ajout de `SheetCellWidthMm` / `SheetCellHeightMm` (`double?`).
    Null = la cellule du produit s'applique (commandes déjà enregistrées inchangées).

- **[MODIFY]** `src/Studio.Printing/PrintOrchestrator.cs`
  - Branche `product.Sheet is { } sheet` : la cellule vient de
    `item.SheetCellWidthMm ?? sheet.CellWidthMm` (idem hauteur).

- **[MODIFY]** `src/Studio.App/Views/IdPhotoView.xaml.cs`
  - `DraftItem` porte désormais la cellule du **document choisi** (`_document.WidthMm/HeightMm`).
  - `MaxCopiesForSelectedProduct` calcule sur la cellule du document, pas sur celle du produit.
  - La liste `ProductCombo` affiche « N photos par planche » recalculé pour ce document, et
    le choix par défaut est le plus petit papier qui porte le nombre de photos demandé.
  - Avant l'envoi : contrôle que la cellule tient sur le papier, message explicite sinon.

- **[MODIFY]** `src/Studio.Core/Domain/Order.cs` + `Studio.Store` (DraftItem)
  - `DraftItem` : deux paramètres de plus, propagés vers `OrderItem`.

- **[NEW]** `tests/Studio.Tests/DnpPaperMatchTests.cs`
  - le (6x4) est retenu pour un 152 × 102 ; aucun format n'est inventé quand rien ne convient ;
  - un format déclaré plus PETIT que le tirage n'est jamais retenu ;
  - la cellule du document l'emporte sur celle du produit.

---

## 2. Écritures noires sur fond noir

### Ce que le code fait aujourd'hui

`App.xaml` pose une palette sombre (`PageBrush #12181E`, `CardBrush #1E2731`) mais **aucun
style implicite** pour `TextBlock`, `CheckBox`, `Slider`. Or le défaut WPF de ces contrôles
est le **noir** (`SystemColors.ControlTextBrush`). Chaque élément qui ne fixe pas son
`Foreground` est donc invisible.

### Relevé exhaustif (fait, 22 occurrences)

| Fichier | Éléments concernés |
|---|---|
| `IdPhotoView.xaml` | `CopiesText`, `QuantityText`, `ComplianceText`, « Noir et blanc », `GrayscaleCheck` |
| `ProductEditView.xaml` | 6 `TextBlock` (« × », « mm », « copies de », « planche de copies », « produit proposé dans les listes »), `SheetCheck`, `EnabledCheck` |
| `PhotoGridView.xaml` | `QuantityText`, `TotalText` |
| `KioskHomeView.xaml` | « 👋 », « Code de sortie », `PinDots` |
| `FolderBrowserView.xaml` | `{Binding Display}`, icône « 📁 » |
| `HomeView.xaml` | `LargeFormatTitle` |
| `KioskGridView.xaml` | `{Binding QuantityLabel}` |
| `KioskDoneView.xaml` | « ✅ » |
| `SourcePickerView.xaml` | `{Binding Label}` |
| `EditSelectionView.xaml` | `Slider` (pouce et rail au gris système) |

### Correction proposée

- **[MODIFY]** `src/Studio.App/App.xaml`
  - **Un seul point de vérité** : `<Style TargetType="TextBlock">` implicite posant
    `Foreground="{StaticResource TextBrush}"`, plus les mêmes pour `CheckBox`, `RadioButton`,
    `Label` et `TextBlock` dans `ContentPresenter`.
  - Les styles nommés existants (`PageTitle`, `Hint`) reçoivent `BasedOn` pour ne rien perdre.
  - **Exception à traiter** : les `ComboBoxItem` / `ListBoxItem` ont un fond CLAIR par défaut ;
    un style implicite TextBlock les rendrait blanc sur blanc. On ajoute donc un
    `<Style TargetType="ComboBoxItem">` et `<Style TargetType="ListBoxItem">` sombres, pour
    que le fond suive le texte au lieu de l'inverse. C'est le point à vérifier à l'écran.
  - `Slider` : style implicite avec pouce et rail sur `AccentBrush` / `PanelBrush`.

- **[MODIFY]** les 10 fichiers XAML ci-dessus : rien à faire si le style implicite suffit ;
  ceux qui demandent une couleur particulière (`ComplianceText`, déjà repeint en code)
  gardent leur affectation explicite, qui l'emporte.

**Vérification** : lancement de l'application et parcours des dix écrans concernés, capture
d'écran à l'appui. Un style implicite touche TOUT l'arbre visuel — c'est le chantier qui
demande le plus de contrôle visuel et le moins de code.

---

## 3. Format « POLA » : que la photo ressemble à un Polaroid

### Ce que le code fait aujourd'hui

Deux produits : `pola` (102 × 152 mm, `Fit`, `BorderMm: 2`) et `10x15pola` (`Fill`, marge 0).
Le mode `Fit` de `ImagePipeline.RenderInto` pose une marge blanche **uniforme** de 2 mm
autour d'une photo au rapport de la source. Ça ne ressemble à rien d'un Polaroid.

### Cotes réelles (film Polaroid 600 / i-Type, source officielle Polaroid)

| | pouces | mm |
|---|---|---|
| Tirage complet | 3,483 × 4,233 | **88,47 × 107,52** |
| Fenêtre image | 3,108 × 3,024 | **78,94 × 76,80** |

D'où les proportions, en fraction du tirage :

- marge latérale : 4,77 mm → **5,39 % de la largeur**
- marge haute : 4,77 mm → **4,44 % de la hauteur**
- image : 89,2 % × 71,4 %
- **bande basse : 25,95 mm → 24,1 % de la hauteur** — c'est elle qui fait le Polaroid

### Correction proposée

- **[NEW]** `src/Studio.Imaging/Geometry/PolaroidFrame.cs`
  - les cotes ci-dessus en constantes nommées, et `Layout(largeurPx, hauteurPx)` rendant le
    `PixelRect` de la fenêtre image. Le cadre est calculé **au rapport réel du film** puis
    centré dans le tirage : sur un 10×15 (rapport 1:1,49 contre 1:1,215), le Polaroid occupe
    toute la largeur et le blanc restant se répartit haut et bas. Un contour de découpe
    marque le vrai bord du Polaroid, à suivre aux ciseaux.

- **[MODIFY]** `src/Studio.Core/Domain/Enums.cs`
  - `FitMode` : troisième valeur **`Polaroid`**, ajoutée EN FIN d'énumération (les commandes
    enregistrées portent des valeurs numériques qu'il ne faut pas déplacer).

- **[MODIFY]** `src/Studio.Imaging/ImagePipeline.cs`
  - `RenderInto` : branche `FitMode.Polaroid` — fenêtre image remplie en `Fill` (la photo est
    donc **carrée**, comme sur un vrai Polaroid), fond blanc, contour de découpe posé sur le
    bord du cadre et non sur celui de la photo.
  - **[NEW]** rendu « à la Polaroid » optionnel dans `ApplyAdjustments` : léger voile
    (contraste −8, noirs relevés +6), dominante chaude (+6 en température), vignettage doux.
    Réglé par un booléen du produit, désactivable — c'est un parti pris esthétique, il ne
    doit pas s'imposer.

- **[MODIFY]** `src/Studio.Core/Domain/Product.cs`
  - `bool PolaroidLook { get; set; }` — le traitement couleur, distinct du cadre.

- **[MODIFY]** `src/Studio.App/Views/ProductEditView.xaml{,.cs}`
  - « Polaroid (cadre carré, bande basse) » comme troisième entrée de `FitCombo`,
    et une case « rendu vieilli ».

- **[MODIFY]** `D:\PhotoStudioData\catalog\products.json`
  - `pola` : `DefaultFit: "Polaroid"`, `BorderMm: 0`, `PolaroidLook: true`.
  - `10x15pola` : idem — les deux portent le même nom au comptoir.

- **[MODIFY]** `src/Studio.App/Views/PhotoGridView.xaml.cs`, `EditSelectionView`
  - l'aperçu et le cadre de recadrage doivent montrer la **fenêtre carrée**, pas le 10×15 :
    sinon l'opérateur cadre sur un rectangle et la photo sort recoupée.

- **[NEW]** `tests/Studio.Tests/PolaroidFrameTests.cs` — proportions, centrage, cas paysage.

---

## 4. Catalogue : supprimer un tirage

### Ce que le code fait aujourd'hui

`CatalogView.xaml` propose Modifier / Dupliquer / Activer-Désactiver / Capturer / Finitions.
**Aucune suppression.** Un produit dupliqué par erreur reste au catalogue pour toujours.

### Défaut trouvé en chemin (à corriger dans le même passage)

`CatalogView.Clone` (ligne 90) et `ProductEditView.OnSave` (ligne 111) **perdent des champs** :
`Output`, `MinilabMachineId`, `MinilabPrintSizeName`, `PriceTiers`, `Sheet.GapMm`,
`Sheet.CutMarks`, `Sheet.CutBorder`, `Sheet.DateStamp`.

Conséquence : **modifier ou dupliquer un tirage du minilab le transforme en produit
imprimante** (`Output` retombe sur son défaut `Printer`) et **efface ses paliers de tarif**.
C'est très probablement ainsi que `ID-FR-6` s'est retrouvé sans `Output`.

### Corrections proposées

- **[MODIFY]** `src/Studio.App/Views/CatalogView.xaml`
  - bouton **« 🗑 Supprimer »** sur chaque ligne, en `DangerBrush`, à droite.

- **[MODIFY]** `src/Studio.App/Views/CatalogView.xaml.cs`
  - `OnDeleteProduct` : confirmation nommant le produit, puis retrait et sauvegarde.
  - **Garde-fou** : un produit cité par une commande du jour n'est pas supprimé mais
    **désactivé**, avec explication — `PrintOrchestrator._catalog.Require(code)` lève sinon,
    et une commande en attente de réimpression deviendrait inexploitable.
  - `Clone` complété avec les huit champs manquants.

- **[MODIFY]** `src/Studio.App/Views/ProductEditView.xaml{,.cs}`
  - liste **« Sortie »** (file Windows / fichier pour Photoshop / minilab Fuji) : c'est le
    champ dont l'absence causait la perte.
  - `OnSave` : `Output`, `MinilabMachineId`, `PriceTiers` conservés.
  - `PrinterCombo` n'est plus obligatoire quand la sortie n'est pas une file Windows.

- **[NEW]** `tests/Studio.Tests/CatalogEditTests.cs` — aller-retour Clone/Save sans perte,
  refus de supprimer un produit référencé.

---

## 5. Agrandissements : format personnalisé (A2, A3…)

### Ce que le code fait aujourd'hui

`PrintFormatView` (ligne 31) :

```csharp
if (_famille == PrintFamily.Quick) lignes.Add(FormatRow.Personnalise());
```

La tuile « Personnalisé » n'existe **que** pour l'impression rapide, et elle mène à
`CustomSizeView`, qui compose des **planches sur papier minilab** (`CustomSheetLayout`) —
sans rapport avec un agrandissement, qui est un tirage unique sorti en fichier pour l'Epson.

Le catalogue s'arrête au 70 × 100 (désactivé) ; **ni A3 (297 × 420) ni A2 (420 × 594)**
ne sont proposés.

### Correction proposée

- **[MODIFY]** `src/Studio.App/Views/PrintFormatView.xaml.cs`
  - la tuile « Personnalisé » apparaît aussi pour `PrintFamily.Enlargement`, avec un libellé
    et une destination propres (« taille au choix · Epson »).

- **[NEW]** `src/Studio.App/Views/CustomEnlargementView.xaml{,.cs}`
  - saisie largeur × hauteur en cm, **plus des tuiles normalisées** : A4, A3, A3+, A2, A1,
    30×45, 40×60, 50×75 — c'est ce que l'exploitant demande vraiment (« pour pouvoir par
    exemple faire des tirages en A2, A3 »).
  - contrôle contre la largeur de rouleau / feuille de la SC-P800 (max 431,8 mm = 17 pouces) :
    au-delà, on le dit avant que l'opérateur n'annonce un prix.
  - **prix : celui du produit catalogue dans lequel la taille TIENT** (décision de
    l'exploitant, 02/08/2026). Si le tirage demandé rentre dans un 30×40, il est facturé au
    prix du 30×40 ; s'il faut un 40×50, c'est le prix du 40×50. Règle : le **moins cher**
    des produits `ManualFile` actifs dont les deux cotes sont ≥ à celles demandées, dans
    l'une ou l'autre orientation. Aucun tarif au dm² à saisir, aucun prix à taper à la main.
    Au-delà du plus grand produit du catalogue, l'écran le dit et refuse.
  - l'écran annonce le produit retenu et son prix AVANT le choix des photos — comme le fait
    déjà `CustomSizeView` pour les planches.
  - mémoire des tailles récentes, sur le modèle de `CustomSizeView`.

- **[MODIFY]** `src/Studio.App/Views/PhotoGridView.xaml.cs`
  - accepte une taille d'agrandissement libre comme il accepte déjà `taillePerso` :
    fabrication d'un `Product` fantôme (`Output = ManualFile`, code « agrandi-perso »),
    remplacé à la commande par un **vrai produit** créé à la volée dans le catalogue
    (code `agr-420x594`), pour que `_catalog.Require` le retrouve à la réimpression.
  - **[MODIFY]** entrée « Personnalisé… » du menu de format (`ProductMenu.Ouvrir`) : sur un
    produit ManualFile, elle ouvre la saisie d'agrandissement et non celle des planches.

- **[NEW]** `tests/Studio.Tests/CustomEnlargementTests.cs` — formats normalisés, refus
  au-delà de 431,8 mm, code de produit engendré stable.

---

## Ce qu'il restera à faire À LA MAIN après le chantier 1

La DS620 ne peut sortir que les formats du **rouleau réellement chargé** : un rouleau 6"
donne le (6x4), le (6x6), le (6x8) et le (6x9) ; un rouleau 5" donne le (5x3.5), le (5x5)
et le (5x7). Les produits planche des deux familles seront au catalogue, mais seuls ceux
du rouleau en place sortiront — c'est une contrainte machine, pas du logiciel. Le contrôle
ajouté au chantier 1 le dira avant l'envoi au lieu de laisser partir un travail muet.

Chaque nouveau produit planche a besoin de son **DEVMODE capturé** (Catalogue → « Capturer
réglages », imprimante allumée) : c'est le dialogue du pilote qui fixe le média et le
surlaminage, et il n'est pas reproductible depuis le code.

---

## Ordre d'exécution proposé

1. **Chantier 2** (lisibilité) — indépendant, visible tout de suite, aucun risque métier.
2. **Chantier 4** (suppression + perte de champs) — corrige un défaut qui abîme le catalogue
   à chaque modification ; à faire avant de toucher aux produits.
3. **Chantier 1** (identité DNP) — dépend du 4 pour éditer `Output` proprement.
4. **Chantier 3** (Polaroid).
5. **Chantier 5** (agrandissement personnalisé).

## Vérification, à chaque chantier

- `dotnet build` : 0 erreur, 0 avertissement nouveau ;
- `dotnet test` : les 655 existants + les nouveaux ;
- application lancée, écran concerné parcouru ;
- pour le chantier 1 : **une planche réellement imprimée sur la DS620**, journal relevé
  (`Impression « … » sur DP-DS620 : demandé …, page obtenue …`).
