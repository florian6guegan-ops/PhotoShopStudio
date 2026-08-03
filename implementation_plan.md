# Plan d'implémentation — 5ᵉ passe

Six chantiers. Chacun peut être validé ou écarté séparément.
Le plan de la 4ᵉ passe est archivé dans `task.md` et `system_architecture.md`.

Le dépôt porte déjà **du travail non commité** (33 fichiers modifiés, 15 nouveaux) :
`PhotoMailer`, `MailSettings`, `BackgroundRemoval`, `IdShortcuts`, `PendingPrintQueue`,
`DiLandPresence`. `dotnet build` : 0 erreur, 0 avertissement. C'est le point de départ.

---

## Ce que l'enquête a trouvé, et qu'il faut lire avant de valider

### A. « Sans que DiLand ne soit allumé » : réalisable à moitié, et il faut le dire

Les bornes ne déposent pas de fichier sur ce PC. Elles ouvrent une connexion
**.NET Remoting** sur `tcp://192.168.1.102:19200`, et ce port est tenu par le processus
**`FitEng.DiLand.Studio` lui-même** (PID 5824 au moment du relevé, avec les deux bornes
connectées : 192.168.1.38 et 192.168.1.20). Il n'y a **aucun service Windows** derrière :
DiLand fermé, le port est fermé, et la commande de la borne n'arrive nulle part — ni
fichier, ni ligne en base.

Reprendre cette écoute demanderait de réimplémenter .NET Remoting et la sérialisation
binaire des objets DevExpress XPO. **.NET 8 ne sait pas faire du Remoting** : ce n'est pas
une question d'effort, la brique n'existe plus.

Ce qui EST faisable, et qui couvre la panne réelle de la boutique (DiLand tombe en OOM
presque tous les jours, cf. `project_diland_oom_pattern`) : **tout ce qui vient APRÈS
l'arrivée devient indépendant de DiLand.** Voir le chantier 6.

