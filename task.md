# Exécution — 5ᵉ passe

Décisions de l'exploitant (03/08/2026) :

- **L'envoi par courriel est facturé : 5,00 € de plus PAR PHOTO.**
- **On ne remplace pas le logiciel des bornes.** Le chantier 6 se fait donc en lecture
  disque : il rend Studio indépendant de DiLand pour tout ce qui suit l'arrivée de la
  commande, mais l'arrivée elle-même reste tributaire de DiLand (voir constat A du plan).
- **Les bugs B et C sont corrigés en premier.**
- **L'historique garde les photos dans les données de Studio, 30 jours** — jamais dans les
  dossiers de DiLand, qu'il purge sans prévenir.

---

## 0. Bugs B et C — recadrages perdus, redressements ignorés ✅

- [x] 0a `DiLandOrderPhoto.EnFractions` + `FromRaw` : le recadrage de DiLand est en
      **PIXELS**, ramené en fractions à la lecture. La conversion vit à UN seul endroit —
      la base et `Order.xml` la réclament tous les deux
- [x] 0b `DiLandRepository.ReadPhotos` lit `Width`, `Height` et `FineRotationAngle`
- [x] 0c `DiLandOrderPhoto.FineRotationDegrees` + `DiLandImporter.Import` le transmet
- [x] 0d `DiLandCropUnitsTests` (6), sur les valeurs RÉELLES de la boutique
- [x] 0e schéma des essais existants complété

## 1. Envoi par courriel des photos d'identité — 5 € par photo ✅

- [x] 1a `ProductOutput.Email`, ajouté EN FIN d'énumération
- [x] 1b `MailProduct` : produit `envoi-courriel` à 5,00 €, créé au catalogue à la première
      utilisation. Jamais retarifé ensuite — le prix se règle au Catalogue
- [x] 1c `PrintOrchestrator` : une enveloppe `Email` est close sur place, sans rendu ni
      spouleur. La lui faire traverser le rendu la mettrait « en attente d'imprimante »
      pour une prestation qui n'en demande aucune
- [x] 1d `AppServices.Mail` / `SaveMail` / `ProduitEnvoiCourriel` + `PhotoMailer.Log`
      branché sur `FileLog`
- [x] 1e `MailSendView` : prix annoncé AVANT l'envoi, adresse, mot facultatif. Préparation
      et envoi hors du fil d'interface
- [x] 1f `IdPhotoView` : bouton **✉ Envoyer**, avec le cadrage en cours
- [x] 1g les fichiers restent sous `DataRoot/courriel/<date>/` ; **rien n'est facturé si
      l'envoi échoue**
- [x] 1h `ProductEditView` : la sortie « Envoi par courriel » ajoutée à la liste — sans
      elle, ouvrir la fiche du produit en aurait fait un produit imprimé
- [x] 1i `MailBillingTests` (6)

## 2. Écran Paramètres ✅

- [x] 2a `SettingsView` : serveur, port, expéditeur, nom affiché, mot de passe
      d'application (`PasswordBox`), Actif
- [x] 2b bouton **« Envoyer un message d'essai »** (`PhotoMailer.EnvoyerUnEssai`), qui passe
      par le MÊME client SMTP qu'un vrai envoi — deux chemins finiraient par différer
- [x] 2c tuile ⚙ Paramètres sur l'accueil
- [x] 2d `PhotoMailer` renvoie vers « Paramètres → Envoi par courriel »

## 3. Redressement : T ARME le mode, la molette règle ✅

- [x] 3a `IdPhotoView` + `CropSurface` : T bascule un mode armé, capté en `PreviewKeyDown`
      **sur la fenêtre**. `Keyboard.IsKeyDown` dépendait du focus, et le focus part sur la
      liste des papiers dès qu'on choisit son tirage — c'est ce qui faisait passer le
      redressement pour cassé
- [x] 3b T maintenue continue de fonctionner ; les champs de saisie sont épargnés
- [x] 3c bandeau orangé visible tant que le mode est armé, Échap pour en sortir
- [x] 3d **collision trouvée** : `CropEditorView` liait déjà T à « pivoter le cadre ». La
      même touche faisait donc DEUX choses sur le même écran. T va au redressement ;
      pivoter le cadre passe sur **F**, et reste à un clic droit
- [x] 3e la bande de vignettes d'`EditSelectionView` suit le mode armé
- [x] 3f libellé « Pivoter la photo (T + molette) » corrigé — ce bouton fait un quart de
      tour, pas un redressement

## 4. Commandes du jour : tirages / photos d'identité ✅

- [x] 4a trois onglets — Tout, Tirages photo, Photos d'identité
- [x] 4b une ligne est « identité » si son produit porte un `Sheet`, avec repli sur
      `OrderItem.SheetCellWidthMm`
- [x] 4c le tri se fait par ENVELOPPE, parce que c'est l'enveloppe qu'on réimprime : une
      enveloppe mixte paraît dans les deux onglets, entière. Rien ne disparaît
- [x] 4d compteur par onglet
- [x] 4e boutons **⬇ Télécharger** et **✏ Modifier** sur chaque commande, tirages ET
      planches d'identité — le même geste que sur une commande de borne. Les photos
      d'origine sont toujours recopiées à la création de la commande, donc elles sont là
      des jours plus tard même si le client a repris sa clé
