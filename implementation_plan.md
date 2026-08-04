# Plan d'implémentation — 7ᵉ passe

Huit demandes de l'exploitant, 04/08/2026. Le plan de la 6ᵉ passe est archivé dans
`task.md`.

Point de départ : `main`, 804 essais verts, la 6ᵉ passe **non encore commitée**
(23 fichiers modifiés dans l'arbre de travail).

---

## 1. Commande 04-007 : deux photos sur quatre ne sont jamais sorties

### Ce que le journal montre

`orders/2026/08/20260804-007-a8843d32/order.log` :

```
11:03:13.078  minilab-submit-start   env=1, machine=A, tirages=20
11:03:14.298  minilab-submitted      commandes=[2608041103132608, 2608041103135879,
                                                2608041103139320, 2608041103141931]
```

4 photos × 5 exemplaires = 20 tirages, **quatre commandes DE100 distinctes ouvertes et
refermées en 1,2 seconde**. Le minilab a accepté les quatre handles. L'exploitant en a vu
sortir deux, à cinq exemplaires chacune : **deux commandes entières ont été perdues à la
machine**, pas des exemplaires manquants.

### Cause retenue — `PIF_Print` n'est pas fait pour une commande par image

La signature du SDK le dit :

```csharp
PIF_Print(StringBuilder orderHandle, ref ST_IMAGE_DATA, ST_PARAM[], uint);
PIF_GetPrintInfo(StringBuilder orderHandle, uint index, ref ST_PRINT_INFO);
```

`PIF_Print` prend le handle de commande **en paramètre**, et `PIF_GetPrintInfo` relit les
tirages d'une commande **par INDICE**. Une commande DE100 porte donc N images : c'est ainsi
que procède le pilote de DiLand, sur les 9 336 tirages de son journal.

`PrintOrchestrator.SubmitToMinilab` ouvre au contraire **une commande par photo** —
`PIF_StartOrder` → `PIF_Print` → `PIF_EndOrder`, quatre fois de suite en une seconde. Le
`lock (_sendSync)` sérialise nos appels mais n'attend pas que la machine ait rangé la
commande précédente. C'est un usage que rien ne garantit, et il est le seul candidat
sérieux à une perte silencieuse : les quatre handles sont revenus `Ok`, aucune erreur n'a
été remontée, et deux tirages n'existent pas.

**Correction : une ENVELOPPE = une commande DE100.** `PIF_StartOrder` une fois, `PIF_Print`
pour chaque page, `PIF_EndOrder` une fois.

### Le défaut qui a rendu l'enquête impossible, et qu'il faut corriger aussi

**Rien n'écrit au journal le verdict du minilab.** `De100Driver.JobFinished` remonte bien
jusqu'à `AppServices`, mais n'y sert qu'à rafraîchir le bandeau : `SuiviImpressions`
n'écrit ni « photo 3 tirée » ni « photo 3 abandonnée après 30 min ». Le fichier de
`04-007` s'arrête donc à l'envoi, et la machine n'a laissé aucune trace. Sans cette ligne,
le prochain incident ne se diagnostiquera pas mieux.

### Fichiers

- `[MODIFY] src/Studio.Printing/Devices/Fuji/IMinilabPrinter.cs` — `Submit` prend
  `IReadOnlyList<De100PrintJob>` et rend UN handle ; `Cancel` inchangé.
- `[MODIFY] src/Studio.Printing/Devices/Fuji/De100Driver.cs` — `Submit(jobs, machineId)` :
  une transaction, un `PIF_Print` par travail, un `OrderId` commun. Chaque travail reste
  suivi par le tracker sous le handle de la commande.
- `[MODIFY] src/Studio.Printing/Devices/Fuji/De100JobTracker.cs` — plusieurs `JobId` sous
  un même `OrderHandle` (aujourd'hui `Dictionary<handle, Entry>`, donc un seul).
- `[MODIFY] src/Studio.Printing/Devices/Fuji/Bridge/De100Protocol.cs` +
  `De100BridgeClient.cs` + `tools/Studio.De100Host/Program.cs` — la requête `submit` porte
  une LISTE de travaux.
- `[MODIFY] src/Studio.Printing/PrintOrchestrator.cs` — `SubmitToMinilab` prépare toutes
  les pages puis appelle `Submit` une fois. L'arrêt s'examine pendant la PRÉPARATION des
  images, plus entre deux envois : une fois la commande partie elle est entière, ce qui est
  le comportement voulu.
- `[MODIFY] src/Studio.App/AppServices.cs` — `JobFinished` écrit au journal :
  numéro de commande, photo, verdict, motif.
- `[NEW] tests/Studio.Tests/De100OrderGroupingTests.cs` — un envoi de quatre pages ouvre
  UNE commande ; le tracker rend les quatre issues ; une erreur au milieu annule la
  commande entière.

⚠ **Ce n'est pas une cause prouvée, c'est la seule hypothèse compatible avec les faits.**
La preuve viendra du journal des verdicts, à la prochaine commande multi-photos.

---

## 2. Les PDF acceptés dans les tirages

Aucune dépendance PDF dans la solution ; Ghostscript n'est pas installé (Magick.NET ne
lira donc pas un PDF tel quel). `PDFtoImage` 5.3.0 embarque PDFium en natif, sans rien à
installer sur le poste — c'est ce qui convient à une boutique.

**Une page = une photo.** Un PDF de trois pages entre dans la grille comme trois vignettes,
recadrables et facturables une par une. Les pages sont rendues en JPEG dans
`DataRoot\cache\pdf\<empreinte>\p001.jpg`, à 300 ppp bornés au plus grand format du
catalogue : tout le reste du logiciel continue de ne connaître que des fichiers image, et
ni le rendu, ni le minilab, ni la DNP n'ont à savoir qu'un PDF existe.

### Fichiers

- `[MODIFY] src/Studio.Imaging/Studio.Imaging.csproj` — `PDFtoImage` 5.3.0.
- `[NEW] src/Studio.Imaging/PdfPages.cs` — `Extraire(pdf, dossierCache)` → liste de JPEG,
  idempotent (empreinte = chemin + date + taille), page par page pour ne pas tenir un
  document de 200 pages en mémoire. Plafond de pages, avec message clair au-delà.
- `[MODIFY] src/Studio.Sources/PhotoScanner.cs` — `.pdf` reconnu ; `IsPdf`.
- `[MODIFY] src/Studio.App/Views/PhotoGridView.xaml.cs` — les PDF trouvés au scan sont
  éclatés en pages avant construction de la grille, sur le fil de fond qui scanne déjà.
- `[NEW] tests/Studio.Tests/PdfPagesTests.cs` — un PDF de deux pages donne deux fichiers,
  la seconde extraction ne réécrit rien, un fichier illisible est écarté sans faire échouer
  l'ouverture du dossier.

**Hors périmètre**, et à dire : photo d'identité (on ne fait pas d'identité depuis un PDF)
et agrandissements (l'écran grand format lit un fichier unique).

---

## 3. Photo d'identité : un peu d'air au-dessus du crâne

`IdPhotoFr.TargetCrownMarginMm` vaut **1,75 mm**, calé sur DiLand le 03/08/2026 (il valait
4 mm avant). C'est ce calage qui serre trop.

**1,75 → 3,0 mm.** Le cadre remonte de 1,25 mm sur 45 : la tête garde exactement sa taille
(`TargetHeadMm` ne bouge pas), seul l'espace au-dessus grandit. Les bornes de conformité
suivent toutes seules (`IdDocumentSpec.CrownMarginMinMm/MaxMm` se calculent depuis la
cible), donc aucun cadrage ne passe à l'orange.

Ne concerne que la norme française de la boutique — les 274 autres documents estiment leur
marge depuis leur format et ne sont pas touchés.

### Fichiers

- `[MODIFY] src/Studio.Imaging/Geometry/IdPhotoFr.cs` — la constante et son commentaire.
- `[MODIFY] tests/Studio.Tests/…IdPhoto…Tests.cs` — les attendus qui citent 1,75.

---

## 4. « Imprimer » doit rendre la main tout de suite

La grille le fait déjà (`PhotoGridView.OnPrint` → `Impressions.Lancer` → `Navigator.Home`).
**`IdPhotoView.OnPrint`, non** : il attend `PrintEnvelope` en entier, affiche une boîte de
dialogue, et ne rentre à l'accueil qu'après. C'est le rendu de la planche qui prend les
secondes — l'écran identité est donc le seul qui fasse attendre l'opérateur.

### Fichiers

- `[MODIFY] src/Studio.App/Views/IdPhotoView.xaml.cs` — `OnPrint` calque la grille :
  création de la commande (court), puis `Impressions.Lancer` et `Navigator.Home`
  immédiatement. Plus de `MessageBox` de succès — l'avancement se lit dans le bandeau du
  haut, comme pour les tirages. `PrinterNotReadyException` est déjà traitée par
  `SuiviImpressions`.

---

## 5. T + molette ne redresse pas

Le journal du 04/08 le montre noir sur blanc :

```
10:59:23.318  Geste « OnStripWheel » · C=False T=False (armé=False) · 4300_001_page-0005.jpg
```

`armé=False` **après que l'opérateur a appuyé sur T**. `CropSurface` s'abonne au
`PreviewKeyDown` de la FENÊTRE dans son `Loaded`, et se désabonne dans son `Unloaded` :

```csharp
Loaded   += (_, _) => { if (Window.GetWindow(this) is { } f) f.PreviewKeyDown += OnPreviewKeyDown; };
Unloaded += (_, _) => { … f.PreviewKeyDown -= OnPreviewKeyDown; RedressementArme = false; };
```

WPF déclenche `Loaded` **plusieurs fois** sur un même élément (reparentage, retemplatage
d'un conteneur) sans `Unloaded` entre les deux. Le gestionnaire est alors abonné deux fois,
et un appui sur T bascule `RedressementArme` **deux fois : le mode ne s'arme jamais** et le
bandeau n'apparaît pas. La touche MAINTENUE ne rattrape pas, parce que le geste attendu
est justement l'appui-relâché.

`IdPhotoView` a la même construction, donc le même défaut.

### Fichiers

- `[MODIFY] src/Studio.App/Controls/CropSurface.xaml.cs` — abonnement idempotent
  (`-=` avant `+=`), fenêtre retenue pour se désabonner de la BONNE.
- `[MODIFY] src/Studio.App/Views/IdPhotoView.xaml.cs` — même correction.
- `[NEW] src/Studio.App/Infrastructure/ToucheFenetre.cs` — le petit utilitaire d'abonnement
  unique, pour que le troisième écran qui en aura besoin ne réinvente pas le défaut.

Non couvert par les essais : `Studio.App` n'est pas référencé par la suite (aucun test ne
presse une touche). **À vérifier à l'œil** — le bandeau « Redressement 0° » doit apparaître
au premier appui sur T.

---

## 6. Le module « Corriger » sur les photos d'identité

`IdPhotoView` n'a que deux cases : noir et blanc, fond blanc. `AdjustView` est déjà un
écran autonome — `(photos, réglages de départ, callback)` — utilisé par la grille.

Bouton **« Corriger »** à côté de « Fond blanc » : il ouvre `AdjustView` sur la photo
courante, et les réglages retenus reviennent dans un champ `_corrections`, qui part dans le
`DraftItem` à l'impression (aujourd'hui construit avec un `ImageAdjustments` ne portant que
`Grayscale` et `WhiteBackground` — ils y sont repris).

