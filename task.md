# Exécution — 4ᵉ passe

Priorité posée par l'exploitant (02/08/2026) : **la DNP d'abord**. Le rouleau ne change
jamais et on n'imprime que sur du 10×15 — les chantiers « un produit planche par média
DNP » du plan sont donc écartés, il n'en faut qu'un.

Facturation des agrandissements à taille libre : **au prix du produit catalogue dans lequel
la taille tient** (30×40 → prix du 30×40 ; 40×50 → prix du 40×50).

## 1. Photos d'identité sur la DS620 — tous les formats de document

Cause : le pilote DP-DS620 ne déclare que **onze formats privés** (RawKind 119 à 129) et
aucun format standard. Le produit `ID-FR-6` était à 152 × 102 mm ; le `(6x4)` du pilote fait
**156,2 × 104,9 mm** (615 × 413 centièmes de pouce). L'écart dépassait la tolérance de
1,5 mm de `FindDriverPaperSize`, qui retombait sur `new PaperSize("Format produit", …)` —
RawKind 0, soit `DMPAPER_USER`. Le DEVMODE capturé était écrasé, la DS620 recevait une
forme inconnue et **jetait le travail sans erreur**. Le journal du 01/08 le montre :
« page obtenue 152×102 mm (**Format produit**) » sur deux commandes, aucun tirage sorti.

- [x] 1a `BitmapPrinter.ChoisirFormat` : plus proche format déclaré, **jamais plus petit**
      que le tirage (−1,5 mm admis), plafonné à +2,5 mm ; les deux orientations, le bon
      sens l'emportant à écart égal. Isolé en méthode `internal` pure : aucun poste de
      développement n'a de DS620 branchée
- [x] 1b `BitmapPrinter.Print` : refus explicite, nommant les formats acceptés, quand
      l'imprimante ne déclare **que** des formes privées et qu'aucune ne convient
- [x] 1b′ `BitmapPrinter.EnsurePageSizeAvailable` + appel dans `PrintOrchestrator` **avant**
      le rendu et avant l'état « Spooled » — sinon l'enveloppe serait proposée à la
      réimpression au démarrage suivant alors que rien n'est parti
- [x] 1c `OrderItem.SheetCellWidthMm` / `SheetCellHeightMm` + `SheetCellSize` + `DraftItem`
      (en DERNIER paramètre : les appelants passent les précédents par position) +
      `OrderService`
- [x] 1d `PrintOrchestrator` : la cellule vient de l'article, sinon du produit
- [x] 1e `IdPhotoView` : la cellule est celle du DOCUMENT choisi (35×45, 26×32, 51×51…),
      capacité recalculée et affichée dans la liste des papiers, papiers trop petits
      écartés, planche pleine par défaut, messages sans « 35×45 » figé
- [x] 1f `products.json` : `ID-FR-6` porté à **156,2 × 104,9 mm** (la forme `(6x4)` du
      pilote), `Output: "Printer"` explicite, renommé « Photos d'identité — planche 10×15 »
- [x] 1g `DnpPaperMatchTests` (10) sur les onze formes réelles de la DS620
- [x] 1h `Studio.PrintProbe papier <imprimante> <Lmm> <Hmm>` : dit si un format sortira,
      **sans gâcher une feuille**. C'est le contrôle qui manquait avant d'enregistrer un
      produit au catalogue

## Vérification

- [x] `dotnet build` : 0 erreur, 0 avertissement nouveau
- [x] `dotnet test` : **665 verts** (655 + 10)
- [x] contrôle sur la VRAIE DP-DS620 (`PrintProbe papier`) :
      156,2 × 104,9 → accepté · 105 × 156,1 → accepté · **152 × 102 → refusé**, avec la
      liste des onze formes
- [ ] planche réellement imprimée sur la DS620, journal relevé
      (`page obtenue … ((6x4), …)` et non plus « Format produit ») — **à faire par
      l'exploitant, machine allumée**

## 2. Écritures noires sur fond noir