Si l'objectif est vraiment « les bornes n'ont plus besoin de DiLand du tout », le seul
chemin honnête est de **remplacer le logiciel de la borne** par un client qui parle à
notre serveur (`UploadServer`, port 8123, l'API bornes existe déjà). C'est un chantier
à part entière, pas une variante de celui-ci.

### B. Les recadrages des bornes sont perdus depuis toujours — bug confirmé

`DiLandRepository.ReadPhotos` et `DiLandImporter.CropOf` lisent `CropX/CropY/CropWidth/
CropHeight` comme des **fractions** de l'image. Sur la vraie base de la boutique, ce sont
des **pixels**.

Relevé sur `Database.db` du 03/08/2026 : **1231 images, 986 recadrées, ZÉRO** dont
`CropWidth` soit ≤ 1. Exemple vérifié — `7ce78654-5372-4e9d-88d1-8f6c478ed02c.jpg`,
image 1536 × 2048, crop `X=0 Y=44 W=1536 H=1958`, soit un rapport 0,784 ≈ le 8x10
commandé.

`CropSpec(0, 44, 1536, 1958)` ne passe pas `IsValid` → on retombe sur `CropSpec.Full`.
**Chaque « Tirer tel quel » recadre donc autrement que ce que le client a validé à la
borne.** Correction : diviser par `Width`/`Height`, présents en base comme dans le XML.

### C. Les bornes redressent, contrairement au commentaire du code

`DiLandImporter.Import` passe `FineRotationDegrees: 0` avec le commentaire
« les bornes ne redressent pas ». La colonne `FineRotationAngle` existe, et **113 des
1231 images** de la base en portent une non nulle. À reprendre.

---

## 1. Envoi par courriel des photos d'identité — terminer le raccordement

### Ce qui existe déjà

- `src/Studio.Core/Mail/MailSettings.cs` — réglages SMTP, `Load`/`Save` dans `config/`,
  `EstUtilisable`, `CeQuiManque()`. Complet.
- `src/Studio.Printing/PhotoMailer.cs` — `Preparer()` (trois fichiers : originale,
  recadrée web 1200 px, recadrée HD) et `Envoyer()` avec traduction des refus SMTP.
  Complet.

### Ce qui manque

**Personne n'appelle ni l'un ni l'autre.** Aucun `using Studio.Core.Mail` hors de ces deux
fichiers. Le code est écrit et mort.

| | Fichier | Changement |
|---|---|---|
| 1a | `[MODIFY]` `src/Studio.App/AppServices.cs` | propriété `Mail` (chargée de `config/mail.json`), `SaveMail(MailSettings)`, et branchement de `PhotoMailer.Log` sur `FileLog` dans `Load` — comme `LargeFormatPrinter.Log`, sans quoi le journal des envois part dans le vide |
| 1b | `[NEW]` `src/Studio.App/Views/MailSendView.xaml(.cs)` | écran d'envoi : adresse du client, mot facultatif du photographe, rappel de ce qui part (trois fichiers), bouton Envoyer. Préparation ET envoi **hors du fil d'interface** — un SMTP qui ne répond pas gèlerait la caisse deux minutes |
| 1c | `[MODIFY]` `src/Studio.App/Views/IdPhotoView.xaml(.cs)` | bouton **✉ Envoyer par courriel** dans la barre d'action, à côté d'« Imprimer la planche ». Il passe le cadrage EN COURS (`_crop`, `_redressement`, `ImageAdjustments`) — c'est ce que l'opérateur a sous les yeux |
| 1d | `[MODIFY]` `PhotoMailer` | les fichiers sont déposés sous `DataRoot/courriel/<aaaa-MM-jj>/<nom>` et **conservés** : si l'envoi échoue, l'opérateur réessaie sans tout refaire (le message le dit déjà, mais rien ne le garantissait) |

**Envoyer n'imprime pas et n'enregistre pas de commande.** Un client peut vouloir les deux,
ou seulement le fichier ; ce sont deux gestes distincts et le prix n'est pas le même.
Question posée plus bas.

## 2. Écran « Paramètres » — configurer le courriel poste par poste

Demandé pour que **les futurs postes opérateur** se configurent sans toucher au code.
`MailSettings` vit déjà dans `D:\PhotoStudioData\config\mail.json`, donc hors du dépôt
public — c'est la bonne place pour un mot de passe d'application.

| | Fichier | Changement |
|---|---|---|
| 2a | `[NEW]` `src/Studio.App/Views/SettingsView.xaml(.cs)` | écran **Paramètres**, section « Envoi par courriel » : serveur, port, adresse d'expédition, nom affiché, mot de passe d'application (`PasswordBox`), interrupteur Actif |
| 2b | `[MODIFY]` `SettingsView` | bouton **« Envoyer un message d'essai »** : s'envoie à l'adresse d'expédition elle-même et rapporte le refus en clair. Sans lui, une configuration fausse se découvre devant un client |
| 2c | `[MODIFY]` `src/Studio.App/Views/HomeView.xaml(.cs)` | tuile **⚙ Paramètres** sur l'accueil |
| 2d | `[MODIFY]` `PhotoMailer.Envoyer` | le message d'erreur dit « Catalogue → Envoi par courriel », qui n'existe pas. À corriger en « Paramètres → Envoi par courriel » |

Aide en clair sur l'écran : Gmail exige un **mot de passe d'application** (validation en
deux étapes activée), le mot de passe du compte est refusé depuis 2022. C'est la question
qui reviendra sur chaque nouveau poste.

## 3. Redressement : T ARME le mode, la molette règle

### Ce que le code fait aujourd'hui

Deux endroits, même geste : `Keyboard.IsKeyDown(Key.T)` **pendant** le cran de molette.

- `src/Studio.App/Views/IdPhotoView.xaml.cs:716` — `OnStageWheel`
- `src/Studio.App/Controls/CropSurface.xaml.cs:475` — `OnWheel`

Trois défauts :

1. **Il faut tenir T et rouler en même temps** — à une main, au comptoir, c'est raté.
2. **`Keyboard.IsKeyDown` dépend du focus.** Le focus posé sur une liste ou un bouton
   (le cas dès qu'on a choisi un papier), la touche part ailleurs et la molette zoome
   au lieu de redresser. C'est très probablement le « mal implémenté » signalé.
3. **Rien à l'écran ne dit que le mode est armé.** Le seul retour est le chiffre en
   degrés, dans un coin de la barre.

### Ce qu'on fait

| | Fichier | Changement |
|---|---|---|
| 3a | `[MODIFY]` `IdPhotoView`, `CropSurface` | **T bascule** un mode `RedressementArme`. Armé, la molette règle l'angle ; Échap ou un second T en sort. La touche est captée en `PreviewKeyDown` **sur la vue**, donc le focus n'a plus d'importance |
| 3b | `[MODIFY]` idem | **T maintenue continue de marcher** : les deux gestes coexistent, personne ne réapprend |
| 3c | `[MODIFY]` `IdPhotoView.xaml`, `CropSurface.xaml` | bandeau visible tant que le mode est armé : « Redressement — molette pour régler · T ou Échap pour sortir », et le champ des degrés mis en évidence |
| 3d | `[MODIFY]` `CropEditorView`, `EditSelectionView` | vérifier les **trois** écrans qui recadrent (cf. `project_crop_gestures`) : la surface est partagée, mais l'armement et le bandeau se posent par écran |
| 3e | `[MODIFY]` `DiLandImporter.Import` | lire `FineRotationAngle` au lieu de forcer 0 (constat C) |

Le pas reste 0,25° par cran et la borne ±15° : ils n'ont pas été mis en cause.

## 4. Commandes du jour : séparer tirages photo et photos d'identité

`OrdersView` liste tout en un seul flot, commande par commande. Une planche d'identité y
est indiscernable d'un paquet de 10×15 — or ce n'est ni le même client, ni le même délai,
ni le même geste de rattrapage.

| | Fichier | Changement |
|---|---|---|
| 4a | `[MODIFY]` `src/Studio.App/Views/OrdersView.xaml(.cs)` | deux onglets — **Tirages photo** / **Photos d'identité** — sur le modèle de la bascule Ordres/Historique de `KioskOrdersView`, que les opérateurs connaissent déjà. Plus un onglet **Tout** |
| 4b | `[MODIFY]` `OrdersView.xaml.cs` | classement d'une ligne : identité si le produit du catalogue porte un `Sheet`, avec repli sur `OrderItem.SheetCellWidthMm` — les commandes déjà enregistrées n'ont pas toutes le champ |
| 4c | `[MODIFY]` `OrdersView.xaml.cs` | **une commande mixte apparaît dans les deux onglets**, n'y montrant que ses lignes concernées. La masquer d'un côté ferait disparaître un tirage à réimprimer |
| 4d | `[MODIFY]` `OrdersView.xaml` | compteur par onglet (« Tirages photo (7) ») : c'est ce qu'on lit en arrivant le matin |

## 5. Historique des bornes : retélécharger et remodifier

L'historique (`KioskOrdersView`, onglet Historique) ne propose que **« Remettre dans la
liste »**. Une commande close ne peut être ni retéléchargée ni rouverte — c'est justement
ce qu'on demande le lendemain, quand le client revient.

Obstacle technique : l'historique tient des `KioskOrderEntry` (journal), pas des
`DiLandOrder`. **Le journal ne mémorise pas `DirectoryName`**, donc on ne sait pas où sont
les photos.

| | Fichier | Changement |
|---|---|---|
| 5a | `[MODIFY]` `src/Studio.Store/DiLand/KioskOrderJournal.cs` | champ `DirectoryName` sur `KioskOrderEntry`, renseigné par `Describe`. Les entrées anciennes ne l'ont pas : repli par recherche de l'Oid dans l'instantané, et message clair si DiLand a purgé |
| 5b | `[MODIFY]` `src/Studio.App/Views/KioskOrdersView.xaml(.cs)` | boutons **⬇ Télécharger** et **✏ Modifier** sur chaque ligne d'historique |
| 5c | `[MODIFY]` `src/Studio.Store/DiLand/DiLandImporter.cs` | `Stage(..., ecraser: true)` : aujourd'hui `CopyPhotoTo` **saute les fichiers déjà présents**. Un second téléchargement rouvrait donc un dossier périmé sans rien dire — c'est le « malgré le fait que les photos ont été téléchargées » de la demande |
| 5d | `[MODIFY]` `KioskOrdersView` | rouvrir depuis l'historique **ne remet pas la commande dans la liste du jour** : `MarkInProgress` refuse déjà une entrée close, on s'appuie dessus |
| 5e | `[MODIFY]` `HomeView` | rappel de `system_architecture.md` : toute action de ligne ajoutée à un écran doit l'être à l'autre. L'historique n'existe que dans `KioskOrdersView` — rien à faire ici, mais le correctif 5c profite aux deux |

## 6. Lire les commandes des bornes sans DiLand — ce qui est atteignable

Lire le constat A avant de valider ce chantier : **l'arrivée** restera tributaire de
DiLand. C'est **tout le reste** qu'on lui prend.

Un dossier de commande est **auto-suffisant** : `Order.xml` + `Files.txt` + `F/`. Vérifié
sur `20260803-1648-ommcdsbz.COM` — 41 photos, 22,55 €, `Sys_Product_Alias="8x10"`, et
chaque image avec `FileName`, `OriginalFileName`, `Quantity`, `Angle`,
`FineRotationAngle`, `CropX/Y/Width/Height`, `Width`, `Height`. **Tout ce que la base
donne, le XML le donne** — sans base, sans instantané, sans DiLand.

| | Fichier | Changement |
|---|---|---|
| 6a | `[NEW]` `src/Studio.Store/DiLand/DiLandOrderXml.cs` | lecture d'`Order.xml` → `DiLandOrder` + `DiLandOrderLine` + `DiLandOrderPhoto`. Le nom du produit vient de `OrderLine/@Sys_Product_Alias` |
| 6b | `[MODIFY]` `src/Studio.Store/DiLand/DiLandRepository.cs` | balayage de **`IncomingOrders\*.COM`** (arrivées, pas encore intégrées : DiLand fermé, tombé, ou tâche d'import bloquée) **et** de `Orders\*.COM`. Le disque devient une source à part entière |
| 6c | `[MODIFY]` `src/Studio.Store/DiLand/DiLandImporter.cs` | `Pending()` fusionne base et disque, **dédoublonné sur `DirectoryName`**. Une commande vue des deux côtés n'apparaît qu'une fois |
| 6d | `[MODIFY]` `KioskOrderJournal` | clé du journal : les commandes venues du XML n'ont pas d'Oid numérique, seulement des GUID. Clé longue **déterministe** dérivée de `Sys_GlobalUniqueId`, pour qu'une commande intégrée plus tard par DiLand ne réapparaisse pas en double |
| 6e | `[MODIFY]` `DiLandRepository.ReadPhotos` + `DiLandImporter.CropOf` | **correction du bug B** : les recadrages sont en pixels, à diviser par `Width`/`Height`. Vaut pour les deux sources |
| 6f | `[MODIFY]` `KioskOrdersView`, `HomeView` | dire d'où vient chaque commande quand DiLand est absent : « lue sur le disque — DiLand est fermé ». `DiLandPresence.IsRunning()` existe déjà |
| 6g | `[NEW]` `tools/Studio.DiLandProbe` (mode `xml`) | vérifier sur les vraies commandes de la boutique que le XML rend la même chose que la base — c'est le seul contrôle possible sans fermer DiLand en pleine journée |

### Essais

`tests/Studio.Tests/` : lecture d'un `Order.xml` réel anonymisé (recadrage en pixels →
fractions, redressement, produit, quantités), dédoublonnage base/disque, clé
déterministe, et non-régression du classement de `OrdersView`.

---

## Vérification, pour chaque chantier

- `dotnet build` : 0 erreur, 0 avertissement nouveau
- `dotnet test` : les 692 verts actuels + les nouveaux
- toutes les clés `StaticResource` des vues se résolvent
- application lancée, écrans parcourus

⚠ Fermer `Studio.De100Host.exe` avant les essais, sinon les trois essais d'intégration
DE100 échouent sur le canal occupé (ce n'est pas une régression, cf. `task.md`).

---

## Trois questions avant de commencer

1. **Envoi par courriel** — le bouton ✉ doit-il aussi enregistrer une commande (donc
   facturer l'envoi), ou reste-t-il un geste gratuit à côté de l'impression ?
2. **Chantier 6** — vu le constat A, va-t-on jusqu'à la lecture disque (qui règle les
   plantages de DiLand mais pas DiLand fermé), ou faut-il d'abord chiffrer le
   remplacement du logiciel des bornes ?
3. **Bugs B et C** (recadrages en pixels, redressement des bornes ignoré) — ils sont
   indépendants du reste et corrigeables tout de suite. À faire en premier ?