L'aperçu de l'écran identité applique les corrections sur la vignette 1600 px déjà chargée,
en amont du détourage et du noir et blanc : `_corrige → _detoure → gris`. Le tirage, lui,
refait tout en pleine résolution par `ImagePipeline`, comme pour le fond blanc.

### Fichiers

- `[MODIFY] src/Studio.App/Views/IdPhotoView.xaml` — le bouton.
- `[MODIFY] src/Studio.App/Views/IdPhotoView.xaml.cs` — `_corrections`, ouverture
  d'`AdjustView`, chaîne d'aperçu, report dans le `DraftItem`.

---

## 7. Les photos triées de la plus RÉCENTE à la plus ancienne

`PhotoScanner.Scan` trie par nom (`StringComparer.OrdinalIgnoreCase`), et les trois écrans
qui l'appellent gardent cet ordre. Le tri par date existe, mais seulement derrière le bouton
« trier », et il est CROISSANT.

Le tri par défaut devient **date décroissante**, sur les trois écrans (tirages, identité,
borne). Le bouton « trier » bascule ensuite vers le nom, puis revient.

`Scan` garde son tri alphabétique — il est déterministe, testé, et c'est lui qui décide de
ce qui rentre sous le plafond de 1 200. Le classement par date est posé APRÈS, à
l'affichage, à partir des dates lues une seule fois.