Cause : WPF donne le NOIR à tout `TextBlock` qui n'hérite pas d'autre chose, et `App.xaml`
ne posait aucun style de texte. Le relevé initial donnait 22 candidats ; **dix étaient de
fausses alertes** — un `TextBlock` posé dans un `Button` hérite de la couleur du bouton, et
les styles `BigButton`, `FlatButton`, `LigneListe` et `Secondaire` en fixent une. Restaient
**douze vraies invisibilités**, toutes posées directement dans un `Grid` ou un `Border`.

- [x] 2a `App.xaml` : styles `Texte` et `Valeur`, avec la RÈGLE écrite en commentaire.
      **Aucun style implicite** — il repeindrait aussi le contenu des menus déroulants et
      des listes, qui gardent le fond clair du système : on troquerait du noir sur noir
      contre du blanc sur blanc
- [x] 2b `IdPhotoView` (4), `ProductEditView` (6), `PhotoGridView` (2), `KioskHomeView` (2),
      `KioskGridView` (1)
- [x] 2c contrôle automatique : toutes les clés `StaticResource` des vues se résolvent
- Non traités, à dessein : les deux emoji (👋, ✅) — les glyphes couleur de Segoe UI Emoji
  ignorent la couleur du texte ; et le `Slider` d'`EditSelectionView`, dont le rail et le
  pouce système restent visibles sur fond sombre

## 3. Format POLA : un vrai cadre Polaroid

Cotes officielles Polaroid (film 600 / i-Type) : tirage **88,47 × 107,52 mm**, fenêtre image
**78,94 × 76,80 mm**. Deux choses font la forme, et l'ancien produit (marge uniforme de
2 mm, mode « photo entière ») n'en rendait aucune : la fenêtre est **presque carrée**, et la
bande du bas fait **près du quart de la hauteur** — 25,95 mm contre 4,77 aux trois autres bords.

- [x] 3a `PolaroidFrame` : les cotes, et `Place()` qui pose le plus grand cadre tenant dans
      le tirage, centré. Sur un 10×15 (0,671 contre 0,823) il occupe toute la largeur et
      laisse du blanc en haut et en bas — un cadre étiré ne serait plus un Polaroid
- [x] 3b `FitMode.Polaroid`, ajouté EN FIN d'énumération
- [x] 3c `ImagePipeline.RenderPolaroid` : composé comme une planche (image blanche +
      `Composite`), et non par un `Extent` décentré qui se joue à un signe près. Contour de
      découpe sur le bord du CADRE, profil ICC sur le tirage entier
- [x] 3d teinte : `InverseLevel(7 %, 95 %)` pour le voile, saturation −12 %, rouge +4 % et
      bleu −3 %. Ni vignettage ni grain : plusieurs secondes par tirage pour un effet que le
      cadre donne déjà
- [x] 3e les écrans de recadrage montrent la FENÊTRE carrée et non la feuille
      (`CropEditorView`, `PhotoGridView`) ; la bascule Remplir/Entier est grisée sur un
      Polaroid, et « Réinitialiser » ne fait plus sauter le cadre
- [x] 3f `ProductEditView` : « Polaroid » comme troisième cadrage
- [x] 3g `products.json` : `pola` et `10x15pola` en `DefaultFit: "Polaroid"`, marge à 0
- [x] 3h `PolaroidFrameTests` (7) + **rendu réel contrôlé à l'œil** sur une photo de la boutique

## 4. Catalogue : supprimer un tirage

- [x] 4a `Product.Copy()` — la copie complète, déplacée du Catalogue vers le domaine pour
      être vérifiable. Il manquait **huit champs** : `Output`, `MinilabMachineId`,
      `MinilabPrintSizeName`, `PriceTiers`, et `GapMm`/`CutMarks`/`CutBorder`/`DateStamp` de
      la planche. Modifier un tirage du minilab le transformait en produit imprimante et
      effaçait ses paliers de tarif
- [x] 4b `ProductCatalog.CountReferences` : les commandes qui citent un produit
- [x] 4c bouton **🗑 Supprimer** dans `CatalogView`, en rouge et en dernier. Un produit cité
      par une commande des 30 derniers jours est **désactivé** et non supprimé — tout le
      circuit appelle `Require(code)`, et une commande en attente de réimpression
      deviendrait inexploitable
