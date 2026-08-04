# Exécution — 7ᵉ passe

Huit demandes de l'exploitant, 04/08/2026. Plan dans `implementation_plan.md`.

Cette passe contient AUSSI la 6ᵉ, qui n'avait pas été commitée (cadrage des bornes,
réglage du détourage, « mettre en attente », état de la DNP par le spouleur).

## 1. Commande 04-007 : une enveloppe = une commande DE100 ✅

- [x] 1a `De100JobTracker` porte plusieurs `JobId` sous un handle ; `Report` rend une issue
      PAR PHOTO — le minilab notifie par commande, en rendre une seule laisserait cinq sur
      six sans verdict. `PendingCount` compte les TIRAGES
- [x] 1b `De100Driver.Submit(jobs, machine)` : `PIF_StartOrder` → `PIF_Print` × N →
      `PIF_EndOrder`, un seul `OrderId`. La commande part entière ou pas du tout
- [x] 1c protocole du relais : `De100SubmitRequest.Jobs` au pluriel. **Un seul
      constructeur** — un second laissait `System.Text.Json` sans règle et le relais aurait
      refusé toutes les demandes (attrapé par les essais)
- [x] 1d `PrintOrchestrator.SubmitToMinilab` : préparation de toutes les pages, puis un
      envoi. L'arrêt s'examine pendant la préparation ; demandé pendant l'envoi, la
      commande est rappelée (`PIF_CancelOrder`)
- [x] 1e **le verdict du minilab écrit au journal** — il ne l'était nulle part, et c'est ce
      qui a rendu l'enquête sur 04-007 impossible
- [x] 1f **trouvé en route** : `De100BridgePrinter._subscribed` n'était jamais vidé. Le
      relais redémarre (deux fois le 04/08) → on ne se réabonnait plus, et plus aucun
      tirage ne recevait son verdict, en silence, pour toute la vie de l'application
- [x] 1g essais : `De100JobTrackerTests` (+3), `De100ProtocolTests` (+1),
      `PrintCancelTests` refaits sur les nouvelles règles

⚠ **Cause non prouvée.** C'est la seule hypothèse compatible avec les faits ; la preuve
viendra du journal des verdicts, à la prochaine commande multi-photos.

## 2. Les PDF acceptés dans les tirages ✅

- [x] 2a `PDFtoImage` 5.3.0 (PDFium natif). Ghostscript n'est pas installé et Magick.NET
      ne lit pas un PDF sans lui
- [x] 2b `PdfPages.Extraire` / `Developper` : une page = une photo, à la place du PDF dans
      la liste, dans `DataRoot\cache\pdf\<empreinte>\`. 200 ppp, 60 pages au plus
- [x] 2c témoin `pages.txt` écrit en dernier : une extraction interrompue se refait au lieu
      de rendre une commande incomplète
- [x] 2d `.pdf` reconnu par `PhotoScanner` ; `IsPdf` pour les écrans qui l'écartent
- [x] 2e `PhotoGridView` éclate les PDF au scan ; identité et borne les filtrent
- [x] 2f `PdfPagesTests` (8), sur un PDF construit à la main — xref calculé pour de bon
- [x] 2g `FolderBrowsingTests` : un dossier de PDF n'est plus « rien à imprimer »

## 3. Photo d'identité : de l'air au-dessus du crâne ✅

- [x] 3a `IdPhotoFr.TargetCrownMarginMm` 1,75 → 3,0. La tête ne change pas de taille

## 4. « Imprimer » rend la main tout de suite (identité) ✅

- [x] 4a `IdPhotoView.OnPrint` → `Impressions.Lancer` + `Navigator.Home`. Plus de boîte de
      dialogue : tout se lit dans le bandeau, comme sur les tirages

## 5. T + molette ne redressait pas ✅

- [x] 5a `Infrastructure/ToucheFenetre.cs` : abonnement idempotent, fenêtre retenue
- [x] 5b `CropSurface` — `Loaded` joué deux fois abonnait deux fois, et T bascule
- [x] 5c `IdPhotoView` — même construction, même défaut

## 6. Module « Corriger » sur les photos d'identité ✅

- [x] 6a bouton « 🎚 Corriger » → `AdjustView` sur la photo courante
- [x] 6b aperçu dans l'ordre du rendu : fond blanc → corrections → noir et blanc
- [x] 6c `ReglagesRetenus()` : un seul endroit pour les trois sorties (planche, courriel,
      aperçu)
- [x] 6d les corrections repartent à neutre en changeant de photo

## 7. Tri par date décroissante par défaut ✅

- [x] 7a `PhotoScanner.TrierParDateDecroissante` — date la plus ANCIENNE des deux que
      Windows tient (une copie de carte remet la création à l'instant de la copie)
- [x] 7b `PhotoGridView` au chargement ; « trier » bascule désormais vers le NOM
- [x] 7c `IdPhotoView`, `KioskGridView`
- [x] 7d `PhotoScannerOrderTests` (6)

## 8. Ctrl+A puis « Remplir » ne changeait qu'une photo ✅

- [x] 8a `SurLesVisees` : `OnToggleFit`, `OnRotateFrame`, `OnRotatePhoto`, `OnResetCrop`
- [x] 8b le mode est déduit de la photo courante puis IMPOSÉ, jamais basculé une à une
- [x] 8c ligne de journal « … sur N photo(s) » — aucun essai ne clique

## Vérification

- [x] `dotnet build` : 0 erreur, 0 avertissement (hors le CS9057 d'OpenCvSharp, antérieur)
- [x] `dotnet test` : **825 verts**, 0 échec
- [x] application lancée : fenêtre ouverte, relais DE100 connecté, serveur d'envoi sur
      8123, aucune exception au démarrage
- [x] natifs PDF déployés (`runtimes/win-x64/native/pdfium.dll`, `libSkiaSharp.dll`)
- [x] `system_architecture.md` mis à jour

⚠ `Studio.App` et `Studio.De100Host` verrouillent les DLL : les fermer avant de bâtir.

## Ce qui n'est PAS couvert par les essais

Tout ce qui se clique. `Studio.App` n'est pas référencé par la suite d'essais — y faire
entrer WPF ferait entrer une dépendance d'interface dans des essais qui tournent sans
écran. Sont donc à contrôler à l'œil : les quatre boutons du panneau de recadrage,
l'abonnement clavier de T, le module « Corriger », et le retour à l'accueil de l'écran
identité.

## Reste à faire par l'exploitant

- [ ] **Une commande de 4 photos ou plus sur le DE100**, et COMPTER ce qui sort. Le journal
      dit désormais le verdict de la machine photo par photo (« Minilab : tirage 04-013-1-002
      — SORTI »). C'est la seule preuve possible que la cause était bien celle-là
- [ ] **T dans « Modifier » et dans identité** : le bandeau « Redressement 0° » doit
      apparaître au PREMIER appui
- [ ] **Ctrl+A puis « Remplir »** sur une planche : toutes les photos doivent basculer
- [ ] **Un PDF de plusieurs pages** posé dans un dossier de tirages
- [ ] **Une photo d'identité** : un cheveu d'air en haut, sans que la tête ait rétréci ; et
      le bouton « Corriger » qui ouvre les curseurs
- [ ] Reste ouvert des passes précédentes : `Paramètres → Envoi par courriel` (mot de passe
      d'application Gmail) puis message d'essai ; tirage POLA réel ; `config\wifi.json` ;
      les deux commandes de bornes absentes de la base de DiLand (#12360 du 18/06 et #6830
      du 25/06)