### Fichiers

- `[MODIFY] src/Studio.Sources/PhotoScanner.cs` — `TrierParDateDecroissante(List<string>)`,
  une lecture de date par fichier, les illisibles en fin de liste plutôt qu'en exception.
- `[MODIFY] src/Studio.App/Views/PhotoGridView.xaml.cs` — appliqué au chargement ; `OnSort`
  bascule date↓ / nom.
- `[MODIFY] src/Studio.App/Views/IdPhotoView.xaml.cs`, `KioskGridView.xaml.cs` — appliqué
  au chargement.
- `[NEW] tests/Studio.Tests/PhotoScannerOrderTests.cs`.

---

## 8. Ctrl+A puis « Remplir » ne change qu'une photo

`EditSelectionView.OnToggleFit` écrit sur `_courante`, jamais sur `Visees()` :

```csharp
var actuel = _courante.FitOverride ?? produit.DefaultFit;
_courante.FitOverride = voulu == produit.DefaultFit ? null : voulu;
```

Or Ctrl+A coche `Ciblee` sur toute la sélection, et c'est bien `Visees()` que suivent les
corrections et le contour de découpe. **Trois autres boutons du même panneau ont le même
défaut** : « Pivoter le cadre », « Pivoter la photo », « Réinitialiser ».