- [x] 4d `ProductEditView` : liste **« Sortie »** (file Windows / minilab Fuji / fichier
      repris à la main). C'est le champ dont l'absence causait la perte. L'imprimante n'est
      plus exigée quand la sortie n'est pas une file Windows
- [x] 4e `OnSave` conserve les quatre réglages de planche que la fiche ne montre pas —
      les recréer à neuf effaçait l'horodatage exigé sur les photos d'identité
- [x] 4f `CatalogEditTests` (7), dont un essai **par réflexion** : il compare la liste des
      propriétés de `Product` à ce que la copie restitue, et échouera dès qu'une propriété
      sera ajoutée sans être recopiée

## 5. Agrandissements : format personnalisé (A2, A3…)

Facturation retenue : **au prix du format du catalogue dans lequel la taille tient**.
Départages : le moins cher d'abord — c'est le prix qu'on annonce — puis le plus petit.

- [x] 5a `EnlargementSizes` : formats normalisés A4 → A0, choix du papier tarifant, code de
      produit déterministe (`agr-297x420`), fabrication du produit
- [x] 5b `CustomEnlargementView` : tuiles normalisées, saisie libre en cm, **prix annoncé
      avant le choix des photos**, mémoire des tailles récentes
- [x] 5c la tuile « Personnalisé » apparaît dans les agrandissements, avec son propre libellé
      et son propre geste — celle de l'impression rapide compose des planches minilab, ce
      qui n'a rien à voir
- [x] 5d le format demandé devient un **vrai produit du catalogue**, ce qui fait fonctionner
      la grille, « Modifier », le rendu, la boîte grand format et surtout `Require` à la
      réimpression. Conséquence voulue : le deuxième A2 est déjà dans la liste
- [x] 5e `CustomEnlargementTests` (13), dont les trois cas de l'exploitant
- [x] 5f entrée **« Agrandissement personnalisé… (A2, A3…) »** dans le MENU de format, dans
      la grille ET dans « Modifier ». Elle est DISTINCTE de « Personnalisé… » : celle-là
      compose des planches minilab, celle-ci sort un tirage unique sur l'Epson — les
      confondre enverrait un A2 au minilab, qui le refuserait

## Vérification d'ensemble

- [x] `dotnet build` : 0 erreur, 0 avertissement nouveau
- [x] `dotnet test` : **692 verts** (655 au départ, 37 nouveaux)
- [x] toutes les clés `StaticResource` des vues se résolvent
- [x] **application lancée** avec le catalogue modifié : elle démarre, la fenêtre s'ouvre,
      le relais DE100 se connecte, aucune exception au journal

### Piège à connaître : le relais DE100 survit à l'application

Tuer `Studio.App` sans le laisser se fermer laisse `Studio.De100Host.exe` en vie, et il
garde le canal nommé. Les trois essais d'intégration `De100BridgeIntegrationTests` échouent
alors sur « Toutes les instances des canaux de communication sont occupées » — ce n'est PAS
une régression du code. `Get-Process Studio.De100Host | Stop-Process` et tout repasse au vert.

## Reste ouvert

- [ ] **Planche identité réellement imprimée sur la DS620** (voir chantier 1)
- [ ] Écrans à parcourir une fois sur place : identité, fiche produit, catalogue, borne.
      Seul l'accueil a été vu à l'écran ; le reste n'a été vérifié que par le compilateur et
      par le contrôle des ressources
- [ ] Un tirage POLA réel, pour juger la teinte sur papier — elle est volontairement
      discrète, elle peut être poussée (`AppliquerTeintePolaroid`)
- [ ] Remplir `D:\PhotoStudioData\config\wifi.json` (SSID + mot de passe) — sans lui, le
      second code QR ne s'affiche pas
- [x] Commité et poussé sur `origin/pilotes-de100-dnp` (le commit emporte aussi la fin de la
      3ᵉ passe, restée non commitée — les fichiers portaient les deux, impossible à séparer)
- [x] Catalogue de la boutique sauvegardé dans `catalog/boutique/`, avec
      `tools\Sauver-Catalogue.cmd` pour le rafraîchir. **Le lancer après chaque changement du
      catalogue**, sinon la copie vieillit en silence
- [x] Fusionné dans `main` et poussé — `main` porte maintenant la version courante