- [x] 4f « Modifier » ne touche PAS la commande d'origine : un tirage depuis cet écran
      donne une nouvelle commande. Une commande déjà encaissée ne doit changer ni de
      contenu ni de montant — l'infobulle le dit

## 5. Historique des bornes : les photos vivent chez nous ✅

- [x] 5a `KioskOrderEntry.ArchiveDirectory` + `KioskOrderJournal.SetArchive`
- [x] 5b `DiLandImporter.Archiver` : les photos sont recopiées dans
      `DataRoot\diland\archive\<oid>` à la prise en charge, tant que les fichiers de DiLand
      sont sûrement là. Attendre la clôture serait trop tard
- [x] 5c **`Purge` efface l'archive AVEC l'entrée**, à 30 jours : sans cela le disque
      grossirait d'un mois de photos de clients
- [x] 5d boutons ⬇ Télécharger et ✏ Modifier dans l'historique, servis depuis NOTRE copie
- [x] 5e `CopyFileTo(..., ecraser:)` : la recopie sautait les fichiers déjà présents, donc
      un second téléchargement rouvrait un dossier périmé sans rien dire
- [x] 5f `ArchiverDepuisDiLand` : rattrapage pour les entrées d'AVANT l'archivage. Seul cas
      où l'historique redescend chez DiLand ; disparaîtra de lui-même en un mois
- [x] 5g le dossier de travail `diland\travail` disparaît : une seule copie interne
- [x] 5h `KioskArchiveTests` (6), dont « DiLand a tout effacé et l'historique sert quand
      même »

## 6. Lire les commandes des bornes sans DiLand ✅

- [x] 6a `DiLandOrderXml` : `Order.xml` → commande, lignes, photos. Produit dans
      `Sys_Product_Alias` ; **date prise sur le NOM DU DOSSIER**, l'attribut `Date` étant
      écrit à l'américaine (`08/03/2026` = 3 août)
- [x] 6b `ReadKioskOrdersFromDisk` balaie `IncomingOrders\*.COM` et `Orders\*.COM` ; les
      `.TMP` sont écartés (réception en cours)
- [x] 6c `Pending()` fusionne base et disque, dédoublonné sur `DirectoryName`, la base
      l'emportant — elle porte le vrai Oid
- [x] 6d clé de journal déterministe (FNV-1a) : `string.GetHashCode()` est randomisé par
      processus, et une clé changeante ferait resurgir toutes les commandes traitées
- [x] 6e `FenetreDuDisque` = 30 j : `Orders` garde des mois, tout y verser noierait la liste
- [x] 6f `KioskOrdersView` avertit quand DiLand est fermé — les bornes ne peuvent alors
      **plus déposer**, et l'opérateur doit le savoir
- [x] 6g `Studio.DiLandProbe xml` : compare disque et base sur les vraies commandes
- [x] 6h `DiLandOrderXmlTests` (8) + `KioskDiskFallbackTests` (6)

## Vérification d'ensemble

- [x] `dotnet build` : 0 erreur, 0 avertissement
- [x] `dotnet test` : **759 verts** (692 au départ, 67 nouveaux)
- [x] toutes les clés `StaticResource` des vues se résolvent
- [x] **application lancée** : fenêtre ouverte, relais DE100 connecté, serveur d'envoi sur
      8123, aucune exception au journal
- [x] **`DiLandProbe xml` sur les VRAIES commandes** : aucun écart entre disque et base

⚠ `Studio.App` et `Studio.De100Host` verrouillent les DLL : les fermer avant de bâtir.
Fermés deux fois le 03/08/2026 avec l'accord de l'exploitant.

## Ce que le contrôle sur la vraie boutique a trouvé

**Deux commandes de bornes absentes de la base de DiLand** — pas même supprimées :

| | |
|---|---|
| #12360 (18-001) | 18/06 10:14 · 18 photos en 10x15 · 10,80 € · 18 recadrées |
| #6830 (25-006) | 25/06 17:08 · 1 photo en 30x40 · 19,90 € · recadrée |

DiLand ne les a jamais intégrées. Elles sont trop anciennes pour remonter dans la liste
(fenêtre de 30 jours) ; `Studio.DiLandProbe xml` les montre. **À regarder par
l'exploitant** : ont-elles été servies ?

## Reste à faire par l'exploitant

- [ ] **Contrôler à l'œil un tirage de borne recadré** : le bug B durait depuis le début, et
      personne n'a jamais vu ce que le cadrage du client donnait réellement
- [ ] Renseigner `Paramètres → Envoi par courriel` (mot de passe d'application Gmail), puis
      **envoyer un message d'essai**
- [ ] Vérifier le prix du produit « Envoi des photos par courriel » au Catalogue (5,00 €)
- [ ] Parcourir les écrans : identité (T + molette), commandes du jour (trois onglets),
      historique des bornes (Télécharger / Modifier)
- [ ] Les deux commandes ci-dessus
- [ ] Reste ouvert des passes précédentes : planche identité réellement imprimée sur la
      DS620, tirage POLA réel, `config\wifi.json`