Les quatre passent sur `Visees()`. Le MODE est décidé par la photo courante puis imposé aux
autres — et non basculé photo par photo, sinon une planche mi-remplie mi-entière
s'inverserait au lieu de s'aligner.

### Fichiers

- `[MODIFY] src/Studio.App/Views/EditSelectionView.xaml.cs` — `OnToggleFit`,
  `OnRotateFrame`, `OnRotatePhoto`, `OnResetCrop` ; une ligne de journal comme pour le
  contour de découpe (`… sur N photo(s)`), qui est ce qui rend ces gestes vérifiables.

---

## Vérification

- `dotnet build` — 0 erreur, 0 avertissement (hors les 2 CS9057 d'OpenCvSharp).
- `dotnet test` — 804 verts au départ, plus les nouveaux.
- Application lancée, les trois écrans ouverts.

⚠ Fermer `Studio.App` et `Studio.De100Host` avant de bâtir : ils verrouillent les DLL et le
tube.

### À contrôler par l'exploitant, parce qu'aucun essai ne le peut

1. **Une commande de 4 photos ou plus sur le DE100**, et compter ce qui sort. Le journal
   dira désormais le verdict de la machine photo par photo.
2. **T dans « Modifier » et dans identité** : le bandeau « Redressement » doit apparaître au
   premier appui.
3. **Un PDF de plusieurs pages** posé dans un dossier de tirages.
4. **Une photo d'identité** : le cadrage doit avoir un cheveu d'air en haut, sans que la
   tête ait rétréci.

---

## Ordre d'exécution proposé

Les points 3, 4, 5, 7 et 8 sont courts et sans risque — ils partent en premier, et
l'exploitant peut s'en servir dès le prochain lancement. Le point 1 (regroupement des
commandes DE100) et le point 2 (PDF) viennent ensuite : ils touchent le protocole du relais
et ajoutent une dépendance.
